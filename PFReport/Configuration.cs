using Dalamud.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PFReport;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 5;

    // Legacy field kept for migration from old configs.
    public List<string> CustomReportableTerms { get; set; } = new();

    public List<ReportableRule> Rules { get; set; } = new();

    public string ReportTemplate { get; set; } =
        "Name: {name}\nWorld: {world}\n\n\"{description}\"\n";

    public void MigrateAndNormalize()
    {
        var loadedVersion = Version;

        if (Rules.Count == 0)
        {
            foreach (var term in CustomReportableTerms)
            {
                var trimmed = term?.Trim() ?? string.Empty;
                if (trimmed.Length == 0)
                    continue;

                Rules.Add(new ReportableRule
                {
                    Id = Guid.NewGuid(),
                    Pattern = trimmed,
                    Mode = RuleMatchMode.Contains,
                    Enabled = true,
                });
            }
        }

        if (Rules.Count == 0)
        {
            // First-load seed rules.
            Rules.Add(new ReportableRule
            {
                Id = Guid.NewGuid(),
                Pattern = "Grand Dice",
                Mode = RuleMatchMode.Contains,
                Enabled = true,
            });

            Rules.Add(new ReportableRule
            {
                Id = Guid.NewGuid(),
                Pattern = "granddice",
                Mode = RuleMatchMode.Contains,
                Enabled = true,
            });
        }
        else if (loadedVersion <= 4)
        {
            // Backfill for older installs.
            EnsureContainsRule("Grand Dice");
            EnsureContainsRule("granddice");
        }

        var cleaned = new List<ReportableRule>();
        var seenContains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenRegex = new HashSet<string>(StringComparer.Ordinal);

        foreach (var maybeRule in Rules)
        {
            if (maybeRule == null)
                continue;

            var rule = maybeRule;
            var pattern = (rule.Pattern ?? string.Empty).Trim();
            if (pattern.Length == 0)
                continue;

            if (rule.Id == Guid.Empty)
                rule.Id = Guid.NewGuid();

            rule.Pattern = pattern;

            if (rule.Mode == RuleMatchMode.Contains)
            {
                var normalized = Filter.Normalise(pattern).Trim();
                if (normalized.Length == 0 || !seenContains.Add(normalized))
                    continue;
            }
            else
            {
                if (!seenRegex.Add(pattern))
                    continue;
            }

            cleaned.Add(rule);
        }

        Rules = cleaned;
        CustomReportableTerms = Rules
            .Where(r => r.Mode == RuleMatchMode.Contains)
            .Select(r => r.Pattern)
            .ToList();

        Version = 5;
    }

    private void EnsureContainsRule(string pattern)
    {
        var normalizedTarget = Filter.Normalise(pattern).Trim();
        if (normalizedTarget.Length == 0)
            return;

        foreach (var existing in Rules)
        {
            if (existing == null || existing.Mode != RuleMatchMode.Contains)
                continue;

            var normalizedExisting = Filter.Normalise(existing.Pattern ?? string.Empty).Trim();
            if (normalizedExisting.Equals(normalizedTarget, StringComparison.OrdinalIgnoreCase))
                return;
        }

        Rules.Add(new ReportableRule
        {
            Id = Guid.NewGuid(),
            Pattern = pattern,
            Mode = RuleMatchMode.Contains,
            Enabled = true,
        });
    }

    public IReadOnlyList<ReportableRule> GetEnabledRules()
    {
        return Rules
            .Where(r => r.Enabled)
            .Select(r => r.Clone())
            .ToList();
    }

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}

[Serializable]
public class ReportableRule
{
    public Guid Id { get; set; }
    public string Pattern { get; set; } = string.Empty;
    public RuleMatchMode Mode { get; set; } = RuleMatchMode.Contains;
    public bool Enabled { get; set; } = true;

    public ReportableRule Clone()
    {
        return new ReportableRule
        {
            Id = Id,
            Pattern = Pattern,
            Mode = Mode,
            Enabled = Enabled,
        };
    }
}

public enum RuleMatchMode
{
    Contains,
    Regex
}
