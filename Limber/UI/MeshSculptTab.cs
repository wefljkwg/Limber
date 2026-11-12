using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Limber.Core;
using Limber.Gpu;
using Limber.Rig;

namespace Limber.UI;

/// <summary>
/// The Mesh morphing page (experimental): reshape the face's own mesh with the mouse, right on the face or on a map
/// (its own window: the face drawn shaded, turned and zoomed as you like). Brushes (Grab, Push / pull, Pinch, Smooth)
/// work wherever the mouse is, with a soft falloff measured along the skin; or select points and drag them.
/// The edits ride along with the face's bones, and Oral's lips wrap first with the edits on top.
/// </summary>
public sealed class MeshSculptTab
{
    private enum Tool { Move, Push, Pinch, Smooth }

    private readonly Selection _selection;
    private int _lastDrawn = -10;
    private bool _overUi;

    // the face
    private MeshSculpt.Model? _model;
    private string _modelFor = "";
    private string _note = "";
    private long _retryAt;
    private string _editsFor = "";

    // the tool
    private bool _sculpting = true;
    private bool _brushMode = true;   // brushes (anywhere on the face) or points picked one by one
    private float _smooth = 0.5f;     // the Smooth brush's strength
    private bool _smoothing;          // the Smooth brush held down (on the face)
    private bool _overMap;            // the mouse is over the map (last frame)

    // the map (its own window)
    private bool _mapOpen;
    private float _mapYaw, _mapPitch, _mapZoom; // (zoom 0: fit the face)
    private Vector2 _mapPan;
    private bool _mapSmoothing;
    private Vector2? _mapBox;
    private int _mapPaint;
    private Vector3[] _mapView = [];
    private Vector2[] _mapScreen = [];
    private bool[] _mapVisible = [];
    private readonly List<(float Depth, int Tri, Vector3 Normal)> _mapTris = new();

    private sealed class MapDrag
    {
        public required Vector2 Start;
        public required Tool Tool;
        public required Dictionary<int, Vector3> Base;
        public required Dictionary<int, float> Weights;
        public required List<(int Mirror, int From)> Mirrors;
        public required Quaternion ToRest;
        public required float Zoom;
        public Vector3 Centre;
    }
    private MapDrag? _mapDrag;
    private Tool _tool = Tool.Move;
    private float _radius = 0.008f;   // falloff along the skin (model metres)
    private float _brush = 16f;       // paint brush (pixels)
    private bool _mirror = true;
    private bool _showDots;
    private bool _hideInner = true;
    private bool _confirmReset;

    // selection and edits
    private readonly HashSet<int> _selected = new();
    private int _selectionVersion;
    private Dictionary<int, float> _preview = new();
    private (int Version, float Radius, bool Mirror) _previewFor = (-1, 0f, false);
    private readonly List<Dictionary<int, Vector3>> _undo = new();
    private long _lastWheel;
    private bool _dirty;
    private long _saveAt;

    // this frame (the scene)
    private MeshSculpt.Posed[] _posed = [];
    private Vector2[] _screen = [];
    private float[] _depth = [];
    private bool[] _visible = [];
    private bool _wantInput;

    private sealed class Drag
    {
        public required Vector2 Start;
        public required Tool Tool;
        public required Dictionary<int, Vector3> Base;
        public required Dictionary<int, float> Weights;
        public required List<(int Mirror, int From)> Mirrors;
        public required Dictionary<int, MeshSculpt.Posed> At;   // where each moving point was at the start
        public required Matrix4x4 WorldToModel;
        public Vector3 Centroid, Right, Up;
        public float InvA, InvB, InvC, InvD;  // pixels -> centimetres along Right / Up
        public float MetresPerPixel;
    }

    private Drag? _drag;
    private Vector2? _boxStart;
    private int _painting; // +1 adding, -1 removing, 0 not painting

    public MeshSculptTab(Selection selection)
    {
        _selection = selection;
    }

    public bool Showing => ImGui.GetFrameCount() - _lastDrawn <= 1;

    private static void Tip(string text)
    {
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip(text);
    }

    // ---- The face model ------------------------------------------------------------------------------------------------

    private unsafe MeshSculpt.Model? ModelOf(ActorRef actor)
    {
        var obj = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)actor.Address;
        if(obj == null || obj->DrawObject == null)
        {
            _note = "The character isn't drawn.";
            return null;
        }
        string file = "";
        foreach(var (_, f) in LiveMesh.ModelFiles((nint)obj->DrawObject))
            if(LipMesh.IsFace(f))
                file = f;
        if(file.Length == 0)
        {
            _note = "Couldn't find the character's face model.";
            return null;
        }
        string key = actor.Address + "|" + file;
        if(key == _modelFor && _model != null)
            return _model;
        if(key == _modelFor && _model == null && Environment.TickCount64 < _retryAt)
            return null;
        _retryAt = Environment.TickCount64 + 3000;
        _modelFor = key;
        try
        {
            var data = LimberTab.ReadModelFile(file);
            _model = data == null ? null : MeshSculpt.Prepare(file, data, 0);
            _note = _model == null ? "Couldn't read the face model." : $"{_model.Points.Count(p => p.Shown)} points on the face.";
        }
        catch(Exception e)
        {
            _model = null;
            _note = "Couldn't read the face: " + e.Message;
        }
        return _model;
    }

    // ---- The page ------------------------------------------------------------------------------------------------------

    public void Draw()
    {
        _lastDrawn = ImGui.GetFrameCount();
        _overUi = ImGui.IsWindowHovered(ImGuiHoveredFlags.RootAndChildWindows | ImGuiHoveredFlags.AllowWhenBlockedByActiveItem
                                        | ImGuiHoveredFlags.AllowWhenBlockedByPopup);
        ImGui.TextColored(new Vector4(1f, 0.75f, 0.3f, 1f), "Experimental.");
        ImGui.SameLine();
        ImGui.TextWrapped("Reshape the face's own mesh with the mouse, right on the face or on the map (expressions, face shape...). "
            + "The edits follow the face's bones and are added on top of the face morphs (and Oral's lips). Only on your screen.");
        var actor = _selection.Actor;
        if(!actor.IsSet)
        {
            ImGui.TextDisabled("Choose a character at the top of the window.");
            return;
        }
        string name = actor.Name ?? "";
        if(name.Length == 0)
         {
            ImGui.TextDisabled("This character has no name to keep its edits under.");
            return;
        }
        var look = Appearance.Read(actor.Address, actor.ObjectIndex);
        if(look == null)
            return;
        // (any character, for expressions; Oral's lips use the edits only on adult bodies, not Lalafell)
        bool oralOk = look.Customize[0] != 3 && look.Customize[2] != 4;
        var model = ModelOf(actor);
        MeshSculpt.Use(actor.Address, name, model, oralOk);
        if(model == null)
        {
            ImGui.TextDisabled(_note);
            return;
        }
        string editsFor = name + "|" + model.Key;
        if(editsFor != _editsFor)
        {
            _editsFor = editsFor;
            _selected.Clear();
            _selectionVersion++;
            _undo.Clear();
            _drag = null;
        }
        var sculpt = MeshSculpt.Edits;
        if(sculpt == null)
            return;

        bool on = sculpt.On;
        if(ImGui.Checkbox($"Edits on {name}'s face###ms_on", ref on))
        {
            sculpt.On = on;
            MeshSculpt.Changed();
            MeshSculpt.Save();
        }
        Tip("Off: the face as it is (the edits are kept). One face at a time: the character chosen here.");
        ImGui.SameLine();
        ImGui.Checkbox("Use the mouse on the face###ms_tool", ref _sculpting);
        Tip("Brushes and points right on the character's face in the scene (only while this page is open).");
        ImGui.SameLine();
        if(ImGui.Button(_mapOpen ? "Close the map###ms_map" : "Open the map###ms_map"))
            _mapOpen = !_mapOpen;
        Tip("The face in a window of its own, drawn shaded so it's easy to see: turn it (right-drag), zoom (wheel),\n"
            + "move it (middle-drag), and use the same brushes and points there.");

        ImGui.Spacing();
        DrawTools();

        float cm = _radius * 100f;
        ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);
        if(ImGui.SliderFloat(_brushMode ? "Brush size###ms_radius" : "Soft falloff###ms_radius", ref cm, 0.1f, 5f, "%.1f cm"))
            _radius = cm / 100f;
        Tip(_brushMode ? "How far round the mouse the brush reaches, fading out (measured along the skin, so the upper lip doesn't pull the lower one)."
            : "How far around the selection the skin moves with it, fading out (measured along the skin).");
        if(_tool == Tool.Smooth)
        {
            ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);
            ImGui.SliderFloat("Smooth strength###ms_smooth", ref _smooth, 0.05f, 1f, "%.2f");
        }
        if(!_brushMode)
        {
            ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);
            ImGui.SliderFloat("Paint size###ms_brush", ref _brush, 4f, 80f, "%.0f px");
            Tip("The paint brush's size for picking points (Shift or Alt + drag).");
        }
        ImGui.Checkbox("Mirror left / right###ms_mirror", ref _mirror);
        Tip("Edits on one side of the face are made on the other side too.");
        ImGui.SameLine();
        ImGui.Checkbox(_brushMode ? "Show the points###ms_dots" : "Show all points###ms_dots", ref _showDots);
        ImGui.SameLine();
        ImGui.Checkbox("Hide eyes, teeth, tongue###ms_inner", ref _hideInner);

        ImGui.Spacing();
        if(ImGui.Button("Clear selection###ms_clear"))
        {
            _selected.Clear();
            _selectionVersion++;
        }
        ImGui.SameLine();
        if(ImGui.Button("Grow###ms_grow"))
            Grow(model, 1);
        Tip("Add the points next to the selection.");
        ImGui.SameLine();
        if(ImGui.Button("Shrink###ms_shrink"))
            Grow(model, -1);
        Tip("Take the selection's edge points off it.");
        ImGui.SameLine();
        if(ImGui.Button("Select mirror###ms_selmirror"))
        {
            foreach(int p in _selected.ToList())
                if(model.Points[p].Mirror >= 0)
                    _selected.Add(model.Points[p].Mirror);
            _selectionVersion++;
        }
        Tip("Add the points across the face from the selected ones.");

        using(ImRaii.Disabled(_undo.Count == 0))
            if(ImGui.Button($"Undo ({_undo.Count})###ms_undo"))
            {
                sculpt.Edits = _undo[^1];
                _undo.RemoveAt(_undo.Count - 1);
                Edited();
            }
        ImGui.SameLine();
        using(ImRaii.Disabled(_selected.Count == 0))
            if(ImGui.Button("Reset selected###ms_resetsel"))
            {
                Remember(sculpt);
                var weights = Weights(model);
                foreach(var (p, w) in weights)
                    if(sculpt.Edits.TryGetValue(p, out var e))
                    {
                        var left = e * (1f - w);
                        if(left.LengthSquared() < 1e-14f)
                            sculpt.Edits.Remove(p);
                        else
                            sculpt.Edits[p] = left;
                    }
                Edited();
            }
        Tip("The selection back to the face as it was (the soft falloff round it eases back too).");
        ImGui.SameLine();
        using(ImRaii.Disabled(sculpt.Edits.Count == 0))
            if(ImGui.Button("Reset all###ms_resetall"))
                _confirmReset = true;
        if(_confirmReset)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1f, 0.6f, 0.4f, 1f), "Undo every edit on this face?");
            ImGui.SameLine();
            if(ImGui.SmallButton("Yes###ms_resetyes"))
            {
                Remember(sculpt);
                sculpt.Edits.Clear();
                Edited();
                _confirmReset = false;
            }
            ImGui.SameLine();
            if(ImGui.SmallButton("No###ms_resetno"))
                _confirmReset = false;
        }

        ImGui.Spacing();
        ImGui.TextDisabled($"{_note} {_selected.Count} selected, {sculpt.Edits.Count} edited (saved by themselves).");
        if(sculpt.Edits.Count > 0 && sculpt.On)
        {
            string through = LipMesh.Running && LipMesh.Owner == actor.Address
                ? (oralOk ? "Shown through Oral's lips." : "Not shown while Oral has this face.")
                : FaceRig.Running && FaceRig.Owner == actor.Address ? "Shown through the face rig."
                : "Starting the face rig to show them... " + FaceRig.Status;
            ImGui.TextDisabled(through + (MeshSculpt.Applied.Length > 0 ? $" ({MeshSculpt.Applied})" : ""));
        }
        ImGui.Spacing();
        ImGui.TextDisabled(_brushMode
            ? "On the face or the map: drag anywhere with the brush (the orange shows what it reaches).\n"
              + "  Grab moves the skin with the mouse, Push / pull: drag up to pull out, down to push in,\n"
              + "  Pinch: drag right to pinch toward the middle, left to spread, Smooth: hold and move over rough spots.\n"
              + "  Shift + mouse wheel: push in / pull out where the brush is."
            : "On the face or the map:\n"
              + "  Click a point: select it.   Drag a selected point: Grab / Push / Pinch the selection.\n"
              + "  Shift + drag: paint points on.   Alt + drag: paint them off.   Ctrl + drag: box select.\n"
              + "  Shift + mouse wheel: push the selection in or pull it out.");

        if(_dirty && Environment.TickCount64 >= _saveAt && _drag == null)
        {
            _dirty = false;
            MeshSculpt.Save();
        }
    }

    private void DrawTools()
    {
        ImGui.TextUnformatted("Use:");
        ImGui.SameLine();
        if(ImGui.RadioButton("Brushes###ms_mode_brush", _brushMode))
            _brushMode = true;
        Tip("Drag anywhere on the face: the brush reshapes the skin round the mouse (no points to hit).");
        ImGui.SameLine();
        if(ImGui.RadioButton("Points###ms_mode_points", !_brushMode))
        {
            _brushMode = false;
            if(_tool == Tool.Smooth)
                _tool = Tool.Move;
        }
        Tip("Pick exactly which points move (click, paint, box), then drag them.");
        ImGui.TextUnformatted("Tool:");
        ImGui.SameLine();
        if(ImGui.RadioButton("Grab###ms_move", _tool == Tool.Move)) _tool = Tool.Move;
        Tip("The skin moves with the mouse, across the screen.");
        ImGui.SameLine();
        if(ImGui.RadioButton("Push / pull###ms_push", _tool == Tool.Push)) _tool = Tool.Push;
        Tip("Drag up to pull the skin out, down to push it in (each point along its own normal).");
        ImGui.SameLine();
        if(ImGui.RadioButton("Pinch###ms_pinch", _tool == Tool.Pinch)) _tool = Tool.Pinch;
        Tip("Drag right to pinch the skin toward the middle, left to spread it apart.");
        if(_brushMode)
        {
            ImGui.SameLine();
            if(ImGui.RadioButton("Smooth###ms_smoothtool", _tool == Tool.Smooth)) _tool = Tool.Smooth;
            Tip("Hold and move over a spot: the edits there even out (bumps and creases from other brushes smooth away).");
        }
    }

    // The Smooth brush: each edit eased toward its neighbours' (the edits even out; the face's own shape stays).
    private void SmoothAt(MeshSculpt.Model model, MeshSculpt.Sculpt sculpt, Dictionary<int, float> weights, float dt)
    {
        float amount = Math.Clamp(_smooth * dt * 12f, 0f, 1f);
        if(amount <= 0f)
            return;
        var now = sculpt.Edits;
        var edits = new Dictionary<int, Vector3>(now);
        foreach(var (p, w) in weights)
        {
            var near = model.Points[p].Neighbours;
            if(near.Length == 0)
                continue;
            var avg = Vector3.Zero;
            foreach(int q in near)
                avg += now.GetValueOrDefault(q);
            avg /= near.Length;
            var e = now.GetValueOrDefault(p);
            var after = e + (avg - e) * (amount * w);
            Put(edits, p, after);
            int m = model.Points[p].Mirror;
            if(_mirror && m >= 0 && !weights.ContainsKey(m))
                Put(edits, m, new Vector3(-after.X, after.Y, after.Z));
        }
        sculpt.Edits = edits;
        MeshSculpt.Changed();
    }

    private static void Put(Dictionary<int, Vector3> edits, int p, Vector3 e)
    {
        if(!MathUtil.IsFinite(e) || e.LengthSquared() < 1e-14f)
            edits.Remove(p);
        else
            edits[p] = e;
    }

    private void Remember(MeshSculpt.Sculpt sculpt)
    {
        _undo.Add(new Dictionary<int, Vector3>(sculpt.Edits));
        if(_undo.Count > 40)
            _undo.RemoveAt(0);
    }

    private void Edited()
    {
        MeshSculpt.Changed();
        _dirty = true;
        _saveAt = Environment.TickCount64 + 800;
    }

    private void Grow(MeshSculpt.Model model, int way)
    {
        if(way > 0)
        {
            foreach(int p in _selected.ToList())
                foreach(int q in model.Points[p].Neighbours)
                    if(model.Points[q].Shown)
                        _selected.Add(q);
        }
        else
        {
            var edge = _selected.Where(p => model.Points[p].Neighbours.Any(q => !_selected.Contains(q))).ToList();
            foreach(int p in edge)
                _selected.Remove(p);
        }
        _selectionVersion++;
    }

    // The selection with its soft falloff (and its mirror, when on): how much each point moves.
    private Dictionary<int, float> Weights(MeshSculpt.Model model)
    {
        var key = (_selectionVersion, _radius, _mirror);
        if(key == _previewFor)
            return _preview;
        var w = MeshSculpt.Falloff(model, _selected, _radius);
        _preview = w;
        _previewFor = key;
        return w;
    }

    private List<(int Mirror, int From)> MirrorsOf(MeshSculpt.Model model, Dictionary<int, float> weights)
    {
        var list = new List<(int, int)>();
        if(!_mirror)
            return list;
        foreach(var p in weights.Keys)
        {
            int m = model.Points[p].Mirror;
            if(m >= 0 && !weights.ContainsKey(m))
                list.Add((m, p));
        }
        return list;
    }

    // ---- On the face (every frame, with the overlays) ------------------------------------------------------------------

    public void DrawScene()
    {
        DrawOnFace();
        DrawMap();
        if(_dirty && Environment.TickCount64 >= _saveAt && _drag == null && _mapDrag == null && !_smoothing && !_mapSmoothing)
        {
            _dirty = false;
            MeshSculpt.Save();
        }
    }

    private void DrawOnFace()
    {
        var actor = _selection.Actor;
        var model = MeshSculpt.Current;
        var sculpt = MeshSculpt.Edits;
        if(!Showing || !_sculpting || !actor.IsSet || model == null || sculpt == null || MeshSculpt.Owner != actor.Address)
        {
            _drag = null;
            _boxStart = null;
            _painting = 0;
            _smoothing = false;
            _wantInput = false;
            return;
        }
        if(!MeshSculpt.Pose(actor.Address, actor.ObjectIndex, model, sculpt, ref _posed, out var sk))
        {
            _wantInput = false;
            return;
        }
        var gui = Plugin.GameGui;
        int n = model.Points.Length;
        if(_screen.Length != n)
        {
            _screen = new Vector2[n];
            _depth = new float[n];
            _visible = new bool[n];
        }
        var camera = Vector3.Zero;
        bool haveCamera = CameraUtil.TryGetMatrices(out var view, out _) && Matrix4x4.Invert(view, out var cameraToWorld)
                          && (camera = new Vector3(cameraToWorld.M41, cameraToWorld.M42, cameraToWorld.M43)) != Vector3.Zero;
        var lo = new Vector2(float.MaxValue);
        var hi = new Vector2(float.MinValue);
        for(int i = 0; i < n; i++)
        {
            var pt = model.Points[i];
            _visible[i] = false;
            if(!pt.Shown || (_hideInner && pt.Inner))
                continue;
            var at = _posed[i].World;
            if(haveCamera && Vector3.Dot(_posed[i].Normal, camera - at) <= 0f)
                continue;
            if(!gui.WorldToScreen(at, out var s, out bool inView) || !inView)
                continue;
            _screen[i] = s;
            _depth[i] = haveCamera ? Vector3.Distance(camera, at) : 0f;
            _visible[i] = true;
            lo = Vector2.Min(lo, s);
            hi = Vector2.Max(hi, s);
        }

        var io = ImGui.GetIO();
        var mouse = ImGui.GetMousePos();
        float scale = ImGuiHelpers.GlobalScale;
        bool overFace = lo.X <= hi.X && mouse.X >= lo.X - 20f && mouse.X <= hi.X + 20f && mouse.Y >= lo.Y - 20f && mouse.Y <= hi.Y + 20f;

        // The point under the mouse: the nearest to the camera of those close to it (points), or simply the nearest
        // to the mouse (brushes: they reach round it anyway, so there's no tiny dot to hit).
        int hovered = -1;
        bool overUi = _overUi || _overMap;
        if(_brushMode)
        {
            float best = 40f * scale;
            for(int i = 0; i < n; i++)
                if(_visible[i])
                {
                    float dd = Vector2.Distance(_screen[i], mouse) + _depth[i] * 2f; // (a little toward the front)
                    if(dd < best)
                    {
                        best = dd;
                        hovered = i;
                    }
                }
        }
        else
        {
            float hoverR = 7f * scale, bestDepth = float.MaxValue;
            for(int i = 0; i < n; i++)
                if(_visible[i] && Vector2.Distance(_screen[i], mouse) <= hoverR && _depth[i] < bestDepth)
                {
                    bestDepth = _depth[i];
                    hovered = i;
                }
        }
        if(overUi && _drag == null && !_smoothing)
            hovered = -1;

        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.Pos);
        ImGui.SetNextWindowSize(viewport.Size);
        var flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoBringToFrontOnFocus
            | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoDocking;
        if(!_wantInput)
            flags |= ImGuiWindowFlags.NoInputs;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        try
        {
            if(!ImGui.Begin("##limber_sculpt_overlay", flags))
                return;
            var draw = ImGui.GetWindowDrawList();
            bool free = !overUi && _drag == null && _boxStart == null && _painting == 0 && !_smoothing;

            // Brushes: the brush where the mouse is.
            if(_brushMode && free && hovered >= 0 && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                if(_tool == Tool.Smooth)
                {
                    Remember(sculpt);
                    _smoothing = true;
                }
                else
                    StartDrag(model, sculpt, sk, mouse, [hovered]);
            }
            if(_smoothing)
            {
                if(ImGui.IsMouseDown(ImGuiMouseButton.Left))
                {
                    if(hovered >= 0)
                        SmoothAt(model, sculpt, MeshSculpt.Falloff(model, [hovered], _radius), io.DeltaTime);
                }
                else
                {
                    _smoothing = false;
                    _dirty = true;
                    _saveAt = Environment.TickCount64;
                }
            }

            // Start: box, paint, or a drag on the selection.
            if(!_brushMode && free && ImGui.IsMouseClicked(ImGuiMouseButton.Left) && (overFace || hovered >= 0))
            {
                if(io.KeyCtrl)
                    _boxStart = mouse;
                else if(io.KeyShift || io.KeyAlt)
                    _painting = io.KeyAlt ? -1 : 1;
                else if(hovered >= 0)
                {
                    if(!_selected.Contains(hovered))
                    {
                        _selected.Clear();
                        _selected.Add(hovered);
                        _selectionVersion++;
                    }
                    StartDrag(model, sculpt, sk, mouse, _selected);
                }
            }

            // Paint.
            if(_painting != 0)
            {
                if(ImGui.IsMouseDown(ImGuiMouseButton.Left))
                {
                    float r = _brush * scale;
                    bool changed = false;
                    for(int i = 0; i < n; i++)
                        if(_visible[i] && Vector2.Distance(_screen[i], mouse) <= r)
                            changed |= _painting > 0 ? _selected.Add(i) : _selected.Remove(i);
                    if(changed)
                        _selectionVersion++;
                }
                else
                    _painting = 0;
            }

            // Box.
            if(_boxStart is { } b0)
            {
                var a = Vector2.Min(b0, mouse);
                var c = Vector2.Max(b0, mouse);
                draw.AddRectFilled(a, c, ImGui.GetColorU32(new Vector4(1f, 0.9f, 0.4f, 0.08f)));
                uint edge = ImGui.GetColorU32(new Vector4(1f, 0.9f, 0.4f, 0.8f));
                draw.AddLine(a, new Vector2(c.X, a.Y), edge, 1.5f);
                draw.AddLine(new Vector2(c.X, a.Y), c, edge, 1.5f);
                draw.AddLine(c, new Vector2(a.X, c.Y), edge, 1.5f);
                draw.AddLine(new Vector2(a.X, c.Y), a, edge, 1.5f);
                if(!ImGui.IsMouseDown(ImGuiMouseButton.Left))
                {
                    for(int i = 0; i < n; i++)
                        if(_visible[i] && _screen[i].X >= a.X && _screen[i].X <= c.X && _screen[i].Y >= a.Y && _screen[i].Y <= c.Y)
                            _selected.Add(i);
                    _selectionVersion++;
                    _boxStart = null;
                }
            }

            // Drag.
            if(_drag is { } drag)
            {
                if(ImGui.IsMouseDown(ImGuiMouseButton.Left))
                    UpdateDrag(drag, sculpt, mouse - drag.Start);
                else
                {
                    _drag = null;
                    _dirty = true;
                    _saveAt = Environment.TickCount64;
                }
            }

            // Shift + wheel: push in / pull out (the selection, or where the brush is).
            bool wheel = !overUi && _drag == null && io.MouseWheel != 0f && io.KeyShift
                         && (_brushMode ? hovered >= 0 : _selected.Count > 0 && (overFace || hovered >= 0));
            if(wheel)
            {
                long now = Environment.TickCount64;
                if(now - _lastWheel > 600)
                    Remember(sculpt);
                _lastWheel = now;
                var weights = _brushMode ? MeshSculpt.Falloff(model, [hovered], _radius) : Weights(model);
                var w2m = sk.WorldToModel;
                float step = io.MouseWheel * 0.0005f;
                foreach(var (p, w) in weights)
                {
                    var rest = Vector3.TransformNormal(Vector3.TransformNormal(_posed[p].Normal * (step * w), w2m), _posed[p].ToRest);
                    Add(sculpt, p, rest);
                    int m = model.Points[p].Mirror;
                    if(_mirror && m >= 0 && !weights.ContainsKey(m))
                        Add(sculpt, m, new Vector3(-rest.X, rest.Y, rest.Z));
                }
                Edited();
            }

            // The points (brushes: what the brush reaches, in orange).
            var weightsNow = _brushMode ? (hovered >= 0 ? MeshSculpt.Falloff(model, [hovered], _radius) : new Dictionary<int, float>())
                : _selected.Count > 0 ? Weights(model) : _preview;
            bool showWeights = _brushMode ? weightsNow.Count > 0 : _selected.Count > 0;
            uint dot = ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.28f));
            uint ring = ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.6f));
            float d = 1.1f * scale;
            for(int i = 0; i < n; i++)
            {
                if(!_visible[i])
                    continue;
                var s = _screen[i];
                if(_selected.Contains(i))
                {
                    draw.AddCircleFilled(s, 3f * scale, ImGui.GetColorU32(new Vector4(1f, 0.92f, 0.3f, 1f)));
                    draw.AddCircle(s, 3f * scale, ring, 0, 1f);
                }
                else if(showWeights && weightsNow.TryGetValue(i, out float w))
                    draw.AddCircleFilled(s, 2.2f * scale, ImGui.GetColorU32(new Vector4(1f, 0.55f, 0.15f, 0.25f + 0.65f * w)));
                else if(_showDots)
                    draw.AddRectFilled(s - new Vector2(d), s + new Vector2(d), dot);
            }
            if(hovered >= 0 && _drag == null && !_brushMode)
                draw.AddCircle(_screen[hovered], 5.5f * scale, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.95f)), 0, 1.5f);
            if(_brushMode && hovered >= 0 && _drag == null && CameraUtil.TryGetAxes(out var camRight, out _)
               && gui.WorldToScreen(_posed[hovered].World + camRight * (_radius * MathF.Max(0.1f, sk.RootScale.Y)), out var edgeAt, out _))
            {
                float pr = MathF.Max(4f, Vector2.Distance(edgeAt, _screen[hovered]));
                draw.AddCircle(_screen[hovered], pr, ImGui.GetColorU32(new Vector4(1f, 0.85f, 0.4f, 0.9f)), 0, 1.5f);
                draw.AddCircleFilled(_screen[hovered], 2.5f * scale, ImGui.GetColorU32(new Vector4(1f, 0.85f, 0.4f, 1f)));
            }
            if(!_brushMode && !overUi && (io.KeyShift || io.KeyAlt) && !io.KeyCtrl && (overFace || _painting != 0))
                draw.AddCircle(mouse, _brush * scale, ImGui.GetColorU32(io.KeyAlt ? new Vector4(1f, 0.4f, 0.4f, 0.8f) : new Vector4(0.5f, 0.9f, 1f, 0.8f)), 0, 1.5f);

            _wantInput = _drag != null || _smoothing || (!overUi && (_brushMode
                ? hovered >= 0
                : hovered >= 0 || _boxStart != null || _painting != 0 || ((io.KeyShift || io.KeyAlt || io.KeyCtrl) && overFace)));
        }
        finally
        {
            ImGui.End();
            ImGui.PopStyleVar(2);
        }
        if(_wantInput)
            ImGui.SetNextFrameWantCaptureMouse(true);
    }

    private static void Add(MeshSculpt.Sculpt sculpt, int p, Vector3 rest)
    {
        if(!MathUtil.IsFinite(rest))
            return;
        var e = sculpt.Edits.GetValueOrDefault(p) + rest;
        if(e.LengthSquared() < 1e-14f)
            sculpt.Edits.Remove(p);
        else
            sculpt.Edits[p] = e;
    }

    private void StartDrag(MeshSculpt.Model model, MeshSculpt.Sculpt sculpt, in ActorSkeleton sk, Vector2 mouse, IReadOnlyCollection<int> seeds)
    {
        if(seeds.Count == 0)
            return;
        var weights = ReferenceEquals(seeds, _selected) ? Weights(model) : MeshSculpt.Falloff(model, seeds, _radius);
        var mirrors = MirrorsOf(model, weights);
        var at = new Dictionary<int, MeshSculpt.Posed>();
        foreach(var p in weights.Keys)
            at[p] = _posed[p];
        foreach(var (m, _) in mirrors)
            at[m] = _posed[m];
        var centroid = Vector3.Zero;
        foreach(int p in seeds)
            centroid += _posed[p].World;
        centroid /= seeds.Count;
        if(!CameraUtil.TryGetAxes(out var right, out var up))
            return;
        var gui = Plugin.GameGui;
        if(!gui.WorldToScreen(centroid, out var o0, out _) || !gui.WorldToScreen(centroid + right * 0.01f, out var oa, out _)
           || !gui.WorldToScreen(centroid + up * 0.01f, out var ou, out _))
            return;
        var ax = oa - o0;
        var uy = ou - o0;
        float det = ax.X * uy.Y - ax.Y * uy.X;
        if(MathF.Abs(det) < 1e-6f || ax.Length() < 1e-3f)
            return;
        Remember(sculpt);
        _drag = new Drag
        {
            Start = mouse,
            Tool = _tool,
            Base = new Dictionary<int, Vector3>(sculpt.Edits),
            Weights = weights,
            Mirrors = mirrors,
            At = at,
            WorldToModel = sk.WorldToModel,
            Centroid = centroid,
            Right = right,
            Up = up,
            InvA = uy.Y / det, InvB = -uy.X / det, InvC = -ax.Y / det, InvD = ax.X / det,
            MetresPerPixel = 0.01f / ax.Length(),
        };
    }

    private void UpdateDrag(Drag drag, MeshSculpt.Sculpt sculpt, Vector2 d)
    {
        var edits = new Dictionary<int, Vector3>(drag.Base);
        var moved = new Dictionary<int, Vector3>();
        var across = (drag.Right * (drag.InvA * d.X + drag.InvB * d.Y) + drag.Up * (drag.InvC * d.X + drag.InvD * d.Y)) * 0.01f;
        float push = -d.Y * drag.MetresPerPixel;
        float pinch = Math.Clamp(d.X * 0.004f, -1.5f, 0.95f);
        foreach(var (p, w) in drag.Weights)
        {
            var at = drag.At[p];
            var world = drag.Tool switch
            {
                Tool.Push => at.Normal * (push * w),
                Tool.Pinch => (drag.Centroid - at.World) * (pinch * w),
                _ => across * w,
            };
            var rest = Vector3.TransformNormal(Vector3.TransformNormal(world, drag.WorldToModel), at.ToRest);
            if(!MathUtil.IsFinite(rest))
                continue;
            moved[p] = rest;
        }
        foreach(var (m, from) in drag.Mirrors)
            if(moved.TryGetValue(from, out var r))
                moved[m] = new Vector3(-r.X, r.Y, r.Z);
        foreach(var (p, r) in moved)
        {
            var e = drag.Base.GetValueOrDefault(p) + r;
            if(e.LengthSquared() < 1e-14f)
                edits.Remove(p);
            else
                edits[p] = e;
        }
        sculpt.Edits = edits;
        MeshSculpt.Changed();
    }

    // ---- The map: the face in a window of its own, drawn shaded (turn: right-drag, zoom: wheel, move: middle-drag) ---

    private void DrawMap()
    {
        if(!_mapOpen)
        {
            _overMap = false;
            _mapDrag = null;
            _mapSmoothing = false;
            return;
        }
        float scale = ImGuiHelpers.GlobalScale;
        ImGui.SetNextWindowSize(new Vector2(560f, 620f) * scale, ImGuiCond.FirstUseEver);
        bool open = true;
        bool visible = ImGui.Begin("Mesh morphing map###limber_morph_map", ref open, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        try
        {
            _overMap = visible && ImGui.IsWindowHovered(ImGuiHoveredFlags.RootAndChildWindows | ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
            if(!visible)
                return;
            var model = MeshSculpt.Current;
            var sculpt = MeshSculpt.Edits;
            if(model == null || sculpt == null || model.PointTriangles.Length == 0)
            {
                ImGui.TextDisabled("Open Mesh morphing (Posing) with a character chosen first.");
                return;
            }
            DrawTools();
            float cm = _radius * 100f;
            ImGui.SetNextItemWidth(160 * scale);
            if(ImGui.SliderFloat(_brushMode ? "Brush size###msm_radius" : "Falloff###msm_radius", ref cm, 0.1f, 5f, "%.1f cm"))
                _radius = cm / 100f;
            ImGui.SameLine();
            ImGui.Checkbox("Mirror###msm_mirror", ref _mirror);
            ImGui.SameLine();
            if(ImGui.SmallButton("Front###msm_front")) { _mapYaw = 0f; _mapPitch = 0f; }
            ImGui.SameLine();
            if(ImGui.SmallButton("Left###msm_left")) { _mapYaw = MathF.PI / 2f; _mapPitch = 0f; }
            ImGui.SameLine();
            if(ImGui.SmallButton("Right###msm_right")) { _mapYaw = -MathF.PI / 2f; _mapPitch = 0f; }
            ImGui.SameLine();
            if(ImGui.SmallButton("Fit###msm_fit")) { _mapZoom = 0f; _mapPan = Vector2.Zero; }

            var origin0 = ImGui.GetCursorScreenPos();
            var size = ImGui.GetContentRegionAvail();
            size.Y -= ImGui.GetTextLineHeightWithSpacing();
            if(size.X < 50f || size.Y < 50f)
                return;
            ImGui.InvisibleButton("##msm_canvas", size, ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight | ImGuiButtonFlags.MouseButtonMiddle);
            bool hoverCanvas = ImGui.IsItemHovered();
            var draw = ImGui.GetWindowDrawList();
            draw.AddRectFilled(origin0, origin0 + size, ImGui.GetColorU32(new Vector4(0.10f, 0.10f, 0.12f, 1f)), 4f * scale);
            draw.PushClipRect(origin0, origin0 + size, true);
            try
            {
                DrawMapCanvas(model, sculpt, draw, origin0, size, hoverCanvas);
            }
            finally
            {
                draw.PopClipRect();
            }
            ImGui.TextDisabled("Right-drag: turn.  Wheel: zoom.  Middle-drag: move.  Shift + wheel: push in / pull out.");
        }
        finally
        {
            ImGui.End();
            _mapOpen = open;
        }
    }

    private void DrawMapCanvas(MeshSculpt.Model model, MeshSculpt.Sculpt sculpt, ImDrawListPtr draw, Vector2 origin0, Vector2 size, bool hoverCanvas)
    {
        var io = ImGui.GetIO();
        var mouse = ImGui.GetMousePos();
        float scale = ImGuiHelpers.GlobalScale;
        var points = model.Points;
        int n = points.Length;
        if(_mapView.Length != n)
        {
            _mapView = new Vector3[n];
            _mapScreen = new Vector2[n];
            _mapVisible = new bool[n];
        }

        // Turn, move, zoom.
        if(hoverCanvas && ImGui.IsMouseDragging(ImGuiMouseButton.Right))
        {
            _mapYaw += io.MouseDelta.X * 0.01f;
            _mapPitch = Math.Clamp(_mapPitch + io.MouseDelta.Y * 0.01f, -1.4f, 1.4f);
        }
        if(hoverCanvas && ImGui.IsMouseDragging(ImGuiMouseButton.Middle))
            _mapPan += io.MouseDelta;
        float fit = MathF.Min(size.X, size.Y) * 0.75f / MathF.Max(0.02f, model.Size);
        float zoom = _mapZoom > 0f ? _mapZoom : fit;
        var origin = origin0 + size * 0.5f + _mapPan;
        if(hoverCanvas && io.MouseWheel != 0f && !io.KeyShift)
        {
            float next = Math.Clamp(zoom * MathF.Pow(1.15f, io.MouseWheel), fit * 0.3f, fit * 20f);
            // (the spot under the mouse stays under it)
            var o2 = mouse - (mouse - origin) * (next / zoom);
            _mapPan += o2 - origin;
            origin = o2;
            zoom = next;
            _mapZoom = next;
        }
        var turn = Quaternion.CreateFromYawPitchRoll(_mapYaw, _mapPitch, 0f);
        var edits = sculpt.Edits;

        // Every point in the view (+X right, +Y up, +Z toward you), the face's middle in the middle.
        for(int i = 0; i < n; i++)
        {
            var v = Vector3.Transform(points[i].Rest + edits.GetValueOrDefault(i) - model.Centre, turn);
            _mapView[i] = v;
            _mapScreen[i] = origin + new Vector2(v.X, -v.Y) * zoom;
            _mapVisible[i] = false;
        }
        // The triangles facing you, far ones first, shaded by how they face a light from the front and above.
        _mapTris.Clear();
        var tris = model.PointTriangles;
        for(int t = 0; t + 2 < tris.Length; t += 3)
        {
            int a = tris[t], b = tris[t + 1], c = tris[t + 2];
            if(_hideInner && (points[a].Inner || points[b].Inner || points[c].Inner))
                continue;
            var fn = Vector3.Cross(_mapView[b] - _mapView[a], _mapView[c] - _mapView[a]);
            if(fn.LengthSquared() < 1e-20f)
                continue;
            // (its outside: the way its points' own normals face)
            var restNormal = Vector3.Transform(points[a].Normal + points[b].Normal + points[c].Normal, turn);
            if(Vector3.Dot(fn, restNormal) < 0f)
                fn = -fn;
            fn = Vector3.Normalize(fn);
            if(fn.Z <= 0f)
                continue;
            _mapTris.Add(((_mapView[a].Z + _mapView[b].Z + _mapView[c].Z) / 3f, t, fn));
        }
        _mapTris.Sort((x, y) => x.Depth.CompareTo(y.Depth));
        var light = Vector3.Normalize(new Vector3(-0.3f, 0.45f, 1f));
        var skin = new Vector3(0.86f, 0.70f, 0.62f);
        foreach(var (_, t, fn) in _mapTris)
        {
            int a = tris[t], b = tris[t + 1], c = tris[t + 2];
            float lit = 0.28f + 0.72f * MathF.Max(0f, Vector3.Dot(fn, light));
            var col = points[a].Inner || points[b].Inner || points[c].Inner ? new Vector3(0.75f, 0.72f, 0.78f) : skin;
            draw.AddTriangleFilled(_mapScreen[a], _mapScreen[b], _mapScreen[c], ImGui.GetColorU32(new Vector4(col * lit, 1f)));
            _mapVisible[a] = _mapVisible[b] = _mapVisible[c] = true;
        }

        // The point under the mouse: the nearest one in front.
        int hovered = -1;
        if(hoverCanvas || _mapDrag != null || _mapSmoothing)
        {
            float best = (_brushMode ? 30f : 8f) * scale;
            float bestZ = float.MinValue;
            for(int i = 0; i < n; i++)
            {
                if(!_mapVisible[i])
                    continue;
                float d = Vector2.Distance(_mapScreen[i], mouse);
                if(d > best + 3f * scale)
                    continue;
                // (the front one of points under the mouse; else the nearest)
                if(d < best - 3f * scale || _mapView[i].Z > bestZ)
                {
                    best = MathF.Min(best, d);
                    bestZ = _mapView[i].Z;
                    hovered = i;
                }
            }
        }

        bool free = hoverCanvas && _mapDrag == null && !_mapSmoothing && _mapBox == null && _mapPaint == 0;
        if(free && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            if(_brushMode && hovered >= 0)
            {
                if(_tool == Tool.Smooth)
                {
                    Remember(sculpt);
                    _mapSmoothing = true;
                }
                else
                    StartMapDrag(model, sculpt, mouse, [hovered], turn, zoom);
            }
            else if(!_brushMode)
            {
                if(io.KeyCtrl)
                    _mapBox = mouse;
                else if(io.KeyShift || io.KeyAlt)
                    _mapPaint = io.KeyAlt ? -1 : 1;
                else if(hovered >= 0)
                {
                    if(!_selected.Contains(hovered))
                    {
                        _selected.Clear();
                        _selected.Add(hovered);
                        _selectionVersion++;
                    }
                    StartMapDrag(model, sculpt, mouse, _selected, turn, zoom);
                }
            }
        }
        if(_mapSmoothing)
        {
            if(ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                if(hovered >= 0)
                    SmoothAt(model, sculpt, MeshSculpt.Falloff(model, [hovered], _radius), io.DeltaTime);
            }
            else
            {
                _mapSmoothing = false;
                _dirty = true;
                _saveAt = Environment.TickCount64;
            }
        }
        if(_mapPaint != 0)
        {
            if(ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                float r = _brush * scale;
                bool changed = false;
                for(int i = 0; i < n; i++)
                    if(_mapVisible[i] && Vector2.Distance(_mapScreen[i], mouse) <= r)
                        changed |= _mapPaint > 0 ? _selected.Add(i) : _selected.Remove(i);
                if(changed)
                    _selectionVersion++;
                draw.AddCircle(mouse, r, ImGui.GetColorU32(new Vector4(0.5f, 0.9f, 1f, 0.8f)), 0, 1.5f);
            }
            else
                _mapPaint = 0;
        }
        if(_mapBox is { } b0)
        {
            var lo = Vector2.Min(b0, mouse);
            var hi = Vector2.Max(b0, mouse);
            uint edge = ImGui.GetColorU32(new Vector4(1f, 0.9f, 0.4f, 0.8f));
            draw.AddLine(lo, new Vector2(hi.X, lo.Y), edge, 1.5f);
            draw.AddLine(new Vector2(hi.X, lo.Y), hi, edge, 1.5f);
            draw.AddLine(hi, new Vector2(lo.X, hi.Y), edge, 1.5f);
            draw.AddLine(new Vector2(lo.X, hi.Y), lo, edge, 1.5f);
            if(!ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                for(int i = 0; i < n; i++)
                    if(_mapVisible[i] && _mapScreen[i].X >= lo.X && _mapScreen[i].X <= hi.X && _mapScreen[i].Y >= lo.Y && _mapScreen[i].Y <= hi.Y)
                        _selected.Add(i);
                _selectionVersion++;
                _mapBox = null;
            }
        }
        if(_mapDrag is { } drag)
        {
            if(ImGui.IsMouseDown(ImGuiMouseButton.Left))
                UpdateMapDrag(model, sculpt, drag, mouse - drag.Start);
            else
            {
                _mapDrag = null;
                _dirty = true;
                _saveAt = Environment.TickCount64;
            }
        }
        // Shift + wheel: push in / pull out.
        if(hoverCanvas && io.KeyShift && io.MouseWheel != 0f && _mapDrag == null && (_brushMode ? hovered >= 0 : _selected.Count > 0))
        {
            long now = Environment.TickCount64;
            if(now - _lastWheel > 600)
                Remember(sculpt);
            _lastWheel = now;
            var weights = _brushMode ? MeshSculpt.Falloff(model, [hovered], _radius) : Weights(model);
            float step = io.MouseWheel * 0.0005f;
            var next = new Dictionary<int, Vector3>(sculpt.Edits);
            foreach(var (p, w) in weights)
            {
                var move = points[p].Normal * (step * w);
                Put(next, p, next.GetValueOrDefault(p) + move);
                int m = points[p].Mirror;
                if(_mirror && m >= 0 && !weights.ContainsKey(m))
                    Put(next, m, next.GetValueOrDefault(m) + new Vector3(-move.X, move.Y, move.Z));
            }
            sculpt.Edits = next;
            Edited();
        }

        // What the brush reaches (orange), the selection (yellow), the brush ring.
        var weightsNow = _brushMode ? (hovered >= 0 && _mapDrag == null ? MeshSculpt.Falloff(model, [hovered], _radius) : new Dictionary<int, float>())
            : _selected.Count > 0 ? Weights(model) : new Dictionary<int, float>();
        foreach(var (p, w) in weightsNow)
            if(_mapVisible[p] && !_selected.Contains(p))
                draw.AddCircleFilled(_mapScreen[p], 2f * scale, ImGui.GetColorU32(new Vector4(1f, 0.55f, 0.15f, 0.2f + 0.6f * w)));
        if(!_brushMode)
            foreach(int p in _selected)
                if(p < n && _mapVisible[p])
                    draw.AddCircleFilled(_mapScreen[p], 2.6f * scale, ImGui.GetColorU32(new Vector4(1f, 0.92f, 0.3f, 1f)));
        if(_showDots)
            for(int i = 0; i < n; i++)
                if(_mapVisible[i] && !weightsNow.ContainsKey(i))
                    draw.AddCircleFilled(_mapScreen[i], 1f * scale, ImGui.GetColorU32(new Vector4(0.2f, 0.2f, 0.25f, 0.5f)));
        if(hovered >= 0 && _mapDrag == null)
        {
            var at = _mapScreen[hovered];
            float ring = _brushMode ? MathF.Max(4f, _radius * zoom) : 5f * scale;
            draw.AddCircle(at, ring, ImGui.GetColorU32(new Vector4(1f, 0.85f, 0.4f, 0.9f)), 0, 1.5f);
        }
    }

    private void StartMapDrag(MeshSculpt.Model model, MeshSculpt.Sculpt sculpt, Vector2 mouse, IReadOnlyCollection<int> seeds, Quaternion turn, float zoom)
    {
        if(seeds.Count == 0)
            return;
        var weights = ReferenceEquals(seeds, _selected) ? Weights(model) : MeshSculpt.Falloff(model, seeds, _radius);
        var centre = Vector3.Zero;
        foreach(int p in seeds)
            centre += model.Points[p].Rest + sculpt.Edits.GetValueOrDefault(p);
        centre /= seeds.Count;
        Remember(sculpt);
        _mapDrag = new MapDrag
        {
            Start = mouse, Tool = _tool, Base = new Dictionary<int, Vector3>(sculpt.Edits), Weights = weights,
            Mirrors = MirrorsOf(model, weights), ToRest = Quaternion.Inverse(turn), Zoom = MathF.Max(1f, zoom), Centre = centre,
        };
    }

    // A drag on the map: in the face's rest space straight away (no skinning in between).
    private void UpdateMapDrag(MeshSculpt.Model model, MeshSculpt.Sculpt sculpt, MapDrag drag, Vector2 d)
    {
        var edits = new Dictionary<int, Vector3>(drag.Base);
        var moved = new Dictionary<int, Vector3>();
        var across = Vector3.Transform(new Vector3(d.X, -d.Y, 0f) / drag.Zoom, drag.ToRest);
        float push = -d.Y / drag.Zoom;
        float pinch = Math.Clamp(d.X * 0.004f, -1.5f, 0.95f);
        foreach(var (p, w) in drag.Weights)
        {
            var pt = model.Points[p];
            var rest = drag.Tool switch
            {
                Tool.Push => pt.Normal * (push * w),
                Tool.Pinch => (drag.Centre - (pt.Rest + drag.Base.GetValueOrDefault(p))) * (pinch * w),
                _ => across * w,
            };
            if(MathUtil.IsFinite(rest))
                moved[p] = rest;
        }
        foreach(var (m, from) in drag.Mirrors)
            if(moved.TryGetValue(from, out var r))
                moved[m] = new Vector3(-r.X, r.Y, r.Z);
        foreach(var (p, r) in moved)
            Put(edits, p, drag.Base.GetValueOrDefault(p) + r);
        sculpt.Edits = edits;
        MeshSculpt.Changed();
    }
}
