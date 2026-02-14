using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Interface.Windowing;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace PFReport.Windows;

public class MainWindow : Window
{
    private readonly Plugin plugin;

    private string templateEditor;
    private string templateTestDescription = string.Empty;
    private string disableDurationInput = "5m";
    private int disableUiMode;

    private bool openStatusOnNextDraw;
    private bool openReportsOnNextDraw;

    private Guid? selectedRuleId;
    private Guid? editingRuleId;
    private string ruleEditorPattern = string.Empty;
    private RuleMatchMode ruleEditorMode = RuleMatchMode.Contains;
    private bool ruleEditorEnabled = true;
    private string rulesEditorState = "Select a rule or click New Rule.";

    private string rulesTestInput = string.Empty;
    private bool rulesTestSelectedOnly;
    private string rulesTestResult = "Run a match test.";

    private string reportsSearch = string.Empty;
    private ulong? selectedReportId;

    public MainWindow(Plugin plugin)
        : base("PFReport###PFReport_Main", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(1020, 700),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };

        this.plugin = plugin;
        templateEditor = plugin.Configuration.ReportTemplate;
    }

    public void OpenStatusTab()
    {
        openStatusOnNextDraw = true;
        openReportsOnNextDraw = false;
        IsOpen = true;
        templateEditor = plugin.Configuration.ReportTemplate;
    }

    public void OpenReportsTab()
    {
        openReportsOnNextDraw = true;
        openStatusOnNextDraw = false;
        IsOpen = true;
    }

    public override void Draw()
    {
        if (!ImGui.BeginTabBar("pfreport_tabs"))
            return;

        var statusFlags = openStatusOnNextDraw ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
        if (ImGui.BeginTabItem("Status", statusFlags))
        {
            DrawStatusTab();
            openStatusOnNextDraw = false;
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Rules"))
        {
            DrawRulesTab();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Template"))
        {
            DrawTemplateTab();
            ImGui.EndTabItem();
        }

        var reportsFlags = openReportsOnNextDraw ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
        if (ImGui.BeginTabItem("Reports", reportsFlags))
        {
            DrawReportsTab();
            openReportsOnNextDraw = false;
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    private void DrawStatusTab()
    {
        var statusColor = plugin.IsFilteringDisabled
            ? new Vector4(1f, 0.52f, 0.4f, 1f)
            : new Vector4(0.5f, 1f, 0.5f, 1f);

        ImGui.TextColored(statusColor, $"Current Status: {plugin.DisableStatusText}");
        ImGui.TextDisabled($"Chat announcements used: {plugin.ReportableChatAnnouncementsUsed}/4");

        ImGui.Spacing();
        if (plugin.IsFilteringDisabled && ImGui.Button("Enable Reporting Now", new Vector2(220, 0)))
            plugin.EnableReporting();

        ImGui.Separator();
        ImGui.TextDisabled("Disable Configuration");
        ImGui.SetNextItemWidth(220);
        ImGui.Combo("Mode", ref disableUiMode, "Duration\0Until Restart\0Until Logout\0");

        if (disableUiMode == 0)
        {
            ImGui.SetNextItemWidth(220);
            ImGui.InputText("Duration", ref disableDurationInput, 64);
            ImGui.TextDisabled("Examples: 5m, 5 minutes, 300");
        }

        if (ImGui.Button("Apply Disable"))
        {
            if (disableUiMode == 1)
            {
                plugin.DisableUntilRestart();
            }
            else if (disableUiMode == 2)
            {
                plugin.DisableUntilLogout();
            }
            else if (Plugin.TryParseDisableDuration(disableDurationInput, out var duration) && duration > TimeSpan.Zero)
            {
                plugin.DisableFor(duration);
            }
            else
            {
                Plugin.ChatGui.Print("[PFReport] Invalid duration. Examples: 5m, 5 minutes, 300");
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        if (ImGui.Button("Clear Captured Listings + Reset Chat Counter", new Vector2(320, 0)))
            plugin.ClearFilteredListings();
    }

    private void DrawRulesTab()
    {
        var rules = plugin.GetAllRules();
        var selectedRule = rules.FirstOrDefault(r => r.Id == selectedRuleId);

        ImGui.TextUnformatted("Rules");
        ImGui.TextDisabled("Pattern-only rules. Contains mode is smart-normalized, Regex mode is advanced.");
        ImGui.Spacing();
        ImGui.TextDisabled(rulesEditorState);

        ImGui.Separator();

        var listWidth = ImGui.GetContentRegionAvail().X * 0.44f;

        ImGui.BeginChild("rules_list", new Vector2(listWidth, -1), true);
        ImGui.TextDisabled("Rule List");
        var newRuleLabelWidth = ImGui.CalcTextSize("New Rule").X + 18f;
        var newRuleButtonX = ImGui.GetWindowWidth() - newRuleLabelWidth - 14f;
        if (newRuleButtonX > ImGui.GetCursorPosX())
            ImGui.SameLine(newRuleButtonX);
        if (ImGui.Button("New Rule"))
            BeginNewRule();
        ImGui.Separator();

        foreach (var rule in rules)
        {
            ImGui.PushID(rule.Id.ToString());

            var enabled = rule.Enabled;
            if (ImGui.Checkbox("##enabled", ref enabled))
                plugin.SetRuleEnabled(rule.Id, enabled);

            ImGui.SameLine();
            var isSelected = selectedRuleId == rule.Id;
            if (ImGui.Selectable($"{rule.Pattern} ({rule.Mode})", isSelected))
            {
                selectedRuleId = rule.Id;
                if (editingRuleId == null)
                    LoadRuleIntoEditor(rule, "Selected rule loaded. Click Save to update or New Rule for a fresh one.");
            }

            ImGui.PopID();
        }

        ImGui.EndChild();

        ImGui.SameLine();

        ImGui.BeginChild("rules_editor", new Vector2(0, -1), true);
        ImGui.TextDisabled(editingRuleId.HasValue ? "Edit Rule" : "Add Rule");

        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("Pattern", ref ruleEditorPattern, 1024);

        var modeInt = (int)ruleEditorMode;
        ImGui.SetNextItemWidth(180);
        if (ImGui.Combo("Mode", ref modeInt, "Contains\0Regex\0"))
            ruleEditorMode = (RuleMatchMode)modeInt;

        ImGui.Checkbox("Enabled##editor_enabled", ref ruleEditorEnabled);
        ImGui.Spacing();

        if (ImGui.Button("Save", new Vector2(120, 0)))
            SaveRuleEditor();

        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(120, 0)))
            CancelRuleEdit(rules);

        ImGui.SameLine();
        if (ImGui.Button("Delete", new Vector2(120, 0)) && editingRuleId.HasValue)
        {
            plugin.DeleteRule(editingRuleId.Value);
            selectedRuleId = null;
            editingRuleId = null;
            BeginNewRule("Deleted selected rule.");
        }

        ImGui.Separator();
        ImGui.TextDisabled("Rule Match Tester");

        ImGui.InputTextMultiline("##rules_test_input", ref rulesTestInput, 32768, new Vector2(-1, 100));
        ImGui.Checkbox("Test selected rule only", ref rulesTestSelectedOnly);

        if (ImGui.Button("Run Match Test"))
        {
            IReadOnlyList<ReportableRule> testRules;

            if (rulesTestSelectedOnly)
            {
                var currentRule = BuildEditorRuleForTesting();
                testRules = currentRule == null ? Array.Empty<ReportableRule>() : new List<ReportableRule> { currentRule };
            }
            else
            {
                testRules = rules.Where(r => r.Enabled).ToList();
            }

            if (Filter.TryFindReportableMatch(rulesTestInput, testRules, out var match))
                rulesTestResult = $"Matched: {match.RulePattern} | Value: {match.MatchedValue}";
            else
                rulesTestResult = "No match.";
        }

        var testColor = rulesTestResult.StartsWith("Matched", StringComparison.OrdinalIgnoreCase)
            ? new Vector4(0.50f, 1.00f, 0.55f, 1f)
            : new Vector4(1.00f, 0.55f, 0.45f, 1f);
        ImGui.TextColored(testColor, rulesTestResult);

        ImGui.EndChild();
    }

    private void DrawTemplateTab()
    {
        ImGui.TextUnformatted("Report Template");
        ImGui.TextDisabled("Use placeholders: {name}, {world}, {description}, optional: {rule}, {matched}, {time}");

        ImGui.InputTextMultiline("##cfg_template", ref templateEditor, 32768, new Vector2(-1, 170));

        if (ImGui.Button("Save Template"))
        {
            plugin.SaveReportTemplate(templateEditor);
            templateEditor = plugin.Configuration.ReportTemplate;
            Plugin.ChatGui.Print("[PFReport] Saved report template.");
        }

        ImGui.SameLine();
        if (ImGui.Button("Reload Template"))
            templateEditor = plugin.Configuration.ReportTemplate;

        ImGui.Spacing();
        ImGui.TextUnformatted("Template Preview");
        ImGui.BeginChild("template_preview", new Vector2(-1, 120), true);
        DrawTemplateWithHighlight(templateEditor);
        ImGui.EndChild();

        ImGui.Separator();
        ImGui.TextUnformatted("Template Test Render");
        ImGui.TextDisabled("Quickly see generated output with current template.");
        ImGui.InputTextMultiline("##cfg_test_desc", ref templateTestDescription, 32768, new Vector2(-1, 90));

        if (Filter.TryFindReportableMatch(templateTestDescription, plugin.GetAllRules().Where(r => r.Enabled).ToList(), out var match))
        {
            var preview = plugin.ApplyTemplate(new FilteredListingEntry(
                0,
                "Example Player",
                "Example World",
                templateTestDescription,
                match.RuleId,
                match.RulePattern,
                match.MatchedValue,
                DateTimeOffset.Now));

            ImGui.BeginChild("template_match_preview", new Vector2(-1, 100), true);
            ImGui.TextUnformatted(preview);
            ImGui.EndChild();
        }
        else
        {
            ImGui.TextDisabled("No active rule matched this test input.");
        }
    }

    private void DrawReportsTab()
    {
        var all = plugin.FilteredListings.OrderByDescending(x => x.SeenAt).ToList();
        var visible = all.Where(x => ReportMatchesSearch(x, reportsSearch)).ToList();

        DrawReportSummaryRow(all.Count);
        ImGui.Separator();

        var leftWidth = ImGui.GetContentRegionAvail().X * 0.46f;

        ImGui.BeginChild("reports_left", new Vector2(leftWidth, -1), true);
        ImGui.TextDisabled("Captured Reports");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##report_search", "Search name, world, rule, description", ref reportsSearch, 256);

        ImGui.Separator();

        if (visible.Count == 0)
        {
            ImGui.TextDisabled("No reports matched your filter.");
            ImGui.EndChild();
            ImGui.SameLine();
            ImGui.BeginChild("reports_right", new Vector2(0, -1), true);
            ImGui.TextDisabled("Select a report to preview and copy.");
            ImGui.EndChild();
            return;
        }

        if (!selectedReportId.HasValue || visible.All(v => v.ListingId != selectedReportId.Value))
            selectedReportId = visible[0].ListingId;

        if (ImGui.BeginTable("reports_list", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY, new Vector2(-1, -1)))
        {
            ImGui.TableSetupColumn("Time", ImGuiTableColumnFlags.WidthFixed, 110f);
            ImGui.TableSetupColumn("Player", ImGuiTableColumnFlags.WidthStretch, 0.34f);
            ImGui.TableSetupColumn("World", ImGuiTableColumnFlags.WidthFixed, 130f);
            ImGui.TableSetupColumn("Rule", ImGuiTableColumnFlags.WidthStretch, 0.28f);
            ImGui.TableHeadersRow();

            foreach (var item in visible)
            {
                ImGui.TableNextRow();
                var selected = selectedReportId == item.ListingId;
                ImGui.TableSetColumnIndex(0);
                if (ImGui.Selectable($"##report_row_{item.ListingId}", selected, ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowItemOverlap))
                    selectedReportId = item.ListingId;
                if (ImGui.IsItemHovered())
                    ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

                ImGui.TableSetColumnIndex(1);
                ImGui.TextUnformatted(item.Name);

                ImGui.TableSetColumnIndex(2);
                ImGui.TextUnformatted(item.World);

                ImGui.TableSetColumnIndex(3);
                ImGui.TextUnformatted(item.MatchedRulePattern);

                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted(item.SeenAt.ToString("HH:mm:ss"));
            }

            ImGui.EndTable();
        }

        ImGui.EndChild();

        ImGui.SameLine();

        ImGui.BeginChild("reports_right", new Vector2(0, -1), true);
        var selectedItem = visible.FirstOrDefault(x => x.ListingId == selectedReportId.Value);
        if (selectedItem == null)
        {
            ImGui.TextDisabled("Select a report to preview and copy.");
            ImGui.EndChild();
            return;
        }

        ImGui.TextUnformatted($"{selectedItem.Name} [{selectedItem.World}]");
        ImGui.TextDisabled($"Seen: {selectedItem.SeenAt:yyyy-MM-dd HH:mm:ss}");
        ImGui.TextColored(new Vector4(1f, 0.78f, 0.42f, 1f), $"Matched rule: {selectedItem.MatchedRulePattern}");

        if (ImGui.Button("Copy Report Text", new Vector2(160, 0)))
        {
            ImGui.SetClipboardText(plugin.ApplyTemplate(selectedItem));
            Plugin.NotificationManager?.AddNotification(new Notification
            {
                Content = "Copied report",
                Type = NotificationType.Success
            });
        }

        ImGui.SameLine();
        if (ImGui.Button("Copy Description", new Vector2(160, 0)))
        {
            ImGui.SetClipboardText(selectedItem.Description);
            Plugin.NotificationManager?.AddNotification(new Notification
            {
                Content = "Copied description",
                Type = NotificationType.Success
            });
        }

        ImGui.Separator();
        ImGui.TextDisabled("Description");
        ImGui.BeginChild("report_description", new Vector2(-1, 120), true);
        ImGui.TextWrapped(selectedItem.Description);
        ImGui.EndChild();

        ImGui.Spacing();
        ImGui.TextDisabled("Generated Report Preview");
        var previewText = plugin.ApplyTemplate(selectedItem);
        ImGui.BeginChild("report_preview", new Vector2(-1, -1), true);
        ImGui.TextWrapped(previewText);
        ImGui.EndChild();

        ImGui.EndChild();
    }

    private void DrawReportSummaryRow(int total)
    {
        var colA = new Vector4(0.60f, 0.86f, 1.00f, 1f);
        var colB = new Vector4(0.70f, 0.90f, 0.62f, 1f);
        var colC = new Vector4(1.00f, 0.73f, 0.52f, 1f);

        DrawMetricCard("Captured", total.ToString(), colA, 220f);
        ImGui.SameLine();
        DrawMetricCard("Chat Notices", $"{plugin.ReportableChatAnnouncementsUsed}/4", colB, 220f);
        ImGui.SameLine();
        DrawMetricCard("Status", plugin.DisableStatusText, colC, 420f);
    }

    private static void DrawMetricCard(string label, string value, Vector4 accent, float width)
    {
        ImGui.BeginChild($"metric_{label}", new Vector2(width, 56), true);
        ImGui.TextDisabled(label);
        ImGui.TextColored(accent, value);
        ImGui.EndChild();
    }

    private static bool ReportMatchesSearch(FilteredListingEntry entry, string search)
    {
        if (string.IsNullOrWhiteSpace(search))
            return true;

        var s = search.Trim();
        return entry.Name.Contains(s, StringComparison.OrdinalIgnoreCase)
            || entry.World.Contains(s, StringComparison.OrdinalIgnoreCase)
            || entry.MatchedRulePattern.Contains(s, StringComparison.OrdinalIgnoreCase)
            || entry.Description.Contains(s, StringComparison.OrdinalIgnoreCase);
    }

    private void BeginNewRule(string? status = null)
    {
        editingRuleId = null;
        ruleEditorPattern = string.Empty;
        ruleEditorMode = RuleMatchMode.Contains;
        ruleEditorEnabled = true;
        rulesEditorState = status ?? "Adding new rule.";
    }

    private void LoadRuleIntoEditor(ReportableRule rule, string status)
    {
        editingRuleId = rule.Id;
        ruleEditorPattern = rule.Pattern;
        ruleEditorMode = rule.Mode;
        ruleEditorEnabled = rule.Enabled;
        rulesEditorState = status;
    }

    private void SaveRuleEditor()
    {
        var trimmedPattern = ruleEditorPattern?.Trim() ?? string.Empty;
        if (trimmedPattern.Length == 0)
        {
            rulesEditorState = "Cannot save: pattern is empty.";
            Plugin.ChatGui.Print("[PFReport] Pattern cannot be empty.");
            return;
        }

        var id = editingRuleId ?? Guid.NewGuid();

        plugin.UpsertRule(new ReportableRule
        {
            Id = id,
            Pattern = trimmedPattern,
            Mode = ruleEditorMode,
            Enabled = ruleEditorEnabled,
        });

        selectedRuleId = id;
        editingRuleId = id;
        rulesEditorState = "Saved.";
    }

    private void CancelRuleEdit(IReadOnlyList<ReportableRule> rules)
    {
        if (selectedRuleId.HasValue)
        {
            var selectedRule = rules.FirstOrDefault(r => r.Id == selectedRuleId.Value);
            if (selectedRule != null)
            {
                LoadRuleIntoEditor(selectedRule, "Edit canceled. Reverted to selected rule.");
                return;
            }
        }

        BeginNewRule("Edit canceled.");
    }

    private ReportableRule? BuildEditorRuleForTesting()
    {
        var trimmedPattern = ruleEditorPattern?.Trim() ?? string.Empty;
        if (trimmedPattern.Length == 0)
            return null;

        return new ReportableRule
        {
            Id = editingRuleId ?? Guid.NewGuid(),
            Pattern = trimmedPattern,
            Mode = ruleEditorMode,
            Enabled = true,
        };
    }

    private static void DrawTemplateWithHighlight(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        var normal = new Vector4(0.83f, 0.84f, 0.86f, 1f);
        var placeholder = new Vector4(0.95f, 0.78f, 0.39f, 1f);
        var lines = text.Replace("\r\n", "\n").Split('\n');
        foreach (var line in lines)
            DrawLineWithHighlight(line, normal, placeholder);
    }

    private static void DrawLineWithHighlight(string line, Vector4 normal, Vector4 placeholder)
    {
        var i = 0;
        var wroteAny = false;

        while (i < line.Length)
        {
            var open = line.IndexOf('{', i);
            if (open < 0)
            {
                WriteSegment(line[i..], normal, wroteAny);
                wroteAny = true;
                break;
            }

            if (open > i)
            {
                WriteSegment(line[i..open], normal, wroteAny);
                wroteAny = true;
            }

            var close = line.IndexOf('}', open + 1);
            if (close < 0)
            {
                WriteSegment(line[open..], normal, wroteAny);
                wroteAny = true;
                break;
            }

            WriteSegment(line.Substring(open, close - open + 1), placeholder, wroteAny);
            wroteAny = true;
            i = close + 1;
        }

        if (!wroteAny)
            ImGui.NewLine();
    }

    private static void WriteSegment(string text, Vector4 color, bool inline)
    {
        if (inline)
            ImGui.SameLine(0f, 0f);

        ImGui.TextColored(color, text);
    }
}
