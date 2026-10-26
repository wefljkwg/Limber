using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Limber.Core;

// The one hook on the game's per-frame bone update, shared by everything in Limber. After the game (and any
// other posing tool) has built the frame's pose, it runs each stage in order: the Koikatsu link first, then
// Limber's IK goals on top, so held/pinned bones and attachments still apply to Koikatsu-driven characters.
public sealed class BoneUpdateHook : IDisposable
{
    // BonePhysicsUpdater::Update - the same pattern Brio uses. Deliberately a constant, not a setting: a wrong
    // pattern could hook an unrelated game function. After a game patch, update it here and rebuild.
    public const string Signature = "48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 41 54 41 56 48 83 EC ?? 48 8B 59 ?? 45 33 E4";

    private const int MaxRehooks = 25;

    private delegate nint UpdateBonesDelegate(nint a1);

    private readonly IGameInteropProvider _interop;
    private readonly IFramework _framework;
    private readonly IPluginLog _log;
    private readonly nint _address;

    private Hook<UpdateBonesDelegate>? _hook;
    // Every detour delegate handed to the game stays referenced for the plugin's lifetime (at most MaxRehooks + 1).
    private readonly List<UpdateBonesDelegate> _detours = [];
    private (string Name, Action Run)[] _stages = [];
    private DateTime[] _lastStageError = [];

    private readonly byte[] _entryBytes = new byte[16];
    private readonly byte[] _entryCheck = new byte[16];
    private int _rehookCount;
    private int _framesSinceCheck;
    private bool _started;

    /// <summary>Whether the stages run at all (the whole plugin's on / off switch); the game's own update always runs.</summary>
    public Func<bool> Running = () => true;

    public string Status { get; private set; }
    public bool IsHooked => _hook != null;

    public BoneUpdateHook(IGameInteropProvider interop, ISigScanner scanner, IFramework framework, IPluginLog log)
    {
        _interop = interop;
        _framework = framework;
        _log = log;

        try
        {
            // Refuse to hook unless the pattern identifies exactly one function.
            var matches = scanner.ScanAllText(Signature);
            if(matches.Length == 1)
            {
                _address = matches[0];
                Status = "Found the game's bone update.";
            }
            else
            {
                Status = matches.Length == 0
                    ? "Couldn't find the game's bone update - this game version needs an updated build. Nothing will be posed."
                    : $"The bone update pattern matches {matches.Length} places - refusing to hook. Nothing will be posed.";
                _log.Warning("Limber: " + Status);
            }
        }
        catch(Exception e)
        {
            Status = "Couldn't search for the game's bone update - see /xllog. Nothing will be posed.";
            _log.Error(e, "Limber: signature scan failed");
        }
    }

    // Install the hook. Stages run in the given order, each one isolated from the others' errors.
    public void Start(params (string Name, Action Run)[] stages)
    {
        if(_started)
            return;
        _started = true;

        _stages = stages;
        _lastStageError = new DateTime[stages.Length];

        if(_address != 0)
            CreateHook();

        _framework.Update += OnFrameworkUpdate;
    }

    private void CreateHook()
    {
        Hook<UpdateBonesDelegate>? created = null;
        try
        {
            // Each detour calls the original captured for its own hook, so a call that is already running when
            // an older hook is disposed never touches a disposed object.
            UpdateBonesDelegate? original = null;
            UpdateBonesDelegate detour = a1 => Detour(original!, a1);
            created = _interop.HookFromAddress<UpdateBonesDelegate>(_address, detour);
            original = created.Original;
            _detours.Add(detour);
            _detours.Add(original);

            // Enable the new hook before letting go of the old one, so a failure never leaves nothing hooked.
            // (For at most one frame both may run; posing twice in a frame is harmless.)
            var old = _hook;
            created.Enable();
            _hook = created;
            old?.Dispose();

            Marshal.Copy(_address, _entryBytes, 0, _entryBytes.Length);
            Status = $"Hooked the game's bone update at 0x{_address:X}.";
        }
        catch(Exception e)
        {
            if(created != null && created != _hook)
                created.Dispose();
            if(_hook == null)
                Status = "Couldn't hook the game's bone update - see /xllog. Nothing will be posed.";
            _log.Error(e, "Limber: failed to hook the bone update");
        }
    }

    private nint Detour(UpdateBonesDelegate original, nint a1)
    {
        // Always let the game (and anything hooked before us) do its own update first, exactly once.
        var result = original(a1);

        bool running;
        try
        {
            running = Running();
        }
        catch
        {
            running = true;
        }
        if(!running)
            return result;

        var stages = _stages;
        for(int i = 0; i < stages.Length; i++)
        {
            try
            {
                stages[i].Run();
            }
            catch(Exception e)
            {
                // This runs every frame; log a failing stage at most every 10 seconds.
                if(DateTime.UtcNow - _lastStageError[i] > TimeSpan.FromSeconds(10))
                {
                    _lastStageError[i] = DateTime.UtcNow;
                    _log.Error(e, $"Limber: error in {stages[i].Name}");
                }
            }
        }

        return result;
    }

    // Stay the last thing to touch the pose each frame, even if another plugin hooks the same function after
    // Limber loaded: if the start of the function changed, hook it again on top.
    private void OnFrameworkUpdate(IFramework framework)
    {
        if(_hook == null || ++_framesSinceCheck < 120)
            return;

        _framesSinceCheck = 0;
        Marshal.Copy(_address, _entryCheck, 0, _entryCheck.Length);
        if(!_entryCheck.AsSpan().SequenceEqual(_entryBytes) && _rehookCount < MaxRehooks)
        {
            _rehookCount++;
            _log.Information("Limber: another plugin hooked the bone update after Limber; re-hooking so Limber's pose is applied last.");
            CreateHook();
        }
    }

    public void Dispose()
    {
        _framework.Update -= OnFrameworkUpdate;
        _hook?.Disable();
        _hook?.Dispose();
        _hook = null;
    }
}
