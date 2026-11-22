using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Sync.Plugin.Apply;
using Sync.Protocol;

namespace Sync.Plugin.Windows;

public sealed class MainWindow : ThemedWindow
{
    private enum Panel
    {
        None,
        AddPair,
        Filter,
        Profile,
    }

    private enum PairTab
    {
        Visible,
        Online,
        Offline,
        Paused,
    }

    private readonly Plugin plugin;
    private Panel panel = Panel.None;
    private string pairInput = string.Empty;
    private string aliasInput = string.Empty;
    private bool aliasEdited;
    private string filter = string.Empty;
    private string? highlight;
    private DateTime highlightUntil;
    private bool scrollToHighlight;
    private string nicknameInput = string.Empty;
    private const int NicknameMaxLength = 32;

    public MainWindow(Plugin plugin) : base("Lunar Sync###LunarSyncMain")
    {
        this.plugin = plugin;
        Flags = ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoScrollbar;
        Size = new Vector2(380, 560);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(320, 380),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        TitleBarButtons =
        [
            new TitleBarButton
            {
                Icon = FontAwesomeIcon.Cog,
                IconOffset = new Vector2(-1, 1),
                Click = _ => plugin.ToggleSettingsUi(),
                ShowTooltip = () => ImGui.SetTooltip("Settings"),
            },
        ];
    }

    public void Highlight(string uid)
    {
        highlight = uid;
        highlightUntil = DateTime.UtcNow.AddSeconds(4);
        scrollToHighlight = true;
        filter = string.Empty;
    }

    protected override void DrawContents()
    {
        if (highlight is not null && DateTime.UtcNow > highlightUntil)
            highlight = null;
        var sync = plugin.Sync;
        DrawHeader(sync);
        ImGui.Separator();
        DrawStatusLine(sync);
        ImGui.Separator();

        if (sync.IsConnected)
        {
            DrawTabs();
            DrawPanel(sync);
        }

        var footerLines = 2 + (sync.LastMessage.Length > 0 ? 1 : 0);
        var footer = ImGui.GetFrameHeightWithSpacing() * footerLines + ImGui.GetStyle().ItemSpacing.Y * 2;
        using (var child = ImRaii.Child("##pairs", new Vector2(0, -footer), false))
        {
            if (child.Success)
            {
                if (sync.IsConnected)
                    DrawPairFolders(sync);
                else
                    DrawNoPairs(sync);
            }
        }

        ImGui.Separator();
        DrawTransfers(sync);
    }

    private void DrawHeader(SyncService sync)
    {
        var id = sync.MyId;
        if (sync.IsConnected && id is not null)
        {
            var main = id.Alias ?? id.DisplayCode;
            Ui.Centered(main, Ui.Accent, 1.6f);
            if (ImGui.IsItemClicked())
                ImGui.SetClipboardText(main);
            Ui.Tooltip(id.Alias is null ? "Your pair code. Click to copy it and send it to a friend." : "Your alias. Friends can pair with it. Click to copy.");
            if (id.Alias is not null)
            {
                Ui.Centered(id.DisplayCode, Ui.Grey, 1f);
                if (ImGui.IsItemClicked())
                    ImGui.SetClipboardText(id.DisplayCode);
                Ui.Tooltip("Your pair code. Click to copy.");
            }
            return;
        }

        var (color, title, detail) = sync.Status switch
        {
            ConnectionStatus.Connecting => (Ui.Yellow, "Connecting", $"Connecting to {sync.StatusDetail}..."),
            ConnectionStatus.Reconnecting => (Ui.Yellow, "Reconnecting", "The connection dropped. Reconnecting..."),
            ConnectionStatus.Rejected => (Ui.Red, "Rejected", sync.StatusDetail),
            ConnectionStatus.Failed => (Ui.Red, "Not connected", sync.IsRunning ? $"{sync.StatusDetail} Retrying..." : sync.StatusDetail),
            _ => (Ui.Grey, "Offline", plugin.WaitingForLogin ? "Connects when you log in." : "Not connected. Use the link button to connect."),
        };
        Ui.Centered(title, color, 1.6f);
        using (ImRaii.PushColor(ImGuiCol.Text, color))
            ImGui.TextWrapped(detail);
    }

    private void DrawStatusLine(SyncService sync)
    {
        var config = plugin.Configuration;
        var style = ImGui.GetStyle();
        var buttonWidth = Ui.IconButtonWidth(FontAwesomeIcon.Unlink);
        var buttons = buttonWidth * 3 + style.ItemSpacing.X * 2;
        var right = ImGui.GetWindowContentRegionMax().X;

        ImGui.AlignTextToFramePadding();
        if (sync.IsConnected)
        {
            var paired = sync.Pairs.Where(p => p.State == PairState.Paired).ToList();
            var online = paired.Count(p => p.Online);
            using (ImRaii.PushColor(ImGuiCol.Text, Ui.Green))
                ImGui.TextUnformatted(online.ToString());
            ImGui.SameLine(0, style.ItemInnerSpacing.X);
            ImGui.TextUnformatted($"of {paired.Count} pair{(paired.Count == 1 ? string.Empty : "s")} online");
        }
        else
        {
            ImGui.TextDisabled("Not connected");
        }

        ImGui.SameLine(Math.Max(ImGui.GetCursorPosX(), right - buttons));
        var applying = config.ApplyPairs;
        using (ImRaii.PushColor(ImGuiCol.Text, Ui.Yellow, !applying))
        {
            if (ImGuiComponents.IconButton("##pauseall", applying ? FontAwesomeIcon.Pause : FontAwesomeIcon.Play))
            {
                config.ApplyPairs = !applying;
                config.Save();
            }
        }
        Ui.Tooltip(applying
            ? "Pause everyone: all pairs look unmodded to you until you resume. They still see your mods."
            : "Paused: pairs look unmodded to you. Click to show their mods again.");

        ImGui.SameLine();
        if (ImGuiComponents.IconButton("##preview", FontAwesomeIcon.Eye))
            plugin.TogglePreviewUi();
        Ui.Tooltip("Sync preview: what you send and uploads.");

        ImGui.SameLine();
        var wanted = plugin.ConnectionWanted;
        using (ImRaii.PushColor(ImGuiCol.Text, wanted ? Ui.Red : Ui.Green))
        {
            if (ImGuiComponents.IconButton("##connect", wanted ? FontAwesomeIcon.Unlink : FontAwesomeIcon.Link))
            {
                if (wanted)
                    plugin.Disconnect();
                else
                    plugin.Connect();
            }
        }
        Ui.Tooltip(wanted ? "Disconnect" : "Connect");
    }

    private void DrawTabs()
    {
        var style = ImGui.GetStyle();
        var tabs = new (Panel Panel, FontAwesomeIcon Icon, string Tip)[]
        {
            (Panel.AddPair, FontAwesomeIcon.UserPlus, "Add a pair"),
            (Panel.Filter, FontAwesomeIcon.Filter, "Filter pairs"),
            (Panel.Profile, FontAwesomeIcon.IdCard, "Your pair code and alias"),
        };
        var width = (ImGui.GetContentRegionAvail().X - style.ItemSpacing.X * (tabs.Length - 1)) / tabs.Length;
        var size = new Vector2(width, ImGui.GetFrameHeight());
        var drawList = ImGui.GetWindowDrawList();

        ImGuiHelpers.ScaledDummy(1f);
        using (ImRaii.PushColor(ImGuiCol.Button, Vector4.Zero))
        {
            for (var i = 0; i < tabs.Length; i++)
            {
                var (target, icon, tip) = tabs[i];
                if (i > 0)
                    ImGui.SameLine();
                var start = ImGui.GetCursorScreenPos();
                bool clicked;
                using (ImRaii.PushFont(UiBuilder.IconFont))
                    clicked = ImGui.Button($"{icon.ToIconString()}##tab{i}", size);
                if (clicked)
                    panel = panel == target ? Panel.None : target;
                Ui.Tooltip(tip);
                if (panel == target)
                {
                    var y = start.Y + size.Y + 1;
                    drawList.AddLine(new Vector2(start.X, y), new Vector2(start.X + size.X, y), ImGui.GetColorU32(Ui.Accent), 2);
                }
            }
        }
        ImGuiHelpers.ScaledDummy(2f);
    }

    private void DrawPanel(SyncService sync)
    {
        switch (panel)
        {
            case Panel.AddPair:
                DrawAddPair(sync);
                break;
            case Panel.Filter:
                ImGui.SetNextItemWidth(-Ui.IconButtonWidth(FontAwesomeIcon.Times) - ImGui.GetStyle().ItemSpacing.X);
                ImGui.InputTextWithHint("##filter", "Filter by name, alias or pair code", ref filter, 64);
                ImGui.SameLine();
                using (ImRaii.Disabled(filter.Length == 0))
                {
                    if (ImGuiComponents.IconButton("##clearfilter", FontAwesomeIcon.Times))
                        filter = string.Empty;
                }
                Ui.Tooltip("Clear the filter");
                break;
            case Panel.Profile:
                DrawProfile(sync);
                break;
            default:
                return;
        }
        ImGui.Separator();
    }

    private void DrawAddPair(SyncService sync)
    {
        const string label = "Send request";
        var buttonWidth = ImGui.CalcTextSize(label).X + ImGui.GetStyle().FramePadding.X * 2;
        ImGui.SetNextItemWidth(-buttonWidth - ImGui.GetStyle().ItemSpacing.X);
        var submitted = ImGui.InputTextWithHint("##pair", "Friend's pair code or alias", ref pairInput, 32, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        using (ImRaii.Disabled(pairInput.Trim().Length == 0))
        {
            if ((ImGui.Button(label) || submitted) && pairInput.Trim().Length > 0)
            {
                sync.RequestPair(pairInput);
                pairInput = string.Empty;
            }
        }
        Ui.HelpText("They see your request and can accept it. You only share data with people you've both paired with.");
    }

    private void DrawProfile(SyncService sync)
    {
        var id = sync.MyId;
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Pair code");
        ImGui.SameLine(110 * ImGuiHelpers.GlobalScale);
        using (ImRaii.PushColor(ImGuiCol.Text, Ui.Accent))
            ImGui.TextUnformatted(id?.DisplayCode ?? "-");
        if (id is not null)
        {
            ImGui.SameLine();
            if (ImGuiComponents.IconButton("##copycode", FontAwesomeIcon.Copy))
                ImGui.SetClipboardText(id.DisplayCode);
            Ui.Tooltip("Copy your pair code");
            ImGui.SameLine();
            using (ImRaii.Disabled(!ImGui.GetIO().KeyCtrl))
            {
                if (ImGuiComponents.IconButton("##newcode", FontAwesomeIcon.Sync))
                    sync.RegenerateCode();
            }
            Ui.Tooltip("New pair code. Hold Ctrl and click. Your old code stops working; existing pairs stay.");
        }

        if (!aliasEdited)
            aliasInput = id?.Alias ?? string.Empty;
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Alias");
        ImGui.SameLine(110 * ImGuiHelpers.GlobalScale);
        const string label = "Set";
        ImGui.SetNextItemWidth(-(ImGui.CalcTextSize(label).X + ImGui.GetStyle().FramePadding.X * 2 + ImGui.GetStyle().ItemSpacing.X));
        if (ImGui.InputTextWithHint("##alias", "Optional, e.g. Moonbeam", ref aliasInput, PairingId.AliasMaxLength))
            aliasEdited = true;
        ImGui.SameLine();
        using (ImRaii.Disabled(!aliasEdited))
        {
            if (ImGui.Button(label))
            {
                sync.SetAlias(aliasInput);
                aliasEdited = false;
            }
        }
        Ui.Tooltip("Friends can pair with your alias instead of the code. Empty it and click Set to clear it.");
    }

    private static void DrawNoPairs(SyncService sync)
    {
        ImGui.Spacing();
        if (!sync.IsConnected)
            ImGui.TextDisabled("Connect to see your pairs.");
        else
            ImGui.TextWrapped("No pairs yet. Copy your pair code from the top of this window and send it to a friend, or add theirs with the person-plus tab.");
    }

    private void DrawPairFolders(SyncService sync)
    {
        var pairs = sync.Pairs;
        var shown = filter.Length == 0 ? pairs : pairs.Where(p =>
            plugin.Configuration.NameOf(p).Contains(filter, StringComparison.OrdinalIgnoreCase)
            || (p.Alias?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
            || PairingId.FormatCode(p.PairCode).Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase)
            || p.PairCode.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        if (pairs.Count == 0)
        {
            DrawNoPairs(sync);
            ImGui.Spacing();
        }
        if (scrollToHighlight && shown.All(p => p.Uid != highlight))
            scrollToHighlight = false;

        var requests = shown.Where(p => p.State != PairState.Paired)
            .OrderBy(p => plugin.Configuration.NameOf(p), StringComparer.OrdinalIgnoreCase).ToList();
        if (requests.Count > 0)
            DrawFolder(sync, $"Requests ({requests.Count})###folder-requests", requests);

        var groups = shown.Where(p => p.State == PairState.Paired).GroupBy(TabOf)
            .ToDictionary(g => g.Key, g => g.OrderBy(p => plugin.Configuration.NameOf(p), StringComparer.OrdinalIgnoreCase).ToList());
        foreach (var tab in Enum.GetValues<PairTab>())
        {
            var members = groups.GetValueOrDefault(tab) ?? [];
            DrawFolder(sync, $"{TabName(tab)} ({members.Count})###folder-{tab}", members);
        }
    }

    private void DrawFolder(SyncService sync, string label, List<PairEntry> members)
    {
        if (scrollToHighlight && members.Any(p => p.Uid == highlight))
            ImGui.SetNextItemOpen(true);
        using var node = ImRaii.TreeNode(label, ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.SpanAvailWidth);
        if (!node.Success)
            return;
        foreach (var pair in members)
            DrawPairRow(sync, pair);
    }

    private PairTab TabOf(PairEntry pair)
    {
        if (plugin.Configuration.PausedPairs.Contains(pair.Uid))
            return PairTab.Paused;
        if (!pair.Online)
            return PairTab.Offline;
        var phase = plugin.PairSync.View(pair.Uid)?.Phase;
        return phase is PairSyncPhase.Applied or PairSyncPhase.Downloading ? PairTab.Visible : PairTab.Online;
    }

    private static string TabName(PairTab tab) => tab switch
    {
        PairTab.Visible => "Visible",
        PairTab.Online => "Online Direct Pairs",
        PairTab.Offline => "Offline Direct Pairs",
        _ => "Paused Direct Pairs",
    };

    private void DrawPairRow(SyncService sync, PairEntry pair)
    {
        using var id = ImRaii.PushId(pair.Uid);
        var config = plugin.Configuration;
        var paused = config.PausedPairs.Contains(pair.Uid);
        var view = pair.State == PairState.Paired && pair.Online ? plugin.PairSync.View(pair.Uid) : null;
        var (icon, color, status) = RowStatus(pair, paused, view);

        if (pair.Uid == highlight)
        {
            var start = ImGui.GetCursorScreenPos();
            var end = new Vector2(start.X + ImGui.GetContentRegionAvail().X, start.Y + ImGui.GetFrameHeight());
            ImGui.GetWindowDrawList().AddRectFilled(start, end, ImGui.GetColorU32(Ui.Accent with { W = 0.25f }), 3f);
            if (scrollToHighlight)
            {
                ImGui.SetScrollHereY(0.5f);
                scrollToHighlight = false;
            }
        }

        ImGui.AlignTextToFramePadding();
        Ui.Icon(icon, color);
        Ui.Tooltip(status);
        ImGui.SameLine();
        ImGui.TextUnformatted(config.NameOf(pair));
        var rightClicked = pair.State == PairState.Paired && ImGui.IsItemClicked(ImGuiMouseButton.Right);
        Ui.Tooltip(Details(pair, status, view, plugin.PairSync.BlockedFor(pair.Uid), config.NicknameOf(pair.Uid)));
        if (view is { Phase: PairSyncPhase.Downloading } && !paused && config.ApplyPairs)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"{view.FilesReady}/{view.FilesTotal}");
        }

        var style = ImGui.GetStyle();
        var count = pair.State == PairState.PendingIncoming || pair.State == PairState.Paired ? 2 : 1;
        var width = Ui.IconButtonWidth(FontAwesomeIcon.EllipsisV) * count + style.ItemSpacing.X * (count - 1);
        ImGui.SameLine(Math.Max(ImGui.GetCursorPosX(), ImGui.GetWindowContentRegionMax().X - width));

        using var disabled = ImRaii.Disabled(!sync.IsConnected);
        switch (pair.State)
        {
            case PairState.PendingIncoming:
                using (ImRaii.PushColor(ImGuiCol.Text, Ui.Green))
                {
                    if (ImGuiComponents.IconButton("##accept", FontAwesomeIcon.Check))
                        sync.Accept(pair);
                }
                Ui.Tooltip("Accept the pair request");
                ImGui.SameLine();
                using (ImRaii.PushColor(ImGuiCol.Text, Ui.Red))
                {
                    if (ImGuiComponents.IconButton("##decline", FontAwesomeIcon.Times))
                        sync.Remove(pair);
                }
                Ui.Tooltip("Decline");
                break;
            case PairState.PendingOutgoing:
                if (ImGuiComponents.IconButton("##cancel", FontAwesomeIcon.Times))
                    sync.Remove(pair);
                Ui.Tooltip("Cancel your request");
                break;
            default:
                using (ImRaii.PushColor(ImGuiCol.Text, Ui.Yellow, paused))
                {
                    if (ImGuiComponents.IconButton("##pause", paused ? FontAwesomeIcon.Play : FontAwesomeIcon.Pause))
                        plugin.SetPaused(pair.Uid, !paused);
                }
                Ui.Tooltip(paused ? "Resume: show this pair's mods again." : "Pause: show this pair unmodded, only on your screen. They still see your mods.");
                ImGui.SameLine();
                if (ImGuiComponents.IconButton("##menu", FontAwesomeIcon.EllipsisV) || rightClicked)
                    ImGui.OpenPopup("##pairmenu");
                DrawPairMenu(sync, pair);
                break;
        }
    }

    private void DrawPairMenu(SyncService sync, PairEntry pair)
    {
        using var popup = ImRaii.Popup("##pairmenu");
        if (!popup.Success)
            return;
        var config = plugin.Configuration;
        if (ImGui.IsWindowAppearing())
            nicknameInput = config.NicknameOf(pair.Uid) ?? string.Empty;
        ImGui.TextDisabled(config.NameOf(pair));
        ImGui.Separator();

        ImGui.TextDisabled("Nickname, only you see it");
        const string setLabel = "Set";
        ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);
        var submitted = ImGui.InputTextWithHint("##nickname", pair.DisplayName, ref nicknameInput, NicknameMaxLength, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        var current = config.NicknameOf(pair.Uid) ?? string.Empty;
        using (ImRaii.Disabled(nicknameInput.Trim() == current))
        {
            if (ImGui.Button(setLabel) || (submitted && nicknameInput.Trim() != current))
                plugin.SetNickname(pair.Uid, nicknameInput);
        }
        Ui.Tooltip("Shown instead of their alias or pair code. Empty it and click Set to go back to their alias.");
        ImGui.Separator();
        if (ImGui.MenuItem("Copy pair code"))
            ImGui.SetClipboardText(PairingId.FormatCode(pair.PairCode));
        if (pair.Alias is not null && ImGui.MenuItem("Copy alias"))
            ImGui.SetClipboardText(pair.Alias);
        ImGui.Separator();

        ImGui.TextDisabled("Load from this pair");
        var pairBlocked = config.BlockedByPair.GetValueOrDefault(pair.Uid);
        foreach (var (filter, name, types) in SyncFilters.All)
        {
            var offForAll = (config.BlockedForAll & filter) != 0;
            var allowed = !offForAll && (pairBlocked & filter) == 0;
            using (ImRaii.Disabled(offForAll))
            {
                if (ImGui.Checkbox($"{name}##load-{filter}", ref allowed))
                    plugin.SetBlocked(pair.Uid, filter, !allowed);
            }
            Ui.Tooltip(offForAll ? $"{name} are off for everyone in Settings > Syncing." : $"{types} files.");
        }
        ImGui.Separator();
        var ctrl = ImGui.GetIO().KeyCtrl;
        using (ImRaii.PushColor(ImGuiCol.Text, Ui.Red, ctrl))
        {
            if (ImGui.MenuItem("Remove pair", string.Empty, false, ctrl))
            {
                sync.Remove(pair);
                plugin.ForgetPair(pair.Uid);
            }
        }
        if (!ctrl)
            Ui.Tooltip("Hold Ctrl to remove this pair.");
    }

    private (FontAwesomeIcon Icon, Vector4 Color, string Status) RowStatus(PairEntry pair, bool paused, PairSyncView? view)
    {
        if (pair.State == PairState.PendingIncoming)
            return (FontAwesomeIcon.Envelope, Ui.Yellow, "Wants to pair with you");
        if (pair.State == PairState.PendingOutgoing)
            return (FontAwesomeIcon.PaperPlane, Ui.Grey, "Request sent, waiting for them");
        if (!pair.Online)
            return (FontAwesomeIcon.User, Ui.Grey, "Offline");
        if (paused || !plugin.Configuration.ApplyPairs)
            return (FontAwesomeIcon.PauseCircle, Ui.Yellow, paused ? "Paused by you" : "Everyone is paused");
        return view?.Phase switch
        {
            PairSyncPhase.Applied when view.FilesFailed > 0 => (FontAwesomeIcon.Eye, Ui.Yellow, $"Visible, {view.FilesFailed} file(s) missing"),
            PairSyncPhase.Applied => (FontAwesomeIcon.Eye, Ui.Green, "Visible, mods applied"),
            PairSyncPhase.Downloading => (FontAwesomeIcon.Download, Ui.Accent, "Nearby, downloading their files"),
            PairSyncPhase.NotNearby => (FontAwesomeIcon.User, Ui.Green, "Online, not nearby"),
            _ => (FontAwesomeIcon.User, Ui.Green, "Online, waiting for their data"),
        };
    }

    private static string Details(PairEntry pair, string status, PairSyncView? view, SyncFilter blocked, string? nickname)
    {
        var lines = new List<string> { status };
        if (pair.State == PairState.Paired && blocked != SyncFilter.None)
            lines.Add($"Not loading: {SyncFilters.Describe(blocked)}");
        if (nickname is not null && pair.Alias is not null)
            lines.Add($"Alias {pair.Alias}");
        if (nickname is not null || pair.Alias is not null)
            lines.Add($"Pair code {PairingId.FormatCode(pair.PairCode)}");
        if (view is not null)
        {
            if (view.AppliedAt is { } at)
                lines.Add($"Applied at {at:HH:mm:ss}, {view.FilesReady} of {view.FilesTotal} files.");
            else if (view.FilesTotal > 0)
                lines.Add($"{view.FilesReady} of {view.FilesTotal} files downloaded.");
            if (view.Phase == PairSyncPhase.NotNearby && view.FilesReady < view.FilesTotal)
                lines.Add("The rest download when their character is near you.");
            if (view.Error is not null)
                lines.Add(view.Error);
        }
        return string.Join("\n", lines);
    }

    private void DrawTransfers(SyncService sync)
    {
        var downloader = plugin.PairSync.Downloader;
        var indent = 30 * ImGuiHelpers.GlobalScale;

        ImGui.AlignTextToFramePadding();
        Ui.Icon(FontAwesomeIcon.Upload, sync.Uploading ? Ui.Accent : Ui.Grey);
        ImGui.SameLine(indent);
        if (sync.Uploading)
            ImGui.TextUnformatted($"Uploading {sync.UploadDone}/{sync.UploadTotal} files");
        else
            ImGui.TextDisabled("No uploads in progress");

        ImGui.AlignTextToFramePadding();
        Ui.Icon(FontAwesomeIcon.Download, downloader.Queued > 0 ? Ui.Accent : Ui.Grey);
        ImGui.SameLine(indent);
        if (downloader.Queued > 0)
            ImGui.TextUnformatted($"Downloading {downloader.Queued} file(s)");
        else
            ImGui.TextDisabled("No downloads in progress");
        var total = $"{Ui.Size(downloader.BytesReceived)} this session";
        ImGui.SameLine(Math.Max(ImGui.GetCursorPosX(), ImGui.GetWindowContentRegionMax().X - ImGui.CalcTextSize(total).X));
        ImGui.TextDisabled(total);

        if (sync.LastMessage.Length > 0)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, sync.LastMessageIsError ? Ui.Red : Ui.Grey))
                ImGui.TextUnformatted(sync.LastMessage);
            Ui.Tooltip(sync.LastMessage);
        }
    }
}
