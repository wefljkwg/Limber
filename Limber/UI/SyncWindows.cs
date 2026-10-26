using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Limber.Koi;
using Limber.Koi.Sync;

namespace Limber.UI;

/// <summary>The "Instructions" pop-up: setup, how sync works, and how privacy is kept.</summary>
public sealed class SyncHelpWindow : Window
{
    public SyncHelpWindow() : base("Limber Sync - Instructions###LimberSyncHelp")
    {
        Size = new Vector2(620, 560);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        if (ImGui.BeginTabBar("##synchelp"))
        {
            foreach (var (title, lines) in SyncHelpText.Sections)
            {
                var tab = title.Split(" (")[0];
                if (!ImGui.BeginTabItem(tab))
                    continue;
                ImGui.Spacing();
                ImGui.TextColored(new Vector4(0.95f, 0.85f, 0.55f, 1f), title);
                ImGui.Separator();
                foreach (var line in lines)
                {
                    ImGui.TextWrapped(line);
                    ImGui.Spacing();
                }
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }
    }
}

/// <summary>Small window that pops up by itself when someone asks to sync or to swap host.</summary>
public sealed class SyncPromptWindow : Window
{
    private readonly KoiLink _plugin;

    public SyncPromptWindow(KoiLink plugin)
        : base("Limber Sync###LimberSyncPrompt", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse)
    {
        _plugin = plugin;
        RespectCloseHotkey = false;
        ShowCloseButton = false; // it must be answered
    }

    public override bool DrawConditions()
    {
        var s = _plugin.Sync;
        return s.State == SyncSession.Phase.IncomingRequest || (s.State == SyncSession.Phase.Active && s.SwapPending);
    }

    public override void PreOpenCheck() => IsOpen = true;

    public override void Draw()
    {
        var s = _plugin.Sync;
        if (s.State == SyncSession.Phase.IncomingRequest && s.Partner != null)
        {
            ImGui.Text($"{s.Partner.Name} ({s.Partner.WorldName}) wants to sync with you.");
            ImGui.Text(s.PartnerWantsHost
                ? "They will be the host: their Koikatsu moves both of you."
                : "You will be the host: your Koikatsu moves both of you.");
            ImGui.TextColored(new Vector4(0.95f, 0.75f, 0.30f, 1f), "Only accept if this is the person you gave your code to.");
            ImGui.Spacing();
            if (_plugin.CurrentPlayer is { } me && ImGui.Button("Accept"))
                s.AcceptRequest(me);
            ImGui.SameLine();
            if (ImGui.Button("Decline"))
                s.DeclineRequest();
        }
        else if (s.SwapPending)
        {
            ImGui.Text(s.IAmHost
                ? "Your partner wants to take over as host."
                : "Your partner wants you to take over as host (your Koikatsu will move both of you).");
            if (ImGui.Button("Accept"))
                s.AnswerSwap(true);
            ImGui.SameLine();
            if (ImGui.Button("Decline"))
                s.AnswerSwap(false);
        }
    }
}
