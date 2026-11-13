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

    private const string Command = "/proppy";

    internal Configuration Config { get; }
    internal PropService Props { get; }
    internal Furniture Furniture { get; }
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
            Props.Update();
        }
        catch(Exception e)
        {
            Log.Error(e, "Proppy: update failed");
        }
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
            if(Props.Place(s.Path, s.Name, s.Position, s.Rotation, s.Scale, s.Collision) != null)
                n++;
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
