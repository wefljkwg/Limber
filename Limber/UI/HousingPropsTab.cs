using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Limber.BodyRig;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Limber.UI;

/// <summary>
/// The "Props" tab: every housing item in the game (indoor furnishings and outdoor yard items) in a searchable list,
/// with a turnable preview of the selected one's model, to place in the scene (only on your screen).
/// </summary>
public sealed unsafe class HousingPropsTab
{
    private readonly HousingProps _props;
    private readonly Selection _selection;

    private string _search = string.Empty;
    private int _where; // 0 all, 1 indoor, 2 outdoor
    private bool _byType;  // world props: in sections by what they are, not where they're from
    private bool _filteredByType;
    private bool _favOnly, _filteredFav;
    private int _filteredFavCount;
    private string FavList => _world ? "world" : "housing";
    private readonly List<(string Section, int Start, int End)> _sections = new();

    private string SectionOf(HousingItem i)
    {
        string s = _world && _byType ? i.Kind : i.Category;
        return s.Length > 0 ? s : "Other";
    }
    private HousingItem? _chosen;
    private HousingItem[] _filtered = [];
    private string _filteredFor = "\0";
    private int _filteredWhere = -1;
    private int _filteredCount = -1;

    private float _yaw = 0.6f, _pitch = 0.35f, _zoom = 1f;

    private static readonly string[] WhereNames = ["Indoor and outdoor", "Indoor", "Outdoor"];

    private readonly bool _world;
    private string Id => _world ? "world" : "housing";

    /// <param name="world">The World props page (the game's shared groups) instead of the housing items.</param>
    public HousingPropsTab(HousingProps props, Selection selection, bool world = false)
    {
        _props = props;
        _selection = selection;
        _world = world;
    }

    public void Draw()
    {
        var catalog = _world ? _props.WorldCatalog : _props.Catalog;
        if(catalog == null)
        {
            bool failed = _world ? _props.WorldFailed : _props.CatalogFailed;
            ImGui.TextDisabled(failed ? $"Couldn't read the {(_world ? "world props" : "housing items")} from the game data." : $"Reading the {(_world ? "world props" : "housing items")}...");
            return;
        }
        ImGui.SetNextItemWidth(_world ? -1 : ImGui.GetContentRegionAvail().X * 0.6f);
        ImGui.InputTextWithHint($"###{Id}_search", _world ? $"Search {catalog.Length} world props" : "Search housing items", ref _search, 64);
        if(!_world)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(-1);
            ImGui.Combo("###housing_where", ref _where, WhereNames, WhereNames.Length);
        }
        else
        {
            if(ImGui.RadioButton("Sort by area###world_by_area", !_byType))
                _byType = false;
            ImGui.SameLine();
            if(ImGui.RadioButton("Sort by type###world_by_type", _byType))
                _byType = true;
            ImGui.SameLine();
        }
        ImGui.Checkbox($"Favorites only ({Favorites.Count(FavList)})###{Id}_favs", ref _favOnly);
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip("Click the star next to an item to add it to your favourites.");

        int favCount = Favorites.Count(FavList);
        if(_filteredFor != _search || _filteredWhere != _where || _filteredCount != catalog.Length || _filteredByType != _byType
           || _filteredFav != _favOnly || (_favOnly && _filteredFavCount != favCount))
        {
            _filteredFor = _search;
            _filteredWhere = _where;
            _filteredCount = catalog.Length;
            _filteredByType = _byType;
            _filteredFav = _favOnly;
            _filteredFavCount = favCount;
            _filtered = catalog.Where(i => (_world || _where == 0 || (_where == 1) == i.Indoors)
                && (!_favOnly || Favorites.Has(FavList, i.GroupPath))
                && (_search.Length == 0 || i.Name.Contains(_search, StringComparison.OrdinalIgnoreCase)
                    || i.Category.Contains(_search, StringComparison.OrdinalIgnoreCase) || i.Kind.Contains(_search, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(SectionOf, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            // where each section starts and ends (worked out once, not every frame)
            _sections.Clear();
            int start = 0;
            for(int k = 1; k <= _filtered.Length; k++)
                if(k == _filtered.Length || SectionOf(_filtered[k]) != SectionOf(_filtered[start]))
                {
                    _sections.Add((SectionOf(_filtered[start]), start, k));
                    start = k;
                }
        }

        float height = Math.Max(220f * ImGuiHelpers.GlobalScale, ImGui.GetContentRegionAvail().Y * 0.55f);
        float listWidth = ImGui.GetContentRegionAvail().X * 0.45f;
        using(var list = ImRaii.Child($"###{Id}_list", new Vector2(listWidth, height), true))
        {
            if(list.Success)
                DrawList();
        }
        ImGui.SameLine();
        using(var preview = ImRaii.Child($"###{Id}_preview", new Vector2(-1, height), true))
        {
            if(preview.Success)
                DrawPreview();
        }

        DrawPlaced();
    }

    // In sections by type (each item's category), open by themselves while searching.
    private void DrawList()
    {
        if(_filtered.Length == 0)
        {
            ImGui.TextDisabled("Nothing matches.");
            return;
        }
        bool searching = _search.Length > 0;
        const int Limit = 500;
        foreach(var (section, i, end) in _sections)
        {
            if(searching)
                ImGui.SetNextItemOpen(true, ImGuiCond.Always);
            bool open = ImGui.TreeNodeEx($"{section} ({end - i})###{Id}_sec_{section}", ImGuiTreeNodeFlags.SpanAvailWidth);
            if(open)
            {
                int shown = 0;
                for(int k = i; k < end; k++)
                {
                    if(++shown > Limit)
                    {
                        ImGui.TextDisabled($"... and {end - i - Limit} more: search to narrow it down.");
                        break;
                    }
                    var item = _filtered[k];
                    Favorites.Star(FavList, item.GroupPath, $"{Id}_{item.GroupPath}");
                    ImGui.SameLine();
                    string label = $"{item.Name}{(item.World || item.Indoors ? "" : "  (outdoor)")}###{Id}_{item.GroupPath}";
                    if(ImGui.Selectable(label, _chosen == item))
                        _chosen = item;
                }
                ImGui.TreePop();
            }
        }
    }

    private void DrawPreview()
    {
        var item = _chosen;
        if(item == null)
        {
            ImGui.TextDisabled("Pick an item to see it here.");
            return;
        }

        ImGui.TextUnformatted(item.Name);
        var actor = _selection.Actor;
        using(ImRaii.Disabled(!actor.IsSet))
        {
            if(ImGui.Button(actor.IsSet ? $"Place in front of {actor.Name}###housing_spawn" : "Choose a character to place it###housing_spawn"))
                SpawnInFront(item, actor);
        }

        var mesh = _props.Preview(item, out bool loading);
        var origin = ImGui.GetCursorScreenPos();
        var size = ImGui.GetContentRegionAvail();
        if(size.X < 20 || size.Y < 20)
            return;
        ImGui.InvisibleButton("###housing_preview_canvas", size);
        var io = ImGui.GetIO();
        if(ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left, 1f))
        {
            _yaw -= io.MouseDelta.X * 0.01f;
            _pitch = Math.Clamp(_pitch + io.MouseDelta.Y * 0.01f, -1.5f, 1.5f);
        }
        if(ImGui.IsItemHovered() && io.MouseWheel != 0)
            _zoom = Math.Clamp(_zoom * MathF.Pow(1.1f, io.MouseWheel), 0.2f, 8f);

        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(origin, origin + size, 0xFF1E1A18);
        if(mesh == null)
        {
            drawList.AddText(origin + new Vector2(8, 8), 0xFFA0A0A0, loading ? "Loading the model..." : "No model to show.");
            return;
        }

        // Turn, fit to the box, shade by facing, draw far to near.
        var centre = (mesh.Min + mesh.Max) * 0.5f;
        float radius = MathF.Max((mesh.Max - mesh.Min).Length() * 0.5f, 1e-3f);
        float pixels = MathF.Min(size.X, size.Y) * 0.45f / radius * _zoom;
        var view = Quaternion.Inverse(Quaternion.CreateFromYawPitchRoll(_yaw, _pitch, 0f));
        var screenCentre = origin + size * 0.5f;
        int n = mesh.Points.Length;
        var points = new Vector3[n];
        for(int i = 0; i < n; i++)
            points[i] = Vector3.Transform(mesh.Points[i] - centre, view);

        int count = mesh.Triangles.Length / 3;
        var order = new (float Depth, int Index)[count];
        for(int t = 0; t < count; t++)
        {
            var a = points[mesh.Triangles[t * 3]];
            var b = points[mesh.Triangles[t * 3 + 1]];
            var c = points[mesh.Triangles[t * 3 + 2]];
            order[t] = ((a.Z + b.Z + c.Z) / 3f, t);
        }
        Array.Sort(order, (x, y) => x.Depth.CompareTo(y.Depth)); // far (more negative z) first

        var light = Vector3.Normalize(new Vector3(0.4f, 0.6f, 0.7f));
        drawList.PushClipRect(origin, origin + size, true);
        foreach(var (_, t) in order)
        {
            var a = points[mesh.Triangles[t * 3]];
            var b = points[mesh.Triangles[t * 3 + 1]];
            var c = points[mesh.Triangles[t * 3 + 2]];
            var normal = Vector3.Cross(b - a, c - a);
            float length = normal.Length();
            if(length < 1e-12f)
                continue;
            normal /= length;
            float shade = 0.25f + 0.75f * MathF.Abs(Vector3.Dot(normal, light));
            byte v = (byte)Math.Clamp(shade * 230f, 0f, 255f);
            uint color = 0xFF000000u | ((uint)(v * 0.85f) << 16) | ((uint)(v * 0.92f) << 8) | v;
            Vector2 S(Vector3 p) => screenCentre + new Vector2(p.X, -p.Y) * pixels;
            drawList.AddTriangleFilled(S(a), S(b), S(c), color);
        }
        drawList.PopClipRect();
        drawList.AddText(origin + new Vector2(6, 4), 0xFFA0A0A0, "Drag: turn   Wheel: zoom");
    }

    private void SpawnInFront(HousingItem item, ActorRef actor)
    {
        var obj = Limber.Core.ActorSkeleton.GetObject(actor.ObjectIndex);
        if(obj == null || (nint)obj != actor.Address)
            return;
        var position = (Vector3)obj->Position;
        float yaw = obj->Rotation;
        var forward = new Vector3(MathF.Sin(yaw), 0f, MathF.Cos(yaw));
        _props.Spawn(item, position + forward * 1.0f, yaw * 180f / MathF.PI);
    }

    private void DrawPlaced()
    {
        var placed = _props.Spawned;
        ImGui.Spacing();
        if(!ImGui.CollapsingHeader($"Placed ({placed.Count})###housing_placed", ImGuiTreeNodeFlags.DefaultOpen))
            return;
        if(placed.Count == 0)
        {
            ImGui.TextDisabled("Nothing placed yet.");
            return;
        }
        if(ImGui.SmallButton("Remove all###housing_remove_all"))
            _props.RemoveAll();

        foreach(var prop in placed)
        {
            using var id = ImRaii.PushId(prop.Id.ToString());
            if(!ImGui.TreeNode($"{prop.Item.Name}###node"))
                continue;

            bool visible = prop.Visible;
            if(ImGui.Checkbox("Visible###visible", ref visible))
            {
                prop.Visible = visible;
                HousingProps.MarkChanged(prop);
            }
            ImGui.SameLine();
            var actor = _selection.Actor;
            using(ImRaii.Disabled(!actor.IsSet))
            {
                if(ImGui.SmallButton("Move to character###to_actor") && actor.IsSet)
                {
                    var obj = Limber.Core.ActorSkeleton.GetObject(actor.ObjectIndex);
                    if(obj != null && (nint)obj == actor.Address)
                    {
                        prop.Position = obj->Position;
                        HousingProps.MarkChanged(prop);
                    }
                }
            }
            ImGui.SameLine();
            if(ImGui.SmallButton("Delete###delete"))
                _props.Remove(prop.Id);

            var position = prop.Position;
            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X * 0.7f);
            if(ImGui.DragFloat3("Position###position", ref position, 0.01f, -10000f, 10000f, "%.2f"))
            {
                prop.Position = position;
                HousingProps.MarkChanged(prop);
            }
            var turn = prop.Turn;
            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X * 0.7f);
            if(ImGui.DragFloat3("Turn (°)###turn", ref turn, 0.5f, -360f, 360f, "%.1f"))
            {
                prop.Turn = turn;
                HousingProps.MarkChanged(prop);
            }
            float scale = prop.Scale;
            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X * 0.7f);
            if(ImGui.DragFloat("Scale###scale", ref scale, 0.005f, 0.01f, 100f, "%.3f"))
            {
                prop.Scale = scale;
                HousingProps.MarkChanged(prop);
            }
            ImGui.TreePop();
        }
    }
}
