using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Limber.BodyShape;
using Limber.Rig;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Newtonsoft.Json.Linq;

namespace Limber.UI;

// The pages the main window's list opens (each one part of what used to be the Limber tab), and Bone scale.
public sealed partial class LimberTab
{
    /// <summary>Freeze motion and pose files (set by the plugin).</summary>
    public Limber.Rig.MotionFreeze? Freeze { get; set; }
    /// <summary>Motion devices: letting go of a bone one moves turns the device off (or it takes hold again).</summary>
    public Limber.Rig.MotionService? Motions { get; set; }

    private readonly Dalamud.Interface.ImGuiFileDialog.FileDialogManager _poseFiles = new();
    private volatile string? _poseStatus;
    private (ushort Id, string Key)? _poseAnimation;
    private bool _poseDuo;
    private bool _poseOral; // a duo pose that's an Oral scene: saves Oral's settings, and starts Oral when loaded
    private ActorRef _posePartner;
    private bool _importPositions;

    /// <summary>The file pickers (drawn every frame by the main window, whichever page is open).</summary>
    public void DrawDialogs()
    {
        ReceiveFromSync();
        if(System.Threading.Interlocked.Exchange(ref _pendingExport, null) is { } export)
        {
            var (text, name) = export;
            _poseStatus = "Choose where to save it.";
            _poseFiles.SaveFileDialog("Save the pose", "Pose files{.pose},.*", name, ".pose", (ok, path) =>
            {
                if(!ok || string.IsNullOrEmpty(path))
                {
                    _poseStatus = null;
                    return;
                }
                try
                {
                    System.IO.File.WriteAllText(path, text);
                    _poseStatus = "Saved " + System.IO.Path.GetFileName(path) + ".";
                }
                catch(Exception e)
                {
                    _poseStatus = "Couldn't save: " + e.Message;
                }
            }, PoseFolder());
        }
        _poseFiles.Draw();
    }

    private Tuple<string, string>? _pendingExport;

    /// <summary>The "Freeze motion" toggle at the top of the window (for the chosen character).</summary>
    public void DrawFreezeToggle()
    {
        if(Freeze == null)
            return;
        var actor = _selection.Actor;
        using(ImRaii.Disabled(!actor.IsSet))
        {
            bool frozen = actor.IsSet && Freeze.IsFrozen(actor.Address);
            if(ImGui.Checkbox("Freeze motion###limber_freeze", ref frozen))
            {
                if(frozen)
                    Freeze.Freeze(actor.Address, actor.ObjectIndex);
                else
                    Freeze.Resume(actor.Address);
            }
        }
        Tooltip("Holds the chosen character exactly as they are now: animation, face, hair and clothes stop moving.\n" +
                "Posing, body shape and the soft bodies still work on top. Untick to let the animation play again.");
        ImGui.SameLine();
        if(ImGui.Button("Freeze all actors###limber_freeze_every"))
        {
            // In GPose: every GPose actor (you and the ones added); outside it: everyone Limber can pose that's drawn.
            bool gpose = Plugin.ClientState.IsGPosing;
            foreach(var c in _selection.ListCharacters())
                if(!gpose || c.ObjectIndex >= 200)
                    if(!Freeze.IsFrozen(c.Address))
                        Freeze.Freeze(c.Address, c.ObjectIndex);
        }
        Tooltip("Freezes every actor at once (in GPose: you and every actor added). \"Resume all\" lets them all move again.");
        int others = Freeze.Count - (actor.IsSet && Freeze.IsFrozen(actor.Address) ? 1 : 0);
        if(others > 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"(+{others} more)");
            ImGui.SameLine();
            if(ImGui.SmallButton("Resume all###limber_freeze_all"))
                Freeze.ResumeAll();
            Tooltip("Lets every frozen character move again.");
        }
    }

    /// <summary>Pose page: the posing switch, pose files, then grabbing and holding bones.</summary>
    public void DrawPosePage()
    {
        DrawGeneral();
        ImGui.Spacing();
        DrawPoseFiles();
        DrawPoseTab();
    }

    private static string PoseFolder()
    {
        var dir = System.IO.Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "Poses");
        try
        {
            System.IO.Directory.CreateDirectory(dir);
        }
        catch
        {
            // (the picker opens wherever it can)
        }
        return dir;
    }

    private void DrawPoseFiles()
    {
        if(Freeze == null || !ImGui.CollapsingHeader("Pose files (export / import)###limber_pose_files"))
            return;
        var actor = _selection.Actor;
        using(ImRaii.Disabled(!actor.IsSet))
        {
            if(ImGui.Button("Export pose...###limber_pose_export"))
            {
                if(_poseDuo)
                    ExportDuo(actor);
                else
                {
                var who = actor;
                _poseStatus = "Reading the pose...";
                Freeze.Export(who.Address, who.ObjectIndex, (json, error) =>
                {
                    if(json == null)
                    {
                        _poseStatus = error;
                        return;
                    }
                    // (this runs in the bone update: the save picker opens on the next window frame)
                    _pendingExport = Tuple.Create(json.ToString(Newtonsoft.Json.Formatting.Indented), string.IsNullOrEmpty(who.Name) ? "pose" : who.Name);
                });
                }
            }
            Tooltip("Saves the chosen character's pose as you see it (body, face and hair) as a .pose file.\nOther posing tools can load it too.");
            ImGui.SameLine();
            if(ImGui.Button("Import pose...###limber_pose_import"))
            {
                var who = actor;
                bool positions = _importPositions;
                _poseFiles.OpenFileDialog("Load a pose", "Pose files{.pose},.*", (ok, paths) =>
                {
                    if(!ok || paths.Count == 0)
                        return;
                    try
                    {
                        var text = System.IO.File.ReadAllText(paths[0]);
                        if(IsDuo(text))
                        {
                            if(Partner(who) is not { } second)
                            {
                                _poseStatus = "That's a duo pose: choose Duo and the partner first.";
                                return;
                            }
                            if(ImportDuo(who, second, text, positions))
                                ShareWithSync(text, who, second, positions);
                            return;
                        }
                        if(ImportSolo(who, text, positions))
                            ShareWithSync(text, who, null, positions);
                    }
                    catch(Exception e)
                    {
                        _poseStatus = "Couldn't read the file: " + e.Message;
                    }
                }, 1, PoseFolder());
            }
            Tooltip("Puts a .pose file (Limber's or another posing tool's) on the chosen character and holds it (Freeze motion goes on).");
        }
        // Solo or duo: a duo saves (and loads) the partner too, where they stand against each other, both animations and
        // every bone attached between the two.
        if(ImGui.RadioButton("Solo###limber_pose_solo", !_poseDuo))
            _poseDuo = false;
        ImGui.SameLine();
        if(ImGui.RadioButton("Duo###limber_pose_duo", _poseDuo))
            _poseDuo = true;
        Tooltip("Duo: the pose file holds both characters (poses, animations, where they stand against each other and the bones "
            + "attached between them). Loading one puts it all back on the chosen character and the partner.");
        if(_poseDuo)
        {
            var others = _selection.ListCharacters().Where(c => c.Address != actor.Address).ToList();
            int current = others.FindIndex(c => c.ObjectIndex == _posePartner.ObjectIndex && c.Name == _posePartner.Name);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);
            if(ImGui.BeginCombo("Partner###limber_pose_partner", current >= 0 ? others[current].Label : "(choose)"))
            {
                for(int i = 0; i < others.Count; i++)
                    if(ImGui.Selectable($"{others[i].Label}###limber_pose_partner_{i}", i == current))
                        _posePartner = others[i];
                ImGui.EndCombo();
            }
            Tooltip("The other character in the duo pose.");
            ImGui.SameLine();
            ImGui.Checkbox("Oral###limber_pose_oral", ref _poseOral);
            Tooltip("An Oral scene: the duo file also keeps Oral's settings (motion, head, mouth, lips...) and who is the mouth, "
                + "and loading it starts Oral on the two with them. Loading a file saved with Oral starts it whether this is ticked or not.");
        }
        ImGui.Checkbox("Import positions too (same body)###limber_pose_positions", ref _importPositions);
        Tooltip("Off (recommended): only the turns are used, so the pose fits any body and race (the hips still go where the file has them, "
                + "so two characters line up as they were).\n" +
                "On: every bone also goes exactly where the file has it; use it for a pose made on this same character.");
        if(!actor.IsSet)
            ImGui.TextDisabled("Choose a character first.");
        else if(_poseStatus is { } status)
            ImGui.TextDisabled(status);
        if(actor.IsSet && _poseAnimation is { } anim)
        {
            ImGui.TextDisabled($"Saved on the animation: {(anim.Key.Length > 0 ? anim.Key : "#" + anim.Id)}");
            ImGui.SameLine();
            if(ImGui.SmallButton("Play it###limber_pose_play_anim"))
                _poseStatus = Limber.Rig.Animations.Play(actor.Address, actor.ObjectIndex, anim.Id);
            Tooltip("Plays the game animation the pose was saved on (untick Freeze motion to see it move).");
        }
        ImGui.Spacing();
    }

    // ---- Duo pose files

    private static bool IsDuo(string json)
    {
        try
        {
            return Newtonsoft.Json.Linq.JObject.Parse(json)["TypeName"]?.Value<string>() == "Limber Duo Pose";
        }
        catch
        {
            return false;
        }
    }

    private ActorRef? Partner(ActorRef actor)
    {
        foreach(var c in _selection.ListCharacters())
            if(c.Address != actor.Address && c.ObjectIndex == _posePartner.ObjectIndex && c.Name == _posePartner.Name)
                return c;
        return null;
    }

    private static Newtonsoft.Json.Linq.JArray V3(Vector3 v) => new(v.X, v.Y, v.Z);
    private static Newtonsoft.Json.Linq.JArray Q4(Quaternion q) => new(q.X, q.Y, q.Z, q.W);
    private static Vector3 ReadV3(Newtonsoft.Json.Linq.JToken? t) => t is Newtonsoft.Json.Linq.JArray a && a.Count >= 3
        ? new Vector3(a[0].Value<float>(), a[1].Value<float>(), a[2].Value<float>()) : Vector3.Zero;
    private static Quaternion ReadQ4(Newtonsoft.Json.Linq.JToken? t) => t is Newtonsoft.Json.Linq.JArray a && a.Count >= 4
        ? Quaternion.Normalize(new Quaternion(a[0].Value<float>(), a[1].Value<float>(), a[2].Value<float>(), a[3].Value<float>())) : Quaternion.Identity;

    private static bool Root(ActorRef who, out Vector3 position, out Quaternion rotation)
    {
        position = Vector3.Zero;
        rotation = Quaternion.Identity;
        if(!Limber.Core.ActorSkeleton.TryGet(who.Address, who.ObjectIndex, new Limber.Core.SkeletonInfoCache(), out var sk) || sk.Info.Count == 0)
            return false;
        // Where the character really is drawn: its skeleton's root bone in the world, so Limber's own move / turn
        // (Move character, done on the skeleton) counts too, not just where the game has the character.
        var root = sk.GetModelFrame(0);
        position = sk.ModelToWorldPoint(root.Position);
        rotation = sk.ModelToWorldRotation(root.Rotation);
        return Limber.Core.MathUtil.IsFinite(position) && Limber.Core.MathUtil.IsFinite(rotation);
    }

    // Both characters' poses (next frame), where they stand against each other, and the bones attached between them.
    private void ExportDuo(ActorRef first)
    {
        if(Freeze == null)
            return;
        if(Partner(first) is not { } second)
        {
            _poseStatus = "Choose the partner first.";
            return;
        }
        if(!Root(first, out var pa, out var ra) || !Root(second, out var pb, out var rb))
        {
            _poseStatus = "Can't read where the two characters are.";
            return;
        }
        var inverse = Quaternion.Inverse(ra);
        var relative = new Newtonsoft.Json.Linq.JObject
        {
            ["Position"] = V3(Vector3.Transform(pb - pa, inverse)),
            ["Rotation"] = Q4(Quaternion.Normalize(inverse * rb)),
        };
        var attachments = new Newtonsoft.Json.Linq.JArray();
        lock(_rigs.Sync)
        {
            foreach(var (who, other, tag) in new[] { (first, second, "First"), (second, first, "Second") })
            {
                var rig = _rigs.GetRig(who.Address);
                if(rig == null)
                    continue;
                foreach(var g in rig.Goals)
                    if(g.Space == GoalSpace.Attached && g.ParentActor == other.Address && g.ParentBone != null && g.HasTarget)
                        attachments.Add(new Newtonsoft.Json.Linq.JObject
                        {
                            ["Who"] = tag,
                            ["Bone"] = g.Bone,
                            ["ParentBone"] = g.ParentBone,
                            ["Position"] = V3(g.Local.Position),
                            ["Rotation"] = Q4(g.Local.Rotation),
                            ["HoldPosition"] = g.HoldPosition,
                            ["LockRotation"] = g.LockRotation,
                            ["Enabled"] = g.Enabled,
                        });
            }
        }
        // An Oral scene: its settings (without who's in it) and which of the two is the mouth.
        JObject? oral = null;
        if(_poseOral)
        {
            var settings = _config.Oral ??= new OralSettings();
            var saved = JObject.FromObject(settings);
            foreach(var key in new[] { "Giver", "GiverIndex", "Receiver", "ReceiverIndex", "Enabled" })
                saved.Remove(key);
            bool secondIsMouth = settings.Giver == second.Name && settings.Receiver == first.Name;
            oral = new JObject { ["Mouth"] = secondIsMouth ? "Second" : "First", ["Settings"] = saved };
        }
        _poseStatus = "Reading both poses...";
        var gate = new object();
        Newtonsoft.Json.Linq.JObject? poseA = null, poseB = null;
        string name = $"{(string.IsNullOrEmpty(first.Name) ? "first" : first.Name)} & {(string.IsNullOrEmpty(second.Name) ? "second" : second.Name)} (duo)";
        void Finish()
        {
            if(poseA == null || poseB == null)
                return;
            var duo = new Newtonsoft.Json.Linq.JObject
            {
                ["FileExtension"] = ".pose",
                ["TypeName"] = "Limber Duo Pose",
                ["FileVersion"] = 1,
                ["First"] = poseA,
                ["Second"] = poseB,
                ["SecondFromFirst"] = relative,
                ["Attachments"] = attachments,
            };
            if(oral != null)
                duo["Oral"] = oral;
            _pendingExport = Tuple.Create(duo.ToString(Newtonsoft.Json.Formatting.Indented), name);
        }
        Freeze.Export(first.Address, first.ObjectIndex, (json, error) =>
        {
            if(json == null)
            {
                _poseStatus = error;
                return;
            }
            lock(gate)
            {
                poseA = json;
                Finish();
            }
        });
        Freeze.Export(second.Address, second.ObjectIndex, (json, error) =>
        {
            if(json == null)
            {
                _poseStatus = error;
                return;
            }
            lock(gate)
            {
                poseB = json;
                Finish();
            }
        });
    }

    // A plain pose file on one character (and the animation it was made on, offered).
    private bool ImportSolo(ActorRef who, string text, bool positions)
    {
        if(Freeze == null)
            return false;
        var bones = Limber.Rig.MotionFreeze.ReadFile(text, out var error);
        _poseAnimation = Limber.Rig.MotionFreeze.ReadAnimation(text);
        if(bones == null)
        {
            _poseStatus = error;
            return false;
        }
        _poseStatus = "Loading the pose...";
        Freeze.Import(who.Address, who.ObjectIndex, bones, positions, message => _poseStatus = message);
        return true;
    }

    // ---- Sync: a pose file loaded here loads on the synced partner's screen too (and theirs here)

    /// <summary>The Koikatsu link (for the Sync tab's session); set by the plugin.</summary>
    public Limber.Koi.KoiLink? SyncLink { get; set; }

    // Which synced character an actor is: 0 = me, 1 = my sync partner, 255 = neither.
    private int SyncRole(ActorRef actor)
    {
        var link = SyncLink;
        if(link == null || actor.Address == 0)
            return 255;
        if(actor.Address == link.SyncSelf.Address)
            return 0;
        if(actor.Address == link.SyncPartner.Address)
            return 1;
        return 255;
    }

    private void ShareWithSync(string text, ActorRef first, ActorRef? second, bool positions)
    {
        var link = SyncLink;
        if(link == null || link.Sync.State != Limber.Koi.Sync.SyncSession.Phase.Active)
            return;
        int a = SyncRole(first), b = second is { } s2 ? SyncRole(s2) : 255;
        if(a == 255 || (second != null && b == 255))
        {
            _poseStatus += " (Not sent to your sync partner: it's only shared when it's on you and your partner.)";
            return;
        }
        if(link.Sync.SendPoseFile(text, a, b, positions))
            _poseStatus += " Sent to your sync partner.";
    }

    // A pose file the sync partner loaded: on the same characters here (their character = my partner, mine = me).
    private void ReceiveFromSync()
    {
        var link = SyncLink;
        if(link == null || Freeze == null || link.Sync.TakePoseFile() is not { } m)
            return;
        ActorRef? Who(int role)
        {
            // (sent from their side: 0 = their character = my partner here; 1 = my character)
            var (address, index) = role == 0 ? link.SyncPartner : role == 1 ? link.SyncSelf : default;
            if(address == 0)
                return null;
            foreach(var c in _selection.ListCharacters())
                if(c.Address == address)
                    return c;
            return new ActorRef(address, index, "");
        }
        try
        {
            if(Who(m.First) is not { } first)
                return;
            if(IsDuo(m.Json))
            {
                if(Who(m.Second) is { } second)
                    ImportDuo(first, second, m.Json, m.Positions);
            }
            else
                ImportSolo(first, m.Json, m.Positions);
            _poseStatus = "From your sync partner: " + _poseStatus;
        }
        catch(Exception e)
        {
            _poseStatus = "Couldn't load your sync partner's pose: " + e.Message;
        }
    }

    // A duo file back on the chosen character and the partner: poses, where the partner stands, animations, attachments.
    private bool ImportDuo(ActorRef first, ActorRef second, string text, bool positions)
    {
        if(Freeze == null)
            return false;
        var root = Newtonsoft.Json.Linq.JObject.Parse(text);
        if(root["First"] is not Newtonsoft.Json.Linq.JObject a || root["Second"] is not Newtonsoft.Json.Linq.JObject b)
        {
            _poseStatus = "The duo pose file is missing a character.";
            return false;
        }
        var bonesA = Limber.Rig.MotionFreeze.ReadFile(a.ToString(), out var errorA);
        var bonesB = Limber.Rig.MotionFreeze.ReadFile(b.ToString(), out var errorB);
        if(bonesA == null || bonesB == null)
        {
            _poseStatus = bonesA == null ? errorA : errorB;
            return false;
        }

        // Where the partner stands against the chosen character (moved with Limber's move / turn).
        if(Actors != null && root["SecondFromFirst"] is Newtonsoft.Json.Linq.JObject rel
           && Root(first, out var pa, out var ra) && Root(second, out var pb, out var rb))
        {
            var wantPosition = pa + Vector3.Transform(ReadV3(rel["Position"]), ra);
            var wantRotation = Quaternion.Normalize(ra * ReadQ4(rel["Rotation"]));
            static float Yaw(Quaternion q)
            {
                var f = Vector3.Transform(Vector3.UnitZ, q);
                return MathF.Atan2(f.X, f.Z) * 180f / MathF.PI;
            }
            float turn = Yaw(wantRotation) - Yaw(rb);
            while(turn > 180f) turn -= 360f;
            while(turn < -180f) turn += 360f;
            var offset = Actors.GetOrAdd(second.Address, second.ObjectIndex);
            lock(Actors.Sync)
            {
                offset.Move += wantPosition - pb;
                offset.Turn = new Vector3(offset.Turn.X + turn, offset.Turn.Y, offset.Turn.Z);
            }
        }

        // The poses (held), and the animations they were made on.
        Freeze.Import(first.Address, first.ObjectIndex, bonesA, positions, message => _poseStatus = message);
        Freeze.Import(second.Address, second.ObjectIndex, bonesB, positions, _ => { });
        foreach(var (who, pose) in new[] { (first, a), (second, b) })
            if(Limber.Rig.MotionFreeze.ReadAnimation(pose.ToString()) is { } anim)
                Limber.Rig.Animations.Play(who.Address, who.ObjectIndex, anim.Id);

        // The attachments between the two.
        int attached = 0;
        if(root["Attachments"] is Newtonsoft.Json.Linq.JArray list)
        {
            lock(_rigs.Sync)
            {
                _rigs.Checkpoint();
                foreach(var item in list.OfType<Newtonsoft.Json.Linq.JObject>())
                {
                    string? bone = item["Bone"]?.Value<string>(), parentBone = item["ParentBone"]?.Value<string>();
                    if(bone == null || parentBone == null)
                        continue;
                    bool isFirst = item["Who"]?.Value<string>() != "Second";
                    var who = isFirst ? first : second;
                    var other = isFirst ? second : first;
                    var rig = _rigs.GetOrCreateRig(who.Address, who.ObjectIndex, who.Name);
                    var goal = rig.GetOrAdd(bone);
                    goal.Space = GoalSpace.Attached;
                    goal.ParentActor = other.Address;
                    goal.ParentObjectIndex = other.ObjectIndex;
                    goal.ParentActorName = other.Name;
                    goal.ParentBone = parentBone;
                    goal.HoldPosition = item["HoldPosition"]?.Value<bool>() ?? true;
                    goal.LockRotation = item["LockRotation"]?.Value<bool>() ?? true;
                    goal.Enabled = item["Enabled"]?.Value<bool>() ?? true;
                    goal.Local = new Limber.Core.Frame { Position = ReadV3(item["Position"]), Rotation = ReadQ4(item["Rotation"]) };
                    goal.HasTarget = true;
                    goal.CapturePosition = false;
                    goal.CaptureRotation = false;
                    attached++;
                }
            }
        }
        // An Oral scene: Oral's settings back, and Oral started on the two.
        bool oralOn = false;
        if(root["Oral"] is JObject oral && oral["Settings"] is JObject saved)
        {
            var settings = _config.Oral ??= new OralSettings();
            try
            {
                Newtonsoft.Json.JsonConvert.PopulateObject(saved.ToString(), settings);
                if(Limber.Koi.Safety.AdultAllowed(first.Address) && Limber.Koi.Safety.AdultAllowed(second.Address))
                {
                    bool secondIsMouth = oral["Mouth"]?.Value<string>() == "Second";
                    var mouth = secondIsMouth ? second : first;
                    var partner = secondIsMouth ? first : second;
                    settings.Giver = mouth.Name;
                    settings.GiverIndex = mouth.ObjectIndex;
                    settings.Receiver = partner.Name;
                    settings.ReceiverIndex = partner.ObjectIndex;
                    settings.Enabled = true;
                    oralOn = true;
                }
                _config.Save();
            }
            catch(Exception e)
            {
                Plugin.Log.Information("Limber: couldn't read the duo pose's Oral settings: " + e.Message);
            }
        }
        _poseStatus = $"Duo pose loaded on {first.Name} and {second.Name}" + (attached > 0 ? $", {attached} attachment{(attached == 1 ? "" : "s")}" : "")
            + (oralOn ? ", Oral started" : "") + ".";
        return true;
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

    // ---- Actors (GPose actors added and removed)

    /// <summary>Adds and removes GPose actors (set by the plugin).</summary>
    public Limber.Rig.ActorSpawner? Spawner { get; set; }

    private bool _spawnSamePlace;
    private string _spawnNpcSearch = "";
    private string? _spawnStatus;

    public void DrawActorsPage()
    {
        if(Spawner == null)
            return;
        if(!Plugin.ClientState.IsGPosing)
        {
            ImGui.TextDisabled("Enter GPose to add actors. Actors added here go away when you leave GPose.");
            return;
        }
        var chosen = _selection.Actor;
        var me = Plugin.ObjectTable.LocalPlayer;

        // The quick one: a new actor (a copy of you), chosen at once.
        var quick = me ?? (chosen.IsSet ? Plugin.ObjectTable.CreateObjectReference(chosen.Address) as Dalamud.Game.ClientState.Objects.Types.ICharacter : null);
        using(ImRaii.Disabled(quick == null))
        {
            if(ImGui.Button("Add new actor###lmb_spawn_new", new System.Numerics.Vector2(-1, 0)) && quick != null)
            {
                int index = Spawner.Spawn(quick, _spawnSamePlace);
                Spawned(index);
                if(index >= 0 && Plugin.ObjectTable[index] is { } added)
                {
                    _config.FollowTarget = false;
                    _config.Save();
                    _selection.Select(ActorRef.From(added));
                }
            }
        }
        Tooltip("Adds a new actor (named Limber 1, Limber 2, ...) a step in front of you, and chooses it at the top.");

        SeparatorText("Add an actor");
        using(ImRaii.Disabled(!chosen.IsSet))
        {
            if(ImGui.Button("Copy of the chosen character###lmb_spawn_chosen"))
            {
                if(Plugin.ObjectTable.CreateObjectReference(chosen.Address) is Dalamud.Game.ClientState.Objects.Types.ICharacter c)
                    Spawned(Spawner.Spawn(c, _spawnSamePlace));
            }
        }
        Tooltip("A new actor that looks like the character chosen at the top (body, face, outfit).");
        ImGui.SameLine();
        using(ImRaii.Disabled(me == null))
        {
            if(ImGui.Button("Copy of me###lmb_spawn_me") && me != null)
                Spawned(Spawner.Spawn(me, _spawnSamePlace));
        }
        Tooltip("A new actor that looks like your own character.");
        ImGui.Checkbox("In the same spot###lmb_spawn_same", ref _spawnSamePlace);
        Tooltip("Off: it appears a step in front of the one it copies. On: exactly where that one stands.");
        if(_spawnStatus != null)
            ImGui.TextDisabled(_spawnStatus);

        // A new actor wearing a look: an NPC's, a saved one, or one copied as text (Appearance & gear > Looks > Copy as text).
        SeparatorText("Add an actor with a look");
        var at = chosen.IsSet ? Plugin.ObjectTable.CreateObjectReference(chosen.Address) as Dalamud.Game.ClientState.Objects.Types.ICharacter : null;
        at ??= me;
        void SpawnLook(Limber.Rig.AppearanceData look)
        {
            if(at == null)
            {
                _spawnStatus = "Choose a character first (the new actor appears next to it).";
                return;
            }
            Spawned(Spawner.Spawn(at, _spawnSamePlace, look.Copy()));
        }
        using(ImRaii.Disabled(at == null))
        {
            if(ImGui.Button("From copied text###lmb_spawn_paste"))
            {
                try
                {
                    var pasted = Newtonsoft.Json.JsonConvert.DeserializeObject<Limber.Rig.AppearanceData>(ImGui.GetClipboardText() ?? "");
                    if(pasted is { Customize.Length: 26 })
                        SpawnLook(pasted);
                    else
                        _spawnStatus = "The copied text isn't a look (use Copy as text on Appearance & gear > Looks).";
                }
                catch
                {
                    _spawnStatus = "The copied text isn't a look (use Copy as text on Appearance & gear > Looks).";
                }
            }
            Tooltip("A new actor wearing the look you copied as text (Appearance & gear > Looks > Copy as text, from here or from someone else).");
            var presets = _config.AppearancePresets ?? new();
            if(presets.Count > 0)
            {
                ImGui.SameLine();
                ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);
                using var combo = ImRaii.Combo("###lmb_spawn_saved", "From a saved look...");
                if(combo.Success)
                    for(int i = 0; i < presets.Count; i++)
                        if(ImGui.Selectable($"{presets[i].Name}###lmb_spawn_saved_{i}"))
                            SpawnLook(presets[i]);
            }
            if(ImGui.TreeNode("From an NPC###lmb_spawn_npc"))
            {
                AppearanceTab.DrawNpcPicker(ref _spawnNpcSearch, "lmb_spawn_npc", npc =>
                {
                    ImGui.SameLine();
                    if(ImGui.SmallButton("Add as an actor"))
                        SpawnLook(npc.Look);
                });
                ImGui.TreePop();
            }
        }

        SeparatorText($"Actors added by Limber ({Spawner.Made.Count})");
        if(Spawner.Made.Count == 0)
            ImGui.TextDisabled("None yet.");
        else
        {
            ushort? remove = null;
            using(var table = ImRaii.Table("###lmb_spawn_table", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            {
                if(table.Success)
                {
                    foreach(var index in Spawner.Made)
                    {
                        var obj = Plugin.ObjectTable[index];
                        using var id = ImRaii.PushId(index);
                        ImGui.TableNextRow();
                        ImGui.TableNextColumn();
                        string name = obj == null ? "(gone)" : obj.Name.TextValue;
                        bool selected = obj != null && chosen.Address == obj.Address;
                        if(ImGui.Selectable($"{(name.Length > 0 ? name : "Added actor")}  #{index}", selected) && obj != null)
                        {
                            _selection.Select(ActorRef.From(obj));
                            _config.FollowTarget = false;
                            _config.Save();
                        }
                        ImGui.TableNextColumn();
                        if(obj != null)
                        {
                            if(ImGui.SmallButton("Target"))
                                Plugin.TargetManager.GPoseTarget = obj;
                            Tooltip("Makes it the GPose target (and the character Limber works on, with Follow my target on).");
                        }
                        ImGui.TableNextColumn();
                        if(ImGui.SmallButton("Remove"))
                            remove = index;
                    }
                }
            }
            if(remove is { } r)
            {
                Freeze?.Resume(Plugin.ObjectTable[r]?.Address ?? 0);
                Spawner.Despawn(r);
            }
            if(ImGui.Button("Remove all###lmb_spawn_clear"))
            {
                Freeze?.ResumeAll();
                Spawner.DespawnAll();
            }
            Tooltip("Removes every actor Limber added (actors from other plugins are left alone).");
        }
        ImGui.Spacing();
    }

    private void Spawned(int index)
    {
        if(index < 0)
        {
            _spawnStatus = Spawner?.LastError ?? "Couldn't add an actor.";
            return;
        }
        int number = Spawner?.NumberOf((ushort)index) ?? 0;
        _spawnStatus = number > 0 ? $"Added Limber {number}. It appears in a moment." : $"Added actor #{index}. It appears in a moment.";
    }

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
            var other = Limber.Rig.RigService.OtherSide(bone);
            if(other != null)
            {
                ImGui.SameLine();
                bool mirror = _config.BoneScaleMirror;
                if(ImGui.Checkbox("Mirror###lmb_bs_mirror", ref mirror))
                {
                    _config.BoneScaleMirror = mirror;
                    _config.Save();
                }
                Tooltip($"On: whatever you do to this bone is done to the other side's too ({HandleCatalog.Label(other)}), so both stay the same.");
                if(mirror)
                {
                    ImGui.SameLine();
                    if(ImGui.SmallButton("Copy to the other side###lmb_bs_copy"))
                        SetBoneScale(who, other, e.Copy());
                    Tooltip("Gives the other side this bone's scale and settings now (Mirror keeps them together from then on).");
                }
            }
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
            {
                SetBoneScale(who, bone, e);
                if(other != null && _config.BoneScaleMirror && snap != null && snap.IndexOf(other) is int o && o >= 0)
                    SetBoneScale(who, other, e.Copy());
            }
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
