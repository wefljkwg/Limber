using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Limber.BodyRig;

namespace Limber.Koi.Props;

/// <summary>A placed Koikatsu prop's own settings on the Props page: colours, blend shapes, bones, dynamic bones.</summary>
internal static class KoiPropEditor
{
    private static string _boneSearch = "";

    public static void Draw(SpawnedProp prop)
    {
        var inst = KoiPropRuntime.For(prop.Id);
        if(inst == null)
        {
            if((prop.Item.Sgb ?? "").StartsWith(KoiProps.GameRoot + "/", StringComparison.OrdinalIgnoreCase))
                ImGui.TextDisabled("To colour and pose it: convert it again (Import props), then place it again.");
            return;
        }
        var rig = inst.Rig;
        KoiPropNodes.Open(prop);
        ImGui.TextDisabled($"Koikatsu prop: {inst.Note}.");

        // Colours: each part (material) with the colours its mask has.
        if(rig.Materials.Count > 0 && ImGui.TreeNodeEx("Colours###koi_colours", ImGuiTreeNodeFlags.DefaultOpen))
        {
            bool apply = false;
            for(int m = 0; m < rig.Materials.Count; m++)
            {
                var material = rig.Materials[m];
                using var id = ImRaii.PushId(m);
                string name = material.Name.Length > 0 ? material.Name : $"Part {m + 1}";
                ImGui.TextUnformatted($"{(rig.Materials.Count > 1 ? $"Part {m + 1}: " : "")}{name}");
                for(int c = 0; c < Math.Clamp(material.Slots, 1, 3); c++)
                {
                    ImGui.SameLine();
                    var colour = inst.Colours[m][c];
                    if(ImGui.ColorEdit4($"###koi_col_{c}", ref colour, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.AlphaBar))
                        inst.Colours[m][c] = colour;
                    if(ImGui.IsItemDeactivatedAfterEdit())
                        apply = true;
                    if(ImGui.IsItemHovered())
                        ImGui.SetTooltip(material.Slots > 1 ? $"Colour {c + 1} of this part (where its colour mask's {(c == 0 ? "red" : c == 1 ? "green" : "blue")} is)." : "This part's colour.");
                }
                ImGui.SameLine();
                if(ImGui.SmallButton("Default###koi_col_def"))
                {
                    inst.Colours[m] = (Vector4[])material.Colours.Clone();
                    apply = true;
                }
            }
            if(apply)
                KoiPropRuntime.Recolour(inst);
            ImGui.TextDisabled("(a colour is put on when you let go of the picker: the prop is drawn again)");
            ImGui.TreePop();
        }

        // Shading (Koikatsu's are drawn toon-shaded; the game lights them as it does everything, so some come out too
        // bright or flat): put into its textures, like the colours.
        if(ImGui.TreeNodeEx("Shading###koi_shading", ImGuiTreeNodeFlags.DefaultOpen))
        {
            bool apply = false;
            void Slider(string label, ref float value, float min, float max, string tip)
            {
                ImGui.SetNextItemWidth(Limber.UI.Fit.Fixed(200f * ImGuiHelpers.GlobalScale));
                ImGui.SliderFloat(label, ref value, min, max, "%.2f");
                if(ImGui.IsItemDeactivatedAfterEdit())
                    apply = true;
                if(ImGui.IsItemHovered())
                    ImGui.SetTooltip(tip + " (Put on when you let go.)");
            }
            // Quick looks: the surface's shine set to match what's round it (e.g. "Oiled skin" next to an oiled body).
            ImGui.TextUnformatted("Surface:");
            foreach(var (label, shine, rough) in new[] { ("Matte", 0.04f, 0.85f), ("Skin", 0.22f, 0.5f), ("Oiled skin", 0.55f, 0.22f), ("Glossy", 0.8f, 0.12f), ("Metal", 1f, 0.28f) })
            {
                ImGui.SameLine();
                if(ImGui.SmallButton($"{label}###koi_look_{label}"))
                {
                    (inst.Shine, inst.Roughness) = (shine, rough);
                    apply = true;
                }
                if(ImGui.IsItemHovered())
                    ImGui.SetTooltip($"Shine {shine:0.00}, roughness {rough:0.00}. Pick what it sits next to, so it reflects light the same way.");
            }
            Slider("Brightness###koi_bright", ref inst.Brightness, 0.1f, 1.5f, "Darker or lighter: lower it for props that look far too bright.");
            Slider("Contrast###koi_contrast", ref inst.Contrast, 0.3f, 2f, "Flatter or punchier.");
            Slider("Colour strength###koi_sat", ref inst.Saturation, 0f, 2f, "Greyer (0) to more vivid.");
            Slider("Shine###koi_shine", ref inst.Shine, 0f, 1f, "How much light it reflects (0: matte).");
            Slider("Roughness###koi_rough", ref inst.Roughness, 0f, 1f, "Sharp highlights (low) or soft, spread ones (high).");
            var glow = inst.Glow;
            if(ImGui.ColorEdit3("Glow###koi_glow", ref glow, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.Float))
                inst.Glow = glow;
            if(ImGui.IsItemDeactivatedAfterEdit())
                apply = true;
            if(ImGui.IsItemHovered())
                ImGui.SetTooltip("Light it gives off on its own (black: none, so the scene lights it). Put on when you let go.");
            if(rig.Materials.Any(m => m.Normal != null))
            {
                Slider("Bumps###koi_bumps", ref inst.Bumps, 0f, 2f, "How much of its own surface detail (its normal map) catches the light (0: smooth).");
                ImGui.SameLine();
                bool flipGreen = inst.BumpsFlipGreen;
                if(ImGui.Checkbox("Flip bumps###koi_bumps_flip", ref flipGreen))
                {
                    inst.BumpsFlipGreen = flipGreen;
                    apply = true;
                }
                if(ImGui.IsItemHovered())
                    ImGui.SetTooltip("Try this if its bumps look dented in where they should stick out (lit from the wrong side).");
            }
            else
                ImGui.TextDisabled("(no bump map in this prop: Bumps not offered)");
            bool faces = inst.FlipFaces, normals = inst.FlipNormals;
            if(ImGui.Checkbox("Turn faces round###koi_flip_faces", ref faces))
            {
                inst.FlipFaces = faces;
                apply = true;
            }
            if(ImGui.IsItemHovered())
                ImGui.SetTooltip("A fix to try if it glows round its edges or looks lit from behind: the game is drawing its insides.");
            ImGui.SameLine();
            if(ImGui.Checkbox("Flip normals###koi_flip_normals", ref normals))
            {
                inst.FlipNormals = normals;
                apply = true;
            }
            if(ImGui.IsItemHovered())
                ImGui.SetTooltip("A fix to try if its light and shadow are on the wrong sides.");
            if(ImGui.SmallButton("Default shading###koi_shading_def"))
            {
                (inst.Brightness, inst.Contrast, inst.Saturation, inst.Shine, inst.Roughness) = (1f, 1f, 1f, 0.16f, 0.5f);
                inst.Glow = Vector3.Zero;
                (inst.Bumps, inst.BumpsFlipGreen) = (1f, true);
                inst.Constants.Clear();
                apply = true;
            }
            ImGui.TextDisabled("(No rim light or toon outline: the game's own shader for props has neither.)");

            // Every value the game's shader takes for its materials, for anything the sliders above don't cover.
            if(KoiProps.Template() is { } template && ImGui.TreeNodeEx("Material values (advanced)###koi_mat_adv"))
            {
                ImGui.TextDisabled("The game shader's own settings for this prop. Hover one for what it does; right-click: default.");
                if(inst.Constants.Count > 0 && ImGui.SmallButton("All back to default###koi_mat_def"))
                {
                    inst.Constants.Clear();
                    apply = true;
                }
                var all = MtrlBuilder.Constants(template.Mtrl).Where(c => c.Id != MtrlBuilder.EmissiveColor && c.Values.Length > 0).ToList();
                void Row(uint cid, float[] defaults)
                {
                    using var cidPush = ImRaii.PushId((int)cid);
                    var values = inst.Constants.TryGetValue(cid, out var set) ? (float[])set.Clone() : (float[])defaults.Clone();
                    bool changed = false;
                    ImGui.SetNextItemWidth(Limber.UI.Fit.Fixed(220f * ImGuiHelpers.GlobalScale));
                    string label = $"{MtrlBuilder.NameOf(cid)}###v";
                    switch(values.Length)
                    {
                        case 1:
                            changed = ImGui.DragFloat(label, ref values[0], 0.01f);
                            break;
                        case 2:
                        {
                            var v = new Vector2(values[0], values[1]);
                            if(changed = ImGui.DragFloat2(label, ref v, 0.01f))
                                (values[0], values[1]) = (v.X, v.Y);
                            break;
                        }
                        case 3:
                        {
                            var v = new Vector3(values[0], values[1], values[2]);
                            if(changed = ImGui.DragFloat3(label, ref v, 0.01f))
                                (values[0], values[1], values[2]) = (v.X, v.Y, v.Z);
                            break;
                        }
                        default:
                        {
                            var v = new Vector4(values[0], values[1], values[2], values[3]);
                            if(changed = ImGui.DragFloat4(label, ref v, 0.01f))
                                (values[0], values[1], values[2], values[3]) = (v.X, v.Y, v.Z, v.W);
                            break;
                        }
                    }
                    if(changed)
                        inst.Constants[cid] = values;
                    if(ImGui.IsItemDeactivatedAfterEdit())
                        apply = true;
                    if(ImGui.IsItemClicked(ImGuiMouseButton.Right) && inst.Constants.Remove(cid))
                        apply = true;
                    if(ImGui.IsItemHovered())
                        ImGui.SetTooltip((MtrlBuilder.TipOf(cid) ?? "Limber doesn't know what this one does.")
                            + "\nDefault: " + string.Join(", ", defaults.Select(d => d.ToString("0.###"))));
                    if(inst.Constants.ContainsKey(cid))
                    {
                        ImGui.SameLine();
                        ImGui.TextDisabled("(changed)");
                    }
                }
                foreach(var (cid, defaults) in all.Where(c => MtrlBuilder.IsUseful(c.Id)))
                    Row(cid, defaults);
                if(ImGui.TreeNodeEx("Others (mostly unused on imported props)###koi_mat_more"))
                {
                    foreach(var (cid, defaults) in all.Where(c => !MtrlBuilder.IsUseful(c.Id)))
                        Row(cid, defaults);
                    ImGui.TreePop();
                }
                ImGui.TreePop();
            }
            if(apply)
                KoiPropRuntime.Recolour(inst);
            ImGui.TreePop();
        }

        // Blend shapes.
        var shapes = rig.ShapeNames.ToList();
        if(shapes.Count > 0 && ImGui.TreeNodeEx($"Shapes ({shapes.Count})###koi_shapes", ImGuiTreeNodeFlags.DefaultOpen))
        {
            foreach(var name in shapes)
            {
                float v = inst.Shapes.GetValueOrDefault(name);
                ImGui.SetNextItemWidth(Limber.UI.Fit.Fixed(200f * ImGuiHelpers.GlobalScale));
                if(ImGui.SliderFloat($"{name}###koi_shape_{name}", ref v, 0f, 100f, "%.0f"))
                {
                    inst.Shapes[name] = v;
                    inst.Dirty = true;
                }
            }
            if(ImGui.SmallButton("All back to 0###koi_shapes_reset"))
            {
                inst.Shapes.Clear();
                inst.Dirty = true;
            }
            ImGui.TreePop();
        }

        // Dynamic bones.
        if(rig.Chains.Count > 0 && ImGui.TreeNodeEx($"Dynamic bones ({rig.Chains.Count})###koi_dyn"))
        {
            bool on = inst.Dynamics;
            if(ImGui.Checkbox("Swing###koi_dyn_on", ref on))
            {
                inst.Dynamics = on;
                inst.Settled = false;
                inst.Dirty = true;
            }
            ImGui.SetNextItemWidth(Limber.UI.Fit.Fixed(160f * ImGuiHelpers.GlobalScale));
            float k = inst.DynamicsStrength;
            if(ImGui.SliderFloat("Weight###koi_dyn_weight", ref k, 0f, 2f, "%.2f"))
            {
                inst.DynamicsStrength = k;
                inst.Settled = false;
            }
            if(ImGui.IsItemHovered())
                ImGui.SetTooltip("How hard gravity pulls the swinging parts (1: as the prop's own settings).");
            if(ImGui.SmallButton("Shake###koi_dyn_shake"))
            {
                for(int b = 0; b < inst.Now.Length; b++)
                    inst.Last[b] = inst.Now[b] + new Vector3(0.01f, 0.005f, 0.01f);
                inst.Settled = false;
            }
            ImGui.TreePop();
        }

        // Bones: nodes on the prop in the game (click one, then move, turn or size it with the gizmo), and the list.
        if(rig.Bones.Count > 1 && ImGui.TreeNodeEx($"Bones ({rig.Bones.Count})###koi_bones", ImGuiTreeNodeFlags.DefaultOpen))
        {
            bool show = KoiPropNodes.Show;
            if(ImGui.Checkbox("Show nodes###koi_nodes_show", ref show))
                KoiPropNodes.Show = show;
            if(ImGui.IsItemHovered())
                ImGui.SetTooltip("Its bones as dots on the prop in the game while this is open. Left-click a dot: choose it (the gizmo goes on it). Right-click a dot: back to how it rests.");
            ImGui.SameLine();
            foreach(var (label, tool) in new[] { ("Rotate", KoiPropNodes.Tool.Rotate), ("Move", KoiPropNodes.Tool.Move), ("Scale", KoiPropNodes.Tool.Scale) })
            {
                if(ImGui.RadioButton($"{label}###koi_nodes_{label}", KoiPropNodes.Op == tool))
                    KoiPropNodes.Op = tool;
                ImGui.SameLine();
            }
            bool worldAxes = KoiPropNodes.WorldAxes;
            if(ImGui.Checkbox("World axes###koi_nodes_world", ref worldAxes))
                KoiPropNodes.WorldAxes = worldAxes;
            if(ImGui.IsItemHovered())
                ImGui.SetTooltip("The gizmo lined up with the world instead of the bone (not for Scale).");

            // The chosen bone: its move, turn and size as numbers.
            int sel = KoiPropNodes.Selected;
            if(sel >= 0 && sel < rig.Bones.Count)
            {
                ImGui.TextUnformatted($"Chosen: {rig.Bones[sel].Name}");
                ImGui.SameLine();
                if(ImGui.SmallButton("Back to rest###koi_node_reset"))
                    KoiPropNodes.Reset(inst, sel);
                ImGui.SameLine();
                if(ImGui.SmallButton("Let go###koi_node_none"))
                    KoiPropNodes.Selected = -1;
                var move = inst.Move[sel];
                ImGui.SetNextItemWidth(Limber.UI.Fit.Fixed(220f * ImGuiHelpers.GlobalScale));
                if(ImGui.DragFloat3("Move###koi_node_move", ref move, 0.001f, -10f, 10f, "%.3f"))
                {
                    inst.Move[sel] = move;
                    inst.Dirty = true;
                    inst.Settled = false;
                }
                var turnSel = inst.Turn[sel];
                ImGui.SetNextItemWidth(Limber.UI.Fit.Fixed(220f * ImGuiHelpers.GlobalScale));
                if(ImGui.DragFloat3("Turn###koi_node_turn", ref turnSel, 0.5f, -180f, 180f, "%.0f°"))
                {
                    inst.Turn[sel] = turnSel;
                    inst.Dirty = true;
                    inst.Settled = false;
                }
                var size = inst.Size[sel];
                ImGui.SetNextItemWidth(Limber.UI.Fit.Fixed(220f * ImGuiHelpers.GlobalScale));
                if(ImGui.DragFloat3("Size###koi_node_size", ref size, 0.005f, 0.01f, 20f, "%.2f"))
                {
                    inst.Size[sel] = size;
                    inst.Dirty = true;
                    inst.Settled = false;
                }
                ImGui.SameLine();
                float even = (size.X + size.Y + size.Z) / 3f;
                ImGui.SetNextItemWidth(Limber.UI.Fit.Fixed(70f * ImGuiHelpers.GlobalScale));
                if(ImGui.DragFloat("###koi_node_size_all", ref even, 0.005f, 0.01f, 20f, "%.2f"))
                {
                    inst.Size[sel] = new Vector3(even);
                    inst.Dirty = true;
                    inst.Settled = false;
                }
                if(ImGui.IsItemHovered())
                    ImGui.SetTooltip("All three sizes at once.");
            }
            else
                ImGui.TextDisabled("Click a dot on the prop (or a bone below) to choose it.");

            ImGui.SetNextItemWidth(Limber.UI.Fit.Fixed(200f * ImGuiHelpers.GlobalScale));
            ImGui.InputTextWithHint("###koi_bone_search", "Search bones", ref _boneSearch, 64);
            ImGui.SameLine();
            if(ImGui.SmallButton("Reset all###koi_bones_reset"))
            {
                for(int b = 0; b < rig.Bones.Count; b++)
                    KoiPropNodes.Reset(inst, b);
            }
            using(var child = ImRaii.Child("###koi_bone_list", new Vector2(-1, 200 * ImGuiHelpers.GlobalScale), true))
            if(child.Success)
            {
                for(int b = 0; b < rig.Bones.Count; b++)
                {
                    var bone = rig.Bones[b];
                    if(_boneSearch.Length > 0 && !bone.Name.Contains(_boneSearch, StringComparison.OrdinalIgnoreCase))
                        continue;
                    int depth = 0;
                    for(int a = bone.Parent; a >= 0 && depth < 12; a = rig.Bones[a].Parent)
                        depth++;
                    using var bid = ImRaii.PushId(b);
                    float indent = _boneSearch.Length == 0 ? depth * 8f * ImGuiHelpers.GlobalScale : 0f;
                    if(indent > 0f)
                        ImGui.Indent(indent);
                    bool posed = inst.Turn[b] != Vector3.Zero || inst.Move[b] != Vector3.Zero || inst.Size[b] != Vector3.One;
                    if(ImGui.Selectable($"{bone.Name}{(posed ? "  (posed)" : "")}###pick", KoiPropNodes.Selected == b))
                        KoiPropNodes.Selected = b;
                    if(ImGui.IsItemClicked(ImGuiMouseButton.Right))
                        KoiPropNodes.Reset(inst, b);
                    if(ImGui.IsItemHovered())
                        ImGui.SetTooltip("Click: choose it (the gizmo goes on it). Right-click: back to how it rests.");
                    if(indent > 0f)
                        ImGui.Unindent(indent);
                }
            }
            ImGui.TreePop();
        }
    }
}
