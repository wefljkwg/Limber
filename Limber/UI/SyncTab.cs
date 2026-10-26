using System;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Limber.Koi;
using Limber.Koi.Sync;

namespace Limber.UI;

/// <summary>The "Sync" tab: one-time relay setup, then invite / join / host controls.</summary>
public sealed class SyncTab
{
    private static readonly Vector4 Good = new(0.45f, 0.85f, 0.45f, 1f);
    private static readonly Vector4 Warn = new(0.95f, 0.75f, 0.30f, 1f);
    private static readonly Vector4 Bad = new(0.95f, 0.40f, 0.40f, 1f);
    private static readonly Vector4 Dim = new(0.65f, 0.65f, 0.65f, 1f);
    private static readonly string[] Channels = ["Channel 1", "Channel 2", "Channel 3", "Channel 4", "Channel 5", "Channel 6", "Channel 7", "Channel 8"];
    private static readonly string[] PartnerChannels = ["Don't move my partner", .. Channels];

    private readonly KoiLink _plugin;
    private readonly SyncHelpWindow _help;

    private string _apiToken = "";
    private string _accountId = "";
    private string _joinCode = "";
    private bool _joinAsHost;
    private volatile string _deployStatus = "";
    private volatile string? _deployLink;
    private volatile bool _deployFailed;
    private Task? _deployTask;
    private CancellationTokenSource? _deployCts;
    private bool _showSetup;
    private volatile string? _subdomainWarning;

    /// <summary>Reports progress synchronously, so a late message can never overwrite the final result.</summary>
    private sealed class StatusProgress(SyncTab tab) : IProgress<string>
    {
        public void Report(string value) => tab._deployStatus = value;
    }

    public void Dispose() => _deployCts?.Cancel();

    private Configuration Cfg => _plugin.Config;
    private SyncSession S => _plugin.Sync;

    public SyncTab(KoiLink plugin, SyncHelpWindow help)
    {
        _plugin = plugin;
        _help = help;
    }

    public void Draw()
    {
        if (ImGui.Button("Instructions"))
            _help.IsOpen = true;
        ImGui.SameLine();
        ImGui.TextColored(Dim, "Sync your poses with another player. Nothing goes through the game's servers.");
        ImGui.Separator();

        DrawRelay();

        if (!S.RelayConfigured)
            return;

        ImGui.Separator();
        DrawSession();
    }

    // ---------------------------------------------------------------------------------------
    // Step 1: your relay

    private void DrawRelay()
    {
        ImGui.Text("1. Your relay");
        if (S.RelayConfigured && !_showSetup)
        {
            var host = new Uri(Cfg.RelayUrl!).Host;
            var (text, color) = S.RelayState switch
            {
                RelayConnection.State.Connected => ($"Connected ({host})", Good),
                RelayConnection.State.Connecting => ("Connecting...", Warn),
                RelayConnection.State.Failed => ("Can't reach your relay: " + S.RelayError + " (retrying)", Bad),
                _ => ("Not connected", Dim),
            };
            var on = Cfg.SyncEnabled;
            if (ImGui.Checkbox("Connect to my relay", ref on))
            {
                Cfg.SyncEnabled = on;
                if (on) S.ConnectRelay(); else S.DisconnectRelay();
                Save();
            }
            ImGui.SameLine();
            ImGui.TextColored(color, text);
            if (_subdomainWarning is { } shown)
                ImGui.TextColored(Warn, $"Note: your partner will see your relay address ({shown}). Your Cloudflare account already had " +
                                        "that name; if it contains anything personal, you can rename it in Cloudflare (Workers & Pages > " +
                                        "your workers.dev subdomain > Change), then press \"Set up my relay again\".");
            if (ImGui.SmallButton("Set up my relay again"))
                _showSetup = true;
            return;
        }

        ImGui.TextWrapped("One-time setup: Limber puts a tiny relay on your own free Cloudflare account. " +
                          "Click Instructions for the 6 short steps.");
        ImGui.SetNextItemWidth(380);
        ImGui.InputText("Cloudflare API token", ref _apiToken, 200, ImGuiInputTextFlags.Password);
        ImGui.SetNextItemWidth(380);
        ImGui.InputText("Account ID (only if asked)", ref _accountId, 64);

        var busy = _deployTask is { IsCompleted: false };
        if (busy)
        {
            ImGui.TextColored(Warn, _deployStatus);
            if (ImGui.Button("Cancel"))
                _deployCts?.Cancel();
        }
        else
        {
            if (ImGui.Button("Deploy my relay") && _apiToken.Trim().Length > 0)
                StartDeploy();
            if (S.RelayConfigured)
            {
                ImGui.SameLine();
                if (ImGui.Button("Keep my current relay"))
                    _showSetup = false;
            }
            if (!string.IsNullOrEmpty(_deployStatus))
                ImGui.TextColored(_deployFailed ? Bad : Good, _deployStatus);
            if (_deployLink is { } link && ImGui.Button("Open that page"))
                Dalamud.Utility.Util.OpenLink(link);
        }
        ImGui.TextColored(Dim, "Your token is only used for this and is never saved.");
    }

    private void StartDeploy()
    {
        var token = _apiToken;
        var account = _accountId;
        _deployFailed = false;
        _deployLink = null;
        _deployStatus = "Starting...";
        _deployCts?.Dispose();
        _deployCts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var ct = _deployCts.Token;
        var progress = new StatusProgress(this);

        _deployTask = Task.Run(async () =>
        {
            try
            {
                var result = await CloudflareDeployer.DeployAsync(token, account, progress, ct);
                await Plugin.Framework.RunOnFrameworkThread(() =>
                {
                    S.DisconnectRelay();
                    Cfg.RelayUrl = result.RelayUrl;
                    Cfg.RelayOwnerToken = result.OwnerToken;
                    Cfg.RelayAccountId = result.AccountId;
                    Cfg.SyncEnabled = true;
                    Save();
                    S.ConnectRelay();
                    _showSetup = false;
                });
                _apiToken = ""; // forget the token
                _deployStatus = "Relay ready. You can delete the API token in Cloudflare now if you like.";
                _subdomainWarning = result.SubdomainLooksPersonal ? new Uri(result.RelayUrl).Host : null;
            }
            catch (CloudflareDeployer.DeployException e)
            {
                _deployFailed = true;
                _deployLink = e.Link;
                _deployStatus = e.Message;
            }
            catch (OperationCanceledException)
            {
                _deployFailed = true;
                _deployStatus = "Cancelled.";
            }
            catch (Exception e)
            {
                _deployFailed = true;
                _deployStatus = "Something went wrong talking to Cloudflare: " + e.Message;
                Plugin.Log.Warning(e, "Limber: relay deploy failed");
            }
        });
    }

    // ---------------------------------------------------------------------------------------
    // Step 2: syncing

    private void DrawSession()
    {
        ImGui.Text("2. Sync");
        if (!string.IsNullOrEmpty(S.Message))
            ImGui.TextColored(S.MessageIsError ? Bad : Dim, S.Message);

        var connected = S.RelayState == RelayConnection.State.Connected;
        switch (S.State)
        {
            case SyncSession.Phase.Idle:
                DrawIdle(connected);
                break;

            case SyncSession.Phase.Inviting:
            case SyncSession.Phase.IncomingRequest:
                ImGui.Text("Your invite code (send it to your partner):");
                var code = S.InviteCode ?? "";
                ImGui.SetNextItemWidth(420);
                ImGui.InputText("##invite", ref code, 200, ImGuiInputTextFlags.ReadOnly);
                ImGui.SameLine();
                if (ImGui.Button("Copy"))
                    ImGui.SetClipboardText(code);
                ImGui.TextColored(Dim, S.State == SyncSession.Phase.IncomingRequest
                    ? "Someone used your code - answer in the pop-up."
                    : "Waiting for your partner to use it...");
                if (ImGui.Button("Cancel invite"))
                    S.CancelInvite();
                break;

            case SyncSession.Phase.Joining:
            case SyncSession.Phase.WaitingForAnswer:
                ImGui.TextColored(Warn, S.State == SyncSession.Phase.Joining ? "Connecting..." : "Waiting for them to accept...");
                if (ImGui.Button("Cancel"))
                    S.Leave();
                break;

            case SyncSession.Phase.Active:
                DrawActive();
                break;
        }

        ImGui.Separator();
        DrawHostSettings();
    }

    private void DrawIdle(bool connected)
    {
        var accept = Cfg.SyncAcceptRequests;
        if (ImGui.Checkbox("Accept sync requests", ref accept))
        {
            Cfg.SyncAcceptRequests = accept;
            Save();
        }
        ImGui.TextColored(Dim, "Needed to create invite codes. You still accept or decline every request.");

        using (ImRaiiDisabled(!connected || !Cfg.SyncAcceptRequests))
        {
            if (ImGui.Button("Create invite code"))
                S.CreateInvite();
        }

        ImGui.Spacing();
        ImGui.Text("Or join someone with their code:");
        ImGui.SetNextItemWidth(420);
        ImGui.InputText("##joincode", ref _joinCode, 200);
        ImGui.SameLine();
        if (ImGui.Button("Paste"))
            _joinCode = ImGui.GetClipboardText().Trim();

        if (ImGui.RadioButton("I'll host (my Koikatsu moves both of us)", _joinAsHost)) _joinAsHost = true;
        if (ImGui.RadioButton("Follow them (their Koikatsu moves both of us)", !_joinAsHost)) _joinAsHost = false;

        using (ImRaiiDisabled(!connected || _joinCode.Trim().Length == 0))
        {
            if (ImGui.Button("Send request"))
                S.Join(_joinCode, _joinAsHost);
        }
        if (!connected)
            ImGui.TextColored(Warn, "Connect to your relay first (step 1).");
    }

    private void DrawActive()
    {
        var p = S.Partner;
        ImGui.TextColored(Good, $"Synced with {p?.Name} ({p?.WorldName})");
        ImGui.Text(S.IAmHost ? "You are the host: your Koikatsu moves both of you." : "Following: their Koikatsu moves both of you.");

        using (ImRaiiDisabled(S.SwapRequested))
        {
            if (ImGui.Button(S.IAmHost ? "Swap host (let them host)" : "Swap host (I'll host)"))
                S.RequestSwap();
        }
        ImGui.SameLine();
        if (ImGui.Button("End sync"))
            S.Leave();
        if (S.SwapRequested)
            ImGui.TextColored(Warn, "Waiting for your partner to answer the swap...");

        if (!_plugin.SyncPartnerVisible)
            ImGui.TextColored(Warn, "Your partner's character isn't near you right now - get close to see them move.");
    }

    private void DrawHostSettings()
    {
        var changed = false;
        if (ImGui.CollapsingHeader("When I'm the host"))
        {
            var mine = Math.Clamp(Cfg.SyncHostChannel, 0, 7);
            ImGui.SetNextItemWidth(200);
            if (ImGui.Combo("Moves my character", ref mine, Channels)) { Cfg.SyncHostChannel = mine; changed = true; }

            var theirs = Math.Clamp(Cfg.SyncPartnerChannel + 1, 0, 8);
            ImGui.SetNextItemWidth(200);
            if (ImGui.Combo("Moves my partner", ref theirs, PartnerChannels)) { Cfg.SyncPartnerChannel = theirs - 1; changed = true; }

            var rate = Cfg.SyncRate;
            ImGui.SetNextItemWidth(200);
            if (ImGui.SliderInt("Updates per second", ref rate, 10, 60)) { Cfg.SyncRate = rate; changed = true; }
            ImGui.TextColored(Dim, "Your Body / Face settings are used on both screens. 30 is smooth and stays well inside the free relay allowance.");
        }

        if (ImGui.CollapsingHeader("When I'm following"))
        {
            ImGui.TextColored(Dim, "On my screen, allow these from the host's setup:");
            var g = Cfg.SyncShowGenitals;
            if (ImGui.Checkbox("Genitals", ref g)) { Cfg.SyncShowGenitals = g; changed = true; }
            ImGui.SameLine();
            var b = Cfg.SyncShowBreasts;
            if (ImGui.Checkbox("Breast physics", ref b)) { Cfg.SyncShowBreasts = b; changed = true; }
        }

        var outside = Cfg.SyncOutsideGPose;
        if (ImGui.Checkbox("Sync outside GPose too", ref outside)) { Cfg.SyncOutsideGPose = outside; changed = true; }
        ImGui.TextColored(Dim, "Only you two see it, on your own screens. Paused during cutscenes.");

        if (changed)
            Save();
    }

    private static IDisposable ImRaiiDisabled(bool disabled)
        => Dalamud.Interface.Utility.Raii.ImRaii.Disabled(disabled);

    private void Save()
    {
        lock (Limber.Koi.Posing.FaceDriver.PresetLock)
            Cfg.Save();
    }
}
