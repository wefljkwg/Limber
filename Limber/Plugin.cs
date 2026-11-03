using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Limber.Core;
using Limber.Koi;
using Limber.Koi.Posing;
using Limber.Rig;
using Limber.UI;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Limber;

// Limber: IK posing (the "Limber" tab) with the Koikatsu live link (formerly KoiPoseLink) packed in.
public sealed class Plugin : IDalamudPlugin
{
    private const string Command = "/limber";
    private const string KoiCommand = "/koilink";
    private const string ShortCommand = "/lmb";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static ITargetManager TargetManager { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static ISigScanner SigScanner { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInterop { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IKeyState KeyState { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;

    private readonly Configuration _config;
    private readonly BoneUpdateHook _hook;
    private readonly RigService _rigs;
    private readonly Selection _selection;
    private readonly HandleOverlay _overlay;
    private readonly KoiLink _koi;
    private readonly VideoTab _video;
    private readonly LiveTab _live;
    private readonly Limber.BodyRig.SoftBody _softBody = new(Limber.BodyRig.RigParts.Breasts);
    private readonly Limber.BodyRig.SoftBody _softBodyTesticles = new(Limber.BodyRig.RigParts.Testicles);
    private readonly Limber.BodyRig.SoftBody _softBodyButt = new(Limber.BodyRig.RigParts.Butt);
    private readonly Limber.BodyShape.BodyShapeLive _bodyShape = new();
    private List<System.Numerics.Vector3[]> _grabFrom = new();
    private (System.Numerics.Vector3[], System.Numerics.Vector3[]) _grabMerged = ([], []);
    private int _softBodyTick;
    private readonly Limber.BodyRig.PropService _props;
    private readonly MotionService _motions;
    private readonly OralService _oral;
    private readonly DynamicBoneService _dynamicBones;
    private readonly FkService _fk;
    private readonly ThrobService _throb;
    private readonly LookAtService _lookAt;
    private readonly TwerkService _twerk;
    private readonly ExpansionService _expansion;
    private readonly BustShapeService _bust;
    private readonly BallThrobService _ballThrob;
    private readonly SymmetryService _symmetry;
    private readonly FacePosingService _facePosing;
    private FacePosingTab? _faceTab;
    private readonly AhegaoService _ahegao;
    private TimelineService? _timeline;
    private FkPosingTab? _fkTab;
    private readonly Limber.BodyRig.HousingProps _housing;
    private readonly Limber.Rig.ActorTransforms _actors = new();
    private readonly Limber.Rig.MotionFreeze _freeze = new();
    private readonly Limber.Rig.ActorSpawner _spawner = new();
    private readonly PropOverlay _propOverlay;
    private readonly MainWindow _mainWindow;
    private readonly WindowSystem _windows = new("Limber");
    private DateTime _lastDrawError = DateTime.MinValue;

    public Plugin()
    {
        _config = LoadConfig();
        HandleCatalog.ShowRigBones = _config.Ik.ShowRigBones;
        // Hotkeys saved by 0.36 (ImGui key numbers): back to the defaults.
        if(_config.Ik.UndoKey is { } uk && uk.Key >= 256) { uk.Key = 0x5A; uk.Ctrl = true; uk.Shift = false; uk.Alt = false; }
        if(_config.Ik.RedoKey is { } rk && rk.Key >= 256) { rk.Key = 0x59; rk.Ctrl = true; rk.Shift = false; rk.Alt = false; }

        // One "Also run outside GPose" for the whole plugin (it used to be separate for posing and the link).
        if(!_config.OutsideGPoseUnified)
        {
            _config.OutsideGPoseUnified = true;
            (_config.Ik ??= new RigSettings()).WorkOutsideGPose = _config.AllowOutsideGPose;
        }
        _config.Ik ??= new RigSettings();
        _config.Ik.Initialize(SaveConfig);

        // One hook on the game's bone update for everything: the Koikatsu link poses first, then Limber's
        // goals (held, pinned and attached bones) are applied on top.
        _hook = new BoneUpdateHook(GameInterop, SigScanner, Framework, Log);
        try
        {
            _rigs = new RigService(_hook, Framework, ClientState, Log, _config.Ik);
            _selection = new Selection(ObjectTable, TargetManager, ClientState, _config.Ik);
            _overlay = new HandleOverlay(_rigs, _selection, _config.Ik, GameGui);
            _koi = new KoiLink(_config);
            _softBody.Settings = () => _config.Ik.SoftBody ??= new Limber.BodyRig.SoftBodySettings();
            _softBodyTesticles.Settings = () => _config.Ik.SoftBodyTesticles ??= Limber.BodyRig.SoftBodySettings.TesticleDefaults();
            _softBodyButt.Settings = () => _config.Ik.SoftBodyButt ??= Limber.BodyRig.SoftBodySettings.ButtDefaults();
            _props = new Limber.BodyRig.PropService(ObjectTable, ClientState, () => _config.Ik.Props);
            _props.HandSize = () => _config.Ik.HandColliderSize;
            _housing = new Limber.BodyRig.HousingProps(DataManager, Log);
            _motions = new MotionService(_rigs, ObjectTable, ClientState, GameGui, () => _config.Ik.Motions ??= new());
            _rigs.MotionFor = _motions.For;
            _oral = new OralService(_rigs, ObjectTable, ClientState, () => _config.Ik.Oral ??= new());
            _throb = new ThrobService(ObjectTable, () => _config.Ik.Throb ??= new()) { InOral = a => _oral.IsPartner(a) };
            _fk = new FkService(ObjectTable, () => _config.Ik.FkPoses ??= new());
            _twerk = new TwerkService(ObjectTable, () => _config.Ik.Twerk ??= new());
            _expansion = new ExpansionService(ObjectTable, () => _config.Ik.Expansion ??= new());
            _bust = new BustShapeService(ObjectTable, () => _config.Ik.BustShapes ??= new());
            _symmetry = new SymmetryService(ObjectTable, () => _config.Ik.Symmetry ??= new());
            _facePosing = new FacePosingService(ObjectTable, () => _config.Ik.FacePoses ??= new());
            _ballThrob = new BallThrobService(ObjectTable, () => _config.Ik.BallThrob ??= new()) { PenisBeat = a => _throb.LevelOf(a) };
            // (Oral's mouth rig has the face while it's on; with it off, Ahegao shapes the face during Oral too)
            _ahegao = new AhegaoService(ObjectTable, () => _config.Ik.Ahegao ??= new()) { Skip = a => _oral.Follows(a) != 0 && (_config.Ik.Oral?.MouthRig ?? true) };
            _lookAt = new LookAtService(ObjectTable, () => _config.Ik.LookAt ??= new()) { Skip = a => _oral.Follows(a) != 0 };
            _dynamicBones = new DynamicBoneService(ObjectTable, () => _config.Ik.DynamicBones ??= new()) { Skip = a => _oral.IsPartner(a) };
            _oral.Allowed = () => _config.PluginEnabled && (ClientState.IsGPosing || _config.Ik.WorkOutsideGPose);
            _rigs.TargetOverride = _oral.HeadTarget;
            _rigs.FollowsActor = _oral.Follows;
            _oral.ReadFile = LimberTab.ReadModelFile;
            // Oral is never on by itself: off at start (not saved), and off whenever an actor is added.
            (_config.Ik.Oral ??= new()).Enabled = false;
            // Nothing else starts by itself either: Limber Animations, body shape and body physics are off whenever
            // Limber starts (turned on on their pages).
            foreach(var s in (_config.Ik.Throb ??= new()).Values) s.Enabled = false;
            foreach(var s in (_config.Ik.BallThrob ??= new()).Values) s.Enabled = false;
            foreach(var s in (_config.Ik.Twerk ??= new()).Values) s.Enabled = false;
            foreach(var s in (_config.Ik.Ahegao ??= new()).Values) s.Enabled = false;
            foreach(var s in (_config.Ik.Expansion ??= new()).Values)
                s.Breasts.Enabled = s.Butt.Enabled = s.Penis.Enabled = false;
            _config.Ik.BodyShapeOn = false;
            _config.Ik.BodyPhysicsOn = false;
            Limber.BodyRig.SoftBody.On = () => _config.Ik.BodyPhysicsOn;
            _spawner.Added = () => { if(_config.Ik.Oral != null) _config.Ik.Oral.Enabled = false; };
            _motions.AlsoStop = _oral.StopFor;
            _rigs.AddUndoPart(new RigService.UndoPart
            {
                Capture = () => Newtonsoft.Json.JsonConvert.SerializeObject(_config.Ik.Motions ?? new()),
                Restore = state =>
                {
                    _config.Ik.Motions = Newtonsoft.Json.JsonConvert.DeserializeObject<System.Collections.Generic.List<MotionProp>>(state) ?? new();
                    SaveConfig();
                },
            });
            // Undo / redo also covers every other setting: sliders, switches, each feature on or off (noticed like
            // the parts below; the props and motions have their own parts, the page shown isn't undone).
            _rigs.AddUndoPart(new RigService.UndoPart { Capture = CaptureSettings, Restore = RestoreSettings, Fresh = () => _settingsShot = "" });
            // Undo / redo also covers the Actor tab and the props (changes there are noticed, no checkpoints needed).
            _rigs.AddUndoPart(new RigService.UndoPart { Capture = _actors.CaptureState, Restore = _actors.RestoreState });
            _rigs.AddUndoPart(new RigService.UndoPart
            {
                Capture = () => Newtonsoft.Json.JsonConvert.SerializeObject(_config.Ik.Props ?? new()),
                Restore = state =>
                {
                    _config.Ik.Props = Newtonsoft.Json.JsonConvert.DeserializeObject<System.Collections.Generic.List<Limber.BodyRig.PropCollider>>(state) ?? new();
                    SaveConfig();
                },
            });
            _softBody.Props = () => _props.World;
            _softBodyTesticles.Props = () => _props.World;
            _softBodyButt.Props = () => _props.World;
            Limber.Gpu.LiveSoftBody.Props = () => _props.World;
            Limber.Gpu.LiveSoftBody.Settings = () => _config.Ik.LiveMesh ??= new Limber.Gpu.LiveMeshSettings();
            Limber.BodyRig.SoftBoneMarks.Source = () => _config.Ik.SoftBoneMarks ??= new();
            // Grab: the skin the fingers wrap onto (the live mesh breasts when it runs, else the soft body bones' resting surface).
            _rigs.Grab.Surface = () =>
            {
                // The breasts' skin with their shape but without what moves it (steady to hold onto): the live mesh's
                // when it runs, else the soft body bones' resting surface.
                var live = Limber.Gpu.LiveSoftBody.Grab;
                if (live.Points.Length > 0 && live.Points.Length == live.Normals.Length)
                    return (live.Points, live.Normals);
                // (merged only when what's in it changes: the grab keeps its lookup grid while the arrays are the same)
                var parts = new List<(System.Numerics.Vector3[] P, System.Numerics.Vector3[] N)>();
                foreach (var soft in new[] { _softBody, _softBodyTesticles, _softBodyButt })
                {
                    var (p, n) = (soft.GrabSurface, soft.GrabNormals);
                    if (p.Length > 0 && p.Length == n.Length)
                        parts.Add((p, n));
                }
                if (parts.Count == 0)
                    return ([], []);
                if (parts.Count == 1)
                    return (parts[0].P, parts[0].N);
                bool same = _grabFrom.Count == parts.Count;
                for (int i = 0; same && i < parts.Count; i++)
                    same = ReferenceEquals(_grabFrom[i], parts[i].P);
                if (!same)
                {
                    _grabFrom = parts.Select(x => x.P).ToList();
                    _grabMerged = (parts.SelectMany(x => x.P).ToArray(), parts.SelectMany(x => x.N).ToArray());
                }
                return _grabMerged;
            };
            // What the soft body bones' physics moved each bone by (taken back out of the grab surface).
            Limber.Gpu.LiveSoftBody.LatticeShift = address =>
            {
                Dictionary<string, System.Numerics.Vector3>? found = null;
                foreach (var soft in new[] { _softBody, _softBodyTesticles, _softBodyButt })
                {
                    if (!soft.Shifts.TryGetValue(address, out var shifts))
                        continue;
                    if (found == null)
                        found = new Dictionary<string, System.Numerics.Vector3>(shifts);
                    else
                        foreach (var (k, v) in shifts)
                            found[k] = v;
                }
                return found;
            };
            // A grabbing hand's colliders only dent the skin; pushing the bones too, its fingers would chase the breast.
            bool SkipGrabbing(Limber.BodyRig.PropWorld p)
            {
                if (p.HandSide < 0 || p.Owner == 0)
                    return false;
                lock (_rigs.Sync)
                    return _rigs.Grab.IsGrabbing(p.Owner, p.HandSide);
            }
            _softBody.Skip = SkipGrabbing;
            _softBodyTesticles.Skip = SkipGrabbing;
            _softBodyButt.Skip = SkipGrabbing;
            // Lock: where the locked hands are this frame (the soft body bones near them follow).
            var lockCache = new SkeletonInfoCache();
            (nint, int, Frame, float, bool)[] LockedHands()
            {
                List<(nint, int, Frame, float, bool)>? found = null;
                lock (_rigs.Sync)
                {
                    foreach (var (address, hand) in _rigs.Grab.Hands)
                    {
                        if (!hand.LockLeft && !hand.LockRight)
                            continue;
                        if (!ActorSkeleton.TryGet(address, hand.Index, lockCache, out var sk))
                            continue;
                        for (int side = 0; side < 2; side++)
                        {
                            if (side == 0 ? !hand.LockLeft : !hand.LockRight)
                                continue;
                            int bone = sk.Info.IndexOf(side == 0 ? "j_te_l" : "j_te_r");
                            if (bone >= 0)
                                (found ??= new()).Add((address, side, sk.GetWorldFrame(bone), hand.LockStrength, hand.LockWhole));
                        }
                    }
                }
                return found?.ToArray() ?? [];
            }
            _softBody.LockedHands = LockedHands;
            _softBodyTesticles.LockedHands = LockedHands;
            _softBodyButt.LockedHands = LockedHands;
            Limber.Gpu.LiveSoftBody.Grabbing = SkipGrabbing;
            Limber.Gpu.LiveSoftBody.GrabFinger = () => FingerGrab.VisibleFinger * Math.Clamp(_config.Ik.HandColliderSize, 0.1f, 10f);
            _rigs.Grab.FingerRadius = () => FingerGrab.VisibleFinger * Math.Clamp(_config.Ik.HandColliderSize, 0.1f, 10f);
            Limber.Gpu.LiveSoftBody.Allowed = () => ClientState.IsGPosing || _config.Ik.WorkOutsideGPose;
            _softBody.BoneStrength = () => _config.Ik.PropBoneStrength;
            _softBodyTesticles.BoneStrength = () => _config.Ik.PropBoneStrength;
            _softBodyButt.BoneStrength = () => _config.Ik.PropBoneStrength;
            _propOverlay = new PropOverlay(_props, _config.Ik, GameGui) { BodyColliders = [_softBody, _softBodyTesticles, _softBodyButt] };
            _overlay.SuppressGizmo = () => _propOverlay.GizmoWanted;
            _overlay.MouseTakenElsewhere = () => _propOverlay.OwnsMouse || _motions.OwnsMouse;
            _motions.Changed = () => _config.Ik.Save();
            _overlay.Motions = _motions;
            _hook.Running = () => _config.PluginEnabled;
            _actors.Allowed = () => ClientState.IsGPosing || _config.Ik.WorkOutsideGPose;
            _actors.StartPriority(GameInterop);
            _freeze.Allowed = () => ClientState.IsGPosing || _config.Ik.WorkOutsideGPose;
            Limber.BodyShape.BodyShapeBuild.SetWorkDir(PluginInterface.ConfigDirectory.FullName);
            _bodyShape.KeepFeet = () => _config.Ik.BodyShapeKeepFeet;
            _hook.Start(("Limber freeze motion", _freeze.RestoreFrame), ("Limber actors", _actors.UpdateFrame), ("Limber breast size", _bust.UpdateFrame), ("Limber body shape", _bodyShape.UpdateFrame), ("the Koikatsu link", _koi.Applier.RunFrame), ("Limber oral (partner)", _oral.UpdatePartner), ("Limber FK posing", _fk.UpdateFrame), ("Limber posing", _rigs.UpdateFrame), ("Limber symmetry", _symmetry.UpdateFrame), ("Limber twerk", _twerk.UpdateFrame), ("Limber expansion", _expansion.UpdateFrame), ("Limber dynamic bones", _dynamicBones.UpdateFrame), ("Limber throbbing", _throb.UpdateFrame), ("Limber testicle throbbing", _ballThrob.UpdateFrame), ("Limber look at", _lookAt.UpdateFrame), ("Limber face posing", _facePosing.UpdateFrame), ("Limber ahegao", _ahegao.UpdateFrame), ("Limber oral", _oral.UpdateMouth),
                ("Limber props", _props.UpdateFrame),
                ("Limber soft body", _softBody.UpdateFrame), ("Limber soft body (testicles)", _softBodyTesticles.UpdateFrame),
                ("Limber soft body (butt)", _softBodyButt.UpdateFrame),
                ("Limber live mesh", Limber.Gpu.LiveSoftBody.Capture), ("Limber body shape (end of frame)", _bodyShape.AfterFrame),
                ("Limber freeze motion (end of frame)", _freeze.CaptureFrame));

            var pluginDir = PluginInterface.AssemblyLocation.DirectoryName ?? ".";
            _video = new VideoTab(_koi, pluginDir, PluginInterface.ConfigDirectory.FullName);
            _live = new LiveTab(_koi, pluginDir);
            Framework.Update += OnFrameworkUpdate;

            var syncHelp = new SyncHelpWindow();
            var limberTab = new LimberTab(_rigs, _selection, _overlay, _config.Ik) { SoftBreasts = _softBody, SoftTesticles = _softBodyTesticles, SoftButt = _softBodyButt, PropService = _props, Actors = _actors, Freeze = _freeze, Spawner = _spawner, Motions = _motions };
            _mainWindow = new MainWindow(_koi, _hook, limberTab, _video, _live, syncHelp,
                new PropsTab(_props, _propOverlay, _selection, _config.Ik) { Motions = _motions },
                new RendererTab(_rigs, _selection, _config.Ik, limberTab) { PropService = _props },
                new HousingPropsTab(_housing, _selection));
            _mainWindow.BodyShapeTab = new BodyShapeTab(_selection, _config.Ik) { RigEverything = limberTab.RigEverything };
            _mainWindow.OralTab = new OralTab(_oral, _selection, _config.Ik);
            _mainWindow.AnimationsTab = new AnimationsTab(_selection);
            _mainWindow.DynamicBonesTab = new DynamicBonesTab(_selection, _config.Ik);
            _fkTab = new FkPosingTab(_selection, _rigs, _fk, _config.Ik);
            limberTab.Fk = _fkTab;
            _faceTab = new FacePosingTab(_selection, _facePosing, _config.Ik);
            _mainWindow.FacePosingTab = _faceTab;
            HandleOverlay.FacePageOpen = () => _faceTab?.Showing == true;
            _mainWindow.FkPosingTab = _fkTab;
            _overlay.FkEditor = _fkTab;
            _fkTab.Overlay = _overlay;
            _mainWindow.LimberAnimationsTab = new LimberAnimationsTab(_selection, _config.Ik, _throb, _twerk, _ahegao, _expansion);
            _mainWindow.BustShapeTab = new BustShapeTab(_selection, _config.Ik, _bust);
            _mainWindow.WorldPropsTab = new HousingPropsTab(_housing, _selection, world: true);
            _mainWindow.LimberAnimationsTab.BallThrob = _ballThrob;
            _mainWindow.LookAtTab = new LookAtTab(_selection, _config.Ik, _lookAt);
            _timeline = new TimelineService(ObjectTable, _config.Ik, _rigs, _actors);
            _mainWindow.TimelineTab = new TimelineTab(_selection, _config.Ik, _timeline);
            _mainWindow.AppearanceTab = new AppearanceTab(_selection, _config.Ik);
            _mainWindow.CameraTab = new CameraTab(_config.Ik);
            _mainWindow.VoiceTab = new VoiceTab(_selection);
            limberTab.SyncLink = _koi;
            _windows.AddWindow(_mainWindow);
            foreach(var pageWindow in _mainWindow.PageWindows())
                _windows.AddWindow(pageWindow);
            _windows.AddWindow(syncHelp);
            var physicsHelp = new BodyPhysicsHelpWindow();
            _windows.AddWindow(physicsHelp);
            limberTab.ShowPhysicsHelp = () => physicsHelp.IsOpen = true;
            _windows.AddWindow(new SyncPromptWindow(_koi));
            if(!_config.HasShownWindow)
            {
                _mainWindow.IsOpen = true;
                _config.HasShownWindow = true;
                SaveConfig();
            }

            bool registered = CommandManager.AddHandler(Command, new CommandInfo(OnCommand)
            {
                HelpMessage = "Open Limber. '/limber on' / '/limber off' turn the whole plugin on or off; '/limber close' closes the window; '/limber undo' / '/limber redo'; '/limber reset' puts moved / scaled characters back.",
            });
            CommandManager.AddHandler(KoiCommand, new CommandInfo(OnKoiCommand)
            {
                HelpMessage = "Koikatsu link: '/koilink on' / '/koilink off' start or stop all links; '/koilink pause' pauses or resumes Koikatsu's animation.",
            });
            // A short alias too (also a way in if another plugin has taken /limber).
            bool shortRegistered = CommandManager.AddHandler(ShortCommand, new CommandInfo(OnCommand) { HelpMessage = "Same as /limber.", ShowInHelp = false });
            Log.Information($"Limber: commands registered: /limber {registered}, /lmb {shortRegistered}");
            if(!registered)
            {
                Log.Warning("Limber: /limber is already used by another plugin; use /lmb instead.");
                ChatGui.PrintError("Limber: another plugin already uses /limber. Use /lmb to open Limber.");
            }

            // Dalamud hides plugin windows in GPose, in cutscenes and whenever the game UI is hidden. Limber is a
            // GPose tool, so keep its window and handles visible in all of them (the window still only opens when
            // you open it; "Hide handles when the game UI is hidden" is in the Limber tab's settings).
            PluginInterface.UiBuilder.DisableGposeUiHide = true;
            PluginInterface.UiBuilder.DisableUserUiHide = true;
            PluginInterface.UiBuilder.DisableAutomaticUiHide = true;

            PluginInterface.UiBuilder.Draw += OnDraw;
            PluginInterface.UiBuilder.OpenMainUi += ToggleWindow;
            PluginInterface.UiBuilder.OpenConfigUi += ToggleWindow;
        }
        catch
        {
            // Dalamud won't call Dispose if loading fails, so undo whatever was already set up.
            PluginInterface.UiBuilder.Draw -= OnDraw;
            PluginInterface.UiBuilder.OpenMainUi -= ToggleWindow;
            PluginInterface.UiBuilder.OpenConfigUi -= ToggleWindow;
            CommandManager.RemoveHandler(Command);
            CommandManager.RemoveHandler(KoiCommand);
            CommandManager.RemoveHandler(ShortCommand);
            _hook.Dispose();
            Framework.Update -= OnFrameworkUpdate;
            _video?.Dispose();
            _live?.Dispose();
            _mainWindow?.DisposeTabs();
            _windows.RemoveAllWindows();
            _rigs?.Dispose();
            _koi?.Dispose();
            throw;
        }
    }

    // The Body Shape tab's live preview: every character on screen that has slider values (or a built shape).
    private void UpdateBodyShapeTargets(bool here)
    {
        var shapes = _config.Ik.BodyShapes;
        var builtAll = _config.Ik.BodyShapeBuilt;
        var bonesAll = _config.Ik.BoneScales;
        if(!here || ((shapes == null || shapes.Count == 0) && (builtAll == null || builtAll.Count == 0) && (bonesAll == null || bonesAll.Count == 0)))
        {
            if(_bodyShape.Targets.Length > 0)
                _bodyShape.Targets = [];
            return;
        }
        // (Body shape off: only what's built into the models, nothing live on top)
        bool preview = _config.Ik.BodyShapePreview && _config.Ik.BodyShapeOn;
        var list = new List<Limber.BodyShape.BodyShapeLive.Target>();
        foreach(var obj in ObjectTable)
        {
            if(obj is not Dalamud.Game.ClientState.Objects.Types.ICharacter || obj.Address == 0)
                continue;
            var name = obj.Name.TextValue;
            Dictionary<string, float>? values = null, built = null;
            Dictionary<string, Limber.BodyShape.BoneScaleEntry>? bones = null;
            shapes?.TryGetValue(name, out values);
            builtAll?.TryGetValue(name, out built);
            bonesAll?.TryGetValue(name, out bones);
            // (kept on with no values too, so a shape just set back to 0 is taken off a paused pose)
            if(values == null && built == null && bones == null)
                continue;
            // (copies: the bone update reads them while the tab may be changing them)
            var live = preview ? new Dictionary<string, float>(values ?? new()) : new Dictionary<string, float>(built ?? new());
            Dictionary<string, Limber.BodyShape.BoneScaleEntry>? boneCopy = null;
            if(bones != null)
                lock(bones)
                    boneCopy = bones.ToDictionary(kv => kv.Key, kv => kv.Value.Copy());
            list.Add(new(obj.Address, obj.ObjectIndex, live, built != null ? new Dictionary<string, float>(built) : null, boneCopy));
        }
        _bodyShape.Targets = list.ToArray();
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        try
        {
            _props.ApplyResults();
            _actors.CheckPriorityHook();
            _spawner.Update();
            bool here = _config.PluginEnabled && (ClientState.IsGPosing || _config.Ik.WorkOutsideGPose);
            if(here)
                _motions.Update(framework.UpdateDelta.TotalSeconds);
            _oral.Update(framework.UpdateDelta.TotalSeconds);
            // Placed housing props: only while Limber runs here (removed when you leave GPose or turn it off).
            _housing.Update(here);
            _props.UpdateWorld();
            HandleUndoKeys();
            UpdateSoftBodyCandidates();
            UpdateBodyShapeTargets(here);
        }
        catch (Exception e)
        {
            if(DateTime.UtcNow - _lastDrawError > TimeSpan.FromSeconds(10))
            {
                _lastDrawError = DateTime.UtcNow;
                Log.Error(e, "Limber: error listing characters for the soft body");
            }
        }

        foreach (var update in new Action[] { _video.Update, _live.Update })
        {
            try
            {
                update();
            }
            catch (Exception e)
            {
                if(DateTime.UtcNow - _lastDrawError > TimeSpan.FromSeconds(10))
                {
                    _lastDrawError = DateTime.UtcNow;
                    Log.Error(e, "Limber: error playing video motion");
                }
            }
        }
    }

    // Save from any thread: the face presets in the config are also read inside the bone-update hook.
    private void SaveConfig()
    {
        lock(FaceDriver.PresetLock)
            _config.Save();
    }

    /// <summary>
    /// Loads Limber's settings. On the first run of this combined build, carries over the settings of an
    /// earlier separate KoiPoseLink install (links, face presets, sync relay) and of an earlier standalone
    /// Limber install (handle options), so nothing has to be set up again. Never fails: worst case, defaults.
    /// </summary>
    private static Configuration LoadConfig()
    {
        Configuration? config = null;
        try
        {
            config = PluginInterface.GetPluginConfig() as Configuration;
        }
        catch(Exception e)
        {
            Log.Warning(e, "Limber: couldn't read the settings file; starting with defaults.");
        }

        try
        {
            var file = PluginInterface.ConfigFile;

            // A standalone Limber kept its options at the top level of this same file (as an older Version).
            RigSettings? standaloneIk = null;
            if(config != null && config.Version < Configuration.CurrentVersion && file.Exists)
            {
                var json = JObject.Parse(File.ReadAllText(file.FullName));
                if(json["Ik"] == null && json["ShowHandles"] != null)
                {
                    StripTypeNames(json);
                    standaloneIk = json.ToObject<RigSettings>();
                }
            }

            // First run of the combined build: bring over the separate KoiPoseLink install's settings.
            if(config == null || standaloneIk != null)
            {
                var old = file.DirectoryName == null ? null : Path.Combine(file.DirectoryName, "KoiPoseLink.json");
                if(old != null && File.Exists(old))
                {
                    var json = JObject.Parse(File.ReadAllText(old));
                    StripTypeNames(json);
                    var koi = json.ToObject<Configuration>();
                    if(koi != null)
                    {
                        config = koi;
                        Log.Information("Limber: carried over your KoiPoseLink settings.");
                    }
                }
            }

            if(config != null && standaloneIk != null)
                config.Ik = standaloneIk;
        }
        catch(Exception e)
        {
            Log.Warning(e, "Limber: couldn't carry over older settings; using defaults for those.");
        }

        return config ?? new Configuration();
    }

    // Dalamud saves type names ("$type") into settings files; the old ones name classes that no longer exist.
    private static void StripTypeNames(JToken token)
    {
        if(token is JObject obj)
        {
            obj.Remove("$type");
            foreach(var property in obj.Properties())
                StripTypeNames(property.Value);
        }
        else if(token is JArray array)
        {
            foreach(var item in array)
                StripTypeNames(item);
        }
    }

    private void OnCommand(string command, string arguments)
    {
        Log.Information($"Limber: got {command} {arguments}".TrimEnd());
        switch(arguments.Trim().ToLowerInvariant())
        {
            case "on":
                _config.PluginEnabled = true;
                SaveConfig();
                ChatGui.Print("Limber is on.");
                break;
            case "off":
                _config.PluginEnabled = false;
                SaveConfig();
                ChatGui.Print("Limber is off.");
                break;
            case "close":
                _mainWindow.IsOpen = false;
                break;
            case "pause":
                _koi.SendAnimationCommand(Protocol.Wire.CmdToggle);
                break;
            case "undo":
                _rigs.Undo();
                break;
            case "redo":
                _rigs.Redo();
                break;
            case "reset":
                // Way back if a character was moved or scaled somewhere you can't reach it.
                _actors.ResetAll();
                ChatGui.Print("Limber: characters back where the game has them.");
                break;
            default:
                OpenWindow();
                Log.Information($"Limber: opening the window ({command})");
                if(!_saidOpen)
                {
                    // Once per session, so it's clear the command arrived (and the window should be up).
                    _saidOpen = true;
                    ChatGui.Print($"Limber: window opened ({command}).");
                }
                break;
        }
    }

    private bool _saidOpen;

    private void OnKoiCommand(string command, string arguments)
    {
        switch(arguments.Trim().ToLowerInvariant())
        {
            case "on":
                _config.Enabled = true;
                SaveConfig();
                ChatGui.Print("Koikatsu link on.");
                break;
            case "off":
                _config.Enabled = false;
                SaveConfig();
                ChatGui.Print("Koikatsu link off.");
                break;
            case "pause":
                _koi.SendAnimationCommand(Protocol.Wire.CmdToggle);
                break;
            default:
                OpenWindow();
                break;
        }
    }

    // Open (and bring to the front), so /limber always shows the window rather than toggling it shut.
    private void OpenWindow()
    {
        _mainWindow.IsOpen = true;
        _mainWindow.BringToFront();
    }

    private void ToggleWindow() => _mainWindow.Toggle();

    private void OnDraw()
    {
        try
        {
            _selection.Update();

            lock(_rigs.Sync)
                _rigs.SetWatched(_selection.Watched());
            // (a playing timeline changes settings by itself: not undo steps)
            _rigs.WatchParts(ImGui.IsAnyItemActive() || ImGui.IsMouseDown(ImGuiMouseButton.Left) || (_timeline?.Playing ?? false));

            bool here = ClientState.IsGPosing || _config.Ik.WorkOutsideGPose;
            // The camera works in GPose and out of it (whenever Limber is on).
            UpdateCamera(_config.PluginEnabled);
            if(_config.PluginEnabled)
                VoiceSwitcher.Update();
            else
                VoiceSwitcher.ResetAll();
            if(_config.PluginEnabled && here)
            {
                _overlay.Draw();
                _faceTab?.DrawScene();
                _propOverlay.Draw();
                _motions.Draw();
                _oral.DrawGuide(GameGui);
                Limber.Gpu.LiveMesh.Update(); // render thread, before the frame is presented
                Limber.Gpu.LiveSoftBody.Update();
                _timeline?.Update(ImGui.GetIO().DeltaTime); // (first: the features below read what it writes)
                _dynamicBones.Refresh();
                _throb.Refresh();
                _lookAt.Refresh();
                _twerk.Refresh();
                _ahegao.Refresh();
                _expansion.Refresh();
                if(_config.ShowBustTab)
                    _bust.Refresh();
                else
                    _bust.Clear();
                _ballThrob.Refresh();
                _symmetry.Refresh();
                _facePosing.Refresh();
                bool mirrored = _fk.Refresh();
                _fkTab?.AfterMirror(mirrored);
                if(mirrored)
                    _config.Ik.Save();
                if(!_config.Ik.BodyPhysicsOn && (Limber.Gpu.LiveSoftBody.Running || Limber.Gpu.LiveSoftBody.Preparing || Limber.Gpu.LiveSoftBody.Resuming))
                    Limber.Gpu.LiveSoftBody.Stop();
                if(_selection.Actor.IsSet && _config.Ik.BodyPhysicsOn)
                    Limber.Gpu.LiveSoftBody.AutoTick(_selection.Actor.Address, _selection.Actor.ObjectIndex, LimberTab.ReadModelFile, LimberTab.LivePartOf);
                _oral.UpdateLipMesh();
                _ahegao.UpdateFaceRig(LimberTab.ReadModelFile);
                if(_config.ShowBustTab)
                    _bust.UpdateMeshes(LimberTab.ReadModelFile);
                else if(Limber.Gpu.BustShapes.Count > 0)
                    Limber.Gpu.BustShapes.Stop();
            }
            else
            {
                _fk.Clear();
                _throb.Clear();
                _lookAt.Clear();
                _twerk.Clear();
                _ahegao.Clear();
                _expansion.Clear();
                _bust.Clear();
                _ballThrob.Clear();
                _symmetry.Clear();
                _facePosing.Clear();
            }
            if(!(_config.PluginEnabled && here) && (Limber.Gpu.LiveSoftBody.Running || Limber.Gpu.LiveMesh.Wobbling || Limber.Gpu.LipMesh.Running || Limber.Gpu.FaceRig.Running || Limber.Gpu.BustShapes.Count > 0))
            {
                // Turned off (or left GPose without "Also run outside GPose"): the models' own vertices back.
                Limber.Gpu.LiveSoftBody.Stop();
                Limber.Gpu.LiveMesh.StopWobble();
                Limber.Gpu.LipMesh.Stop();
                Limber.Gpu.FaceRig.Stop();
                Limber.Gpu.BustShapes.Stop();
            }
            _typing = ImGui.GetIO().WantTextInput;
        }
        catch(Exception e)
        {
            if(DateTime.UtcNow - _lastDrawError > TimeSpan.FromSeconds(10))
            {
                _lastDrawError = DateTime.UtcNow;
                Log.Error(e, "Limber: error drawing handles");
            }
        }

        // ("Hide Limber window when UI is hidden": the windows wait; the handles follow their own setting)
        if(!(_config.HideWindowWithGameUi && GameGui.GameUiHidden))
            _windows.Draw();
    }

    // Undo / redo with the player's hotkeys (Hotkeys tab): from the game's key state on the framework thread, so
    // they work whether or not a Limber window has focus (not while typing in one of its text boxes).
    private volatile bool _typing;

    // ---- Camera (in GPose and out)

    private bool _noCollideOn;

    private void UpdateCamera(bool on)
    {
        try
        {
            CameraControl.Update(on && _config.Ik.CameraUnlock, _config.Ik.CameraMaxDistance);
            CameraControl.SetOffset(on ? _config.Ik.CameraOffset : System.Numerics.Vector3.Zero);
            bool noCollide = on && _config.Ik.CameraNoCollide;
            if(noCollide != _noCollideOn && CameraControl.SetNoCollision(noCollide))
                _noCollideOn = noCollide;
            else if(noCollide != _noCollideOn)
                _noCollideOn = noCollide; // (not found: don't keep trying every frame)
        }
        catch(Exception e)
        {
            Log.Debug(e, "Limber: camera update failed");
        }
    }

    // ---- Undo / redo of the settings

    // Left out: the page shown (not a change to undo), and the props and motions (their own undo parts).
    private sealed class UndoSettingsResolver : Newtonsoft.Json.Serialization.DefaultContractResolver
    {
        private static readonly HashSet<string> Skip = ["UiPage", "Motions", "Props"];

        protected override IList<Newtonsoft.Json.Serialization.JsonProperty> CreateProperties(Type type, Newtonsoft.Json.MemberSerialization memberSerialization)
        {
            var list = base.CreateProperties(type, memberSerialization);
            return type == typeof(RigSettings) ? list.Where(p => p.PropertyName == null || !Skip.Contains(p.PropertyName)).ToList() : list;
        }
    }

    private static readonly Newtonsoft.Json.JsonSerializerSettings UndoJson = new()
    {
        ContractResolver = new UndoSettingsResolver(),
        ObjectCreationHandling = Newtonsoft.Json.ObjectCreationHandling.Replace,
        ReferenceLoopHandling = Newtonsoft.Json.ReferenceLoopHandling.Ignore,
    };

    private string _settingsShot = "";
    private long _settingsShotAt;
    private bool _mouseWasDown;

    // The settings as text, read again a few times a second and whenever the mouse is let go (all of them each frame
    // would cost frames).
    private string CaptureSettings()
    {
        long now = Environment.TickCount64;
        bool down = ImGui.IsMouseDown(ImGuiMouseButton.Left);
        bool released = _mouseWasDown && !down;
        _mouseWasDown = down;
        if(_settingsShot.Length == 0 || released || now - _settingsShotAt > 300)
        {
            _settingsShot = Newtonsoft.Json.JsonConvert.SerializeObject(_config.Ik, UndoJson);
            _settingsShotAt = now;
        }
        return _settingsShot;
    }

    private void RestoreSettings(string state)
    {
        Newtonsoft.Json.JsonConvert.PopulateObject(state, _config.Ik, UndoJson);
        _settingsShot = ""; // (read fresh next time)
        SaveConfig();
    }

    private void HandleUndoKeys()
    {
        var open = _config.Ik.OpenKey ??= new();
        if(open.PressedNow(KeyState) && !LimberTab.CapturingHotkey && !_typing)
        {
            _mainWindow.Toggle();
            Log.Information("Limber: window toggled by its hotkey");
        }
        var undo = _config.Ik.UndoKey ??= new();
        var redo = _config.Ik.RedoKey ??= new();
        bool undoPressed = undo.PressedNow(KeyState), redoPressed = redo.PressedNow(KeyState);
        if(!_config.PluginEnabled || !_config.Ik.UndoHotkeys || !_rigs.IsActive || LimberTab.CapturingHotkey || _typing)
            return;
        if(undoPressed)
            _rigs.Undo();
        else if(redoPressed)
            _rigs.Redo();
    }

    /// <summary>Characters near you (up to 12), a few times a second, for the soft body (it skips any without the rig).</summary>
    private void UpdateSoftBodyCandidates()
    {
        if(++_softBodyTick % 10 != 0)
            return;
        _props.Refresh(); // props follow their characters as they come and go

        bool Wanted(Limber.BodyRig.SoftBodySettings? s) => s != null && (s.Enabled || !s.ShapeIsDefault);
        // Outside GPose only with "Also run outside GPose" (main Advanced tab).
        bool allowedHere = ClientState.IsGPosing || _config.Ik.WorkOutsideGPose;
        if(!allowedHere || (!Wanted(_config.Ik.SoftBody) && !Wanted(_config.Ik.SoftBodyTesticles) && !Wanted(_config.Ik.SoftBodyButt)))
        {
            _softBody.Candidates = [];
            _softBodyTesticles.Candidates = [];
            _softBodyButt.Candidates = [];
            return;
        }
        var me = ObjectTable.Length > 0 ? ObjectTable[0] : null;
        var centre = me?.Position ?? System.Numerics.Vector3.Zero;
        var list = new List<(nint, ushort, float)>();
        foreach(var obj in ObjectTable)
        {
            if(obj is not Dalamud.Game.ClientState.Objects.Types.ICharacter || obj.Address == 0)
                continue;
            float distance = System.Numerics.Vector3.DistanceSquared(obj.Position, centre);
            if(distance > 40f * 40f)
                continue;
            list.Add((obj.Address, obj.ObjectIndex, distance));
        }
        list.Sort((a, b) => a.Item3.CompareTo(b.Item3));
        var candidates = list.Take(12).Select(c => (c.Item1, c.Item2)).ToArray();
        _softBody.Candidates = candidates;
        _softBodyTesticles.Candidates = candidates;
        _softBodyButt.Candidates = candidates;
    }

    public void Dispose()
    {
        CameraControl.Dispose();
        try { VoiceSwitcher.ResetAll(); } catch { }
        PluginInterface.UiBuilder.Draw -= OnDraw;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleWindow;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleWindow;
        CommandManager.RemoveHandler(Command);
        CommandManager.RemoveHandler(KoiCommand);
        CommandManager.RemoveHandler(ShortCommand);

        Limber.Gpu.LiveMesh.StopWobble();
        Limber.Gpu.LiveSoftBody.Stop();
        Limber.Gpu.LipMesh.Stop();
        Limber.Gpu.FaceRig.Stop();
        Limber.Gpu.BustShapes.Stop();
        Limber.Gpu.SkinCapture.Dispose();
        // Unhook first so nothing runs against the parts being torn down.
        _hook.Dispose();
        _actors.Dispose();
        _spawner.Dispose();
        Framework.Update -= OnFrameworkUpdate;
        _housing.Dispose();
        _video.Dispose();
        _live.Dispose();
        _mainWindow.DisposeTabs();
        _windows.RemoveAllWindows();
        _rigs.Dispose();
        _koi.Dispose();
    }
}
