using Dalamud.Game.Gui.PartyFinder.Types;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace PFReport;

public static class Filter
{
    private static readonly Dictionary<char, string> Replacements = new()
    {
        ['\ue055'] = "1",
        ['\ue056'] = "2",
        ['\ue057'] = "3",
        ['\ue058'] = "4",
        ['\ue059'] = "5",
        ['\ue099'] = "10",
        ['\ue09a'] = "11",
        ['\ue09b'] = "12",
        ['\ue09c'] = "13",
        ['\ue09d'] = "14",
        ['\ue09e'] = "15",
        ['\ue09f'] = "16",
        ['\ue0a0'] = "17",
        ['\ue0a1'] = "18",
        ['\ue0a2'] = "19",
        ['\ue0a3'] = "20",
        ['\ue0a4'] = "21",
        ['\ue0a5'] = "22",
        ['\ue0a6'] = "23",
        ['\ue0a7'] = "24",
        ['\ue0a8'] = "25",
        ['\ue0a9'] = "26",
        ['\ue0aa'] = "27",
        ['\ue0ab'] = "28",
        ['\ue0ac'] = "29",
        ['\ue0ad'] = "30",
        ['\ue0ae'] = "31",
        ['\ue0af'] = "+",
        ['\ue070'] = "?",
        ['\ue022'] = "A",
        ['\ue024'] = "_A",
        ['\ue0b0'] = "E",
    };

    private const char LowestReplacement = '\ue022';

    public static bool TryFindReportableMatch(IPartyFinderListing listing, IReadOnlyList<ReportableRule> rules, out FilterMatchResult match)
    {
        return TryFindReportableMatch(listing.Description.ToString(), rules, out match);
    }

    public static bool TryFindReportableMatch(string descriptionText, IReadOnlyList<ReportableRule> rules, out FilterMatchResult match)
    {
        match = default;

        if (rules.Count == 0 || string.IsNullOrWhiteSpace(descriptionText))
            return false;

        var normalized = Normalise(descriptionText);
        var normalizedCompact = CompactAlphaNumeric(normalized);

        foreach (var rule in rules)
        {
            if (!rule.Enabled)
                continue;

            if (rule.Mode == RuleMatchMode.Regex)
            {
                try
                {
                    var regex = new Regex(rule.Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(75));
                    var m = regex.Match(descriptionText);
                    if (m.Success)
                    {
                        match = new FilterMatchResult(rule.Id, rule.Pattern, m.Value);
                        return true;
                    }
                }
                catch
                {
                    continue;
                }

                continue;
            }

            var patternNormalized = Normalise(rule.Pattern).Trim();
            if (patternNormalized.Length == 0)
                continue;

            if (normalized.Contains(patternNormalized, StringComparison.OrdinalIgnoreCase))
            {
                match = new FilterMatchResult(rule.Id, rule.Pattern, patternNormalized);
                return true;
            }

            var patternCompact = CompactAlphaNumeric(patternNormalized);
            if (patternCompact.Length > 0 && normalizedCompact.Contains(patternCompact, StringComparison.OrdinalIgnoreCase))
            {
                match = new FilterMatchResult(rule.Id, rule.Pattern, patternCompact);
                return true;
            }
        }

        return false;
    }

    public static string Normalise(string input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var builder = new StringBuilder(input.Length);
        foreach (var c in input)
        {
            if (c >= LowestReplacement)
            {
                if (c >= 0xe071 && c <= 0xe08a)
                {
                    builder.Append((char)(c - 0xe030));
                    continue;
                }

                if (c >= 0xe060 && c <= 0xe069)
                {
                    builder.Append((char)(c - 0xe030));
                    continue;
                }

                if (c >= 0xe0b1 && c <= 0xe0b9)
                {
                    builder.Append((char)(c - 0xe080));
                    continue;
                }

                if (c >= 0xe090 && c <= 0xe098)
                {
                    builder.Append((char)(c - 0xe05f));
                    continue;
                }

                if (Replacements.TryGetValue(c, out var rep))
                {
                    builder.Append(rep);
                    continue;
                }
            }

            builder.Append(c);
        }

        return builder.ToString().Normalize(NormalizationForm.FormKD);
    }

    private static string CompactAlphaNumeric(string input)
    {
        if (string.IsNullOrEmpty(input))
            return string.Empty;

        var sb = new StringBuilder(input.Length);
        foreach (var c in input)
        {
            if (char.IsLetterOrDigit(c))
                sb.Append(char.ToLowerInvariant(c));
        }

        return sb.ToString();
    }
}

public readonly record struct FilterMatchResult(Guid RuleId, string RulePattern, string MatchedValue);
