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
    private HousingItem? _chosen;
    private HousingItem[] _filtered = [];
    private string _filteredFor = "\0";
    private int _filteredWhere = -1;
    private int _filteredCount = -1;

    private float _yaw = 0.6f, _pitch = 0.35f, _zoom = 1f;

    private static readonly string[] WhereNames = ["Indoor and outdoor", "Indoor", "Outdoor"];

    public HousingPropsTab(HousingProps props, Selection selection)
    {
        _props = props;
        _selection = selection;
    }

    public void Draw()
    {
        var catalog = _props.Catalog;
        if(catalog == null)
        {
            ImGui.TextDisabled(_props.CatalogFailed ? "Couldn't read the housing items from the game data." : "Reading the housing items...");
            return;
        }

        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X * 0.6f);
        ImGui.InputTextWithHint("###housing_search", "Search housing items", ref _search, 64);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1);
        ImGui.Combo("###housing_where", ref _where, WhereNames, WhereNames.Length);

        if(_filteredFor != _search || _filteredWhere != _where || _filteredCount != catalog.Length)
        {
            _filteredFor = _search;
            _filteredWhere = _where;
            _filteredCount = catalog.Length;
            _filtered = catalog.Where(i => (_where == 0 || (_where == 1) == i.Indoors)
                && (_search.Length == 0 || i.Name.Contains(_search, StringComparison.OrdinalIgnoreCase))).ToArray();
        }

        float height = Math.Max(220f * ImGuiHelpers.GlobalScale, ImGui.GetContentRegionAvail().Y * 0.55f);
        float listWidth = ImGui.GetContentRegionAvail().X * 0.45f;
        using(var list = ImRaii.Child("###housing_list", new Vector2(listWidth, height), true))
        {
            if(list.Success)
                DrawList();
        }
        ImGui.SameLine();
        using(var preview = ImRaii.Child("###housing_preview", new Vector2(-1, height), true))
        {
            if(preview.Success)
                DrawPreview();
        }

        DrawPlaced();
    }

    private void DrawList()
    {
        int shown = 0;
        const int Limit = 600;
        foreach(var item in _filtered)
        {
            if(++shown > Limit)
            {
                ImGui.TextDisabled($"... and {_filtered.Length - Limit} more: search to narrow it down.");
                break;
            }
            string label = $"{item.Name}{(item.Indoors ? "" : "  (outdoor)")}###housing_{(item.Indoors ? "i" : "o")}{item.ModelKey}";
            if(ImGui.Selectable(label, _chosen == item))
                _chosen = item;
        }
        if(_filtered.Length == 0)
            ImGui.TextDisabled("Nothing matches.");
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
