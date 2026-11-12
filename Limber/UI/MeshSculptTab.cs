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
/// The Mesh sculpt page (experimental): pinch and drag the face's own vertices with the mouse, right on the face.
/// Select points (click, paint, box), then drag them: Move, Push / pull (along the skin) or Pinch (toward each other).
/// The edits ride along with the face's bones, and Oral's lips wrap first with the edits on top.
/// </summary>
public sealed class MeshSculptTab
{
    private enum Tool { Move, Push, Pinch }

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
    private Tool _tool = Tool.Move;
    private float _radius = 0.008f;   // falloff along the skin (model metres)
    private float _brush = 16f;       // paint brush (pixels)
    private bool _mirror = true;
    private bool _showDots = true;
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
        ImGui.TextWrapped("Pinch and drag the face's own mesh with the mouse, right on the face (expressions, face shape...). The edits "
            + "follow the face's bones and are added on top of the face morphs (and Oral's lips). Only on your screen.");
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
        ImGui.Checkbox("Sculpt on the face###ms_tool", ref _sculpting);
        Tip("Show the face's points and use the mouse on them (only while this page is open).");

        ImGui.Spacing();
        ImGui.TextUnformatted("Drag does:");
        ImGui.SameLine();
        if(ImGui.RadioButton("Move###ms_move", _tool == Tool.Move)) _tool = Tool.Move;
        Tip("The selection moves with the mouse, across the screen.");
        ImGui.SameLine();
        if(ImGui.RadioButton("Push / pull###ms_push", _tool == Tool.Push)) _tool = Tool.Push;
        Tip("Up pulls the selection out of the skin, down pushes it in (each point along its own normal).");
        ImGui.SameLine();
        if(ImGui.RadioButton("Pinch###ms_pinch", _tool == Tool.Pinch)) _tool = Tool.Pinch;
        Tip("Right pinches the selection together (toward its middle), left spreads it apart.");

        float cm = _radius * 100f;
        ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);
        if(ImGui.SliderFloat("Soft falloff###ms_radius", ref cm, 0f, 5f, "%.1f cm"))
            _radius = cm / 100f;
        Tip("How far around the selection the skin moves with it, fading out (measured along the skin, so the upper lip doesn't pull the lower one).");
        ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);
        ImGui.SliderFloat("Brush###ms_brush", ref _brush, 4f, 80f, "%.0f px");
        Tip("The paint brush's size (Shift or Alt + drag).");
        ImGui.Checkbox("Mirror left / right###ms_mirror", ref _mirror);
        Tip("Edits on one side of the face are made on the other side too.");
        ImGui.SameLine();
        ImGui.Checkbox("Show all points###ms_dots", ref _showDots);
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
        ImGui.TextDisabled("On the face:\n"
            + "  Click a point: select it.   Drag a selected point: Move / Push / Pinch the selection.\n"
            + "  Shift + drag: paint points on.   Alt + drag: paint them off.   Ctrl + drag: box select.\n"
            + "  Mouse wheel over the face: push the selection in or pull it out.");

        if(_dirty && Environment.TickCount64 >= _saveAt && _drag == null)
        {
            _dirty = false;
            MeshSculpt.Save();
        }
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
        var actor = _selection.Actor;
        var model = MeshSculpt.Current;
        var sculpt = MeshSculpt.Edits;
        if(!Showing || !_sculpting || !actor.IsSet || model == null || sculpt == null || MeshSculpt.Owner != actor.Address)
        {
            _drag = null;
            _boxStart = null;
            _painting = 0;
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

        // The point under the mouse: the nearest to the camera of those close to it.
        int hovered = -1;
        float hoverR = 7f * scale, bestDepth = float.MaxValue;
        for(int i = 0; i < n; i++)
            if(_visible[i] && Vector2.Distance(_screen[i], mouse) <= hoverR && _depth[i] < bestDepth)
            {
                bestDepth = _depth[i];
                hovered = i;
            }

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
            bool free = !_overUi && _drag == null && _boxStart == null && _painting == 0;

            // Start: box, paint, or a drag on the selection.
            if(free && ImGui.IsMouseClicked(ImGuiMouseButton.Left) && (overFace || hovered >= 0))
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
                    StartDrag(model, sculpt, sk, mouse);
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

            // Wheel: push in / pull out.
            if(!_overUi && _drag == null && io.MouseWheel != 0f && _selected.Count > 0 && (overFace || hovered >= 0))
            {
                long now = Environment.TickCount64;
                if(now - _lastWheel > 600)
                    Remember(sculpt);
                _lastWheel = now;
                var weights = Weights(model);
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

            // The points.
            var weightsNow = _selected.Count > 0 ? Weights(model) : _preview;
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
                else if(_selected.Count > 0 && weightsNow.TryGetValue(i, out float w))
                    draw.AddCircleFilled(s, 2.2f * scale, ImGui.GetColorU32(new Vector4(1f, 0.55f, 0.15f, 0.25f + 0.65f * w)));
                else if(_showDots)
                    draw.AddRectFilled(s - new Vector2(d), s + new Vector2(d), dot);
            }
            if(hovered >= 0 && _drag == null)
                draw.AddCircle(_screen[hovered], 5.5f * scale, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.95f)), 0, 1.5f);
            if(!_overUi && (io.KeyShift || io.KeyAlt) && !io.KeyCtrl && (overFace || _painting != 0))
                draw.AddCircle(mouse, _brush * scale, ImGui.GetColorU32(io.KeyAlt ? new Vector4(1f, 0.4f, 0.4f, 0.8f) : new Vector4(0.5f, 0.9f, 1f, 0.8f)), 0, 1.5f);

            _wantInput = !_overUi && (hovered >= 0 || _drag != null || _boxStart != null || _painting != 0
                                      || ((io.KeyShift || io.KeyAlt || io.KeyCtrl) && overFace)
                                      || (_selected.Count > 0 && overFace && io.MouseWheel != 0f));
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

    private void StartDrag(MeshSculpt.Model model, MeshSculpt.Sculpt sculpt, in ActorSkeleton sk, Vector2 mouse)
    {
        if(_selected.Count == 0)
            return;
        var weights = Weights(model);
        var mirrors = MirrorsOf(model, weights);
        var at = new Dictionary<int, MeshSculpt.Posed>();
        foreach(var p in weights.Keys)
            at[p] = _posed[p];
        foreach(var (m, _) in mirrors)
            at[m] = _posed[m];
        var centroid = Vector3.Zero;
        foreach(int p in _selected)
            centroid += _posed[p].World;
        centroid /= _selected.Count;
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
}
