using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace Limber.UI;

/// <summary>The Timeline in a window of its own (Timeline page > Move to a new window), until that's turned off.</summary>
public sealed class TimelineWindow : Window
{
    private readonly TimelineTab _tab;
    private readonly RigSettings _config;

    public TimelineWindow(TimelineTab tab, RigSettings config) : base("Limber Timeline###LimberTimelineWindow")
    {
        _tab = tab;
        _config = config;
        Size = new Vector2(900, 420);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    // Its X: Dalamud closes it, but this re-opened it from the setting on the next frame before OnClose ever ran, so
    // the X did nothing. Closed since last frame (by the X) -> OnClose first, then the setting decides.
    private bool _wasOpen;

    public override void PreOpenCheck()
    {
        if(_wasOpen && !IsOpen)
            OnClose();
        IsOpen = _config.TimelineOwnWindow;
        _wasOpen = IsOpen;
    }

    // (closing it with its X puts the Timeline back on its page)
    public override void OnClose()
    {
        if(_config.TimelineOwnWindow)
        {
            _config.TimelineOwnWindow = false;
            _config.Save();
        }
    }

    public override void Draw() => _tab.Draw();
}
