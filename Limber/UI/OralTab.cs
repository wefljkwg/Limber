using System.Collections.Generic;
using System;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Limber.Rig;

namespace Limber.UI;

/// <summary>The Oral page: the chosen character's mouth follows a partner's shaft (adult characters only).</summary>
public sealed class OralTab
{
    private readonly OralService _oral;
    private readonly Selection _selection;
    private readonly RigSettings _config;

    public OralTab(OralService oral, Selection selection, RigSettings config)
    {
        _oral = oral;
        _selection = selection;
        _config = config;
    }

    private OralSettings S => _config.Oral ??= new OralSettings();

    public void Draw()
    {
        var actor = _selection.Actor;
        var s = S;
        if(!actor.IsSet)
        {
            ImGui.TextDisabled("Choose a character at the top of the window.");
            return;
        }
        if(!Limber.Koi.Safety.AdultAllowed(actor.Address))
        {
            ImGui.TextDisabled("Only for adult characters.");
            return;
        }

        bool changed = false;
        ImGui.TextWrapped($"Mouth: {actor.Name}. The head follows the partner's shaft, the jaw opens to fit and the lips wrap "
            + "round it. Place the two close together first. The partner needs an IVCS / YAS skeleton with a penis.");
        ImGui.Spacing();

        // ---- Partner and start / stop
        var partners = _selection.ListCharacters()
            .Where(c => c.Address != actor.Address && Limber.Koi.Safety.AdultAllowed(c.Address))
            .ToList();
        int current = partners.FindIndex(p => p.Name == s.Receiver && p.ObjectIndex == s.ReceiverIndex);
        if(current < 0)
            current = partners.FindIndex(p => p.Name == s.Receiver);
        string preview = current >= 0 ? partners[current].Label : partners.Count == 0 ? "(nobody else here)" : "(choose)";
        ImGui.SetNextItemWidth(260 * ImGuiHelpers.GlobalScale);
        if(ImGui.BeginCombo("Partner###oral_partner", preview))
        {
            for(int i = 0; i < partners.Count; i++)
                if(ImGui.Selectable($"{partners[i].Label}###oral_p{i}", i == current))
                {
                    s.Receiver = partners[i].Name;
                    s.ReceiverIndex = partners[i].ObjectIndex;
                    changed = true;
                }
            ImGui.EndCombo();
        }

        bool on = s.Enabled && s.Giver == actor.Name;
        if(ImGui.Button(on ? "Stop###oral_toggle" : "Start###oral_toggle", new System.Numerics.Vector2(120 * ImGuiHelpers.GlobalScale, 0)))
        {
            if(on)
                s.Enabled = false;
            else if(!string.IsNullOrEmpty(s.Receiver))
            {
                s.Giver = actor.Name;
                s.GiverIndex = actor.ObjectIndex;
                s.Enabled = true;
            }
            changed = true;
        }
        Tooltip("Starts / stops (it eases in and out). Letting go of the head on the IK Posing page stops it too.");
        if(s.Enabled && s.Giver != actor.Name)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"(running on {s.Giver})");
        }
        ImGui.SameLine();
        bool guide = s.ShowGuide;
        if(ImGui.Checkbox("Show the shaft###oral_guide", ref guide))
            s.ShowGuide = guide;
        Tooltip("Draws the shaft as Limber sees it (its line and thickness rings), where the lips should be (green dot) "
            + "and where they are (orange ring). Handy for checking the fit, e.g. with the partner hidden.");
        if(_oral.Running && _oral.Status.Length > 0)
            ImGui.TextDisabled(_oral.Status);
        if(s.LipMesh && Limber.Gpu.LipMesh.Status.Length > 0)
            ImGui.TextDisabled(Limber.Gpu.LipMesh.Status);

        // ---- Motion
        if(Section("Motion", true))
        {
            changed |= Slider("Speed", "strokes / s", () => s.Speed, v => s.Speed = v, 0.1f, 3f, "%.2f",
                "Strokes per second.");
            changed |= Slider("Deepest", "", () => s.Deepest * 100f, v => s.Deepest = v / 100f, 0f, 100f, "%.0f%%",
                "How far down the shaft the lips go at the deepest (0% = the tip, 100% = the base).");
            changed |= Slider("Shallowest", "", () => s.Shallowest * 100f, v => s.Shallowest = v / 100f, 0f, 100f, "%.0f%%",
                "How far down they stay at the shallowest.");
            changed |= Slider("Variation", "", () => s.Variation * 100f, v => s.Variation = v / 100f, 0f, 100f, "%.0f%%",
                "0%: every stroke the same. Higher: each stroke has its own depth and pace, with the odd pause at the deep end.");
            changed |= Slider("Partner moves", "", () => s.PartnerMoves * 100f, v => s.PartnerMoves = v / 100f, 0f, 100f, "%.0f%%",
                "How much the partner's shaft moves along: it tilts up as the mouth goes deeper and is tugged by the lips on the way out.");
        }

        // ---- Head
        if(Section("Head", true))
        {
            changed |= Slider("Head angle", "°", () => s.HeadAngle, v => s.HeadAngle = v, -40f, 40f, "%.0f",
                "Tilts the head on top of facing down the shaft (+ = chin down).");
            changed |= Slider("Chin up for big", "°", () => s.ChinUp, v => s.ChinUp = v, 0f, 30f, "%.0f",
                "With a big shaft the head tips back (chin up) to take it straighter: this many degrees at the biggest.");
            changed |= Slider("Nod", "°", () => s.Nod, v => s.Nod = v, 0f, 15f, "%.0f",
                "How much the head nods with each stroke.");
            changed |= Slider("Follow the bend", "", () => s.FollowBend * 100f, v => s.FollowBend = v / 100f, 0f, 100f, "%.0f%%",
                "0%: the head lines up with the shaft as a whole. Higher: it also turns with the shaft's bend where the lips are.");
            changed |= Slider("Mouth up / down", "mm", () => s.OffsetUp * 1000f, v => s.OffsetUp = v / 1000f, -30f, 30f, "%.1f",
                "Moves the head so the mouth lines up with the shaft, if it sits a little high or low.");
            changed |= Slider("Mouth left / right", "mm", () => s.OffsetRight * 1000f, v => s.OffsetRight = v / 1000f, -30f, 30f, "%.1f",
                "Moves the head so the mouth lines up with the shaft, if it sits a little to one side.");
        }

        // ---- Mouth (the lips' live mesh)
        bool mouthOpen = Section("Mouth", true);
        if(mouthOpen)
        {
            bool mouthRig = s.MouthRig;
            if(ImGui.Checkbox("Mouth rig###oral_mouth_rig", ref mouthRig))
            {
                s.MouthRig = mouthRig;
                changed = true;
            }
            Tooltip("Off: Oral leaves the mouth alone (no lips, jaw, teeth or cheeks moved); only the head moves.");
        }
        if(mouthOpen && s.MouthRig)
        {
            bool liveLips = s.LipMesh;
            if(ImGui.Checkbox("Lips wrap the shaft (live mesh)###oral_lipmesh", ref liveLips))
            {
                s.LipMesh = liveLips;
                changed = true;
            }
            Tooltip("The face model itself is shaped round the shaft every frame: the lips wrap and seal round it, the jaw "
                + "and cheeks make room, and nothing of the face clips through it. Only on your screen. "
                + "Off: the older way, with the lip bones only (its settings are under Lip bones).");
            if(s.LipMesh)
            {
                changed |= Slider("Opening size", "", () => s.OpenSize * 100f, v => s.OpenSize = v / 100f, 50f, 200f, "%.0f%%",
                    "The opening against the shaft, all round (100% = just round it; higher leaves room, lower squeezes).");
                changed |= Slider("Opening height", "", () => s.OpenHeight * 100f, v => s.OpenHeight = v / 100f, 50f, 200f, "%.0f%%",
                    "The opening's height on its own (top to bottom).");
                changed |= Slider("Opening width", "", () => s.OpenWidth * 100f, v => s.OpenWidth = v / 100f, 50f, 200f, "%.0f%%",
                    "The opening's width on its own (corner to corner).");
                if(ImGui.SmallButton("Reset the opening###oral_open_reset"))
                {
                    s.OpenSize = s.OpenHeight = s.OpenWidth = 1f;
                    changed = true;
                }
                changed |= Slider("Lip seal", "", () => s.LipSeal * 100f, v => s.LipSeal = v / 100f, 0f, 100f, "%.0f%%",
                    "How tightly the lips press on the shaft.");
                changed |= Slider("Pout", "", () => s.Pout * 100f, v => s.Pout = v / 100f, 0f, 100f, "%.0f%%",
                    "How far the lips push forward together round the shaft.");
                changed |= Slider("Lip grip ", "", () => s.LipGrip * 100f, v => s.LipGrip = v / 100f, 0f, 100f, "%.0f%%",
                    "How much the lips hold on to the shaft: pulled out with it on the way out, tucked in on the way in.");
            }
            changed |= Slider("Jaw", "", () => s.JawOpen * 100f, v => s.JawOpen = v / 100f, 0f, 150f, "%.0f%%",
                "How far the jaw drops: 100% = its usual share (more with a bigger shaft).");
            if(s.LipMesh)
                changed |= Slider("Cheeks for big", "", () => s.Cheeks * 100f, v => s.Cheeks = v / 100f, 0f, 150f, "%.0f%%",
                    "With a big shaft the cheeks fill out to make room.");
            changed |= Slider("Suction", "", () => s.Suction * 100f, v => s.Suction = v / 100f, 0f, 100f, "%.0f%%",
                "Cheeks drawn in on the way out.");
            bool teeth = s.HideTeeth;
            if(ImGui.Checkbox("Hide the teeth###oral_teeth", ref teeth))
            {
                s.HideTeeth = teeth;
                changed = true;
            }
            Tooltip("Shrinks the teeth away into the mouth while it runs, so they don't show or poke through.");
        }

        // ---- The shaft
        if(Section("Shaft", false))
        {
            changed |= Slider("Thickness", "cm", () => s.Thickness * 200f, v => s.Thickness = v / 200f, 1f, 8f, "%.1f",
                "The shaft's thickness (across), when it isn't read from the model.");
            changed |= Slider("Lip tightness", "mm", () => -s.Tightness * 1000f, v => s.Tightness = -v / 1000f, -6f, 6f, "%.1f",
                "Makes the shaft count as thinner (higher) or thicker (lower) where the lips hold it.");
            bool measure = s.MeasureShaft;
            if(ImGui.Checkbox("Read the shaft from the model (experimental)###oral_measure", ref measure))
            {
                s.MeasureShaft = measure;
                changed = true;
            }
            Tooltip("Off (recommended for now): the shaft follows its bones, as thick as the Thickness slider says. "
                + "On: its shape is read from the partner's model (still being worked on; can be wrong on some bodies).");
            if(s.MeasureShaft)
            {
                if(_oral.ShaftInfo.Length > 0)
                    ImGui.TextDisabled(_oral.ShaftInfo);
                string[] fits = ["Not refitted", "Refit A", "Refit B", "Refit C", "Refit D"];
                int fit = Math.Clamp(s.FitVariant + 1, 0, fits.Length - 1);
                ImGui.SetNextItemWidth(160 * ImGuiHelpers.GlobalScale);
                if(ImGui.Combo("Refit (testing)###oral_fit", ref fit, fits, fits.Length))
                {
                    s.FitVariant = fit - 1;
                    changed = true;
                }
                Tooltip("With Show the shaft on, pink dots show the shaft's vertices as Limber reads them. Pick the option where "
                    + "the dots sit right on the real shaft.");
            }
        }

        // ---- The older lip-bone way (only when the live mesh is off)
        if(s.MouthRig && !s.LipMesh && Section("Lip bones", false))
        {
            if(ImGui.BeginTable("###oral_mouth_table", 2, ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("###oral_mouth_sliders", ImGuiTableColumnFlags.WidthFixed, 380 * ImGuiHelpers.GlobalScale);
                ImGui.TableSetupColumn("###oral_mouth_editor", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                changed |= Slider("Lip fit", "", () => s.LipFit * 100f, v => s.LipFit = v / 100f, 0f, 100f, "%.0f%%",
                    "How closely the lips wrap round the shaft.");
                changed |= Slider("Lip grip", "", () => s.LipGrip * 100f, v => s.LipGrip = v / 100f, 0f, 100f, "%.0f%%",
                    "How much the lips hold on and get pulled along with each stroke.");
                changed |= Slider("Corner tuck", "", () => s.CornerTuck * 100f, v => s.CornerTuck = v / 100f, 0f, 100f, "%.0f%%",
                    "How snugly the mouth corners tuck in against the shaft.");
                changed |= Slider("Roundness", "", () => s.Roundness * 100f, v => s.Roundness = v / 100f, 0f, 100f, "%.0f%%",
                    "100%: the lips make a round ring round the shaft. Lower: they keep more of their own oval shape.");
                changed |= Slider("Mouth width", "", () => s.MouthWidth * 100f, v => s.MouthWidth = v / 100f, 50f, 200f, "%.0f%%",
                    "The opening's width against its height (100% = as wide as tall).");
                changed |= Slider("Pout", "", () => s.Pout * 100f, v => s.Pout = v / 100f, 0f, 100f, "%.0f%%",
                    "Lips pushed forward and rolled outward round the shaft.");
                changed |= Slider("Lip thickness", "", () => s.LipThickness * 100f, v => s.LipThickness = v / 100f, 50f, 250f, "%.0f%%",
                    "How full the lips are while it runs (100% = as they are).");
                ImGui.TableNextColumn();
                changed |= DrawMouthEditor(s);
                ImGui.EndTable();
            }
        }

        // ---- Details
        if(Section("Details", false))
        {
            if(ImGui.Button("Save this moment for the bench###oral_bench"))
                _benchNote = _oral.ExportBench();
            Tooltip("Saves what the lips are working from right now (the face model, its bones, the shaft and the partner's "
                + "shaft model) so Claude can test changes against exactly this scene. Start Oral first, ideally with the shaft "
                + "deep in. Game files only: no names or folders.");
            if(_benchNote.Length > 0)
                ImGui.TextDisabled(_benchNote);
            if(_oral.MouthBones.Length > 0 && ImGui.TreeNode("Mouth bones found###oral_bones"))
            {
                ImGui.TextWrapped(_oral.MouthBones);
                ImGui.TreePop();
            }
            DrawResearch(actor);
        }

        if(changed)
            _config.Save();
    }

    private string _benchNote = "";

    private static bool Section(string title, bool open)
    {
        ImGui.Spacing();
        return ImGui.CollapsingHeader($"{title}###oral_section_{title}", open ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None);
    }

    // ---- Research for exact mesh capture (the first step: where does the game keep each bone's final matrix?)

    private List<string>? _research;
    private bool _watching;

    private unsafe void DrawResearch(ActorRef actor)
    {
        if(!ImGui.TreeNode("Research: exact mesh capture###oral_research"))
            return;
        ImGui.TextWrapped("Looks for where the game keeps the final bone matrices it draws this character with. "
            + "Press it in GPose with the character standing still, then copy the result and send it over.");
        if(ImGui.Button("Search memory###oral_research_go"))
            _research = Research(actor, watch: false);

        if(_research != null)
        {
            ImGui.SameLine();
            if(ImGui.Button("Copy###oral_research_copy"))
                ImGui.SetClipboardText(string.Join("\n", _research));
            foreach(var line in _research)
                ImGui.TextWrapped(line);
        }
        ImGui.TreePop();
    }

    private unsafe List<string> Research(ActorRef actor, bool watch)
    {
        var lines = new List<string>();
        try
        {
            var cache = new Limber.Core.SkeletonInfoCache();
            if(!Limber.Core.ActorSkeleton.TryGet(actor.Address, actor.ObjectIndex, cache, out var sk))
            {
                lines.Add("Can't read this character's skeleton.");
                return lines;
            }
            var targets = new List<(string, float[])>();
            foreach(var name in new[] { "j_kosi", "j_sebo_b", "j_kao" })
            {
                int bone = sk.Info.IndexOf(name);
                if(bone < 0)
                    continue;
                var rest = OralService.RestMatrix(sk, name);
                if(!System.Numerics.Matrix4x4.Invert(rest, out var restInverse))
                    continue;
                var frame = sk.GetModelFrame(bone);
                var scale = sk.GetModelScale(bone);
                var posed = System.Numerics.Matrix4x4.CreateScale(scale) * System.Numerics.Matrix4x4.CreateFromQuaternion(frame.Rotation)
                    * System.Numerics.Matrix4x4.CreateTranslation(frame.Position);
                var model = restInverse * posed;
                var world = model * sk.ModelToWorld;
                foreach(var (space, m) in new[] { ("model", model), ("world", world), ("pose", posed), ("pose world", posed * sk.ModelToWorld) })
                {
                    targets.Add(($"{name} {space} rows", new[] { m.M11, m.M12, m.M13, m.M21, m.M22, m.M23, m.M31, m.M32, m.M33, m.M41, m.M42, m.M43 }));
                    targets.Add(($"{name} {space} cols", new[] { m.M11, m.M21, m.M31, m.M41, m.M12, m.M22, m.M32, m.M42, m.M13, m.M23, m.M33, m.M43 }));
                }
                // The turn / size part only (the move may be kept from another origin, e.g. the camera).
                float nan = float.NaN;
                foreach(var (space, m) in new[] { ("model", model), ("world", world) })
                {
                    targets.Add(($"{name} {space} turn rows", new[] { m.M11, m.M12, m.M13, m.M21, m.M22, m.M23, m.M31, m.M32, m.M33, nan, nan, nan }));
                    targets.Add(($"{name} {space} turn cols", new[] { m.M11, m.M21, m.M31, nan, m.M12, m.M22, m.M32, nan, m.M13, m.M23, m.M33, nan }));
                }
            }
            lines.Add($"Character #{actor.ObjectIndex}, {targets.Count} patterns.");
            if(watch)
            {
                lines.Add("(watching the graphics writes was removed: it wasn't safe for the game)");
                _watching = true;
                return lines;
            }
            var drawObject = (nint)((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)actor.Address)->DrawObject;
            lines.AddRange(Limber.Gpu.LiveMesh.FindFloats(drawObject, targets, 0.002f));
        }
        catch(Exception e)
        {
            lines.Add("Failed: " + e.Message);
        }
        return lines;
    }

    private static bool Slider(string label, string unit, Func<float> get, Action<float> set, float min, float max, string format, string tip)
    {
        float v = get();
        ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);
        bool changed = ImGui.SliderFloat($"{label}{(unit.Length > 0 ? $" ({unit})" : "")}###oral_{label}", ref v, min, max, format);
        if(changed)
            set(Math.Clamp(v, min, max));
        Tooltip(tip);
        return changed;
    }

    private string? _dragBone;

    /// <summary>
    /// The mouth shape editor: the lip bones as dots on a flat plane (as if looking at the mouth, the shaft's edge as
    /// the circle). Drag a dot to place that bone; right-click it to give it back to Limber; mirrored by default.
    /// </summary>
    private bool DrawMouthEditor(OralSettings s)
    {
        bool changed = false;
        var dots = _oral.LipDots;
        float side = MathF.Min(ImGui.GetContentRegionAvail().X, 260 * ImGuiHelpers.GlobalScale);
        if(side < 80)
            return false;
        var size = new System.Numerics.Vector2(side, side);
        var origin = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton("###oral_mouth_canvas", size, ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight);
        bool hovered = ImGui.IsItemHovered(), active = ImGui.IsItemActive();
        var draw = ImGui.GetWindowDrawList();
        var centre = origin + size * 0.5f;
        float unit = side * 0.32f; // the shaft's edge
        draw.AddRectFilled(origin, origin + size, 0xFF1E1A18);
        draw.AddLine(new System.Numerics.Vector2(origin.X, centre.Y), new System.Numerics.Vector2(origin.X + side, centre.Y), 0xFF505050);
        draw.AddLine(new System.Numerics.Vector2(centre.X, origin.Y), new System.Numerics.Vector2(centre.X, origin.Y + side), 0xFF505050);
        draw.AddCircle(centre, unit, 0xFF4080C0, 48, 1.5f);
        System.Numerics.Vector2 Screen(float x, float y) => centre + new System.Numerics.Vector2(x, -y) * unit;
        var mouse = ImGui.GetIO().MousePos;
        string? hover = null;
        float best = 10f * ImGuiHelpers.GlobalScale;
        foreach(var d in dots)
        {
            float dist = System.Numerics.Vector2.Distance(Screen(d.X, d.Y), mouse);
            if(dist < best)
            {
                best = dist;
                hover = d.Bone;
            }
        }
        if(hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            _dragBone = hover;
        if(!ImGui.IsMouseDown(ImGuiMouseButton.Left) && _dragBone != null)
        {
            _dragBone = null;
            changed = true; // saved once, when the drag ends
        }
        if(active && _dragBone != null)
        {
            var at = (mouse - centre) / unit;
            float x = Math.Clamp(at.X, -1.8f, 1.8f), y = Math.Clamp(-at.Y, -1.8f, 1.8f);
            Move(s, _dragBone, x, y);
        }
        if(hovered && hover != null && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
        {
            Move(s, hover, float.NaN, float.NaN);
            changed = true;
        }
        foreach(var d in dots)
        {
            var at = Screen(d.X, d.Y);
            uint colour = d.Bone == _dragBone ? 0xFF33DDFF : d.Bone == hover ? 0xFF66CCFF : d.Moved ? 0xFF2299FF : 0xFF1A8CFF;
            draw.AddCircleFilled(at, 5f * ImGuiHelpers.GlobalScale, d.Moved ? colour : 0xFF808080);
            draw.AddCircle(at, 5f * ImGuiHelpers.GlobalScale, colour, 12, 1.5f);
        }
        if(dots.Length == 0)
            draw.AddText(origin + new System.Numerics.Vector2(8, 8), 0xFFA0A0A0, "Start Oral to see the lips here.");
        if(hover != null && hovered)
            ImGui.SetTooltip($"{Limber.Rig.HandleCatalog.Label(hover)} ({hover})\nDrag to move it; right-click to put it back.");

        bool mirror = s.LipShapeMirror;
        if(ImGui.Checkbox("Mirror###oral_shape_mirror", ref mirror))
        {
            s.LipShapeMirror = mirror;
            changed = true;
        }
        Tooltip("Moving a bone on one side moves its partner on the other side the mirror way.");
        ImGui.SameLine();
        if(ImGui.SmallButton("Reset shape###oral_shape_reset"))
        {
            s.LipShape = new();
            changed = true;
        }
        Tooltip("Every lip bone back to where Limber puts it (round the shaft).");
        ImGui.TextDisabled("Grey: placed by Limber. Orange: placed by you.\nThe circle is the shaft's edge.");
        return changed;
    }

    // Puts one lip bone (and with Mirror its other-side partner) at x, y; NaN gives it back to Limber. The settings
    // get a new copy each time (the bone update reads them while this runs).
    private void Move(OralSettings s, string bone, float x, float y)
    {
        var shape = new Dictionary<string, float[]>(s.LipShape ?? new());
        void Set(string name, float px, float py)
        {
            if(float.IsNaN(px))
                shape.Remove(name);
            else
                shape[name] = [px, py];
        }
        Set(bone, x, y);
        if(s.LipShapeMirror)
        {
            string? other = bone.EndsWith("_l", StringComparison.Ordinal) ? bone[..^2] + "_r"
                : bone.EndsWith("_r", StringComparison.Ordinal) ? bone[..^2] + "_l" : null;
            if(other != null && Array.Exists(_oral.LipDots, d => d.Bone == other))
                Set(other, float.IsNaN(x) ? x : -x, y);
        }
        s.LipShape = shape;
    }

    private static void SeparatorText(string text)
    {
        ImGui.Spacing();
        ImGui.TextColored(new System.Numerics.Vector4(0.75f, 0.75f, 0.8f, 1f), text);
        ImGui.Separator();
    }

    private static void Tooltip(string text)
    {
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip(text);
    }
}
