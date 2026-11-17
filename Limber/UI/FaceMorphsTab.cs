using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Limber.Gpu;
using Limber.Koi;
using Limber.Rig;

namespace Limber.UI;

/// <summary>The Face morphs page (experimental): blend shapes on the face, presets, Oral's drive, and importing.</summary>
public sealed class FaceMorphsTab
{
    private readonly FaceMorphService _service;
    private readonly Selection _selection;
    private readonly RigSettings _config;
    private string _search = "";
    private string _importSearch = "";
    private readonly HashSet<string> _ticked = new();
    private bool _tickedSuggested;
    private int _tickedFor;
    private FaceMorphs.Face? _face;
    private string _faceFor = "";
    private string _faceNote = "";
    private long _retryAt;

    public FaceMorphsTab(FaceMorphService service, Selection selection, RigSettings config)
    {
        _service = service;
        _selection = selection;
        _config = config;
    }

    private static void Tip(string text)
    {
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip(text);
    }

    // The selected character's face model, read for morphs (once per face model).
    private unsafe FaceMorphs.Face? FaceOf(ActorRef actor)
    {
        var obj = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)actor.Address;
        if(obj == null || obj->DrawObject == null)
        {
            _faceNote = "The character isn't drawn.";
            return null;
        }
        string file = "";
        foreach(var (_, f) in LiveMesh.ModelFiles((nint)obj->DrawObject))
            if(LipMesh.IsFace(f))
                file = f;
        if(file.Length == 0)
        {
            _faceNote = "Couldn't find the character's face model.";
            return null;
        }
        string key = actor.Address + "|" + file;
        // (read again when the face's morphs were changed, e.g. imported: it's then forgotten)
        if(key == _faceFor && _face != null && ReferenceEquals(FaceMorphs.Find(file, _face.ByteWidth), _face))
            return _face;
        if(key == _faceFor && _face == null && Environment.TickCount64 < _retryAt)
            return null;
        _retryAt = Environment.TickCount64 + 3000;
        _faceFor = key;
        try
        {
            var data = LimberTab.ReadModelFile(file);
            _face = data == null ? null : FaceMorphs.Prepare(file, data, 0);
            _faceNote = _face == null ? "Couldn't read the face model's lips (an unusual face?)." : string.Join("; ", _face.Report) + ".";
        }
        catch(Exception e)
        {
            _face = null;
            _faceNote = "Couldn't read the face: " + e.Message;
        }
        return _face;
    }

    public void Draw()
    {
        ImGui.TextColored(new Vector4(1f, 0.75f, 0.3f, 1f), "Experimental.");
        ImGui.SameLine();
        ImGui.TextWrapped("Blend shapes on the face: Limber's own (made from where this face's features are) and ones imported from "
            + "Virt-a-Mate and Koikatsu. They're added to the face when it's drawn (through the face rig, or the lips' live mesh during Oral). "
            + "Only on your screen. Adult characters only.");
        var actor = _selection.Actor;
        if(!actor.IsSet)
        {
            ImGui.TextDisabled("Choose a character at the top of the window.");
            return;
        }
        string name = actor.Name ?? "";
        if(name.Length == 0)
        {
            ImGui.TextDisabled("This character has no name to keep its morphs under.");
            return;
        }
        if(!Limber.Koi.Safety.AdultAllowed(actor.Address))
        {
            ImGui.TextDisabled("Face morphs are for adult characters only.");
            return;
        }
        var all = _config.FaceMorphs ??= new();
        if(!all.TryGetValue(name, out var s))
            all[name] = s = new FaceMorphSettings();
        bool on = s.Enabled;
        if(ImGui.Checkbox($"Face morphs on {name}###fm_on", ref on))
        {
            s.Enabled = on;
            _config.Save();
        }
        Tip("On: the sliders below (and Oral's drive) shape this character's face. One face at a time (the one Oral has, if any).");
        if(_service.Status.Length > 0)
            ImGui.TextDisabled(_service.Status);
        if(s.Enabled)
        {
            // (what's happening: for when nothing shows)
            bool mine = FaceMorphs.Wanted == actor.Address;
            bool rig = FaceRig.Running && FaceRig.Owner == actor.Address, lips = LipMesh.Running && LipMesh.Owner == actor.Address;
            ImGui.TextDisabled(!mine ? (FaceMorphs.Active ? "Another character's face has the morphs right now (one face at a time)." : "No morph is set above 0 yet.")
                : rig ? $"Shown through the face rig. Last write: {(FaceMorphs.Applied.Length > 0 ? FaceMorphs.Applied : "none yet")}."
                : lips ? $"Shown through Oral's lips mesh. Last write: {(FaceMorphs.Applied.Length > 0 ? FaceMorphs.Applied : "none yet")}."
                : "Waiting for the face rig to start on this character...");
            if(FaceRig.Status.Length > 0)
                ImGui.TextDisabled(FaceRig.Status);
        }

        var face = FaceOf(actor);
        ImGui.TextDisabled(_faceNote);

        // ---- Presets
        ImGui.Spacing();
        ImGui.TextUnformatted("Presets:");
        foreach(var (presetName, tip, values) in FaceMorphService.Presets)
        {
            ImGui.SameLine();
            if(ImGui.SmallButton($"{presetName}###fm_p_{presetName}"))
            {
                s.Values = new Dictionary<string, float>(values);
                s.Enabled = true;
                _config.Save();
            }
            Tip(tip + " (Morphs you haven't imported are skipped.)");
        }
        ImGui.SameLine();
        if(ImGui.SmallButton("All to 0###fm_clear"))
        {
            s.Values.Clear();
            _config.Save();
        }
        ImGui.TextDisabled("For the ohogao face as an animation: Limber Animations > Ahegao, type Ohogao.");

        // ---- Sliders
        if(ImGui.CollapsingHeader("Shapes###fm_shapes", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.SetNextItemWidth(Limber.UI.Fit.Fixed(220f * ImGuiHelpers.GlobalScale));
            ImGui.InputTextWithHint("###fm_search", "Search shapes", ref _search, 64);
            var shapes = new List<(string Name, string Group, string Tip)>();
            foreach(var (n, g, t) in FaceMorphs.Own)
                shapes.Add((n, g, t));
            if(face != null)
                foreach(var m in face.Morphs.Values.Where(m => m.Source != "Limber").OrderBy(m => m.Group).ThenBy(m => m.Name))
                    shapes.Add((m.Name, m.Group, m.Tip));
            // (a preset's names the face doesn't have yet: listed too, so they can be turned down)
            foreach(var k in s.Values.Keys)
                if(!shapes.Any(x => x.Name == k))
                    shapes.Add((k, "Not on this face (import it)", "Not imported onto this face yet: it does nothing until it is."));
            bool changed = false;
            foreach(var group in shapes.Where(x => _search.Length == 0 || x.Name.Contains(_search, StringComparison.OrdinalIgnoreCase)).GroupBy(x => x.Group))
            {
                if(!ImGui.TreeNodeEx($"{group.Key} ({group.Count()})###fm_g_{group.Key}", group.Key is "Lips" or "Cheeks" ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None))
                    continue;
                foreach(var (n, _, t) in group)
                {
                    float v = s.Values.GetValueOrDefault(n);
                    ImGui.SetNextItemWidth(Limber.UI.Fit.Fixed(200f * ImGuiHelpers.GlobalScale));
                    if(ImGui.SliderFloat($"{n}###fm_v_{n}", ref v, -1f, 1.5f, "%.2f"))
                    {
                        if(MathF.Abs(v) < 0.005f)
                            s.Values.Remove(n);
                        else
                            s.Values[n] = v;
                        changed = true;
                    }
                    if(ImGui.IsItemClicked(ImGuiMouseButton.Right))
                    {
                        s.Values.Remove(n);
                        changed = true;
                    }
                    Tip(t + " Right-click: 0.");
                    ImGui.SameLine();
                    var conform = s.Conform ??= new();
                    int c = (int)MathF.Round(conform.GetValueOrDefault(n, 1f));
                    ImGui.SetNextItemWidth(Limber.UI.Fit.Fixed(90f * ImGuiHelpers.GlobalScale));
                    if(ImGui.SliderInt($"###fm_c_{n}", ref c, 1, 100, "conform %d"))
                    {
                        if(c <= 1)
                            conform.Remove(n);
                        else
                            conform[n] = c;
                        changed = true;
                    }
                    Tip("Face conforming: how much the rest of the face follows this morph (1 = only its own area moves;\n"
                        + "higher: the cheeks, jaw and skin round it go along instead of stretching). Right-click: 1.");
                    if(ImGui.IsItemClicked(ImGuiMouseButton.Right))
                    {
                        conform.Remove(n);
                        changed = true;
                    }
                    if(_service.Last.TryGetValue(n, out var now) && MathF.Abs(now - v) > 0.01f)
                    {
                        ImGui.SameLine();
                        ImGui.TextDisabled($"now {now:0.00}");
                    }
                }
                ImGui.TreePop();
            }
            if(changed)
                _config.Save();
        }

        // ---- Oral
        if(ImGui.CollapsingHeader("With Oral###fm_oral", ImGuiTreeNodeFlags.DefaultOpen))
        {
            bool follow = s.FollowOral;
            if(ImGui.Checkbox("Follow Oral's stroke###fm_follow", ref follow))
            {
                s.FollowOral = follow;
                _config.Save();
            }
            Tip("When this character is the one with the mouth on the Oral page: the face reacts to the stroke, on top of the sliders above.");
            bool changed = false;
            void Amount(string label, string tip, Func<float> get, Action<float> set)
            {
                float v = get();
                ImGui.SetNextItemWidth(Limber.UI.Fit.Fixed(200f * ImGuiHelpers.GlobalScale));
                if(ImGui.SliderFloat(label, ref v, 0f, 1.5f, "%.2f"))
                {
                    set(v);
                    changed = true;
                }
                Tip(tip);
            }
            Amount("Lips stretched###fm_o_stretch", "The lips stretched round the shaft, by how big it is for the mouth.", () => s.Stretch, v => s.Stretch = v);
            Amount("Vacuum###fm_o_vacuum", "Cheeks sucked in and lips held out, hardest on the way out.", () => s.Vacuum, v => s.Vacuum = v);
            Amount("Deep###fm_o_deep", "The face lengthened when it goes deep.", () => s.Deep, v => s.Deep = v);
            Amount("Nose pressed###fm_o_nose", "The nose squashed against the body at the deepest.", () => s.NosePress, v => s.NosePress = v);
            Amount("Breathing###fm_o_breath", "The nostrils flaring with the breaths.", () => s.Breathing, v => s.Breathing = v);
            Amount("Strain###fm_o_strain", "Heavy lids, brows up inside, eye bags when it's deep.", () => s.Strain, v => s.Strain = v);
            Amount("Throat bulge###fm_o_throat", "The neck swelling when it goes down the throat.", () => s.ThroatBulge, v => s.ThroatBulge = v);
            if(changed)
                _config.Save();
        }

        // ---- Importing
        if(ImGui.CollapsingHeader("Import from Virt-a-Mate / Koikatsu###fm_import"))
            DrawImport(face);
    }

    private void DrawImport(FaceMorphs.Face? face)
    {
        ImGui.TextWrapped("Their morphs are fitted onto this face (the features lined up, each morph's moves carried over and sized to it) "
            + "and saved for this face model next to Limber's settings. They're made from your own installs and stay on this PC.");
        string vam = _config.VamFolder ?? "";
        ImGui.SetNextItemWidth(Limber.UI.Fit.Label(140f * ImGuiHelpers.GlobalScale));
        if(ImGui.InputTextWithHint("###fm_vam", "Virt-a-Mate's folder (the one with VaM.exe)", ref vam, 400))
        {
            _config.VamFolder = vam;
            _config.Save();
        }
        ImGui.SameLine();
        using(ImRaii.Disabled(FaceMorphImport.Busy || vam.Length == 0))
            if(ImGui.Button("Scan VaM###fm_scan_vam"))
            {
                FaceMorphImport.ScanVam(vam);
                _tickedSuggested = false;
            }
        string koi = _config.KoikatsuFolder ?? "";
        ImGui.TextDisabled(koi.Length > 0 ? $"Koikatsu: {koi}" : "Koikatsu's folder: set it on Koikatsu > Import props.");
        ImGui.SameLine();
        using(ImRaii.Disabled(FaceMorphImport.Busy || koi.Length == 0))
            if(ImGui.Button("Scan Koikatsu###fm_scan_koi"))
            {
                FaceMorphImport.ScanKoikatsu(koi);
                _tickedSuggested = false;
            }
        if(FaceMorphImport.Status.Length > 0)
            ImGui.TextWrapped(FaceMorphImport.Status);

        var candidates = FaceMorphImport.Vam.Concat(FaceMorphImport.Koikatsu).ToList();
        int stamp = candidates.Count;
        if(!_tickedSuggested || _tickedFor != stamp)
        {
            _tickedSuggested = true;
            _tickedFor = stamp;
            foreach(var c in candidates.Where(c => c.Suggested))
                _ticked.Add(c.Key);
        }
        if(candidates.Count > 0)
        {
            ImGui.SetNextItemWidth(Limber.UI.Fit.Fixed(220f * ImGuiHelpers.GlobalScale));
            ImGui.InputTextWithHint("###fm_isearch", "Search morphs", ref _importSearch, 64);
            ImGui.SameLine();
            if(ImGui.SmallButton("Tick suggested###fm_tick_s"))
                foreach(var c in candidates.Where(c => c.Suggested))
                    _ticked.Add(c.Key);
            ImGui.SameLine();
            if(ImGui.SmallButton("Untick all###fm_untick"))
                _ticked.Clear();
            using(var child = ImRaii.Child("###fm_candidates", new Vector2(-1, 240 * ImGuiHelpers.GlobalScale), true))
            if(child.Success)
            {
                foreach(var group in candidates.Where(c => _importSearch.Length == 0 || c.Name.Contains(_importSearch, StringComparison.OrdinalIgnoreCase))
                             .GroupBy(c => c.Source + ": " + (c.Group.Length > 0 ? c.Group : "Other")))
                {
                    if(!ImGui.TreeNodeEx($"{group.Key} ({group.Count()})###fm_cg_{group.Key}", group.Any(c => c.Suggested) ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None))
                        continue;
                    foreach(var c in group)
                    {
                        bool t = _ticked.Contains(c.Key);
                        if(ImGui.Checkbox($"{c.Name}{(c.Suggested ? "  (suggested)" : "")}###fm_c_{c.Key}", ref t))
                        {
                            if(t)
                                _ticked.Add(c.Key);
                            else
                                _ticked.Remove(c.Key);
                        }
                        Tip($"{c.Origin}; moves {c.Vertex.Length} points.");
                    }
                    ImGui.TreePop();
                }
            }
            int n = candidates.Count(c => _ticked.Contains(c.Key));
            using(ImRaii.Disabled(FaceMorphImport.Busy || face == null || n == 0))
                if(ImGui.Button($"Import {n} onto this face###fm_do_import"))
                    FaceMorphImport.Import(face!, candidates.Where(c => _ticked.Contains(c.Key)).ToList());
            if(face == null)
                ImGui.TextDisabled("(the face needs to be read first: see above)");
        }

        if(face != null)
        {
            var imported = face.Morphs.Values.Where(m => m.Source != "Limber").OrderBy(m => m.Name).ToList();
            if(imported.Count > 0 && ImGui.TreeNodeEx($"Imported onto this face ({imported.Count})###fm_imported"))
            {
                foreach(var m in imported)
                {
                    using var id = ImRaii.PushId(m.Name);
                    if(ImGui.SmallButton("Remove###fm_rm"))
                    {
                        FaceMorphs.DeleteImported(face.Key, m.Name);
                        _faceFor = "";
                    }
                    ImGui.SameLine();
                    ImGui.TextUnformatted($"{m.Name}  ({m.Vertex.Length} points)");
                }
                ImGui.TreePop();
            }
        }
    }
}
