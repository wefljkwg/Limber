using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Limber.BodyShape;
using Limber.Rig;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Limber.UI;

// The pages the main window's list opens (each one part of what used to be the Limber tab), and Bone scale.
public sealed partial class LimberTab
{
    /// <summary>Pose page: the posing switch, then grabbing and holding bones.</summary>
    public void DrawPosePage()
    {
        DrawGeneral();
        ImGui.Spacing();
        DrawPoseTab();
    }

    /// <summary>Hands page: preset finger poses.</summary>
    public void DrawHandsPage()
    {
        var actor = _selection.Actor;
        if(!actor.IsSet)
        {
            ImGui.TextDisabled("Choose a character first.");
            return;
        }
        DrawHandPoses(actor);
    }

    public void DrawAttachPage() => DrawAttachTab();

    public void DrawMovePage() => DrawActorTab();

    public void DrawPosingSettingsPage() => DrawSettingsTab();

    /// <summary>Whether posing is on (for the start page's checklist).</summary>
    public bool PosingOn => _config.Enabled;

    public bool PosingHookReady => _rigs.HookReady;

    public bool HasCharacter => _selection.Actor.IsSet;

    // ---- Bone scale

    private string _scaleSearch = string.Empty;
    private bool _scaleSameAxes = true;
    private bool _scaleOnlyChanged;

    public void DrawBoneScalePage()
    {
        var actor = _selection.Actor;
        if(!actor.IsSet)
        {
            ImGui.TextDisabled("Choose a character first.");
            return;
        }
        string who = actor.Name ?? "";
        if(who.Length == 0)
        {
            ImGui.TextDisabled("This character has no name to keep its bone scales under.");
            return;
        }
        bool adult = Limber.Koi.Safety.AdultAllowed(actor.Address);
        var all = _config.BoneScales ??= new();
        all.TryGetValue(who, out var entries);

        ImGui.TextWrapped("Make any bone bigger or smaller. For each bone you choose whether the bones below it grow with it, " +
                          "whether they move out with it, and whether it follows the size of the bones above it.");
        ImGui.TextDisabled("Kept for this character by name; only on your screen. Works in and out of GPose (see Advanced).");
        ImGui.Spacing();

        // Which bone: the one picked on the character (a dot), or from the list.
        SeparatorText("Bone");
        var bone = _selection.Bone;
        if(bone != null && !BoneScales.Allowed(bone, adult))
            bone = null;
        BoneSnapshot? snap;
        lock(_rigs.Sync)
            snap = _rigs.GetSnapshot(actor.Address);
        if(bone != null && snap != null && snap.IndexOf(bone) is int at && at >= 0 && snap.Parts[at] != 0)
        {
            ImGui.TextDisabled($"{HandleCatalog.Label(bone)} is on the face / hair skeleton, which can't be scaled here yet.");
            bone = null;
        }
        if(bone == null)
            ImGui.TextDisabled("Click a dot on the character, or pick a bone from the list below.");
        else
        {
            ImGui.TextColored(Accent, HandleCatalog.Label(bone));
            if(HandleCatalog.Label(bone) != bone)
            {
                ImGui.SameLine();
                ImGui.TextDisabled($"({bone})");
            }
        }
        DrawScaleBoneList(actor, adult, entries);

        if(bone != null)
        {
            SeparatorText("Scale");
            var e = entries != null && entries.TryGetValue(bone, out var found) ? found.Copy() : new BoneScaleEntry();
            bool changed = false;
            ImGui.Checkbox("Same on every axis###lmb_bs_same", ref _scaleSameAxes);
            Tooltip("On: one size for the whole bone. Off: length, width and depth separately (the bone's own axes).");
            var s = e.Scale;
            if(_scaleSameAxes)
            {
                float u = (s.X + s.Y + s.Z) / 3f;
                ImGui.SetNextItemWidth(-90 * ImGuiHelpers.GlobalScale);
                if(ImGui.DragFloat("Size###lmb_bs_size", ref u, 0.005f, BoneScales.Min, BoneScales.Max, "%.3f", ImGuiSliderFlags.AlwaysClamp))
                {
                    e.Scale = new Vector3(u);
                    changed = true;
                }
            }
            else
            {
                ImGui.SetNextItemWidth(-90 * ImGuiHelpers.GlobalScale);
                if(ImGui.DragFloat3("X / Y / Z###lmb_bs_xyz", ref s, 0.005f, BoneScales.Min, BoneScales.Max, "%.3f", ImGuiSliderFlags.AlwaysClamp))
                {
                    e.Scale = s;
                    changed = true;
                }
            }
            Tooltip("1 = as it is. Ctrl+click to type a value.");

            ImGui.Spacing();
            bool childrenScale = e.ChildrenScale, childrenMove = e.ChildrenMove, follow = e.FollowParent;
            if(ImGui.Checkbox("Bones below it get bigger too###lmb_bs_cs", ref childrenScale))
            {
                e.ChildrenScale = childrenScale;
                changed = true;
            }
            Tooltip("On: everything below this bone is scaled with it (a bigger hand has bigger fingers).\nOff: only this bone's own part of the body changes size.");
            if(ImGui.Checkbox("Bones below it move with it###lmb_bs_cm", ref childrenMove))
            {
                e.ChildrenMove = childrenMove;
                changed = true;
            }
            Tooltip("On: the bones below move out (or in) as it grows (a longer upper arm puts the elbow further out).\nOff: they stay where they were; only its size changes.");
            if(ImGui.Checkbox("Follows the size of the bones above it###lmb_bs_fp", ref follow))
            {
                e.FollowParent = follow;
                changed = true;
            }
            Tooltip("On: when a bone above this one is scaled, this one is too.\nOff: this bone keeps its own size whatever the bones above it do (it still moves with them).");

            if(ImGui.Button("Reset this bone###lmb_bs_reset"))
            {
                e = new BoneScaleEntry();
                changed = true;
            }
            if(changed)
                SetBoneScale(who, bone, e);
        }

        if(_scaleDirty && !ImGui.IsAnyItemActive())
        {
            _scaleDirty = false;
            _config.Save();
        }

        // Everything scaled on this character.
        int count = entries?.Count ?? 0;
        SeparatorText($"Scaled bones ({count})");
        if(count == 0)
            ImGui.TextDisabled("None yet.");
        else
        {
            string? remove = null;
            using(var table = ImRaii.Table("###lmb_bs_table", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            {
                if(table.Success)
                {
                    foreach(var (name, en) in entries!.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList())
                    {
                        using var id = ImRaii.PushId(name);
                        ImGui.TableNextRow();
                        ImGui.TableNextColumn();
                        if(ImGui.Selectable(HandleCatalog.Label(name), _selection.Bone == name))
                            _selection.Bone = name;
                        ImGui.TableNextColumn();
                        var sc = en.Scale;
                        string size = MathF.Abs(sc.X - sc.Y) < 1e-4f && MathF.Abs(sc.Y - sc.Z) < 1e-4f ? $"{sc.X:0.###}" : $"{sc.X:0.##} / {sc.Y:0.##} / {sc.Z:0.##}";
                        var flags = new List<string>();
                        if(!en.ChildrenScale) flags.Add("below keep size");
                        if(!en.ChildrenMove) flags.Add("below stay");
                        if(!en.FollowParent) flags.Add("own size");
                        ImGui.TextUnformatted(flags.Count > 0 ? $"{size}  ({string.Join(", ", flags)})" : size);
                        ImGui.TableNextColumn();
                        if(ImGui.SmallButton("Remove"))
                            remove = name;
                    }
                }
            }
            if(remove != null)
                SetBoneScale(who, remove, new BoneScaleEntry());
            if(ImGui.Button("Remove all###lmb_bs_clear") && ImGui.GetIO().KeyCtrl)
            {
                lock(entries!)
                    entries.Clear();
                _config.Save();
            }
            Tooltip("Hold Ctrl and click: every bone of this character back to its own size.");
        }
    }

    private void DrawScaleBoneList(ActorRef actor, bool adult, Dictionary<string, BoneScaleEntry>? entries)
    {
        BoneSnapshot? snapshot;
        lock(_rigs.Sync)
            snapshot = _rigs.GetSnapshot(actor.Address);
        if(snapshot == null)
        {
            ImGui.TextDisabled("This character's bones aren't available right now (Limber needs to be running here).");
            return;
        }
        ImGui.SetNextItemWidth(-130 * ImGuiHelpers.GlobalScale);
        ImGui.InputTextWithHint("###lmb_bs_search", "Search (hand, thigh, tail...)", ref _scaleSearch, 64);
        ImGui.SameLine();
        ImGui.Checkbox("Scaled only###lmb_bs_only", ref _scaleOnlyChanged);
        using var child = ImRaii.Child("###lmb_bs_list", new Vector2(-1, 130 * ImGuiHelpers.GlobalScale), true);
        if(!child.Success)
            return;
        for(int i = 0; i < snapshot.Names.Length; i++)
        {
            var name = snapshot.Names[i];
            // (the body's own skeleton: the face, hair and other extra skeletons can't be scaled here yet)
            if(snapshot.Parts[i] != 0 || !HandleCatalog.IsPickable(name, adult) || !BoneScales.Allowed(name, adult))
                continue;
            bool scaled = entries != null && entries.ContainsKey(name);
            if(_scaleOnlyChanged && !scaled)
                continue;
            string label = HandleCatalog.Label(name);
            if(_scaleSearch.Length > 0 && !name.Contains(_scaleSearch, StringComparison.OrdinalIgnoreCase)
                && !label.Contains(_scaleSearch, StringComparison.OrdinalIgnoreCase))
                continue;
            string text = label == name ? name : $"{label}  ({name})";
            if(scaled)
                text = "* " + text;
            if(ImGui.Selectable($"{text}###bs_pick_{name}", _selection.Bone == name))
                _selection.Bone = name;
        }
    }

    private void SetBoneScale(string who, string bone, BoneScaleEntry e)
    {
        var all = _config.BoneScales ??= new();
        if(!all.TryGetValue(who, out var entries))
        {
            if(e.IsDefault)
                return;
            all[who] = entries = new Dictionary<string, BoneScaleEntry>();
        }
        lock(entries)
        {
            if(e.IsDefault)
                entries.Remove(bone);
            else
                entries[bone] = e;
        }
        // (saved once the slider is let go, not every frame of a drag)
        _scaleDirty = true;
    }

    private bool _scaleDirty;
}
