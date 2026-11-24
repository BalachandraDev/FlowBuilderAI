using System.Text.RegularExpressions;
using FlowCapture.Application.Abstractions;

namespace FlowCapture.Application.Services;

/// <summary>
/// Best-effort removal of patient identifiers before text is stored for analysis or sent to the AI. Placeholders
/// are [EMAIL], [PHONE], [UHID], [IP_NUMBER], [ID_NUMBER], [PATIENT_NAME], [NAME]. It is deliberately conservative
/// about menu and screen names (it does not mask arbitrary capitalised words), so the user must still review the
/// masked text. It is not a guarantee that no identifier survives.
/// </summary>
public static class PhiMaskerCore
{
    private const string IdValue = @"(?=[A-Z0-9/-]*\d)[A-Z0-9][A-Z0-9/-]{2,}";   // must contain a digit

    private static readonly (Regex Pattern, string Replacement)[] Rules =
    [
        (new Regex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", RegexOptions.Compiled), "[EMAIL]"),
        (new Regex(@"(?<![\w])(?:\+?91[\s-]?)?[6-9]\d{4}[\s-]?\d{5}(?![\w])", RegexOptions.Compiled), "[PHONE]"),
        (new Regex($@"\b(?:UHID|MRN|MR\s*No\.?|MRD\s*No\.?|hospital\s*(?:id|number))\s*[:#-]?\s*{IdValue}", RegexOptions.Compiled | RegexOptions.IgnoreCase), "[UHID]"),
        (new Regex($@"\b(?:IP\s*(?:No\.?|number|num)|admission\s*(?:no\.?|number))\s*[:#-]?\s*{IdValue}", RegexOptions.Compiled | RegexOptions.IgnoreCase), "[IP_NUMBER]"),
        (new Regex(@"\b\d{4}[\s-]?\d{4}[\s-]?\d{4}\b", RegexOptions.Compiled), "[ID_NUMBER]"),
        (new Regex(@"\b[A-Z]{2,5}\d{6,}\b", RegexOptions.Compiled), "[UHID]"),
        (new Regex(@"\b(?:Mr|Mrs|Ms|Miss|Smt|Shri|Master|Baby)\b\.?\s+[A-Z][a-z]+(?:\s+[A-Z][a-z]+)?", RegexOptions.Compiled), "[NAME]"),
    ];

    private static readonly Regex PatientNameCue = new(
        @"(?<cue>(?i:\bpatient(?:'s)?\s+name\s*(?:is|:)?|\bname\s+of\s+the\s+patient\s*(?:is|:)?))\s+[A-Z][a-z]+(?:\s+[A-Z][a-z]+)?",
        RegexOptions.Compiled);

    public static MaskResult Mask(string text, IReadOnlyCollection<string>? names = null)
    {
        if (string.IsNullOrEmpty(text)) return new MaskResult(text ?? "", 0);
        var count = 0;
        var result = text;

        foreach (var (pattern, replacement) in Rules)
            result = pattern.Replace(result, _ => { count++; return replacement; });

        result = PatientNameCue.Replace(result, m => { count++; return m.Groups["cue"].Value + " [PATIENT_NAME]"; });

        if (names is { Count: > 0 })
        {
            var alternation = string.Join("|", names.Where(n => n.Trim().Length >= 3).Select(n => Regex.Escape(n.Trim())));
            if (alternation.Length > 0)
                result = Regex.Replace(result, $@"\b(?:{alternation})\b", _ => { count++; return "[NAME]"; }, RegexOptions.IgnoreCase);
        }
        return new MaskResult(result, count);
    }
}

public sealed class PhiMasker(ISettingsService settings) : IPhiMasker
{
    public async Task<MaskResult> MaskAsync(string text, CancellationToken ct = default)
    {
        var names = await settings.GetNameDictionaryAsync(ct);
        return PhiMaskerCore.Mask(text, names);
    }
}
