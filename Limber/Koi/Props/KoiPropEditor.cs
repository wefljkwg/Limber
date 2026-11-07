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
                ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
                ImGui.SliderFloat(label, ref value, min, max, "%.2f");
                if(ImGui.IsItemDeactivatedAfterEdit())
                    apply = true;
                if(ImGui.IsItemHovered())
                    ImGui.SetTooltip(tip + " (Put on when you let go.)");
            }
            Slider("Brightness###koi_bright", ref inst.Brightness, 0.1f, 1.5f, "Darker or lighter: lower it for props that look far too bright.");
            Slider("Contrast###koi_contrast", ref inst.Contrast, 0.3f, 2f, "Flatter or punchier.");
            Slider("Colour strength###koi_sat", ref inst.Saturation, 0f, 2f, "Greyer (0) to more vivid.");
            Slider("Shine###koi_shine", ref inst.Shine, 0f, 1f, "How much light it reflects (0: matte).");
            Slider("Roughness###koi_rough", ref inst.Roughness, 0f, 1f, "Sharp highlights (low) or soft, spread ones (high).");
            var glow = inst.Glow;
            if(ImGui.ColorEdit3("Glow###koi_glow", ref glow, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.HDR | ImGuiColorEditFlags.Float))
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
                ImGui.TextDisabled("The game shader's own settings for this prop. Right-click one: back to the default.");
                foreach(var (cid, defaults) in MtrlBuilder.Constants(template.Item2))
                {
                    if(cid == MtrlBuilder.EmissiveColor || defaults.Length == 0)
                        continue;
                    using var cidPush = ImRaii.PushId((int)cid);
                    var values = inst.Constants.TryGetValue(cid, out var set) ? (float[])set.Clone() : (float[])defaults.Clone();
                    bool changed = false;
                    ImGui.SetNextItemWidth(240 * ImGuiHelpers.GlobalScale);
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
                    if(inst.Constants.ContainsKey(cid))
                    {
                        ImGui.SameLine();
                        ImGui.TextDisabled("(changed)");
                    }
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
                ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
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
            ImGui.SetNextItemWidth(160 * ImGuiHelpers.GlobalScale);
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

        // Bones.
        if(rig.Bones.Count > 1 && ImGui.TreeNodeEx($"Bones ({rig.Bones.Count})###koi_bones"))
        {
            ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
            ImGui.InputTextWithHint("###koi_bone_search", "Search bones", ref _boneSearch, 64);
            ImGui.SameLine();
            if(ImGui.SmallButton("Reset all###koi_bones_reset"))
            {
                Array.Clear(inst.Turn);
                inst.Dirty = true;
                inst.Settled = false;
            }
            using(var child = ImRaii.Child("###koi_bone_list", new Vector2(-1, 220 * ImGuiHelpers.GlobalScale), true))
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
                    if(_boneSearch.Length == 0)
                        ImGui.Indent(depth * 8f * ImGuiHelpers.GlobalScale);
                    var turn = inst.Turn[b];
                    ImGui.SetNextItemWidth(180 * ImGuiHelpers.GlobalScale);
                    if(ImGui.DragFloat3($"{bone.Name}###turn", ref turn, 0.5f, -180f, 180f, "%.0f°"))
                    {
                        inst.Turn[b] = turn;
                        inst.Dirty = true;
                        inst.Settled = false;
                    }
                    if(ImGui.IsItemClicked(ImGuiMouseButton.Right))
                    {
                        inst.Turn[b] = Vector3.Zero;
                        inst.Dirty = true;
                    }
                    if(ImGui.IsItemHovered())
                        ImGui.SetTooltip("Turn (yaw, pitch, roll) on top of how it rests. Right-click: back to rest.");
                    if(_boneSearch.Length == 0)
                        ImGui.Unindent(depth * 8f * ImGuiHelpers.GlobalScale);
                }
            }
            ImGui.TreePop();
        }
    }
}
