using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace StutterBegone
{
    /// <summary>
    /// Splits the game's giant shared UI canvas into one small canvas per screen.
    ///
    /// The game pools EVERY full-screen UI under ONE shared ScreenSpaceOverlay canvas
    /// ('Canvas'). With a full mod set that canvas holds ~144,000-150,000 CanvasRenderers
    /// (only ~50-140 active at a time). Unity invalidates and rebuilds at the CANVAS
    /// granularity, so anything that dirties that canvas (a tooltip transition, opening a
    /// screen) pays the full ~144k cost. This plugin reparents every top-level screen onto
    /// its own tiny overlay canvas, so a change only rebuilds that screen's canvas.
    ///
    /// Every top-level child is moved to a new root ScreenSpaceOverlay canvas with
    /// sortingOrder = BaseSortingOrder + its original sibling index, which preserves the
    /// exact original layering (z-order). A GraphicRaycaster is added to each new canvas
    /// (and registered with the game's RaycasterManager) so the UI stays clickable.
    ///
    /// The tooltip (InputTooltipListDisplay) is moved too - it is the per-frame churn
    /// source, and on its own canvas a tooltip transition rebuilds ~20 elements instead of
    /// ~144k, which is the root fix for the hover stutter. No separate tooltip handling.
    ///
    /// Screens listed in ExcludeNames are left on the shared canvas. The default is empty
    /// (reparent everything); add a name there if a screen breaks (e.g. one a mod locates by
    /// path or a direct-child Transform.Find that runs at runtime).
    ///
    /// This plugin makes no Harmony patches. It triggers on the shop scene load
    /// (SceneManager.sceneLoaded; the shop scene is named "Start", the menu/boot scene is
    /// skipped) and runs once per scene load; the split is idempotent, so a re-load (or an
    /// additive scene load) is a safe no-op on already-split screens.
    /// </summary>
    [BepInPlugin("dr.eldon.stutter.begone", "StutterBegone", "1.3.0")]
    public class Plugin : BaseUnityPlugin
    {
        private ConfigEntry<bool> _enabled;
        private ConfigEntry<bool> _verbose;
        private ConfigEntry<string> _exclude;
        private ConfigEntry<int> _delayFrames;
        private ConfigEntry<int> _baseSortOrder;
        private ConfigEntry<KeyCode> _rescanKey;

        private Canvas _giant;
        // Canvas watcher state: ourMaxSort = first sortingOrder slot above ALL our
        // reparented screens. New runtime canvases are shifted to ourMaxSort + their own
        // sortingOrder (preserves relative order). Monotonic: only ever grows, so a re-split
        // (same canvas) never pushes the baseline back down.
        private int _ourMaxSort;
        private HashSet<GameObject> _knownCanvases;
        private HashSet<string> _excluded = new HashSet<string>(StringComparer.Ordinal);

        // Idempotent-split state (per giant-canvas instance). _screenSort gives each screen a
        // stable sortingOrder so re-splits never collide; _nextSortIndex only grows. Both are
        // reset when the giant canvas is recreated (a new scene load).
        private Canvas _splitCanvas;
        private int _nextSortIndex;
        private readonly Dictionary<Transform, int> _screenSort = new Dictionary<Transform, int>();

        // Default: reparent EVERYTHING (empty exclude list). If a screen breaks (a mod
        // locates it by path, or a direct-child Transform.Find that runs at runtime), add
        // its name to ExcludeNames.
        private static readonly string DefaultExcludes = string.Empty;

        // The shared UI canvas is named "Canvas" in this game. Used as the primary (precise)
        // signal when locating it; a most-top-level-children heuristic is the fallback if the
        // name is ever changed.
        private const string GiantCanvasDefaultName = "Canvas";

        // The shop/game scene is named "Start". The menu/boot scene's name is unreliable
        // (observed as both "" and "Title" across runs), so we identify the shop by its
        // consistent name rather than trying to detect the menu.
        private const string ShopSceneName = "Start";

        private void Awake()
        {
            _enabled = Config.Bind("General", "Enabled", true,
                "Master switch. False = original (single giant canvas) behavior.");

            _verbose = Config.Bind("General", "Verbose", false,
                "Log detailed debug output (per-screen reparenting, canvas census, build progress, " +
                "rescan details). Off = only errors and warnings are logged.");

            _exclude = Config.Bind("General", "ExcludeNames", DefaultExcludes,
                "Comma-separated screen names to leave on the shared canvas (NOT reparented). " +
                "Default is empty (reparent everything). Add a name here if that screen breaks.");

            _delayFrames = Config.Bind("General", "ApplyDelayFrames", 10,
                "Frames to wait after the scene is ready before splitting. Lets other mods finish " +
                "initializing (and cache references) before transforms move.");

            _baseSortOrder = Config.Bind("General", "BaseSortingOrder", 100,
                "Base sortingOrder for new screen canvases. Each moved screen gets " +
                "BaseSortingOrder + its original sibling index, which preserves the original " +
                "layering. Keep it above the shared canvas (0).");

            _rescanKey = Config.Bind("General", "RescanCanvasesKey", KeyCode.None,
                "Key to press to rescan for new runtime canvases (e.g. a mod's overlay/modal) " +
                "and shift them above the reparented screens so they stay on top. No default; " +
                "set a key to enable the rescan.");

            ParseExcludes();
            SceneManager.sceneLoaded += OnSceneLoaded;
            // Hot case: we may Awake after a game scene already loaded (the boot/menu scene's
            // sceneLoaded fires before BepInEx runs our Awake). If we're already in the shop
            // scene, trigger now; otherwise the event below catches the next shop-scene load.
            var active = SceneManager.GetActiveScene();
            if (IsShopScene(active))
                StartCoroutine(RunSplit(active));
            Logger.LogInfo($"[StutterBegone] loaded (Enabled={_enabled.Value}, Verbose={_verbose.Value}, scene-triggered, RescanKey={_rescanKey.Value}, Exclude=[{_exclude.Value}])");
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        // Trigger on the shop scene load. Only the shop scene (named "Start") is split; the
        // menu/boot scene (named "" or "Title") is skipped. The shop screens are eager scene
        // objects present as soon as the scene loads, so this is the consistent trigger - no
        // content-loading wait required. The split is idempotent, so re-loads (new day,
        // loading a save) are safe no-ops.
        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (!_enabled.Value) return;
            if (!IsShopScene(scene)) return;
            StartCoroutine(RunSplit(scene));
        }

        // Only the shop/game scene is split. It is consistently named "Start"; the menu/boot
        // scene is named "" or "Title" (unreliable), so we match the shop positively instead of
        // trying to detect the menu.
        private static bool IsShopScene(Scene scene)
        {
            return scene.name == ShopSceneName;
        }

        private void ParseExcludes()
        {
            _excluded.Clear();
            if (_exclude == null) return;
            foreach (var raw in _exclude.Value.Split(','))
            {
                var s = raw.Trim();
                if (s.Length > 0) _excluded.Add(s);
            }
        }

        // Verbose-only logging. Emits only when the Verbose option is on; errors and
        // genuine warnings are logged unconditionally (those calls are NOT routed through here).
        private void LogInfo(string message)
        {
            if (_verbose.Value) Logger.LogInfo(message);
        }

        // Runs once per shop-scene load (event-triggered; see OnSceneLoaded / the Awake hot case).
        // Waits a short grace so other mods finish initializing, finds the giant canvas, and splits
        // it. The split is idempotent, so re-running on a re-load (or an additive scene load) is a
        // safe no-op on already-split screens.
        private IEnumerator RunSplit(Scene scene)
        {
            for (int i = 0; i < Mathf.Max(0, _delayFrames.Value); i++) yield return null;

            _giant = FindGiantCanvas();
            if (_giant == null)
            {
                Logger.LogWarning($"[StutterBegone] shop scene '{scene.name}' loaded but the giant canvas was not found - skipping.");
                yield break;
            }

            LogInfo($"[StutterBegone] splitting on scene '{scene.name}': canvas '{_giant.gameObject.name}' " +
                    $"(children={_giant.transform.childCount}, active={_giant.gameObject.activeInHierarchy})");

            try
            {
                ParseExcludes();
                LogCensus("BEFORE");
                ApplySplit();
                LogCensus("AFTER");
            }
            catch (Exception e)
            {
                Logger.LogError("[StutterBegone] split failed: " + e);
            }
        }

        /// <summary>
        /// Adds a GraphicRaycaster to a canvas root (so its UI is clickable) and registers it
        /// with the game's RaycasterManager so the game's SetUIRaycastEnabled toggles it
        /// together with the rest. Settings are copied from the shared canvas's raycaster.
        /// </summary>
        private void AddAndRegisterRaycaster(GameObject canvasRoot)
        {
            try
            {
                var rc = canvasRoot.GetComponent<GraphicRaycaster>();
                if (rc == null)
                {
                    rc = canvasRoot.AddComponent<GraphicRaycaster>();
                    var src = _giant != null ? _giant.GetComponent<GraphicRaycaster>() : null;
                    if (src != null)
                    {
                        rc.blockingObjects = src.blockingObjects;
                        rc.ignoreReversedGraphics = src.ignoreReversedGraphics;
                    }
                }
                RegisterWithRaycasterManager(rc);
            }
            catch (Exception e)
            {
                Logger.LogWarning("[StutterBegone] failed to add raycaster to " + canvasRoot.name + ": " + e.Message);
            }
        }

        /// <summary>
        /// Best-effort: add a GraphicRaycaster to RaycasterManager.Instance.m_RaycasterList
        /// (via reflection, no hard game-assembly dependency) so the game manages it.
        /// </summary>
        private void RegisterWithRaycasterManager(GraphicRaycaster rc)
        {
            try
            {
                Type rmType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try { rmType = asm.GetType("RaycasterManager"); } catch { }
                    if (rmType != null) break;
                }
                if (rmType == null) return;
                var instProp = rmType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                if (instProp == null) return;
                object inst = instProp.GetValue(null);
                if (inst == null) return;
                var listField = rmType.GetField("m_RaycasterList", BindingFlags.Public | BindingFlags.Instance);
                if (listField == null) return;
                var list = listField.GetValue(inst) as System.Collections.IList;
                if (list == null) return;
                if (!list.Contains(rc)) list.Add(rc);
            }
            catch (Exception e)
            {
                Logger.LogWarning("[StutterBegone] could not register raycaster with RaycasterManager: " + e.Message);
            }
        }

        // Find the shared canvas robustly. The screens are eager scene objects, so the canvas
        // exists as soon as the shop scene loads. Resolution order:
        //   (1) The canvas we already split, if it still exists - it IS the giant. This matters
        //       after a split: the giant then has few/zero top-level children (they moved onto
        //       per-screen canvases) while each per-screen canvas has one, so a blind re-scan
        //       could pick a wrong canvas on a later scan (e.g. an additive scene load).
        //   (2) Name match "Canvas" among ALL canvases, including INACTIVE ones (phone closed) -
        //       GameObject.Find (active-only) would miss the inactive giant.
        //   (3) Fallback: the canvas hosting the most top-level children (the shared canvas holds
        //       every shop screen as a direct child - stable under mod content changes and
        //       MSPF's panel pooling; a CanvasRenderer count is not).
        private Canvas FindGiantCanvas()
        {
            if (_splitCanvas != null)
                return _splitCanvas;

            Canvas[] all = FindObjectsOfType<Canvas>(true);
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] != null && all[i].gameObject.name == GiantCanvasDefaultName)
                    return all[i];
            }
            Canvas best = null; int bestCC = -1, bestCR = -1;
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] == null) continue;
                int cc = all[i].transform.childCount;
                int cr = all[i].GetComponentsInChildren<CanvasRenderer>(true).Length;
                if (cc > bestCC || (cc == bestCC && cr > bestCR)) { best = all[i]; bestCC = cc; bestCR = cr; }
            }
            return best;
        }

        private void ApplySplit()
        {
            if (_giant == null) return;

            // Detect a recreated canvas (a scene load destroyed the old one) and reset the
            // per-canvas idempotency state. Same canvas (re-split) keeps its state so sorts stay
            // stable and ourMaxSort only ever grows.
            bool freshCanvas = _splitCanvas == null || _giant != _splitCanvas;
            if (freshCanvas)
            {
                _splitCanvas = _giant;
                _nextSortIndex = 0;
                _screenSort.Clear();
                _ourMaxSort = 0;
                _knownCanvases = new HashSet<GameObject>();
            }

            int n = _giant.transform.childCount;
            // Snapshot the children BEFORE reparenting: moving a child out of the shared canvas
            // changes childCount/GetChild, so we iterate a fixed copy.
            var children = new Transform[n];
            for (int i = 0; i < n; i++)
                children[i] = _giant.transform.GetChild(i);
            int baseSort = _baseSortOrder.Value;

            int moved = 0, skippedExcl = 0;

            // Reparent EVERY top-level child (except ExcludeNames) onto its own canvas.
            // Each screen gets a STABLE sortingOrder (base + a monotonically increasing index)
            // so a re-split never collides with an earlier assignment. On a fresh canvas the
            // index equals the sibling order, which preserves the original z-order.
            for (int j = 0; j < n; j++)
            {
                var t = children[j];
                if (t == null) continue;
                if (_excluded.Contains(t.name))
                {
                    LogInfo($"[StutterBegone]   skip (excluded): '{t.name}'");
                    skippedExcl++;
                    continue;
                }
                int cr = t.GetComponentsInChildren<CanvasRenderer>(true).Length;
                int sortOrder;
                if (!_screenSort.TryGetValue(t, out sortOrder))
                {
                    sortOrder = baseSort + _nextSortIndex++;
                    _screenSort[t] = sortOrder;
                }

                if (t.gameObject.activeInHierarchy)
                    LogInfo($"[StutterBegone]   NOTE: '{t.name}' is ACTIVE while being split (may cause a one-frame visual blip).");

                ReparentScreen(t, cr, sortOrder);
                moved++;
            }

            Logger.LogInfo($"[StutterBegone] done: moved={moved} skippedExcluded={skippedExcl} (canvas '{_giant.gameObject.name}', fresh={freshCanvas})");

            // Monotonic baseline for the canvas watcher: first sortingOrder slot above ALL our
            // screens. Only ever grows (never resets downward), so new runtime canvases are
            // always shifted above everything we have placed.
            _ourMaxSort = Mathf.Max(_ourMaxSort, baseSort + _nextSortIndex);
            // (Re)baseline the known canvases (union: add any new ones; a fresh canvas already reset).
            foreach (var c in FindObjectsOfType<Canvas>())
                if (c != null) _knownCanvases.Add(c.gameObject);
            LogInfo($"[StutterBegone] canvas watcher armed (ourMaxSort={_ourMaxSort}, known={_knownCanvases.Count} canvases at split); press '{_rescanKey.Value}' to rescan");
        }

        void Update()
        {
            if (_knownCanvases == null) return;
            if (_rescanKey.Value == KeyCode.None) return;
            if (Input.GetKeyDown(_rescanKey.Value))
                RescanCanvases();
        }

        private void RescanCanvases()
        {
            var sw = Stopwatch.StartNew();
            Canvas[] all = FindObjectsOfType<Canvas>();
            int shifted = 0;
            for (int i = 0; i < all.Length; i++)
            {
                var c = all[i];
                if (c == null) continue;
                if (_knownCanvases.Contains(c.gameObject)) continue;
                if (!IsRootCanvas(c)) continue; // nested canvases stay unmarked
                _knownCanvases.Add(c.gameObject);
                int old = c.sortingOrder;
                int newSort = Math.Clamp(_ourMaxSort + old, -32768, 32767);
                c.sortingOrder = newSort;
                shifted++;
                LogInfo($"[StutterBegone]   shifted new runtime canvas '{c.gameObject.name}' sort {old} -> {newSort}");
            }
            sw.Stop();
            LogInfo($"[StutterBegone] rescan: {all.Length} canvases, {shifted} new shifted, {sw.Elapsed.TotalMilliseconds:F3} ms");
        }

        private static bool IsRootCanvas(Canvas c)
        {
            Transform t = c.transform.parent;
            while (t != null)
            {
                if (t.GetComponent<Canvas>() != null) return false; // has an ancestor canvas -> nested
                t = t.parent;
            }
            return true;
        }

        private void ReparentScreen(Transform screen, int cr, int sortOrder)
        {
            try
            {
                string oldPath = GetPath(screen);
                var go = new GameObject("StutterBegone_" + screen.name);
                go.layer = screen.gameObject.layer;
                var c = go.AddComponent<Canvas>();
                c.renderMode = RenderMode.ScreenSpaceOverlay;
                // sortingOrder is stored internally as Int16; clamp to [-32768, 32767].
                c.sortingOrder = Math.Clamp(sortOrder, -32768, 32767);
                c.overrideSorting = true;
                c.pixelPerfect = false;

                var src = _giant.GetComponent<CanvasScaler>();
                if (src != null)
                {
                    var s = go.AddComponent<CanvasScaler>();
                    s.uiScaleMode = src.uiScaleMode;
                    s.referenceResolution = src.referenceResolution;
                    s.matchWidthOrHeight = src.matchWidthOrHeight;
                    s.dynamicPixelsPerUnit = src.dynamicPixelsPerUnit;
                }

                // CRITICAL: a ScreenSpaceOverlay canvas needs a GraphicRaycaster for its UI to
                // be clickable/hoverable. Without one the screen renders but the EventSystem
                // can't raycast it (the reported "not interactable" bug).
                AddAndRegisterRaycaster(go);

                screen.SetParent(go.transform, worldPositionStays: false);
                int after = c.GetComponentsInChildren<CanvasRenderer>(true).Length;
                LogInfo($"[StutterBegone]   REPARENT '{screen.name}' CR {cr}->{after} sort={sortOrder}  {oldPath} -> {GetPath(screen)}");
            }
            catch (Exception e)
            {
                Logger.LogError($"[StutterBegone] reparent failed for '{screen.name}': {e}");
            }
        }

        private void LogCensus(string phase)
        {
            try
            {
                Canvas[] all = FindObjectsOfType<Canvas>();
                var rows = new List<(string name, int cr, int active)>();
                int total = 0, totalActive = 0;
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i] == null) continue;
                    CanvasRenderer[] crs = all[i].GetComponentsInChildren<CanvasRenderer>(true);
                    int active = 0;
                    for (int j = 0; j < crs.Length; j++) if (crs[j] != null && crs[j].gameObject.activeInHierarchy) active++;
                    total += crs.Length; totalActive += active;
                    rows.Add((all[i].gameObject.name, crs.Length, active));
                }
                rows.Sort((a, b) => b.cr - a.cr);
                LogInfo($"[StutterBegone] CENSUS {phase}: {all.Length} canvases, {total} CanvasRenderers total, {totalActive} active");
                for (int i = 0; i < rows.Count && i < 12; i++)
                    LogInfo($"[StutterBegone]   {phase} {rows[i].name}: {rows[i].cr} CR ({rows[i].active} active)");
            }
            catch (Exception e)
            {
                Logger.LogError($"[StutterBegone] census failed ({phase}): {e}");
            }
        }

        private static string GetPath(Transform t)
        {
            string p = t.name;
            while (t.parent != null) { t = t.parent; p = t.name + "/" + p; }
            return p;
        }
    }
}
