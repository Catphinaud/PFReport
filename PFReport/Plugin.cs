using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Command;
using Dalamud.Game.Gui.PartyFinder.Types;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.Automation;
using PFReport.Windows;

namespace PFReport;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/pfreport";
    private const int MaxReportablePfChatAnnouncements = 4;
    private const string DefaultReportTemplate =
        "Name: {name}\nWorld: {world}\n\n\"{description}\"\n";

    public static Plugin Instance { get; private set; } = null!;

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IPartyFinderGui PartyList { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static INotificationManager NotificationManager { get; private set; } = null!;

    private readonly List<FilteredListingEntry> filteredListings = new();
    private readonly HashSet<ulong> seenListingIds = new();
    private readonly HashSet<int> seenListingHashes = new();

    private readonly DalamudLinkPayload openReportLink;
    private readonly DalamudLinkPayload openSupportLink;

    private DisableMode disableMode = DisableMode.None;
    private DateTimeOffset? disabledUntilUtc;
    private IReadOnlyList<ReportableRule> enabledRules = Array.Empty<ReportableRule>();
    private int reportableChatAnnouncementsUsed;

    public Configuration Configuration { get; init; }

    public readonly WindowSystem WindowSystem = new("PFReport");
    private MainWindow MainWindow { get; init; }

    public Plugin()
    {
        Instance = this;
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        EnsureConfigDefaultsAndMigrate();
        ReloadRules();

        MainWindow = new MainWindow(this);
        WindowSystem.AddWindow(MainWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open PF report UI. /pfreport status | /pfreport disable [5m|5 minutes|300|restart|logout] | /pfreport enable"
        });

        PluginInterface.UiBuilder.Draw += OnUiDraw;
        PluginInterface.UiBuilder.OpenConfigUi += OpenStatusUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;

        PartyList.ReceiveListing += PartyListOnReceiveListing;
        ECommonsMain.Init(PluginInterface, this);

        openReportLink = ChatGui.AddChatLinkHandler(0, OnLinkAction);
        openSupportLink = ChatGui.AddChatLinkHandler(1, OnLinkAction);
    }

    public void Dispose()
    {
        ChatGui.RemoveChatLinkHandler();
        PartyList.ReceiveListing -= PartyListOnReceiveListing;
        PluginInterface.UiBuilder.Draw -= OnUiDraw;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenStatusUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;

        WindowSystem.RemoveAllWindows();
        CommandManager.RemoveHandler(CommandName);
        ECommonsMain.Dispose();
    }

    private void EnsureConfigDefaultsAndMigrate()
    {
        Configuration.MigrateAndNormalize();

        if (string.IsNullOrWhiteSpace(Configuration.ReportTemplate))
            Configuration.ReportTemplate = DefaultReportTemplate;

        Configuration.Save();
    }

    private void OnUiDraw()
    {
        MaintainDisableState();
        WindowSystem.Draw();
    }

    private void PartyListOnReceiveListing(IPartyFinderListing listing, IPartyFinderListingEventArgs _)
    {
        try
        {
            if (IsFilteringDisabled)
                return;

            if (!Filter.TryFindReportableMatch(listing, enabledRules, out var match))
                return;

            AddFilteredListing(listing, match);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "PartyListOnReceiveListing failed");
        }
    }

    private void OnCommand(string _, string args)
    {
        MaintainDisableState();
        var trimmed = args.Trim();

        if (trimmed.Length == 0)
        {
            MainWindow.Toggle();
            return;
        }

        var split = trimmed.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var sub = split[0].ToLowerInvariant();

        if (sub is "status" or "config")
        {
            OpenStatusUi();
            return;
        }

        if (sub == "enable")
        {
            EnableReporting();
            ChatGui.Print("[PFReport] Reporting enabled.");
            return;
        }

        if (sub != "disable")
        {
            ChatGui.Print("[PFReport] Unknown command. Use /pfreport, /pfreport status, /pfreport enable, or /pfreport disable [duration|restart|logout].");
            return;
        }

        var modeArg = split.Length > 1 ? split[1].Trim() : "restart";

        if (modeArg.Equals("restart", StringComparison.OrdinalIgnoreCase))
        {
            DisableUntilRestart();
            ChatGui.Print("[PFReport] Reporting disabled until plugin restart.");
            return;
        }

        if (modeArg.Equals("logout", StringComparison.OrdinalIgnoreCase))
        {
            DisableUntilLogout();
            ChatGui.Print("[PFReport] Reporting disabled until logout.");
            return;
        }

        if (TryParseDisableDuration(modeArg, out var duration) && duration > TimeSpan.Zero)
        {
            DisableFor(duration);
            ChatGui.Print($"[PFReport] Reporting disabled for {FormatDuration(duration)}.");
            return;
        }

        ChatGui.Print("[PFReport] Usage: /pfreport | /pfreport status | /pfreport disable [5m|5 minutes|300|restart|logout] | /pfreport enable");
    }

    public static bool TryParseDisableDuration(string input, out TimeSpan duration)
    {
        duration = default;
        var value = input.Trim().ToLowerInvariant();
        if (value.Length == 0)
            return false;

        if (int.TryParse(value, out var secondsOnly))
        {
            duration = TimeSpan.FromSeconds(secondsOnly);
            return true;
        }

        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 2 && int.TryParse(parts[0], out var spacedAmount))
        {
            if (TryParseUnit(parts[1], out var multiplierSeconds))
            {
                duration = TimeSpan.FromSeconds(spacedAmount * multiplierSeconds);
                return true;
            }

            return false;
        }

        var numberChars = value.TakeWhile(char.IsDigit).ToArray();
        var unitChars = value.Skip(numberChars.Length).ToArray();
        if (numberChars.Length == 0 || unitChars.Length == 0)
            return false;

        if (!int.TryParse(new string(numberChars), out var amount))
            return false;

        var unit = new string(unitChars);
        if (!TryParseUnit(unit, out var unitMultiplierSeconds))
            return false;

        duration = TimeSpan.FromSeconds(amount * unitMultiplierSeconds);
        return true;
    }

    private static bool TryParseUnit(string unit, out int multiplierSeconds)
    {
        multiplierSeconds = unit switch
        {
            "s" or "sec" or "secs" or "second" or "seconds" => 1,
            "m" or "min" or "mins" or "minute" or "minutes" => 60,
            "h" or "hr" or "hrs" or "hour" or "hours" => 3600,
            "d" or "day" or "days" => 86400,
            _ => 0
        };

        return multiplierSeconds > 0;
    }

    private static string FormatDuration(TimeSpan value)
    {
        if (value.TotalDays >= 1)
            return $"{(int)value.TotalDays}d";
        if (value.TotalHours >= 1)
            return $"{(int)value.TotalHours}h";
        if (value.TotalMinutes >= 1)
            return $"{(int)value.TotalMinutes}m";
        return $"{(int)value.TotalSeconds}s";
    }

    private void MaintainDisableState()
    {
        if (disableMode == DisableMode.UntilLogout && !ECommons.GameHelpers.Player.Available)
        {
            EnableReporting();
            ChatGui.Print("[PFReport] Reporting re-enabled after logout.");
            return;
        }

        if (disableMode != DisableMode.UntilTime || disabledUntilUtc == null)
            return;

        if (DateTimeOffset.UtcNow < disabledUntilUtc.Value)
            return;

        EnableReporting();
        ChatGui.Print("[PFReport] Reporting re-enabled.");
    }

    private void OnLinkAction(uint commandId, SeString _)
    {
        if (commandId == openReportLink.CommandId)
        {
            MainWindow.OpenReportsTab();
            return;
        }

        if (commandId == openSupportLink.CommandId)
        {
            try
            {
                Chat.ExecuteCommand("/supportdesk");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to execute support command");
                ChatGui.PrintError("Failed to open support. Check logs for details.");
            }
        }
    }

    public void ReloadRules()
    {
        Configuration.MigrateAndNormalize();
        enabledRules = Configuration.GetEnabledRules();
        Configuration.Save();
    }

    public IReadOnlyList<ReportableRule> GetAllRules()
    {
        return Configuration.Rules.Select(r => r.Clone()).ToList();
    }

    public void UpsertRule(ReportableRule rule)
    {
        var sanitized = rule.Clone();
        sanitized.Pattern = (sanitized.Pattern ?? string.Empty).Trim();

        if (sanitized.Pattern.Length == 0)
            return;

        if (sanitized.Id == Guid.Empty)
            sanitized.Id = Guid.NewGuid();

        var index = Configuration.Rules.FindIndex(r => r.Id == sanitized.Id);
        if (index >= 0)
            Configuration.Rules[index] = sanitized;
        else
            Configuration.Rules.Add(sanitized);

        Configuration.MigrateAndNormalize();
        ReloadRules();
    }

    public void DeleteRule(Guid ruleId)
    {
        Configuration.Rules.RemoveAll(r => r.Id == ruleId);
        Configuration.MigrateAndNormalize();
        ReloadRules();
    }

    public void SetRuleEnabled(Guid ruleId, bool enabled)
    {
        var rule = Configuration.Rules.FirstOrDefault(r => r.Id == ruleId);
        if (rule == null)
            return;

        rule.Enabled = enabled;
        Configuration.MigrateAndNormalize();
        ReloadRules();
    }

    public void ToggleMainUi()
    {
        MainWindow.Toggle();
    }

    public void OpenStatusUi()
    {
        MainWindow.OpenStatusTab();
    }

    public void SaveReportTemplate(string template)
    {
        Configuration.ReportTemplate = string.IsNullOrWhiteSpace(template)
            ? DefaultReportTemplate
            : template;

        Configuration.Save();
    }

    public string ApplyTemplate(FilteredListingEntry entry)
    {
        return Configuration.ReportTemplate
            .Replace("{name}", entry.Name, StringComparison.OrdinalIgnoreCase)
            .Replace("{world}", entry.World, StringComparison.OrdinalIgnoreCase)
            .Replace("{rule}", entry.MatchedRulePattern, StringComparison.OrdinalIgnoreCase)
            .Replace("{matched}", entry.MatchedValue, StringComparison.OrdinalIgnoreCase)
            .Replace("{description}", entry.Description, StringComparison.OrdinalIgnoreCase)
            .Replace("{time}", entry.SeenAt.ToString("yyyy-MM-dd HH:mm:ss"), StringComparison.OrdinalIgnoreCase);
    }

    public void AddFilteredListing(IPartyFinderListing listing, FilterMatchResult match)
    {
        if (seenListingIds.Contains(listing.Id))
            return;

        var name = listing.Name.ToString();
        var world = listing.HomeWorld.ValueNullable?.Name.ToString() ?? "?";
        var description = listing.Description.ToString();
        var hash = HashCode.Combine(name, world, description);
        if (seenListingHashes.Contains(hash))
            return;

        seenListingIds.Add(listing.Id);
        seenListingHashes.Add(hash);

        var entry = new FilteredListingEntry(
            listing.Id,
            name,
            world,
            description,
            match.RuleId,
            match.RulePattern,
            match.MatchedValue,
            DateTimeOffset.Now);

        filteredListings.Add(entry);
        const int maxSize = 500;
        if (filteredListings.Count > maxSize)
            filteredListings.RemoveRange(0, filteredListings.Count - maxSize);

        if (reportableChatAnnouncementsUsed >= MaxReportablePfChatAnnouncements)
            return;

        reportableChatAnnouncementsUsed++;

        var sb = new SeStringBuilder();
        sb.AddText("Reportable PF: ");
        sb.AddUiForeground(name, 43);
        sb.AddText(" (");
        sb.AddUiForeground(world, 61);
        sb.AddText(") [");
        sb.AddUiForeground(match.RulePattern, 31);
        sb.AddText("] (");

        sb.AddUiForeground(61);
        sb.Add(openReportLink);
        sb.AddText("Open report UI");
        sb.Add(RawPayload.LinkTerminator);
        sb.AddUiForegroundOff();

        sb.AddText(" | ");
        sb.AddPartyFinderLink(listing.Id);
        sb.AddUiForeground(62);
        sb.AddText("Open in-game");
        sb.AddUiForegroundOff();
        sb.Add(RawPayload.LinkTerminator);

        sb.AddText(" | ");
        sb.AddUiForeground(63);
        sb.Add(openSupportLink);
        sb.AddText("Open Support");
        sb.Add(RawPayload.LinkTerminator);
        sb.AddUiForegroundOff();
        sb.AddText(")");

        ChatGui.Print(sb.Build());
    }

    public void ClearFilteredListings()
    {
        filteredListings.Clear();
        seenListingIds.Clear();
        seenListingHashes.Clear();
        reportableChatAnnouncementsUsed = 0;
    }

    public void EnableReporting()
    {
        disableMode = DisableMode.None;
        disabledUntilUtc = null;
    }

    public void DisableUntilRestart()
    {
        disableMode = DisableMode.UntilRestart;
        disabledUntilUtc = null;
    }

    public void DisableUntilLogout()
    {
        disableMode = DisableMode.UntilLogout;
        disabledUntilUtc = null;
    }

    public void DisableFor(TimeSpan duration)
    {
        disableMode = DisableMode.UntilTime;
        disabledUntilUtc = DateTimeOffset.UtcNow.Add(duration);
    }

    public IReadOnlyList<FilteredListingEntry> FilteredListings => filteredListings;
    public int ReportableChatAnnouncementsUsed => reportableChatAnnouncementsUsed;

    public bool IsFilteringDisabled
    {
        get
        {
            MaintainDisableState();
            return disableMode != DisableMode.None;
        }
    }

    public string DisableStatusText
    {
        get
        {
            MaintainDisableState();
            return disableMode switch
            {
                DisableMode.None => "Enabled",
                DisableMode.UntilRestart => "Disabled until restart",
                DisableMode.UntilLogout => "Disabled until logout",
                DisableMode.UntilTime when disabledUntilUtc.HasValue => $"Disabled until {disabledUntilUtc.Value:yyyy-MM-dd HH:mm:ss} UTC",
                _ => "Disabled"
            };
        }
    }
}

public enum DisableMode
{
    None,
    UntilTime,
    UntilRestart,
    UntilLogout
}

public sealed record FilteredListingEntry(
    ulong ListingId,
    string Name,
    string World,
    string Description,
    Guid MatchedRuleId,
    string MatchedRulePattern,
    string MatchedValue,
    DateTimeOffset SeenAt);
