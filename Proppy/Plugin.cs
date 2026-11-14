using System;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace Proppy;

/// <summary>
/// Proppy: housing items placed anywhere, on your screen only, with their collision (stand, walk and jump on them).
/// Each zone's layout is kept and can come back by itself. Separate from Limber.
/// </summary>
public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager Commands { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IObjectTable Objects { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IDataManager Data { get; private set; } = null!;
    [PluginService] internal static ISigScanner SigScanner { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static ITextureProvider Textures { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static ITargetManager Targets { get; private set; } = null!;

    private const string Command = "/proppy";

    internal Configuration Config { get; }
    internal PropService Props { get; }
    internal Furniture Furniture { get; }
    internal Seat Seat { get; }
    private readonly WindowSystem _windows = new("Proppy");
    private readonly MainWindow _window;
    private bool _away = true;      // between zones (or logged out): no items
    private uint _zone;
    private int _arriveWait;

    public Plugin()
    {
        Config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Props = new PropService(SigScanner, Log);
        Furniture = new Furniture(Data);
        Seat = new Seat(Data);
        _window = new MainWindow(this);
        _windows.AddWindow(_window);
        Commands.AddHandler(Command, new CommandInfo((_, _) => _window.Toggle())
        {
            HelpMessage = "Open Proppy: place housing items anywhere (only on your screen) and stand on them.",
        });
        PluginInterface.UiBuilder.Draw += Draw;
        PluginInterface.UiBuilder.OpenMainUi += _window.Toggle;
        PluginInterface.UiBuilder.OpenConfigUi += _window.Toggle;
        Framework.Update += Update;
        ClientState.Logout += OnLogout;
    }

    private void Draw()
    {
        _windows.Draw();
        _window.DrawGizmo();
    }

    private void OnLogout(int type, int code)
    {
        Seat.StandUp();
        Props.RemoveAll();
        _away = true;
    }

    private bool BetweenZones => Condition[ConditionFlag.BetweenAreas] || Condition[ConditionFlag.BetweenAreas51] || !ClientState.IsLoggedIn;

    private void Update(IFramework framework)
    {
        try
        {
            // Leaving the zone: the items go at once (the zone they belong to is about to go too).
            if(BetweenZones)
            {
                if(!_away)
                {
                    Seat.StandUp();
                    Props.RemoveAll();
                    _away = true;
                }
                return;
            }
            if(_away)
            {
                // Arrived: once you're there and the zone has settled a moment, its saved items come back.
                if(Objects.LocalPlayer == null)
                    return;
                if(++_arriveWait < 60)
                    return;
                _arriveWait = 0;
                _away = false;
                _zone = ClientState.TerritoryType;
                if(Config.AutoRestore)
                    Restore();
            }
            Props.Update(Config.CollisionJustForYou ? JustForYou() : null);
            Seat.Update(Props);
        }
        catch(Exception e)
        {
            Log.Error(e, "Proppy: update failed");
        }
    }

    // ---- Solid only for you: who's around (looked at every few frames, not every frame)

    private readonly System.Collections.Generic.List<Vector3> _others = new();
    private Vector3 _you;
    private bool _haveYou;
    private int _scanWait;

    private Func<PropService.Prop, bool> JustForYou()
    {
        if(--_scanWait <= 0)
        {
            _scanWait = 5;
            _others.Clear();
            _haveYou = false;
            var me = Objects.LocalPlayer;
            if(me != null)
            {
                _you = me.Position;
                _haveYou = true;
            }
            ulong myId = me?.GameObjectId ?? 0;
            uint myEntity = me?.EntityId ?? 0;
            foreach(var obj in Objects)
            {
                if(obj == null || obj.Address == 0 || obj.ObjectIndex == 0 || obj.ObjectIndex == 201)
                    continue; // (you, and your GPose copy)
                if(obj is not Dalamud.Game.ClientState.Objects.Types.ICharacter)
                    continue;
                if(myEntity != 0 && obj.OwnerId == myEntity)
                    continue; // (your minion, chocobo or pet)
                if(me != null && obj.GameObjectId == myId)
                    continue;
                _others.Add(obj.Position);
            }
        }
        return prop =>
        {
            float size = MathF.Max(prop.Scale.X, MathF.Max(prop.Scale.Y, prop.Scale.Z));
            float clear = MathF.Max(1f, Config.ClearRadius) * MathF.Max(0.25f, size);
            // (far from you: nothing needs it solid)
            if(!_haveYou || Vector3.DistanceSquared(_you, prop.Position) > (clear + 6f) * (clear + 6f))
                return false;
            foreach(var at in _others)
                if(Vector3.DistanceSquared(at, prop.Position) < clear * clear)
                    return false;
            return true;
        };
    }

    internal uint Zone => _zone;
    internal bool Away => _away;

    /// <summary>This zone's saved items placed again (any placed now are removed first).</summary>
    internal int Restore()
    {
        Props.RemoveAll();
        if(!Config.Layouts.TryGetValue(_zone, out var saved))
            return 0;
        int n = 0;
        foreach(var s in saved)
            if(Props.Place(s.Path, s.Name, s.Position, s.Rotation, s.Scale, s.Collision) is { } p)
            {
                p.SitOffset = s.SitOffset;
                p.SitTurn = s.SitTurn;
                p.LieOffset = s.LieOffset;
                p.LieTurn = s.LieTurn;
                n++;
            }
        return n;
    }

    /// <summary>This zone's items as they are now, kept.</summary>
    internal void SaveLayout()
    {
        if(_away)
            return;
        Config.Layouts[_zone] = Props.Props.Select(p => new SavedProp
        {
            Path = p.Path, Name = p.Name, Position = p.Position, Rotation = p.Rotation, Scale = p.Scale, Collision = p.Collision,
            SitOffset = p.SitOffset, SitTurn = p.SitTurn, LieOffset = p.LieOffset, LieTurn = p.LieTurn,
        }).ToList();
        if(Config.Layouts[_zone].Count == 0)
            Config.Layouts.Remove(_zone);
        PluginInterface.SavePluginConfig(Config);
    }

    internal void SaveConfig() => PluginInterface.SavePluginConfig(Config);

    /// <summary>Where a new item goes: in front of you, facing the way you face.</summary>
    internal (Vector3 Position, float Yaw) InFront()
    {
        var me = Objects.LocalPlayer;
        if(me == null)
            return (Vector3.Zero, 0f);
        float yaw = me.Rotation;
        var forward = new Vector3(MathF.Sin(yaw), 0f, MathF.Cos(yaw));
        return (me.Position + forward * Math.Clamp(Config.PlaceDistance, 0f, 30f), yaw * 180f / MathF.PI);
    }

    public void Dispose()
    {
        Framework.Update -= Update;
        ClientState.Logout -= OnLogout;
        PluginInterface.UiBuilder.Draw -= Draw;
        PluginInterface.UiBuilder.OpenMainUi -= _window.Toggle;
        PluginInterface.UiBuilder.OpenConfigUi -= _window.Toggle;
        Commands.RemoveHandler(Command);
        _windows.RemoveAllWindows();
        // (removed on the framework thread, where the game expects it)
        try
        {
            Framework.RunOnFrameworkThread(() => Props.Dispose()).Wait(2000);
        }
        catch(Exception e)
        {
            Log.Warning(e, "Proppy: couldn't remove the items on unload");
        }
    }
}
