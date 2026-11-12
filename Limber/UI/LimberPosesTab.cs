using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Limber.Rig;

namespace Limber.UI;

/// <summary>The Limber Poses page: ready-made whole-body poses, put on the chosen character as its FK pose.</summary>
public sealed class LimberPosesTab
{
    private readonly Selection _selection;
    private readonly RigSettings _config;
    private readonly LimberPoses _poses;
    private readonly ActorTransforms _actors;
    // How far each character was lowered by the pose on it (taken back off with the pose, or when another goes on).
    private readonly System.Collections.Generic.Dictionary<nint, float> _lowered = new();
    private bool _mirror;
    // The variant chosen for each pose that has a choice (by name).
    private readonly System.Collections.Generic.Dictionary<string, string> _variant = new();
    private string _status = "";
    /// <summary>IK posing (to hand bones over to it).</summary>
    public RigService? Rigs { get; set; }
    // Bones ticked to hand over, and bones to take off FK a few frames from now (once IK has caught where they are).
    private readonly System.Collections.Generic.HashSet<string> _pick = new() { "j_kosi", "j_te_l", "j_te_r", "j_asi_d_l", "j_asi_d_r", "j_kao" };
    private (string Character, string[] Bones, int Frames)? _handOver;

    public LimberPosesTab(Selection selection, RigSettings config, LimberPoses poses, ActorTransforms actors)
    {
        _actors = actors;
        _selection = selection;
        _config = config;
        _poses = poses;
    }

    /// <summary>Framework thread: the poses worked out, saved as the characters' FK poses.</summary>
    public void TakeResults()
    {
        // (bones handed to IK come off FK once IK has caught where they are)
        if(_handOver is { } ho)
        {
            int left = ho.Frames - 1;
            if(left <= 0)
            {
                if(_config.FkPoses != null && _config.FkPoses.TryGetValue(ho.Character, out var f))
                {
                    foreach(var b in ho.Bones)
                        f.Bones.Remove(b);
                    _config.Save();
                }
                _handOver = null;
            }
            else
                _handOver = ho with { Frames = left };
        }


        while(_poses.TryTake(out var r))
        {
            var all = _config.FkPoses ??= new();
            if(!all.TryGetValue(r.Character, out var fk))
                all[r.Character] = fk = new FkPoseSettings();
            // (a hold pose: these bones hold the pose's turns; finger poses and other bones already posed stay)
            // (switching between still and on top of the animation: the other kind's bones don't mix with it)
            if(fk.Layered != r.Layered)
                fk.Bones.Clear();
            fk.Layered = r.Layered;
            fk.Enabled = true;
            foreach(var (bone, s) in r.Bones)
                fk.Bones[bone] = s;
            // (the whole character down to the floor, or back up, with Move character: everything on it comes along)
            Lower(r.Address, r.Index, r.Drop);
            _config.Save();
            _status = $"{r.Pose} on {r.Character}.";
        }
    }

    private void Lower(nint address, ushort index, float drop)
    {
        float was = _lowered.TryGetValue(address, out var w) ? w : 0f;
        if(MathF.Abs(drop - was) < 1e-5f)
            return;
        lock(_actors.Sync)
        {
            var o = _actors.GetOrAdd(address, index);
            o.Move = new Vector3(o.Move.X, o.Move.Y - was + drop, o.Move.Z);
        }
        if(drop == 0f)
            _lowered.Remove(address);
        else
            _lowered[address] = drop;
    }

    public void Draw()
    {
        var actor = _selection.Actor;
        if(!actor.IsSet)
        {
            ImGui.TextDisabled("Choose a character at the top of the window.");
            return;
        }
        ImGui.TextWrapped("Ready-made poses, made to fit the chosen character's own skeleton. Each one is put on as the "
            + "character's FK pose, so you can fine-tune it on the FK Posing page (and IK posing works on top).");
        ImGui.Checkbox("Mirrored (left and right swapped)###lposes_mirror", ref _mirror);
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip("The other side: left and right swapped.");
        ImGui.SameLine();
        bool freeze = _config.LimberPoseFreeze;
        if(ImGui.Checkbox("Freeze###lposes_freeze", ref freeze))
        {
            _config.LimberPoseFreeze = freeze;
            _config.Save();
        }
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip("Off (default): the pose goes on top of the animation, so the character keeps breathing and moving a little in it.\n"
                + "On: the posed bones hold the pose exactly and stop animating.\n(Takes effect the next time a pose is put on.)");
        ImGui.Spacing();

        float width = 160 * ImGuiHelpers.GlobalScale;
        var shown = new System.Collections.Generic.HashSet<string>();
        foreach(var first in LimberPoses.All)
        {
            if(!shown.Add(first.Name))
                continue;
            var variants = Array.FindAll(LimberPoses.All, p => p.Name == first.Name);
            var pose = first;
            if(variants.Length > 1 && _variant.TryGetValue(first.Name, out var chosen))
                pose = Array.Find(variants, p => p.Variant == chosen) ?? first;
            if(ImGui.Button($"{pose.Name}###lposes_{pose.Name}", new Vector2(width, 0)))
                _poses.Put(actor.Address, actor.ObjectIndex, actor.Name, _mirror ? LimberPoses.Mirrored(pose) : pose, _config.LimberPoseFreeze);
            if(variants.Length > 1)
            {
                foreach(var v in variants)
                {
                    ImGui.SameLine();
                    if(ImGui.RadioButton($"{v.Variant}###lposes_{v.Name}_{v.Variant}", ReferenceEquals(v, pose)))
                        _variant[v.Name] = v.Variant;
                }
            }
            ImGui.SameLine();
            ImGui.TextDisabled(pose.Tip);
        }

        ImGui.Spacing();
        ImGui.Separator();
        bool has = _config.FkPoses != null && _config.FkPoses.TryGetValue(actor.Name, out var fk) && fk.Bones.Count > 0;
        using(ImRaii.Disabled(!has))
        {
            if(ImGui.Button("Take the pose off###lposes_clear"))
            {
                _config.FkPoses!.Remove(actor.Name);
                Lower(actor.Address, actor.ObjectIndex, 0f);
                _config.Save();
                _status = $"{actor.Name} is back to their animation.";
            }
        }
        if(ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Clears the character's FK pose (the pose from here and anything set on the FK Posing page): back to the animation.");
        if(_status.Length > 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(_status);
        }
        DrawHandOver(actor);
    }

    // Port the pose: chosen bones held by IK posing (where the pose has them), and / or only those kept on FK.
    private void DrawHandOver(ActorRef actor)
    {
        if(_config.FkPoses == null || !_config.FkPoses.TryGetValue(actor.Name, out var fk) || fk.Bones.Count == 0)
            return;
        ImGui.Spacing();
        if(!ImGui.CollapsingHeader("Port the pose to IK / FK posing###lposes_port"))
            return;
        ImGui.TextWrapped("The pose is already the character's FK pose: fine-tune it on the FK Posing page, and IK posing works on top. "
            + "Here you can hand chosen bones to IK posing (held where the pose has them, then dragged with the dots), or keep only chosen bones on FK.");
        if(ImGui.SmallButton("Ends###lposes_pick_ends"))
        {
            _pick.Clear();
            foreach(var b in new[] { "j_kosi", "j_te_l", "j_te_r", "j_asi_d_l", "j_asi_d_r", "j_kao" })
                _pick.Add(b);
        }
        ImGui.SameLine();
        if(ImGui.SmallButton("All###lposes_pick_all"))
            foreach(var b in fk.Bones.Keys)
                _pick.Add(b);
        ImGui.SameLine();
        if(ImGui.SmallButton("None###lposes_pick_none"))
            _pick.Clear();
        using(var child = ImRaii.Child("###lposes_port_list", new Vector2(-1, 150 * ImGuiHelpers.GlobalScale), true))
        {
            if(child.Success)
                foreach(var bone in System.Linq.Enumerable.OrderBy(fk.Bones.Keys, BoneOrder))
                {
                    bool on = _pick.Contains(bone);
                    if(ImGui.Checkbox($"{BoneLabel(bone)}###lposes_pick_{bone}", ref on))
                    {
                        if(on)
                            _pick.Add(bone);
                        else
                            _pick.Remove(bone);
                    }
                }
        }
        var picked = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Where(fk.Bones.Keys, _pick.Contains));
        using(ImRaii.Disabled(picked.Length == 0 || Rigs == null))
        {
            if(ImGui.Button("Hold with IK###lposes_ik"))
                ToIk(actor, picked, takeOffFk: false);
            if(ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("IK posing holds each ticked bone where the pose has it now (place and turn). The FK pose stays under it.\n"
                    + "Drag their dots to move them; the arms, legs and back bend to follow.");
            ImGui.SameLine();
            if(ImGui.Button("Hand over to IK###lposes_ik_move"))
                ToIk(actor, picked, takeOffFk: true);
            if(ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("The same, and the ticked bones then come off the FK pose: IK alone holds them.");
        }
        using(ImRaii.Disabled(picked.Length == 0))
        {
            ImGui.SameLine();
            if(ImGui.Button("Keep only these on FK###lposes_fk_only"))
            {
                foreach(var b in System.Linq.Enumerable.ToList(fk.Bones.Keys))
                    if(!_pick.Contains(b))
                        fk.Bones.Remove(b);
                _config.Save();
                _status = $"Only the ticked bones are kept on {actor.Name}'s FK pose; the rest animate again.";
            }
            if(ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("The unticked bones come off the FK pose (back to the animation); the ticked stay, to edit on the FK Posing page.");
        }
    }

    private void ToIk(ActorRef actor, string[] bones, bool takeOffFk)
    {
        if(Rigs == null)
            return;
        Rigs.Checkpoint();
        lock(Rigs.Sync)
        {
            var rig = Rigs.GetOrCreateRig(actor.Address, actor.ObjectIndex, actor.Name);
            foreach(var bone in bones)
            {
                var goal = rig.GetOrAdd(bone);
                goal.Enabled = true;
                goal.HoldPosition = true;
                goal.LockRotation = true;
                goal.HasTarget = false;
                goal.CapturePosition = true;
                goal.CaptureRotation = true;
            }
        }
        if(takeOffFk)
            _handOver = (actor.Name, bones, 6);
        _status = takeOffFk ? $"{bones.Length} bone(s) handed over to IK posing." : $"{bones.Length} bone(s) held with IK posing.";
        if(!_config.Enabled)
            _status += " (Turn Posing on, IK Posing page, for IK to hold them.)";
    }

    private static int BoneOrder(string bone)
    {
        string[] order = ["j_kosi", "j_sebo_a", "j_sebo_b", "j_sebo_c", "j_kubi", "j_kao", "j_sako", "j_ude_a", "j_ude_b", "j_te", "j_asi_a", "j_asi_b", "j_asi_c", "j_asi_d", "j_asi_e"];
        for(int i = 0; i < order.Length; i++)
            if(bone.StartsWith(order[i], StringComparison.Ordinal))
                return i * 2 + (bone.EndsWith("_r", StringComparison.Ordinal) ? 1 : 0);
        return 100;
    }

    private static string BoneLabel(string bone)
    {
        string side = bone.EndsWith("_l", StringComparison.Ordinal) ? " (left)" : bone.EndsWith("_r", StringComparison.Ordinal) ? " (right)" : "";
        string b = side.Length > 0 ? bone[..^2] : bone;
        string name = b switch
        {
            "j_kosi" => "Hips", "j_sebo_a" => "Lower back", "j_sebo_b" => "Middle back", "j_sebo_c" => "Chest", "j_kubi" => "Neck", "j_kao" => "Head",
            "j_sako" => "Shoulder", "j_ude_a" => "Upper arm", "j_ude_b" => "Forearm", "j_te" => "Hand",
            "j_asi_a" => "Thigh", "j_asi_b" => "Knee", "j_asi_c" => "Shin", "j_asi_d" => "Foot", "j_asi_e" => "Toes",
            _ => b,
        };
        return $"{name}{side}  ({bone})";
    }
}
