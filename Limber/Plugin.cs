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
    private readonly Limber.BodyRig.HousingProps _housing;
    private readonly Limber.Rig.ActorTransforms _actors = new();
    private readonly PropOverlay _propOverlay;
    private readonly MainWindow _mainWindow;
    private readonly WindowSystem _windows = new("Limber");
    private DateTime _lastDrawError = DateTime.MinValue;

    public Plugin()
    {
        _config = LoadConfig();
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
            _rigs.AddUndoPart(new RigService.UndoPart
            {
                Capture = () => Newtonsoft.Json.JsonConvert.SerializeObject(_config.Ik.Motions ?? new()),
                Restore = state =>
                {
                    _config.Ik.Motions = Newtonsoft.Json.JsonConvert.DeserializeObject<System.Collections.Generic.List<MotionProp>>(state) ?? new();
                    SaveConfig();
                },
            });
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
            _overlay.MouseTakenElsewhere = () => _propOverlay.OwnsMouse;
            _hook.Running = () => _config.PluginEnabled;
            _actors.Allowed = () => ClientState.IsGPosing || _config.Ik.WorkOutsideGPose;
            _actors.StartPriority(GameInterop);
            Limber.BodyShape.BodyShapeBuild.SetWorkDir(PluginInterface.ConfigDirectory.FullName);
            _bodyShape.KeepFeet = () => _config.Ik.BodyShapeKeepFeet;
            _hook.Start(("Limber actors", _actors.UpdateFrame), ("Limber body shape", _bodyShape.UpdateFrame), ("the Koikatsu link", _koi.Applier.RunFrame), ("Limber posing", _rigs.UpdateFrame),
                ("Limber props", _props.UpdateFrame),
                ("Limber soft body", _softBody.UpdateFrame), ("Limber soft body (testicles)", _softBodyTesticles.UpdateFrame),
                ("Limber soft body (butt)", _softBodyButt.UpdateFrame),
                ("Limber live mesh", Limber.Gpu.LiveSoftBody.Capture), ("Limber body shape (end of frame)", _bodyShape.AfterFrame));

            var pluginDir = PluginInterface.AssemblyLocation.DirectoryName ?? ".";
            _video = new VideoTab(_koi, pluginDir, PluginInterface.ConfigDirectory.FullName);
            _live = new LiveTab(_koi, pluginDir);
            Framework.Update += OnFrameworkUpdate;

            var syncHelp = new SyncHelpWindow();
            var limberTab = new LimberTab(_rigs, _selection, _overlay, _config.Ik) { SoftBreasts = _softBody, SoftTesticles = _softBodyTesticles, SoftButt = _softBodyButt, PropService = _props, Actors = _actors };
            _mainWindow = new MainWindow(_koi, _hook, limberTab, _video, _live, syncHelp,
                new PropsTab(_props, _propOverlay, _selection, _config.Ik) { Motions = _motions },
                new RendererTab(_rigs, _selection, _config.Ik, limberTab) { PropService = _props },
                new HousingPropsTab(_housing, _selection));
            _mainWindow.BodyShapeTab = new BodyShapeTab(_selection, _config.Ik) { RigEverything = limberTab.RigEverything };
            _windows.AddWindow(_mainWindow);
            _windows.AddWindow(syncHelp);
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
        bool preview = _config.Ik.BodyShapePreview;
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
            bool here = _config.PluginEnabled && (ClientState.IsGPosing || _config.Ik.WorkOutsideGPose);
            if(here)
                _motions.Update(framework.UpdateDelta.TotalSeconds);
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
            _rigs.WatchParts(ImGui.IsAnyItemActive() || ImGui.IsMouseDown(ImGuiMouseButton.Left));

            bool here = ClientState.IsGPosing || _config.Ik.WorkOutsideGPose;
            if(_config.PluginEnabled && here)
            {
                _overlay.Draw();
                _propOverlay.Draw();
                _motions.Draw();
                Limber.Gpu.LiveMesh.Update(); // render thread, before the frame is presented
                Limber.Gpu.LiveSoftBody.Update();
            }
            else if(Limber.Gpu.LiveSoftBody.Running || Limber.Gpu.LiveMesh.Wobbling)
            {
                // Turned off (or left GPose without "Also run outside GPose"): the models' own vertices back.
                Limber.Gpu.LiveSoftBody.Stop();
                Limber.Gpu.LiveMesh.StopWobble();
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

        _windows.Draw();
    }

    // Undo / redo with the player's hotkeys (Hotkeys tab): from the game's key state on the framework thread, so
    // they work whether or not a Limber window has focus (not while typing in one of its text boxes).
    private volatile bool _typing;

    private void HandleUndoKeys()
    {
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
        PluginInterface.UiBuilder.Draw -= OnDraw;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleWindow;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleWindow;
        CommandManager.RemoveHandler(Command);
        CommandManager.RemoveHandler(KoiCommand);
        CommandManager.RemoveHandler(ShortCommand);

        Limber.Gpu.LiveMesh.StopWobble();
        Limber.Gpu.LiveSoftBody.Stop();
        // Unhook first so nothing runs against the parts being torn down.
        _hook.Dispose();
        _actors.Dispose();
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
