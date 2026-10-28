using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using ILLGames.Unity.Component;
using Manager;
using SV;
using SV.MapSelectScene;
using MapInfo = SV.MapSelectScene.MapInfo;
using UnityEngine;

namespace SVS_MapMenuExpansion;

[BepInPlugin(GUID, "SVS Map Menu Expansion", "1.2.0")]
[BepInDependency("SVS_MapLoader", BepInDependency.DependencyFlags.SoftDependency)]
public class Plugin : BasePlugin
{
    public const string GUID = "SVS_MapMenuExpansion";

    internal static new ManualLogSource Log;
    internal static ConfigEntry<int> FirstCustomMapID;
    internal static ConfigEntry<bool> IncludeOriginalMaps;
    internal static ConfigEntry<bool> DrawLinks;
    internal static ConfigEntry<int> ThumbnailTime;

    public override void Load()
    {
        Log = base.Log;
        FirstCustomMapID = Config.Bind("General", "First Custom Map ID", 100,
            "Maps with an ID at or above this value are treated as mod maps and get a tile in the mod map grid");
        IncludeOriginalMaps = Config.Bind("General", "Include Original Maps", true,
            "Also list the game's own maps in the grid so mod maps link to them");
        DrawLinks = Config.Bind("General", "Draw Map Links", true,
            "Draw lines between maps that the game lists as neighbours");
        ThumbnailTime = Config.Bind("General", "Thumbnail Time Index", 1,
            "Which of the map 2D backgrounds (00-03, one per time of day) is used for its thumbnail");
        Harmony.CreateAndPatchAll(typeof(Hooks), GUID);
    }
}

internal static class Hooks
{
    // ---- layout (pixels of the 1920x1080 canvas; the window title bar adds 32 to y in screenshots)
    const int Columns = 6, VisibleRows = 4;
    const float CellW = 300f, CellH = 240f, TileScale = 0.6f, GridCentreY = 575f;
    const float TileHalfH = 72f, TileW = 245f, LabelH = 46f; // tile size on screen at TileScale

    // ---- link drawing: one texture covering the grid, redrawn when the view scrolls
    const float LinkX0 = 60f, LinkY0 = 100f, LinkW = 1800f, LinkH = 960f, LinkRes = 0.75f;
    const float LineOutlinePx = 10f, LineFillPx = 6f, LineCorner = 16f;
    const int ArcSteps = 8;
    static readonly int TexW = Mathf.RoundToInt(LinkW * LinkRes), TexH = Mathf.RoundToInt(LinkH * LinkRes);

    // ---- scroll bar and drag scrolling
    const float BarX = 1872f, BarHalfW = 5f;
    const float DragStepPx = 100f, DragClickPx = 12f;

    class MenuState
    {
        public readonly List<MapInfo> Tiles = new();
        public readonly List<int> Ids = new();                 // real map ID per tile (the tile's own _id is left alone)
        public readonly List<(int a, int b)> Edges = new();    // tile indexes
        public readonly List<(float col, int row)> Cells = new(); // grid cell per tile (col is fractional in centred rows)
        public readonly HashSet<int> StockIds = new();          // map IDs of the game's own tiles (layer 0)
        public readonly List<List<Vector2>> Routes = new();     // polyline per edge in layout space (x px, y = row * CellH + offset)
        public readonly List<GameObject> Labels = new();       // overlay name strip per tile
        public int RowCount;
        public readonly List<(MapInfo button, bool blue)> Buttons = new();

        public GameObject Backdrop, LinkImage, BarTrack, BarThumb;
        public UnityEngine.UI.Scrollbar RealBar; // clone of the option menu scroll bar, when available
        public bool BarSync;                     // true while we move the bar ourselves (ignore its change event)
        public Texture2D LinkTex;
        public Il2CppStructArray<Color32> LinkPixels;
        public float[] OutlineA, FillA;   // coverage of the bright (front) links
        public float[] OutlineD, FillD;   // coverage of the dimmed (back) links
        public readonly List<bool> EdgeIsTree = new();    // per edge: parent/child link of the spanning tree
        public int Hover = -1;                            // tile index under the mouse (-1 none)
        public MapInfo Toggle, Original;

        public bool Open, ScreenWasOpen, BarDragging, Dragging, DragMoved;
        public float LastOpened = -10f;
        public int Row;
        public float DragStartY, DragPressY, DragAccum;
        public MapSelect Ui;
    }

    static readonly Dictionary<int, MenuState> States = new();

    // ------------------------------------------------------------------ coordinate mapping
    // pixel -> local-space mapping, calibrated from the stock tiles' positions in the 1920x1080 map screen
    static float _minX, _maxY, _ux, _vy, _z;
    static Vector3 _baseScale;

    static void Calibrate(Il2CppReferenceArray<MapInfo> tiles)
    {
        float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
        foreach (var t in tiles)
        {
            // stock tiles are top-left pivoted, so their visual centre is pos + rect.center * scale
            var rc = t.GetComponent<RectTransform>().rect.center;
            var sc = t.transform.localScale;
            var p = t.transform.localPosition + new Vector3(rc.x * sc.x, rc.y * sc.y, 0f);
            minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x);
            minY = Mathf.Min(minY, p.y); maxY = Mathf.Max(maxY, p.y);
        }
        // stock layout (screenshot px): leftmost tile centre x=480, rightmost x=1600, top row y=222, bottom row y=962
        _ux = (maxX - minX) / 1120f;
        _vy = (maxY - minY) / 740f;
        _minX = minX;
        _maxY = maxY;
        _z = tiles[0].transform.localPosition.z;
        _baseScale = tiles[0].transform.localScale;
    }

    static Vector3 PxToLocal(Vector2 px) => new Vector3(_minX + (px.x - 480f) * _ux, _maxY - (px.y - 222f) * _vy, _z);

    // ------------------------------------------------------------------ build
    [HarmonyPostfix]
    [HarmonyPatch(typeof(MapSelect), nameof(MapSelect.Initialize))]
    static void AfterInitialize(MapSelect __instance)
    {
        try { Build(__instance); }
        catch (System.Exception e) { Plugin.Log.LogError($"Map select build failed: {e}"); }
    }

    static void Build(MapSelect ui)
    {
        var originals = ui._mapInfos;
        var maps = SingletonInitializerAsync<MapManager>.Instance?.MapListTable;
        if (originals == null || originals.Length == 0 || maps == null) return;

        int key = ui.GetInstanceID();
        if (States.ContainsKey(key)) return;
        var st = new MenuState { Ui = ui };
        States[key] = st;

        var template = originals[0];
        var parent = template.transform.parent;
        var panel = parent.parent != null && parent.parent.parent != null ? parent.parent.parent : parent;
        Calibrate(originals);

        // opaque panel that covers the stock map (tiles, frames, routes) while the grid is open
        var coverSrc = template.transform.Find("Cover");
        if (coverSrc != null)
        {
            st.Backdrop = Object.Instantiate(coverSrc.gameObject, parent);
            st.Backdrop.name = "CustomMapBackdrop";
            var brt = st.Backdrop.GetComponent<RectTransform>();
            brt.anchorMin = brt.anchorMax = new Vector2(0.5f, 0.5f);
            brt.pivot = new Vector2(0.5f, 0.5f);
            brt.sizeDelta = new Vector2(4400f, 2800f);
            brt.localPosition = Vector3.zero;
            var img = st.Backdrop.GetComponent<UnityEngine.UI.Image>();
            img.color = new Color(0.08f, 0.09f, 0.13f, 1f);
            img.raycastTarget = true;
            st.Backdrop.SetActive(false);
        }

        // entries: stock maps first (cloned from their own tile so the thumbnail matches), then mod maps
        var entries = new List<(int id, MapInfo source)>();
        var stockIds = new HashSet<int>();
        foreach (var m in originals) stockIds.Add(m._id);
        if (Plugin.IncludeOriginalMaps.Value) foreach (var id in stockIds) st.StockIds.Add(id);
        if (Plugin.IncludeOriginalMaps.Value)
            foreach (var m in originals.OrderBy(m => m._id)) entries.Add((m._id, m));
        var ids = new List<int>();
        foreach (var kv in maps) ids.Add(kv.Key);
        foreach (var id in ids.Where(i => i >= Plugin.FirstCustomMapID.Value && !stockIds.Contains(i)).OrderBy(i => i))
            entries.Add((id, template));

        foreach (var (id, source) in entries)
        {
            if (!maps.ContainsKey(id) || string.IsNullOrEmpty(maps[id].Name)) continue;
            if (maps[id].Kind != 0) continue; // Kind 1 = private spaces (Your Room, toilets, bushes, tent...): not travel destinations
            var tile = Clone(source, parent, $"CustomMap_{id}", maps[id].Name, 46f, false);
            PrepareTile(tile);
            int mapId = id;
            tile._button.onClick.AddListener(new System.Action(() => Select(st, ui, mapId)));
            tile.gameObject.SetActive(false);
            st.Tiles.Add(tile);
            st.Ids.Add(id);
        }

        BuildEdges(st, maps);
        ComputeLayout(st);
        if (!Plugin.DrawLinks.Value) st.Edges.Clear();
        ComputeRoutes(st);
        BuildLinkImage(st, template, parent);

        // names are separate overlay objects (the game's tile scripts hide anything inside the tile)
        var shown = DisplayNames(st, maps);
        for (int i = 0; i < st.Tiles.Count; i++) st.Labels.Add(MakeLabel(parent, shown[i], template));
        BuildScrollBar(st, template, parent);
        InitThumbnails(st, maps);

        // buttons live beside the Map title / back button, not in the animated tile group
        st.Toggle = MakeButton(template, panel, "CustomMap_Toggle", "Mod Maps", 960f, 100f, new Color(0.10f, 0.55f, 0.60f, 0.95f));
        st.Toggle._button.onClick.AddListener(new System.Action(() => SetOpen(st, !st.Open)));
        st.Buttons.Add((st.Toggle, false));

        // same spot as the toggle: shown instead of it while the grid is open
        st.Original = MakeButton(template, panel, "CustomMap_Original", "Original Map", 960f, 100f, new Color(0.55f, 0.30f, 0.15f, 0.95f));
        st.Original._button.onClick.AddListener(new System.Action(() => SetOpen(st, false)));
        st.Buttons.Add((st.Original, true));

        SetOpen(st, false);
        Plugin.Log.LogInfo($"Map select: built {st.Tiles.Count} tiles, {st.Edges.Count} links, {st.RowCount} rows");
    }

    // maps that share a name (e.g. two "Your Room") get the name of a linked map added so they can be told apart
    static string[] DisplayNames(MenuState st, Il2CppSystem.Collections.Generic.Dictionary<int, MapInfoParam> maps)
    {
        int n = st.Ids.Count;
        var names = new string[n];
        for (int i = 0; i < n; i++) names[i] = maps[st.Ids[i]].Name;
        var counts = new Dictionary<string, int>();
        foreach (var name in names) counts[name] = counts.TryGetValue(name, out int c) ? c + 1 : 1;

        var result = (string[])names.Clone();
        for (int i = 0; i < n; i++)
        {
            if (counts[names[i]] < 2) continue;
            // linked map with a unique name, lowest id first
            var neighbours = new List<int>();
            foreach (var (a, b) in st.Edges)
            {
                if (a == i) neighbours.Add(b);
                else if (b == i) neighbours.Add(a);
            }
            int pick = neighbours.Where(j => counts[names[j]] < 2).OrderBy(j => st.Ids[j]).DefaultIfEmpty(-1).First();
            if (pick >= 0) result[i] = $"{names[i]} ({names[pick]})";
        }
        return result;
    }

    // the game's tile scripts re-enable hidden children, so shrink them away as well
    static void Hide(Transform t)
    {
        if (t == null) return;
        t.localScale = Vector3.zero;
        t.gameObject.SetActive(false);
    }

    // ------------------------------------------------------------------ open detection / per-frame
    [HarmonyPostfix]
    [HarmonyPatch(typeof(MapSelect), nameof(MapSelect.OpenExe))]
    static void AfterOpen(MapSelect __instance)
    {
        if (States.TryGetValue(__instance.GetInstanceID(), out var st)) { OnScreenOpened(st); st.ScreenWasOpen = true; }
    }

    static void OnScreenOpened(MenuState st)
    {
        // the open hook and the per-frame detector both fire for one opening; handle it once
        if (Time.unscaledTime - st.LastOpened < 0.5f) return;
        st.LastOpened = Time.unscaledTime;

        ApplyStyle(st);
        int idx = CurrentTileIndex(st);
        // drop any state left from the previous visit (drag, scroll bar, old scroll position)
        st.Row = 0;
        st.Dragging = st.DragMoved = st.BarDragging = false;
        // start on the grid only when the player is in a mod map; stock maps and maps without a tile open the stock screen
        SetOpen(st, idx >= 0 && st.Ids[idx] >= Plugin.FirstCustomMapID.Value);
    }

    // the game re-applies tile icon state shortly after a tile is enabled, so keep our icon choice in force
    [HarmonyPostfix]
    [HarmonyPatch(typeof(SimulationScene), "Update")]
    static void EveryFrame()
    {
        try { PumpThumbnails(); }
        catch (System.Exception e) { Plugin.Log.LogError($"thumbnail step failed: {e}"); _thumbQueue.Clear(); }

        foreach (var st in States.Values)
        {
            // detect the map screen opening (the open hook alone is not reliable)
            bool screenOpen;
            try { screenOpen = st.Ui != null && st.Ui.IsOpen(); }
            catch (System.Exception) { screenOpen = false; }
            if (screenOpen && !st.ScreenWasOpen) OnScreenOpened(st);
            st.ScreenWasOpen = screenOpen;
            if (!st.Open) continue;

            try
            {
                float wheel = Input.mouseScrollDelta.y;
                if (wheel != 0f) Scroll(st, wheel > 0f ? -1 : 1);
                if (!UpdateScrollBar(st)) UpdateDrag(st);
                int hv = HoverIndex(st);
                if (hv != st.Hover) { st.Hover = hv; RenderLinks(st); }
            }
            catch (System.Exception) { /* legacy input unavailable */ }

            int here = SingletonInitializerAsync<MapManager>.Instance?.MapID ?? -1;
            for (int i = 0; i < st.Tiles.Count; i++)
            {
                var tile = st.Tiles[i];
                if (!tile.gameObject.activeSelf) continue;
                var pIcon = tile.transform.Find("pIcon");
                bool isHere = st.Ids[i] == here;
                if (pIcon != null && pIcon.gameObject.activeSelf != isHere) pIcon.gameObject.SetActive(isHere);
            }
        }
    }

    // ------------------------------------------------------------------ tiles and buttons
    static MapInfo Clone(MapInfo source, Transform parent, string name, string title, float fontSize, bool labelled = true)
    {
        var go = Object.Instantiate(source.gameObject, parent);
        go.name = name;
        var info = go.GetComponent<MapInfo>();
        info.SetTitle(title);
        var t = info._title;
        var src = t.GetTmpText();
        // the game keeps re-enabling its own title pill, so shrink it away instead of just hiding it
        t.transform.localScale = Vector3.zero;
        t.gameObject.SetActive(false);

        if (src != null && _stockFont == null) { _stockFont = src.font; _stockFontMaterial = src.fontSharedMaterial; }
        if (!labelled) return info;

        var tile = go.transform;
        var cover = tile.Find("Cover");
        if (cover != null)
        {
            var strip = Object.Instantiate(cover.gameObject, tile);
            strip.name = "CustomLabelBG";
            strip.SetActive(true);
            Bottom(strip.GetComponent<RectTransform>(), 0f);
        }
        if (src != null)
        {
            var label = Object.Instantiate(src.gameObject, tile);
            label.name = "CustomLabel";
            foreach (var c in label.GetComponents<Component>())
            {
                var n = c.GetIl2CppType().Name;
                if (n == "UIBindData" || n == "ContentSizeFitter" || n == "LayoutElement") Object.Destroy(c);
            }
            label.SetActive(true);
            Bottom(label.GetComponent<RectTransform>(), 2f);
            var tmp = label.GetComponent<TMPro.TMP_Text>();
            tmp.text = title;
            tmp.fontSize = fontSize;
            tmp.color = Color.white;
            tmp.alignment = TMPro.TextAlignmentOptions.Center;
            tmp.enableWordWrapping = true;
            label.transform.SetAsLastSibling();
        }
        return info;
    }

    static TMPro.TMP_FontAsset _stockFont;
    static Material _stockFontMaterial;

    // strip the game's per-tile state scripts and overlays: they dim the tile and hide our children
    static void PrepareTile(MapInfo tile)
    {
        foreach (var c in tile.GetComponents<Component>())
        {
            var n = c.GetIl2CppType().Name;
            if (n == "UI_IconStateObjectEvent" || n == "PopAnimationUI") Object.Destroy(c);
        }
        foreach (var child in new[] { "Highlight", "Cover", "TitleBG" })
        {
            var t = tile.transform.Find(child);
            if (t != null) Object.Destroy(t.gameObject);
        }
    }

    static GameObject NewUi(string name, Transform parent)
    {
        var types = new Il2CppReferenceArray<Il2CppSystem.Type>(1);
        types[0] = Il2CppInterop.Runtime.Il2CppType.Of<RectTransform>();
        var go = new GameObject(name, types);
        go.transform.SetParent(parent, false);
        return go;
    }

    // dark strip + text, built from scratch so nothing in the stock tile can switch it off
    static GameObject MakeLabel(Transform parent, string text, MapInfo template)
    {
        var bg = NewUi("CustomTileLabel", parent);
        var bgRt = bg.GetComponent<RectTransform>();
        bgRt.anchorMin = bgRt.anchorMax = new Vector2(0.5f, 0.5f);
        bgRt.pivot = new Vector2(0.5f, 0.5f);
        var img = bg.AddComponent<UnityEngine.UI.Image>();
        img.color = new Color(0f, 0f, 0f, 0.65f);
        img.raycastTarget = false;

        var tgo = NewUi("Text", bg.transform);
        var trt = tgo.GetComponent<RectTransform>();
        trt.anchorMin = Vector2.zero;
        trt.anchorMax = Vector2.one;
        trt.offsetMin = new Vector2(4f, 1f);
        trt.offsetMax = new Vector2(-4f, -1f);
        var tmp = tgo.AddComponent<TMPro.TextMeshProUGUI>();
        var font = _font != null ? _font : _stockFont;
        if (font != null) tmp.font = font;
        var mat = _font != null ? _fontMaterial : _stockFontMaterial;
        if (mat != null) tmp.fontSharedMaterial = mat;
        tmp.text = text;
        tmp.color = Color.white;
        tmp.alignment = TMPro.TextAlignmentOptions.Center;
        tmp.enableWordWrapping = true;
        tmp.enableAutoSizing = true;
        tmp.fontSizeMin = 7f;
        tmp.fontSizeMax = 14f;
        tmp.raycastTarget = false;
        bg.SetActive(false);
        return bg;
    }

    // plain labelled button built from a tile clone, drawn with the option-menu sprite once it is found
    static MapInfo MakeButton(MapInfo template, Transform parent, string name, string text, float px, float py, Color color)
    {
        var info = Clone(template, parent, name, text, 24f);
        var tr = info.transform;
        foreach (var child in new[] { "Thumbnail", "pIcon", "Cover", "Highlight" }) Hide(tr.Find(child));
        var rt = info.GetComponent<RectTransform>();
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(284f, 64f);
        // the tile's own Image is disabled by the game, so paint the button with the label strip instead
        var bg = tr.Find("CustomLabelBG");
        var bgRt = bg.GetComponent<RectTransform>();
        bgRt.anchorMin = Vector2.zero;
        bgRt.anchorMax = Vector2.one;
        bgRt.offsetMin = bgRt.offsetMax = Vector2.zero;
        bg.GetComponent<UnityEngine.UI.Image>().color = color;
        bg.SetAsFirstSibling();
        var label = tr.Find("CustomLabel").GetComponent<RectTransform>();
        label.anchorMin = label.anchorMax = new Vector2(0.5f, 0.5f);
        label.pivot = new Vector2(0.5f, 0.5f);
        label.sizeDelta = new Vector2(270f, 60f);
        label.anchoredPosition = Vector2.zero;
        // fixed screen position: panel is centred on the 1920x1080 canvas; screenshot px include a 32px title bar
        rt.localScale = Vector3.one;
        rt.localPosition = new Vector3(px - 960f, 572f - py, 0f);
        return info;
    }

    // strip along the bottom edge of the tile, 64 high, inset from the sides
    static void Bottom(RectTransform rt, float lift)
    {
        rt.anchorMin = new Vector2(0f, 0f);
        rt.anchorMax = new Vector2(1f, 0f);
        rt.pivot = new Vector2(0.5f, 0f);
        rt.offsetMin = new Vector2(10f, 10f + lift);
        rt.offsetMax = new Vector2(-10f, 74f + lift);
    }

    static void Place(MapInfo tile, float px, float py, float scale)
    {
        var rt = tile.GetComponent<RectTransform>();
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.localScale = _baseScale * scale;
        rt.localPosition = PxToLocal(new Vector2(px, py));
    }

    // ------------------------------------------------------------------ choosing a map
    static void Select(MenuState st, MapSelect ui, int id)
    {
        if (st.DragMoved) return; // that was a drag, not a click
        try { ui.Method_Private_UniTask_Nullable_1_Int32_0(new Il2CppSystem.Nullable<int>(id)); }
        catch (System.Exception e) { Plugin.Log.LogError($"Map select: choose failed: {e}"); }
    }

    // ------------------------------------------------------------------ view state
    static int MaxRow(MenuState st) => Mathf.Max(0, st.RowCount - VisibleRows);

    static void Scroll(MenuState st, int rows)
    {
        st.Row = Mathf.Clamp(st.Row + rows, 0, MaxRow(st));
        Refresh(st);
    }

    static void SetOpen(MenuState st, bool open)
    {
        st.Open = open;
        st.Hover = -1;
        if (open)
        {
            int idx = CurrentTileIndex(st);
            if (idx >= 0) st.Row = Mathf.Clamp(st.Cells[idx].row - (VisibleRows - 1) / 2, 0, MaxRow(st)); // bring the current map into view, roughly centred
        }
        Refresh(st);
    }

    // tile under the mouse among the visible ones (-1 none)
    static int HoverIndex(MenuState st)
    {
        var mp = MousePx();
        for (int i = 0; i < st.Tiles.Count; i++)
        {
            if (!IsVisible(st, i)) continue;
            var c = CellCentre(st, i);
            if (Mathf.Abs(mp.x - c.x) <= TileW / 2f && Mathf.Abs(mp.y - c.y) <= TileHalfH) return i;
        }
        return -1;
    }

    static int CurrentTileIndex(MenuState st)
    {
        int here = SingletonInitializerAsync<MapManager>.Instance?.MapID ?? -1;
        return st.Ids.IndexOf(here);
    }

    static float TileX(float col) => 960f + (col - (Columns - 1) / 2f) * CellW;
    static float BaseY => GridCentreY - (VisibleRows - 1) / 2f * CellH; // screen y of the first visible row when Row == 0

    static Vector2 CellCentre(MenuState st, int index)
    {
        var (col, row) = st.Cells[index];
        return new Vector2(TileX(col), BaseY + (row - st.Row) * CellH);
    }

    static bool IsVisible(MenuState st, int index)
    {
        int row = st.Cells[index].row - st.Row;
        return row >= 0 && row < VisibleRows;
    }

    static void Refresh(MenuState st)
    {
        if (st.Backdrop != null) st.Backdrop.SetActive(st.Open);
        int here = SingletonInitializerAsync<MapManager>.Instance?.MapID ?? -1;
        for (int i = 0; i < st.Tiles.Count; i++)
        {
            var tile = st.Tiles[i];
            bool show = st.Open && IsVisible(st, i);
            var label = st.Labels[i];
            if (show)
            {
                var c = CellCentre(st, i);
                Place(tile, c.x, c.y, TileScale);
                var lrt = label.GetComponent<RectTransform>();
                lrt.localScale = Vector3.one;
                lrt.localPosition = PxToLocal(new Vector2(c.x, c.y + TileHalfH - LabelH / 2f));
                lrt.sizeDelta = new Vector2(TileW * _ux, LabelH * _vy);
            }
            tile.gameObject.SetActive(show);
            label.SetActive(show);
            // after SetActive: enabling a tile makes the game reset its icon state
            var pIcon = tile.transform.Find("pIcon");
            if (pIcon != null) pIcon.gameObject.SetActive(st.Ids[i] == here);
        }

        RenderLinks(st);
        RefreshScrollBar(st);
        st.Toggle.gameObject.SetActive(!st.Open);
        st.Original.gameObject.SetActive(st.Open);
    }

    // ------------------------------------------------------------------ links (rounded routes, drawn into one texture)
    static void BuildEdges(MenuState st, Il2CppSystem.Collections.Generic.Dictionary<int, MapInfoParam> maps)
    {
        var index = new Dictionary<int, int>();
        for (int i = 0; i < st.Ids.Count; i++) index[st.Ids[i]] = i;

        // NearFloors is treated as UNDIRECTED: A listing B, or B listing A, gives one link (min/max key dedupes both)
        var seen = new HashSet<(int, int)>();
        var degree = new int[st.Ids.Count];
        void AddEdge(int i, int j)
        {
            var edge = (Mathf.Min(i, j), Mathf.Max(i, j));
            if (!seen.Add(edge)) return;
            st.Edges.Add(edge);
            degree[i]++; degree[j]++;
        }

        for (int i = 0; i < st.Ids.Count; i++)
        {
            var param = maps[st.Ids[i]];
            if (param.NearFloors == null) continue;
            foreach (var other in param.NearFloors)
            {
                if (other == st.Ids[i]) continue; // self link (e.g. 105 near [105])
                if (index.TryGetValue(other, out int j)) AddEdge(i, j);
            }
        }

        // a map NearFloors leaves isolated may still be reachable through TakingFloors: use the first listed target
        for (int i = 0; i < st.Ids.Count; i++)
        {
            if (degree[i] > 0) continue;
            var taking = maps[st.Ids[i]].TakingFloors;
            if (taking == null) continue;
            foreach (var other in taking)
                if (other != st.Ids[i] && index.TryGetValue(other, out int j))
                {
                    AddEdge(i, j);
                    break;
                }
        }
    }

    // ------------------------------------------------------------------ compact grid layout
    // Every linked map gets a cell of a Columns x Rows grid. A simulated-annealing search moves and swaps tiles so that
    // linked maps sit in neighbouring cells (a link between side-by-side or stacked tiles costs nothing), links do not
    // run through other tiles and as few links as possible cross. Empty rows are dropped afterwards and the used columns
    // are centred. The result only depends on the link graph, so it is cached in layout.txt next to the thumbnails.
    sealed class GridSearch
    {
        const float WCross = 6f, WPass = 8f;
        static readonly int[] DC = { 1, -1, 0, 0, 1, -1, 1, -1 };
        static readonly int[] DR = { 0, 0, 1, -1, 1, 1, -1, -1 };

        public readonly int N, M, Cols, Rows;
        readonly int[] A, B;
        readonly float[] Wt;                    // how much each link wants to be short
        readonly int[][] Inc;
        public readonly int[] Col, Row, At;     // At[row * Cols + col] = tile or -1
        readonly int[] _mark, _aff = new int[96];
        int _stamp;

        public GridSearch(int n, int[] a, int[] b, float[] weights, int cols, int rows)
        {
            N = n; M = a.Length; Cols = cols; Rows = rows; A = a; B = b; Wt = weights;
            Col = new int[n]; Row = new int[n]; At = new int[cols * rows];
            _mark = new int[M];
            var inc = new List<int>[n];
            for (int i = 0; i < n; i++) inc[i] = new List<int>();
            for (int e = 0; e < M; e++) { inc[a[e]].Add(e); inc[b[e]].Add(e); }
            Inc = inc.Select(l => l.ToArray()).ToArray();
        }

        static int Orient(int px, int py, int qx, int qy, int rx, int ry) => (qx - px) * (ry - py) - (qy - py) * (rx - px);

        bool Cross(int ei, int ej)
        {
            int a = A[ei], b = B[ei], c = A[ej], d = B[ej];
            if (a == c || a == d || b == c || b == d) return false;
            int ax = Col[a], ay = Row[a], bx = Col[b], by = Row[b], cx = Col[c], cy = Row[c], dx = Col[d], dy = Row[d];
            if (System.Math.Max(ax, bx) < System.Math.Min(cx, dx) || System.Math.Max(cx, dx) < System.Math.Min(ax, bx)
                || System.Math.Max(ay, by) < System.Math.Min(cy, dy) || System.Math.Max(cy, dy) < System.Math.Min(ay, by)) return false;
            long o1 = Orient(cx, cy, dx, dy, ax, ay), o2 = Orient(cx, cy, dx, dy, bx, by);
            long o3 = Orient(ax, ay, bx, by, cx, cy), o4 = Orient(ax, ay, bx, by, dx, dy);
            return o1 * o2 < 0 && o3 * o4 < 0;
        }

        // tiles standing between the ends of an edge that runs along a row or a column
        int Pass(int e)
        {
            int a = A[e], b = B[e], ca = Col[a], ra = Row[a], cb = Col[b], rb = Row[b], count = 0;
            if (ca == cb) { int lo = System.Math.Min(ra, rb), hi = System.Math.Max(ra, rb); for (int r = lo + 1; r < hi; r++) if (At[r * Cols + ca] >= 0) count++; }
            else if (ra == rb) { int lo = System.Math.Min(ca, cb), hi = System.Math.Max(ca, cb); for (int c = lo + 1; c < hi; c++) if (At[ra * Cols + c] >= 0) count++; }
            return count;
        }

        float Local(int cnt)
        {
            float e = 0f;
            for (int k = 0; k < cnt; k++)
            {
                int ei = _aff[k], a = A[ei], b = B[ei];
                int dc = System.Math.Abs(Col[a] - Col[b]), dr = System.Math.Abs(Row[a] - Row[b]);
                if (dc + dr != 1) e += Wt[ei] * (dc + 1.6f * dr - 1f);
                e += WPass * Pass(ei);
            }
            for (int k = 0; k < cnt; k++)
            {
                int ei = _aff[k];
                for (int ej = 0; ej < M; ej++)
                {
                    if (ej == ei || (_mark[ej] == _stamp && ej < ei)) continue;
                    if (Cross(ei, ej)) e += WCross;
                }
            }
            return e;
        }

        public float Total()
        {
            float e = 0f;
            for (int ei = 0; ei < M; ei++)
            {
                int a = A[ei], b = B[ei];
                int dc = System.Math.Abs(Col[a] - Col[b]), dr = System.Math.Abs(Row[a] - Row[b]);
                if (dc + dr != 1) e += Wt[ei] * (dc + 1.6f * dr - 1f);
                e += WPass * Pass(ei);
                for (int ej = ei + 1; ej < M; ej++) if (Cross(ei, ej)) e += WCross;
            }
            return e;
        }

        public float Run(int seed, int iterations)
        {
            var rng = new System.Random(seed);
            int cells = Cols * Rows;
            var perm = new int[cells];
            for (int i = 0; i < cells; i++) perm[i] = i;
            for (int i = cells - 1; i > 0; i--) { int j = rng.Next(i + 1); (perm[i], perm[j]) = (perm[j], perm[i]); }
            for (int i = 0; i < At.Length; i++) At[i] = -1;
            for (int i = 0; i < N; i++) { Col[i] = perm[i] % Cols; Row[i] = perm[i] / Cols; At[perm[i]] = i; }

            const float t0 = 2.5f, t1 = 0.03f;
            for (int it = 0; it < iterations; it++)
            {
                float T = t0 * Mathf.Pow(t1 / t0, it / (float)iterations);
                int i = rng.Next(N), tc, tr;
                if (rng.NextDouble() < 0.6 && Inc[i].Length > 0)
                {
                    int ei = Inc[i][rng.Next(Inc[i].Length)];
                    int nb = A[ei] == i ? B[ei] : A[ei], d = rng.Next(8);
                    tc = Col[nb] + DC[d]; tr = Row[nb] + DR[d];
                }
                else { tc = rng.Next(Cols); tr = rng.Next(Rows); }
                if (tc < 0 || tc >= Cols || tr < 0 || tr >= Rows || (tc == Col[i] && tr == Row[i])) continue;
                int j = At[tr * Cols + tc];

                _stamp++;
                int cnt = 0;
                foreach (int e in Inc[i]) if (_mark[e] != _stamp) { _mark[e] = _stamp; _aff[cnt++] = e; }
                if (j >= 0) foreach (int e in Inc[j]) if (_mark[e] != _stamp) { _mark[e] = _stamp; _aff[cnt++] = e; }

                float before = Local(cnt);
                int oc = Col[i], orow = Row[i];
                if (j < 0) { At[oc + orow * Cols] = -1; At[tc + tr * Cols] = i; Col[i] = tc; Row[i] = tr; }
                else { Col[j] = oc; Row[j] = orow; Col[i] = tc; Row[i] = tr; At[tc + tr * Cols] = i; At[oc + orow * Cols] = j; }
                float delta = Local(cnt) - before;
                if (delta <= 0f || rng.NextDouble() < System.Math.Exp(-delta / T)) continue;

                // undo
                if (j < 0) { At[tc + tr * Cols] = -1; At[oc + orow * Cols] = i; Col[i] = oc; Row[i] = orow; }
                else { Col[i] = oc; Row[i] = orow; Col[j] = tc; Row[j] = tr; At[oc + orow * Cols] = i; At[tc + tr * Cols] = j; }
            }
            return Total();
        }
    }

    static string LayoutCachePath => System.IO.Path.Combine(System.IO.Directory.GetParent(ThumbDir).FullName, "layout.txt");

    static string LayoutKey(MenuState st)
    {
        return $"v6|{Columns}|{string.Join(",", st.Ids)}|{string.Join(",", st.Edges.Select(e => st.Ids[e.a] + "-" + st.Ids[e.b]))}";
    }

    static bool TryLoadLayout(MenuState st, string key, (int col, int row)[] cellOf, List<int> linked)
    {
        try
        {
            if (!System.IO.File.Exists(LayoutCachePath)) return false;
            var lines = System.IO.File.ReadAllLines(LayoutCachePath);
            if (lines.Length < 2 || lines[0] != key) return false;
            var byId = new Dictionary<int, int>();
            foreach (int i in linked) byId[st.Ids[i]] = i;
            int got = 0;
            for (int k = 1; k < lines.Length; k++)
            {
                var p = lines[k].Split(' ');
                if (p.Length != 3 || !int.TryParse(p[0], out int id) || !int.TryParse(p[1], out int c) || !int.TryParse(p[2], out int r)) return false;
                if (!byId.TryGetValue(id, out int tile)) return false;
                cellOf[tile] = (c, r); got++;
            }
            return got == linked.Count;
        }
        catch (System.Exception) { return false; }
    }

    static void SaveLayout(MenuState st, string key, (int col, int row)[] cellOf, List<int> linked)
    {
        try
        {
            var lines = new List<string> { key };
            foreach (int i in linked) lines.Add($"{st.Ids[i]} {cellOf[i].col} {cellOf[i].row}");
            System.IO.File.WriteAllLines(LayoutCachePath, lines);
        }
        catch (System.Exception e) { Plugin.Log.LogWarning($"layout cache not saved: {e.Message}"); }
    }

    static void ComputeLayout(MenuState st)
    {
        int n = st.Ids.Count;
        if (n == 0) return;
        var adj = new List<int>[n];
        for (int i = 0; i < n; i++) adj[i] = new List<int>();
        foreach (var (a, b) in st.Edges) { adj[a].Add(b); adj[b].Add(a); }
        foreach (var l in adj) l.Sort();

        var linked = Enumerable.Range(0, n).Where(i => adj[i].Count > 0).ToList();
        var singles = Enumerable.Range(0, n).Where(i => adj[i].Count == 0).OrderBy(i => st.Ids[i]).ToList();
        var cellOf = new (int col, int row)[n];

        string key = LayoutKey(st);
        bool cached = TryLoadLayout(st, key, cellOf, linked);
        if (!cached && linked.Count > 0)
        {
            var map = new Dictionary<int, int>();
            for (int k = 0; k < linked.Count; k++) map[linked[k]] = k;
            var ea = st.Edges.Select(e => map[e.a]).ToArray();
            var eb = st.Edges.Select(e => map[e.b]).ToArray();
            // links that touch one of the game's own maps matter most: keep those pairs side by side
            var ew = st.Edges.Select(e => (st.StockIds.Contains(st.Ids[e.a]) || st.StockIds.Contains(st.Ids[e.b])) ? 2.5f : 1f).ToArray();
            int rows = System.Math.Max(6, Mathf.CeilToInt(linked.Count / (float)Columns) + 1);
            GridSearch best = null; float bestE = float.MaxValue;
            for (int restart = 0; restart < 6; restart++)
            {
                var gs = new GridSearch(linked.Count, ea, eb, ew, Columns, rows);
                float energy = gs.Run(1 + restart, 120000);
                if (energy < bestE) { bestE = energy; best = gs; }
            }
            for (int k = 0; k < linked.Count; k++) cellOf[linked[k]] = (best.Col[k], best.Row[k]);
            SaveLayout(st, key, cellOf, linked);
        }

        // drop empty rows, centre the used columns
        var cells = new (float col, int row)[n];
        int rowCount = 0;
        if (linked.Count > 0)
        {
            var usedRows = linked.Select(i => cellOf[i].row).Distinct().OrderBy(r => r).ToList();
            int minC = linked.Min(i => cellOf[i].col), maxC = linked.Max(i => cellOf[i].col);
            float shift = (Columns - 1 - (maxC - minC)) / 2f - minC;
            foreach (int i in linked) cells[i] = (cellOf[i].col + shift, usedRows.IndexOf(cellOf[i].row));
            rowCount = usedRows.Count;
        }
        for (int k = 0; k < singles.Count; k++)
        {
            int inRow = System.Math.Min(Columns, singles.Count - (k / Columns) * Columns);
            cells[singles[k]] = ((Columns - inRow) / 2f + k % Columns, rowCount + k / Columns);
        }
        if (singles.Count > 0) rowCount += (singles.Count + Columns - 1) / Columns;

        // spanning tree (link styling only): rooted at the middle of each group, parents one step closer to the root
        var depth = new int[n]; var parent = new int[n]; var childCount = new int[n]; var comp = new int[n];
        for (int i = 0; i < n; i++) { depth[i] = -1; parent[i] = -1; comp[i] = -1; }
        int groups = 0;
        int[] Bfs(int src)
        {
            var d = new int[n]; for (int i = 0; i < n; i++) d[i] = -1;
            var q = new Queue<int>(); d[src] = 0; q.Enqueue(src);
            while (q.Count > 0) { int u = q.Dequeue(); foreach (int v in adj[u]) if (d[v] < 0) { d[v] = d[u] + 1; q.Enqueue(v); } }
            return d;
        }
        var ecc = new int[n]; var reach = new List<int>[n];
        foreach (int i in linked) { var d = Bfs(i); ecc[i] = d.Max(); reach[i] = Enumerable.Range(0, n).Where(x => d[x] >= 0).ToList(); }
        foreach (int i in linked)
        {
            if (comp[i] >= 0) continue;
            foreach (int m in reach[i]) comp[m] = groups;
            int root = reach[i].OrderBy(m => ecc[m]).ThenByDescending(m => adj[m].Count).ThenBy(m => st.Ids[m]).First();
            var order = new List<int>(); var q = new Queue<int>();
            depth[root] = 0; q.Enqueue(root);
            while (q.Count > 0) { int u = q.Dequeue(); order.Add(u); foreach (int v in adj[u]) if (depth[v] < 0) { depth[v] = depth[u] + 1; q.Enqueue(v); } }
            foreach (int u in order)
            {
                if (u == root) continue;
                int bestP = -1;
                foreach (int v in adj[u]) if (depth[v] == depth[u] - 1 && (bestP < 0 || childCount[v] > childCount[bestP])) bestP = v;
                parent[u] = bestP; childCount[bestP]++;
            }
            groups++;
        }

        st.Cells.AddRange(cells);
        st.EdgeIsTree.Clear();
        foreach (var (a, b) in st.Edges) st.EdgeIsTree.Add(parent[a] == b || parent[b] == a);
        st.RowCount = System.Math.Max(1, rowCount);
    }

    // ------------------------------------------------------------------ lane routing
    class LaneReq { public int Gap; public float XMin, XMax, Y; public int Lane; public float[] FromAbove = new float[0], ToBelow = new float[0]; }
    static bool InSpan(float x, LaneReq r) => x > r.XMin - 0.5f && x < r.XMax + 0.5f;
    // crossings made when a is drawn above b (smaller y): b's incoming verticals pass a's run, a's outgoing ones pass b's run
    static int AboveCost(LaneReq a, LaneReq b)
    {
        int c = 0;
        foreach (var x in b.FromAbove) if (InSpan(x, a)) c++;
        foreach (var x in a.ToBelow) if (InSpan(x, b)) c++;
        return c;
    }
    class RouteInfo { public int U, V, Kind; public float Gx, Xu, Xv; public LaneReq Up, Lo; }

    // Edges are routed in layout space (x px, y = row * CellH + offset) once, with horizontal "lanes" inside the gaps
    // between rows and vertical "gutters" between tile columns, so a line never runs behind an unrelated tile and
    // overlapping lines always get different lanes / gutters.
    static void ComputeRoutes(MenuState st)
    {
        st.Routes.Clear();
        int n = st.Ids.Count, edgeCount = st.Edges.Count;
        if (edgeCount == 0) return;

        var rowTiles = new Dictionary<int, List<int>>();
        for (int i = 0; i < n; i++)
        {
            int r = st.Cells[i].row;
            if (!rowTiles.TryGetValue(r, out var list)) rowTiles[r] = list = new List<int>();
            list.Add(i);
        }
        foreach (var list in rowTiles.Values) list.Sort((p, q) => st.Cells[p].col.CompareTo(st.Cells[q].col));

        var gutters = new List<(float x, int r0, int r1)>();
        float PickGutter(float target, int r0, int r1)
        {
            var blocked = new List<(float lo, float hi)>();
            for (int r = r0; r <= r1; r++)
                if (rowTiles.TryGetValue(r, out var tl))
                    foreach (int t in tl)
                    {
                        float cx = TileX(st.Cells[t].col);
                        blocked.Add((cx - TileW / 2f - 12f, cx + TileW / 2f + 12f));
                    }
            float best = target, bestD = float.MaxValue;
            for (float x = LinkX0 + 20f; x <= LinkX0 + LinkW - 20f; x += 4f)
            {
                bool ok = true;
                foreach (var (lo, hi) in blocked) if (x >= lo && x <= hi) { ok = false; break; }
                if (!ok) continue;
                foreach (var g in gutters) if (g.r0 <= r1 && g.r1 >= r0 && Mathf.Abs(g.x - x) < 11f) { ok = false; break; }
                if (!ok) continue;
                float d = Mathf.Abs(x - target);
                if (d < bestD) { bestD = d; best = x; }
            }
            if (Mathf.Abs(best - target) <= 6f) best = target; // the step grid can miss the exact centre by a few px: avoid a tiny kink
            gutters.Add((best, r0, r1));
            return best;
        }

        var reqs = new Dictionary<(int, int, int), LaneReq>();
        // x0 is where the line arrives from above; x1 where it leaves again (downwards, or upwards when bothAbove)
        LaneReq Req(int gap, int owner, int kind, float x0, float x1, bool bothAbove = false)
        {
            var key = (gap, owner, kind);
            if (!reqs.TryGetValue(key, out var r)) reqs[key] = r = new LaneReq { Gap = gap, XMin = Mathf.Min(x0, x1), XMax = Mathf.Max(x0, x1),
                FromAbove = bothAbove ? new[] { x0, x1 } : new[] { x0 }, ToBelow = bothAbove ? new float[0] : new[] { x1 } };
            else { r.XMin = Mathf.Min(r.XMin, Mathf.Min(x0, x1)); r.XMax = Mathf.Max(r.XMax, Mathf.Max(x0, x1)); }
            return r;
        }

        // phase A0: order every edge top-to-bottom, then give each edge its own exit/entry point along the tile edge
        var us = new int[edgeCount]; var vs = new int[edgeCount];
        var down = new Dictionary<int, List<int>>(); // tile -> edges leaving through its bottom
        var up = new Dictionary<int, List<int>>();   // tile -> edges entering through its top
        void AddPort(Dictionary<int, List<int>> d, int tile, int e)
        {
            if (!d.TryGetValue(tile, out var l)) d[tile] = l = new List<int>();
            l.Add(e);
        }
        for (int e = 0; e < edgeCount; e++)
        {
            var (a0, b0) = st.Edges[e];
            int u = a0, v = b0;
            var ca = st.Cells[a0]; var cb = st.Cells[b0];
            if (ca.row > cb.row || (ca.row == cb.row && ca.col > cb.col)) { u = b0; v = a0; }
            us[e] = u; vs[e] = v;
            int ru0 = st.Cells[u].row, rv0 = st.Cells[v].row;
            if (ru0 == rv0)
            {
                var tl0 = rowTiles[ru0];
                if (System.Math.Abs(tl0.IndexOf(u) - tl0.IndexOf(v)) == 1) continue; // side by side: straight, no ports
                AddPort(down, u, e); AddPort(down, v, e);                              // U route leaves both bottoms
            }
            else { AddPort(down, u, e); AddPort(up, v, e); }
        }
        // an edge between two tiles of the same column with no tile between them is one straight vertical line
        var colClear = new bool[edgeCount];
        for (int e = 0; e < edgeCount; e++)
        {
            var cu = st.Cells[us[e]]; var cv = st.Cells[vs[e]];
            if (cv.row <= cu.row || Mathf.Abs(cu.col - cv.col) > 0.01f) continue;
            bool clear = true;
            for (int t = 0; t < n && clear; t++)
            {
                if (t == us[e] || t == vs[e]) continue;
                var ct = st.Cells[t];
                if (Mathf.Abs(ct.col - cu.col) < 0.5f && ct.row > cu.row && ct.row < cv.row) clear = false;
            }
            colClear[e] = clear;
        }

        // x offset of each end of each edge, ordered by where the other end is so lines do not cross at the tile.
        // A tile that has a neighbour directly below/above it (same column, next row) keeps the CENTRE of that side for the
        // straight line to it, so the two tiles are joined by one perfectly vertical line; other links use offsets beside it.
        var offU = new float[edgeCount]; var offV = new float[edgeCount];
        void Spread(Dictionary<int, List<int>> d, bool upSide)
        {
            foreach (var kv in d)
            {
                int tile = kv.Key;
                float tx = TileX(st.Cells[tile].col);
                int Other(int e) => (us[e] == tile) ? vs[e] : us[e];
                float OtherX(int e) => TileX(st.Cells[Other(e)].col);
                void Set(int e, float off) { if (us[e] == tile && !(upSide && vs[e] == tile)) offU[e] = off; else offV[e] = off; }

                int straight = -1;
                foreach (int e in kv.Value)
                    if (colClear[e]) { straight = e; break; }

                if (straight >= 0)
                {
                    Set(straight, 0f);
                    var left = kv.Value.Where(e => e != straight && OtherX(e) < tx).OrderByDescending(OtherX).ToList();   // nearest first
                    var right = kv.Value.Where(e => e != straight && OtherX(e) >= tx).OrderBy(OtherX).ToList();
                    float sp = Mathf.Min(16f, (TileW / 2f - 16f) / Mathf.Max(1, Mathf.Max(left.Count, right.Count)));
                    for (int k = 0; k < left.Count; k++) Set(left[k], -(k + 1) * sp);
                    for (int k = 0; k < right.Count; k++) Set(right[k], (k + 1) * sp);
                    continue;
                }

                var list = kv.Value.OrderBy(e => OtherX(e) * 1000f + Other(e)).ToList();
                int n = list.Count;
                float spacing = n > 1 ? Mathf.Min(16f, (TileW - 44f) / (n - 1)) : 0f;
                for (int k = 0; k < n; k++) Set(list[k], (k - (n - 1) / 2f) * spacing);
            }
        }
        Spread(down, false);
        Spread(up, true);

        // phase A: classify every edge and ask for the lanes it needs (one lane per edge, so different destinations never share a line)
        var infos = new RouteInfo[edgeCount];
        for (int e = 0; e < edgeCount; e++)
        {
            int u = us[e], v = vs[e];
            int ru = st.Cells[u].row, rv = st.Cells[v].row;
            float xu = TileX(st.Cells[u].col), xv = TileX(st.Cells[v].col);
            var info = new RouteInfo { U = u, V = v };
            if (ru == rv)
            {
                var tl = rowTiles[ru];
                if (System.Math.Abs(tl.IndexOf(u) - tl.IndexOf(v)) == 1) { info.Kind = 0; info.Xu = xu; info.Xv = xv; }   // neighbours in the row: straight
                else
                {
                    info.Kind = 3; info.Xu = xu + offU[e]; info.Xv = xv + offV[e];
                    info.Up = Req(ru, 1000000 + e, 2, info.Xu, info.Xv, true);                               // U through the gap below the row
                }
            }
            else
            {
                info.Xu = xu + offU[e]; info.Xv = xv + offV[e];
                if (colClear[e]) { info.Kind = 1; info.Xv = info.Xu; }                                  // straight down an empty column
                else if (rv == ru + 1)
                {
                    if (Mathf.Abs(info.Xu - info.Xv) < 1f) info.Kind = 1;                                // straight down
                    else { info.Kind = 2; info.Up = Req(ru, 2000000 + e, 0, info.Xu, info.Xv); }
                }
                else
                {
                    info.Kind = 4;                                                                      // skips rows: lane, gutter, lane
                    info.Gx = PickGutter(xv, ru + 1, rv - 1);
                    info.Up = Req(ru, 3000000 + e, 0, info.Xu, info.Gx);
                    info.Lo = Req(rv - 1, 4000000 + e, 1, info.Gx, info.Xv);
                }
            }
            infos[e] = info;
        }

        // phase B: per gap, pack the requests into lanes (requests that do not overlap in x may share a lane)
        float gapH = CellH - 2f * TileHalfH, usable = gapH - 2f * 10f;
        foreach (var grp in reqs.Values.GroupBy(r => r.Gap))
        {
            // order the lines top to bottom so that as few verticals as possible cut through another line's horizontal run
            var list = new List<LaneReq>();
            foreach (var r in grp.OrderBy(r => r.XMin).ThenBy(r => r.XMax))
            {
                int at = list.Count;
                while (at > 0 && AboveCost(r, list[at - 1]) < AboveCost(list[at - 1], r)) at--;
                list.Insert(at, r);
            }
            var lanesUsed = new List<List<LaneReq>>();
            foreach (var r in list)
            {
                int li = -1;
                for (int k = 0; k < lanesUsed.Count && li < 0; k++)
                    if (lanesUsed[k].All(o => o.XMax + 20f < r.XMin || r.XMax + 20f < o.XMin)) li = k;
                if (li < 0) { lanesUsed.Add(new List<LaneReq>()); li = lanesUsed.Count - 1; }
                lanesUsed[li].Add(r);
                r.Lane = li;
            }
            var laneEnd = lanesUsed;
            int lanes = laneEnd.Count;
            float spacing = lanes > 1 ? Mathf.Clamp(usable / (lanes - 1), 11f, 16f) : 0f; // parallel lines are never closer than 11 px
            float mid = grp.Key * CellH + CellH / 2f;
            foreach (var r in list) r.Y = mid + (r.Lane - (lanes - 1) / 2f) * spacing;
        }

        // phase C: polylines (they start/end just inside the tile edges; tiles draw on top)
        for (int e = 0; e < edgeCount; e++)
        {
            var info = infos[e];
            int ru = st.Cells[info.U].row, rv = st.Cells[info.V].row;
            float xu = info.Xu, xv = info.Xv;
            float yu = ru * CellH, yv = rv * CellH;
            var pts = new List<Vector2>();
            switch (info.Kind)
            {
                case 0:
                    pts.Add(new Vector2(xu + EdgeX, yu)); pts.Add(new Vector2(xv - EdgeX, yu));
                    break;
                case 1:
                    pts.Add(new Vector2(xu, yu + EdgeY)); pts.Add(new Vector2(xv, yv - EdgeY));
                    break;
                case 2:
                    pts.Add(new Vector2(xu, yu + EdgeY)); pts.Add(new Vector2(xu, info.Up.Y));
                    pts.Add(new Vector2(xv, info.Up.Y)); pts.Add(new Vector2(xv, yv - EdgeY));
                    break;
                case 3:
                    pts.Add(new Vector2(xu, yu + EdgeY)); pts.Add(new Vector2(xu, info.Up.Y));
                    pts.Add(new Vector2(xv, info.Up.Y)); pts.Add(new Vector2(xv, yu + EdgeY));
                    break;
                default:
                    pts.Add(new Vector2(xu, yu + EdgeY)); pts.Add(new Vector2(xu, info.Up.Y));
                    pts.Add(new Vector2(info.Gx, info.Up.Y)); pts.Add(new Vector2(info.Gx, info.Lo.Y));
                    pts.Add(new Vector2(xv, info.Lo.Y)); pts.Add(new Vector2(xv, yv - EdgeY));
                    break;
            }
            // drop repeated points so rounded corners only appear where the line really turns
            var clean = new List<Vector2>();
            foreach (var p in pts) if (clean.Count == 0 || Vector2.Distance(clean[clean.Count - 1], p) > 0.5f) clean.Add(p);
            if (clean.Count < 2) clean.Add(clean[0] + new Vector2(0.01f, 0f));
            st.Routes.Add(clean);
        }
    }

    static void BuildLinkImage(MenuState st, MapInfo template, Transform parent)
    {
        st.OutlineA = new float[TexW * TexH];
        st.FillA = new float[TexW * TexH];
        st.OutlineD = new float[TexW * TexH];
        st.FillD = new float[TexW * TexH];
        st.LinkPixels = new Il2CppStructArray<Color32>(TexW * TexH);
        st.LinkTex = new Texture2D(TexW, TexH, TextureFormat.RGBA32, false);
        st.LinkTex.filterMode = FilterMode.Bilinear;
        st.LinkTex.wrapMode = TextureWrapMode.Clamp;

        st.LinkImage = Object.Instantiate(template.transform.Find("Cover").gameObject, parent);
        st.LinkImage.name = "CustomMapLinks";
        var rt = st.LinkImage.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.localPosition = PxToLocal(new Vector2(LinkX0 + LinkW / 2f, LinkY0 + LinkH / 2f));
        rt.sizeDelta = new Vector2(LinkW * _ux, LinkH * _vy);
        var img = st.LinkImage.GetComponent<UnityEngine.UI.Image>();
        img.sprite = Sprite.Create(st.LinkTex, new Rect(0f, 0f, TexW, TexH), new Vector2(0.5f, 0.5f), 100f);
        img.type = UnityEngine.UI.Image.Type.Simple;
        img.color = Color.white;
        img.raycastTarget = false;
        st.LinkImage.SetActive(false);

        // tiles must draw over the links
        foreach (var tile in st.Tiles) tile.transform.SetAsLastSibling();
    }

    const float EdgeX = TileW / 2f - 4f, EdgeY = TileHalfH - 4f; // routes start just inside the tile edge, tiles draw on top

    // rounded corners like the stock routes
    static List<Vector2> Rounded(List<Vector2> v)
    {
        var pts = new List<Vector2> { v[0] };
        for (int i = 1; i < v.Count - 1; i++)
        {
            Vector2 p = v[i - 1], c = v[i], n = v[i + 1];
            float d1 = Vector2.Distance(p, c), d2 = Vector2.Distance(c, n);
            if (d1 < 0.5f || d2 < 0.5f) { pts.Add(c); continue; }
            float r = Mathf.Min(LineCorner, d1 / 2f, d2 / 2f);
            Vector2 a = c + (p - c) / d1 * r, b = c + (n - c) / d2 * r;
            pts.Add(a);
            for (int k = 1; k < ArcSteps; k++)
            {
                float t = k / (float)ArcSteps, u = 1f - t;
                pts.Add(u * u * a + 2f * u * t * c + t * t * b);
            }
            pts.Add(b);
        }
        pts.Add(v[v.Count - 1]);
        return pts;
    }

    static void StampPath(MenuState st, List<Vector2> pts, float[] outA, float[] fillA, float outlinePx, float fillPx)
    {
        for (int i = 0; i + 1 < pts.Count; i++) Stamp(st, pts[i], pts[i + 1], outA, fillA, outlinePx, fillPx);
    }

    // Tree links are drawn in full; cross links thin and dim. While the mouse is on a tile, every link of that tile is
    // bright and all other links are dimmed. Two coverage layers (front/back) keep overlaps from adding up.
    static void RenderLinks(MenuState st)
    {
        bool any = false;
        bool hovering = st.Hover >= 0;
        if (st.Open && st.Edges.Count > 0)
        {
            System.Array.Clear(st.OutlineA, 0, st.OutlineA.Length);
            System.Array.Clear(st.FillA, 0, st.FillA.Length);
            System.Array.Clear(st.OutlineD, 0, st.OutlineD.Length);
            System.Array.Clear(st.FillD, 0, st.FillD.Length);
            for (int ei = 0; ei < st.Edges.Count; ei++)
            {
                var (a, b) = st.Edges[ei];
                var screen = new List<Vector2>();
                float minY = float.MaxValue, maxY = float.MinValue;
                foreach (var p in st.Routes[ei])
                {
                    var q = new Vector2(p.x, BaseY + p.y - st.Row * CellH);
                    screen.Add(q);
                    minY = Mathf.Min(minY, q.y); maxY = Mathf.Max(maxY, q.y);
                }
                if (maxY < LinkY0 || minY > LinkY0 + LinkH) continue; // route is entirely outside the link area
                bool incident = hovering && (a == st.Hover || b == st.Hover);
                bool tree = ei < st.EdgeIsTree.Count && st.EdgeIsTree[ei];
                bool front = hovering ? incident : tree;
                float outPx = incident ? 12f : (tree ? LineOutlinePx : 9f);
                float fillPx = incident ? 8f : (tree ? LineFillPx : 4.5f);
                StampPath(st, Rounded(screen), front ? st.OutlineA : st.OutlineD, front ? st.FillA : st.FillD, outPx, fillPx);
                any = true;
            }
        }
        st.LinkImage.SetActive(any);
        if (!any) return;

        // outline (dark, soft) under fill (cream); the back layer is composited under the front layer
        float backAlpha = hovering ? 0.35f : 0.65f;
        var outlineRgb = new Vector3(0.28f, 0.25f, 0.18f);
        var fillRgb = new Vector3(0.98f, 0.95f, 0.85f);
        var backRgb = hovering ? fillRgb : new Vector3(0.62f, 0.90f, 0.95f); // links that are not parent/child links: soft cyan
        for (int i = 0; i < st.FillA.Length; i++)
        {
            float ff = st.FillA[i], of = st.OutlineA[i] * 0.6f;
            float fb = st.FillD[i] * backAlpha, ob = st.OutlineD[i] * 0.6f * backAlpha;
            float aF = ff + of * (1f - ff);
            float aB = fb + ob * (1f - fb);
            float a = aF + aB * (1f - aF);
            if (a <= 0.003f) { st.LinkPixels[i] = new Color32(0, 0, 0, 0); continue; }
            var premulF = fillRgb * ff + outlineRgb * of * (1f - ff);
            var premulB = backRgb * fb + outlineRgb * ob * (1f - fb);
            var rgb = (premulF + premulB * (1f - aF)) / a;
            st.LinkPixels[i] = new Color32((byte)(Mathf.Clamp01(rgb.x) * 255f), (byte)(Mathf.Clamp01(rgb.y) * 255f), (byte)(Mathf.Clamp01(rgb.z) * 255f), (byte)(Mathf.Clamp01(a) * 255f));
        }
        st.LinkTex.SetPixels32(st.LinkPixels);
        st.LinkTex.Apply(false);
    }

    // anti-aliased thick segment into a pair of coverage maps (coordinates: canvas px -> texture px)
    static void Stamp(MenuState st, Vector2 p0, Vector2 p1, float[] outA, float[] fillA, float outlinePx, float fillPx)
    {
        var a = new Vector2((p0.x - LinkX0) * LinkRes, (LinkY0 + LinkH - p0.y) * LinkRes);
        var b = new Vector2((p1.x - LinkX0) * LinkRes, (LinkY0 + LinkH - p1.y) * LinkRes);
        float ho = outlinePx / 2f * LinkRes, hf = fillPx / 2f * LinkRes;

        int x0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a.x, b.x) - ho - 1f));
        int x1 = Mathf.Min(TexW - 1, Mathf.CeilToInt(Mathf.Max(a.x, b.x) + ho + 1f));
        int y0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a.y, b.y) - ho - 1f));
        int y1 = Mathf.Min(TexH - 1, Mathf.CeilToInt(Mathf.Max(a.y, b.y) + ho + 1f));
        var ab = b - a;
        float len2 = Mathf.Max(ab.sqrMagnitude, 0.0001f);

        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                var p = new Vector2(x + 0.5f, y + 0.5f);
                float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / len2);
                float d = (p - (a + ab * t)).magnitude;
                // y counts down from the top of the link area and the image shows these rows top-down as stored
                int idx = y * TexW + x;
                float ao = Mathf.Clamp01(ho + 0.5f - d), af = Mathf.Clamp01(hf + 0.5f - d);
                if (ao > outA[idx]) outA[idx] = ao;
                if (af > fillA[idx]) fillA[idx] = af;
            }
    }

    // ------------------------------------------------------------------ scroll bar and dragging
    static float BarTop => GridCentreY - VisibleRows * CellH / 2f;
    static float BarHeight => VisibleRows * CellH;

    static GameObject MakeBarPart(MapInfo template, Transform parent, string name, Color color)
    {
        var go = Object.Instantiate(template.transform.Find("Cover").gameObject, parent);
        go.name = name;
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        var img = go.GetComponent<UnityEngine.UI.Image>();
        img.color = color;
        img.raycastTarget = false;
        go.SetActive(false);
        return go;
    }

    static void BuildScrollBar(MenuState st, MapInfo template, Transform parent)
    {
        st.BarTrack = MakeBarPart(template, parent, "CustomMapBarTrack", new Color(1f, 1f, 1f, 0.25f));
        st.BarThumb = MakeBarPart(template, parent, "CustomMapBarThumb", new Color(0.85f, 1f, 1f, 0.95f));
    }

    // clone the option menu's own scroll bar once it is available (its sprites, handle and drag come with it)
    static void EnsureRealBar(MenuState st)
    {
        if (st.RealBar != null || _optionBar == null) return;
        var go = Object.Instantiate(_optionBar.gameObject, st.BarTrack.transform.parent);
        go.name = "CustomMapScrollbar";
        var sb = go.GetComponent<UnityEngine.UI.Scrollbar>();
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.localScale = Vector3.one;
        sb.direction = UnityEngine.UI.Scrollbar.Direction.TopToBottom;
        sb.numberOfSteps = 0;
        sb.onValueChanged.RemoveAllListeners();
        sb.onValueChanged.AddListener(new System.Action<float>(v => OnBarChanged(st, v)));
        go.SetActive(false);
        st.RealBar = sb;
    }

    static void OnBarChanged(MenuState st, float v)
    {
        if (st.BarSync || !st.Open) return;
        int row = Mathf.RoundToInt(v * MaxRow(st));
        if (row != st.Row) { st.Row = row; Refresh(st); }
    }

    static void RefreshScrollBar(MenuState st)
    {
        bool show = st.Open && MaxRow(st) > 0;
        if (st.RealBar != null)
        {
            st.BarTrack.SetActive(false);
            st.BarThumb.SetActive(false);
            st.RealBar.gameObject.SetActive(show);
            if (!show) return;
            var brt = st.RealBar.GetComponent<RectTransform>();
            brt.localPosition = PxToLocal(new Vector2(BarX, BarTop + BarHeight / 2f));
            brt.sizeDelta = new Vector2(_optionBarWidth, BarHeight * _vy);
            st.BarSync = true;
            st.RealBar.size = (float)VisibleRows / st.RowCount;
            st.RealBar.value = (float)st.Row / MaxRow(st);
            st.BarSync = false;
            st.RealBar.transform.SetAsLastSibling();
            return;
        }
        st.BarTrack.SetActive(show);
        st.BarThumb.SetActive(show);
        if (!show) return;

        var rt = st.BarTrack.GetComponent<RectTransform>();
        rt.localPosition = PxToLocal(new Vector2(BarX, BarTop + BarHeight / 2f));
        rt.sizeDelta = new Vector2(BarHalfW * 2f * _ux, BarHeight * _vy);

        float thumbH = Mathf.Max(40f, BarHeight * VisibleRows / st.RowCount);
        float travel = BarHeight - thumbH;
        float thumbCentreY = BarTop + thumbH / 2f + travel * st.Row / MaxRow(st);
        var trt = st.BarThumb.GetComponent<RectTransform>();
        trt.localPosition = PxToLocal(new Vector2(BarX, thumbCentreY));
        trt.sizeDelta = new Vector2(BarHalfW * 2f * _ux, thumbH * _vy);
    }

    // mouse position in the same pixel space as the layout (1920x1080 canvas + 32px window title bar)
    static Vector2 MousePx()
    {
        var m = Input.mousePosition;
        return new Vector2(m.x * 1920f / Screen.width, 32f + (Screen.height - m.y) * 1080f / Screen.height);
    }

    // returns true while the scroll bar owns the mouse
    static bool UpdateScrollBar(MenuState st)
    {
        if (MaxRow(st) <= 0) { st.BarDragging = false; return false; }
        var mp = MousePx();
        if (st.RealBar != null)
        {
            // the real scroll bar handles its own drag; just keep the content drag from starting on it
            bool inside = Mathf.Abs(mp.x - BarX) <= 24f && mp.y >= BarTop - 10f && mp.y <= BarTop + BarHeight + 10f;
            if (Input.GetMouseButtonDown(0) && inside) st.BarDragging = true;
            if (st.BarDragging && !Input.GetMouseButton(0)) st.BarDragging = false;
            return st.BarDragging;
        }
        if (Input.GetMouseButtonDown(0) && Mathf.Abs(mp.x - BarX) <= BarHalfW + 6f && mp.y >= BarTop && mp.y <= BarTop + BarHeight)
            st.BarDragging = true;
        if (!st.BarDragging) return false;
        if (!Input.GetMouseButton(0)) { st.BarDragging = false; return false; }

        float f = Mathf.Clamp01((mp.y - BarTop) / BarHeight);
        int row = Mathf.RoundToInt(f * MaxRow(st));
        if (row != st.Row) { st.Row = row; Refresh(st); }
        return true;
    }

    // drag the grid with the left mouse button: moving the mouse up scrolls down, like dragging a list
    static void UpdateDrag(MenuState st)
    {
        float y = Input.mousePosition.y;
        if (Input.GetMouseButtonDown(0))
        {
            st.Dragging = true;
            st.DragMoved = false;
            st.DragStartY = st.DragPressY = y;
            st.DragAccum = 0f;
        }
        else if (st.Dragging && Input.GetMouseButton(0))
        {
            float dy = y - st.DragStartY;
            if (Mathf.Abs(y - st.DragPressY) > DragClickPx) st.DragMoved = true;
            st.DragStartY = y;
            st.DragAccum += dy;
            while (st.DragAccum >= DragStepPx) { Scroll(st, 1); st.DragAccum -= DragStepPx; }
            while (st.DragAccum <= -DragStepPx) { Scroll(st, -1); st.DragAccum += DragStepPx; }
        }
        else if (st.Dragging)
        {
            st.Dragging = false; // DragMoved stays set until the next press so the release click can be ignored
        }
    }

    // ------------------------------------------------------------------ thumbnails from the maps' own 2D backgrounds
    // The mod ships a 2048x1152 background per time of day for most maps (Map2dBundlePath + Map2dAsset + "00".."03").
    // They are shrunk once into small PNGs next to the plugin config and reused afterwards. Only bundles the game has
    // already loaded are read (loading one here could make the map mod's own load fail).
    static readonly Queue<int> _thumbQueue = new();
    static float _thumbNext;
    static int _thumbsCreated;
    const int ThumbW = 384, ThumbH = 216;

    static string ThumbDir => System.IO.Path.Combine(BepInEx.Paths.GameRootPath, "UserData", "plugins", "MapMenuExpansion", "thumbs");

    static Transform ThumbnailOf(MenuState st, int index) => st.Tiles[index].transform.Find("Thumbnail");

    static void ShowThumb(MenuState st, int index, Texture2D tex)
    {
        var t = ThumbnailOf(st, index);
        var img = t != null ? t.GetComponent<UnityEngine.UI.Image>() : null;
        if (img == null) return;
        img.sprite = Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
        img.color = Color.white;
    }

    static void ShowPlaceholder(MenuState st, int index)
    {
        var t = ThumbnailOf(st, index);
        var img = t != null ? t.GetComponent<UnityEngine.UI.Image>() : null;
        if (img == null) return;
        img.sprite = null; // flat colour instead of another map's picture
        img.color = new Color(0.20f, 0.24f, 0.30f, 1f);
    }

    static void InitThumbnails(MenuState st, Il2CppSystem.Collections.Generic.Dictionary<int, MapInfoParam> maps)
    {
        System.IO.Directory.CreateDirectory(ThumbDir);
        var pending = new List<int>();
        for (int i = 0; i < st.Ids.Count; i++)
        {
            int id = st.Ids[i];
            if (st.StockIds.Contains(id)) continue; // stock tiles keep the game's own thumbnails
            string file = System.IO.Path.Combine(ThumbDir, id + ".png");
            if (System.IO.File.Exists(file))
            {
                var tex = new Texture2D(2, 2, TextureFormat.RGB24, false);
                if (ImageConversion.LoadImage(tex, System.IO.File.ReadAllBytes(file))) { ShowThumb(st, i, tex); continue; }
            }
            ShowPlaceholder(st, i);
            pending.Add(id);
        }
        foreach (int id in pending.OrderBy(i => maps[i].Map2dBundlePath ?? "").ThenBy(i => i)) _thumbQueue.Enqueue(id);
    }

    // asset index over every bundle that is currently loaded: texture name (lower case, no extension) -> bundle + full asset name.
    // Bundle names are empty or hashed, so assets are found by their own names instead.
    static readonly Dictionary<string, (AssetBundle bundle, string asset)> _bundleIndex = new();
    static readonly HashSet<int> _indexedBundles = new();
    static float _indexAt;
    static AssetBundle _ownBundle;      // a bundle we opened from disk ourselves (closed again as soon as possible)
    static string _ownBundlePath;
    static Dictionary<string, string> _ownNames = new();

    static void RefreshBundleIndex()
    {
        if (Time.unscaledTime < _indexAt) return;
        _indexAt = Time.unscaledTime + 3f;
        foreach (var bundle in Resources.FindObjectsOfTypeAll<AssetBundle>())
        {
            if (bundle == null || bundle == _ownBundle) continue;
            if (!_indexedBundles.Add(bundle.GetInstanceID())) continue;
            foreach (var name in bundle.GetAllAssetNames())
            {
                string lower = name.ToLowerInvariant();
                if (!lower.EndsWith(".png") || !lower.Contains("/sv_m")) continue;
                _bundleIndex[System.IO.Path.GetFileNameWithoutExtension(lower)] = (bundle, name);
            }
        }
    }

    static void CloseOwnBundle()
    {
        if (_ownBundle == null) return;
        _ownBundle.Unload(false); // keep nothing: the game may want to open this file itself later
        _ownBundle = null;
        _ownBundlePath = null;
        _ownNames.Clear();
    }

    // returns the texture for 'stem' (e.g. sv_m139_01) from a loaded bundle, or from the bundle file opened briefly from disk
    static Texture2D LoadBackground(string stem, string bundleRelPath)
    {
        stem = stem.ToLowerInvariant();
        if (_bundleIndex.TryGetValue(stem, out var hit)) return hit.bundle.LoadAsset<Texture2D>(hit.asset);

        if (string.IsNullOrEmpty(bundleRelPath)) return null;
        string full = System.IO.Path.Combine(BepInEx.Paths.GameRootPath, "abdata", bundleRelPath.Replace('/', System.IO.Path.DirectorySeparatorChar));
        if (_ownBundle == null || _ownBundlePath != full)
        {
            CloseOwnBundle();
            if (!System.IO.File.Exists(full)) return null;
            _ownBundle = AssetBundle.LoadFromFile(full);
            _ownBundlePath = full;
            if (_ownBundle == null) { Plugin.Log.LogWarning($"thumbnails: could not open {bundleRelPath} (the game probably has it open)"); return null; }
            foreach (var name in _ownBundle.GetAllAssetNames())
                _ownNames[System.IO.Path.GetFileNameWithoutExtension(name.ToLowerInvariant())] = name;
        }
        return _ownNames.TryGetValue(stem, out var asset) ? _ownBundle.LoadAsset<Texture2D>(asset) : null;
    }

    // one map per tick so the shrinking never stalls a frame
    static void PumpThumbnails()
    {
        if (_thumbQueue.Count == 0)
        {
            if (_ownBundle != null) CloseOwnBundle();
            if (_thumbsCreated > 0) { Plugin.Log.LogInfo($"Map select: {_thumbsCreated} thumbnails created"); _thumbsCreated = 0; }
            return;
        }
        if (Time.unscaledTime < _thumbNext) return;
        _thumbNext = Time.unscaledTime + 0.2f;
        var st = States.Values.FirstOrDefault();
        var maps = SingletonInitializerAsync<MapManager>.Instance?.MapListTable;
        if (st == null || maps == null) return;
        RefreshBundleIndex();

        int id = _thumbQueue.Dequeue();
        int index = st.Ids.IndexOf(id);
        if (index < 0 || !maps.ContainsKey(id)) return;
        var param = maps[id];
        if (string.IsNullOrEmpty(param.Map2dAsset)) return; // no 2D background for this map: placeholder stays

        Texture2D big = null;
        foreach (int slot in new[] { Plugin.ThumbnailTime.Value, 1, 0, 2, 3 })
        {
            big = LoadBackground($"{param.Map2dAsset}{slot:00}", param.Map2dBundlePath);
            if (big != null) break;
        }
        if (big == null) { Plugin.Log.LogWarning($"thumbnail for map {id}: no background '{param.Map2dAsset}xx' found"); return; }

        var rt = RenderTexture.GetTemporary(ThumbW, ThumbH, 0);
        var previous = RenderTexture.active;
        Graphics.Blit(big, rt);
        RenderTexture.active = rt;
        var small = new Texture2D(ThumbW, ThumbH, TextureFormat.RGB24, false);
        small.ReadPixels(new Rect(0f, 0f, ThumbW, ThumbH), 0, 0);
        small.Apply();
        RenderTexture.active = previous;
        RenderTexture.ReleaseTemporary(rt);
        Resources.UnloadAsset(big); // the 2048x1152 original is not needed any more

        System.IO.File.WriteAllBytes(System.IO.Path.Combine(ThumbDir, id + ".png"), ImageConversion.EncodeToPNG(small));
        ShowThumb(st, index, small);
        _thumbsCreated++;
    }

    // ------------------------------------------------------------------ option-menu button look
    // sprites + font are only in memory once the option scene has loaded
    static Sprite _tealSprite, _blueSprite;
    static TMPro.TMP_FontAsset _font;
    static Material _fontMaterial;
    static Sprite _barTrackSprite, _barThumbSprite;
    static Color _barTrackColor = new Color(1f, 1f, 1f, 0.25f), _barThumbColor = new Color(0.85f, 1f, 1f, 0.95f);
    static UnityEngine.UI.Scrollbar _optionBar;
    static float _optionBarWidth = 12f;

    static void ResolveStyle()
    {
        if (_tealSprite == null || _blueSprite == null)
        {
            foreach (var sp in Resources.FindObjectsOfTypeAll<Sprite>())
            {
                if (sp.name == "sp_sv_manage_00_000") _tealSprite = sp;
                else if (sp.name == "sp_sv_option_00_001") _blueSprite = sp;
            }
        }
        if (_font == null)
        {
            foreach (var b in Resources.FindObjectsOfTypeAll<UnityEngine.UI.Button>())
            {
                var img = b.GetComponent<UnityEngine.UI.Image>();
                if (img == null || img.sprite == null || img.sprite.name != "sp_sv_manage_00_000") continue;
                var txt = b.GetComponentInChildren<TMPro.TMP_Text>(true);
                if (txt == null || txt.font == null) continue;
                _font = txt.font;
                _fontMaterial = txt.fontSharedMaterial;
                break;
            }
        }
        if (_optionBar == null)
        {
            foreach (var sb in Resources.FindObjectsOfTypeAll<UnityEngine.UI.Scrollbar>())
            {
                var path = sb.name;
                for (var t = sb.transform.parent; t != null && path.Length < 120; t = t.parent) path = t.name + "/" + path;
                if (path.Contains("CustomMap")) continue;
                if (_optionBar == null && path.Contains("ConfigScene")) _optionBar = sb;
            }
            if (_optionBar != null) _optionBarWidth = Mathf.Max(10f, _optionBar.GetComponent<RectTransform>().rect.width);
        }
        if (_barThumbSprite == null)
        {
            // the option menu's scroll bar: copy its sprites and colours
            foreach (var sb in Resources.FindObjectsOfTypeAll<UnityEngine.UI.Scrollbar>())
            {
                var handle = sb.handleRect != null ? sb.handleRect.GetComponent<UnityEngine.UI.Image>() : null;
                if (handle == null || handle.sprite == null) continue;
                _barThumbSprite = handle.sprite;
                _barThumbColor = handle.color;
                var track = sb.GetComponent<UnityEngine.UI.Image>();
                if (track != null && track.sprite != null) { _barTrackSprite = track.sprite; _barTrackColor = track.color; }
                break;
            }
        }
    }

    static void ApplyStyle(MenuState st)
    {
        ResolveStyle();
        foreach (var (button, blue) in st.Buttons)
        {
            var sprite = blue ? _blueSprite : _tealSprite;
            var bg = button.transform.Find("CustomLabelBG");
            if (bg != null && sprite != null)
            {
                var img = bg.GetComponent<UnityEngine.UI.Image>();
                img.sprite = sprite;
                img.type = UnityEngine.UI.Image.Type.Sliced;
                img.color = Color.white;
            }
            var label = button.transform.Find("CustomLabel");
            var tmp = label != null ? label.GetComponent<TMPro.TMP_Text>() : null;
            if (tmp != null && _font != null)
            {
                tmp.font = _font;
                if (_fontMaterial != null) tmp.fontSharedMaterial = _fontMaterial;
                tmp.fontSize = 24f;
            }
        }
        // map name labels share the option-menu font
        if (_font != null)
            foreach (var lb in st.Labels)
            {
                var tmp = lb.GetComponentInChildren<TMPro.TMP_Text>(true);
                if (tmp == null) continue;
                tmp.font = _font;
                if (_fontMaterial != null) tmp.fontSharedMaterial = _fontMaterial;
            }

        EnsureRealBar(st);

        // fallback scroll bar: same sprites as the option menu (rounded thumb)
        var thumbImg = st.BarThumb.GetComponent<UnityEngine.UI.Image>();
        thumbImg.color = _barThumbColor;
        if (_barThumbSprite != null) { thumbImg.sprite = _barThumbSprite; thumbImg.type = UnityEngine.UI.Image.Type.Sliced; }
        var trackImg = st.BarTrack.GetComponent<UnityEngine.UI.Image>();
        trackImg.color = _barTrackColor;
        if (_barTrackSprite != null) { trackImg.sprite = _barTrackSprite; trackImg.type = UnityEngine.UI.Image.Type.Sliced; }
    }
}
