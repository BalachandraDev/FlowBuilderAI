using System.Net;
using System.Text.RegularExpressions;
using FlowCapture.Shared.Content;

namespace FlowCapture.Application.Analysis;

/// <summary>
/// A cheap check that an analysis did not squash a list into one step. Notes very often list the pages or options of a screen
/// ("Treatment Method: how waste is destroyed. Waste Category: ..."), and a model that is short of time or strength sometimes writes
/// "configures the six categories" as a single step, which leaves the developers with nothing to work from. This finds such lists in the
/// notes and, when most of the listed items did not become a step or screen of their own, says so. The caller then asks the AI once more,
/// with this message; the AI decides whether the items really are separate pages or only fields of one screen.
/// </summary>
public static partial class AnalysisCompleteness
{
    private static readonly HashSet<string> NotAnItem = new(StringComparer.OrdinalIgnoreCase)
    {
        "note", "notes", "priority", "frequency", "category", "trigger", "pre-conditions", "preconditions", "post-conditions", "postconditions",
        "title", "module", "department", "date", "actor", "actors", "role", "roles", "scope", "purpose", "requirement", "requirements", "id",
        "status", "example", "summary", "description", "client", "interviewee", "language", "time", "question", "answer",
    };

    // "Name: something", optionally after a bullet or a number. The name is one to five words.
    [GeneratedRegex(@"^[ \t]*(?:[-*•·][ \t]*|\d{1,2}[.)][ \t]*)?(?<name>[A-Z][A-Za-z0-9&/()'-]*(?:[ ][A-Za-z0-9&/()'-]+){0,4}?)[ \t]*[:：][ \t]*\S", RegexOptions.Multiline)]
    private static partial Regex ItemLine();

    [GeneratedRegex(@"<entry\b[^>]*>(?<body>.*?)</entry>", RegexOptions.Singleline)]
    private static partial Regex Entry();

    /// <summary>The names of the listed items in the notes of an analysis request, in order. Empty unless there are at least three.</summary>
    public static IReadOnlyList<string> ListedItems(string userMessage)
    {
        var names = new List<string>();
        foreach (Match entry in Entry().Matches(userMessage))
        {
            var body = WebUtility.HtmlDecode(entry.Groups["body"].Value);
            foreach (Match line in ItemLine().Matches(body))
            {
                var name = line.Groups["name"].Value.Trim();
                if (name.Length is < 2 or > 40 || NotAnItem.Contains(name) || names.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                names.Add(name);
            }
        }
        return names.Count >= 3 ? names : [];
    }

    /// <summary>The problems to tell the AI about: empty when the notes list nothing, or when the listed items are covered.</summary>
    public static IReadOnlyList<string> Check(string userMessage, FlowContent content)
    {
        var items = ListedItems(userMessage);
        if (items.Count < 3) return [];

        static string Norm(string s) => Regex.Replace(s ?? "", @"[^a-z0-9]+", " ", RegexOptions.IgnoreCase).Trim().ToLowerInvariant();
        var places = content.Steps.Select(s => Norm(s.Screen)).Concat(content.Screens.Select(s => Norm(s.MenuName))).Where(p => p.Length > 0).ToList();
        var missing = items.Where(i => !places.Any(p => p.Contains(Norm(i)))).ToList();

        // fewer steps than listed items, and most items have no step or screen named after them
        if (content.Steps.Count >= items.Count || missing.Count < 3 || missing.Count * 2 < items.Count) return [];

        return
        [
            $"The notes contain a list of {items.Count} items: {string.Join(", ", items)}. " +
            $"Only {content.Steps.Count} step{(content.Steps.Count == 1 ? "" : "s")} {(content.Steps.Count == 1 ? "was" : "were")} written, and these have no step or screen of their own: {string.Join(", ", missing)}. " +
            "If they are separate pages, screens, masters, tabs, menus or options, EACH must be its own step (`screen` = the item's name exactly as the notes write it) " +
            "with its own entry in `screens` listing the fields the notes name for it, reached from the step that offers them, which lists the first step of every one of them in `next`. " +
            "Do not merge them into one step and do not summarise them as \"the categories\". " +
            "If they are only fields of one screen, keep them as that screen's fields and leave the steps as they are.",
        ];
    }
}
