using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FlowCapture.Shared.Content;

namespace FlowCapture.Application.Documents;

public sealed record GuideOpenQuestion(string Key, string Question, string Status);

public sealed record GuideInput(
    string DocNumber, string ProjectName, string FlowTitle, string Department, string PreparedBy, DateTime Created, int FlowVersionNo,
    FlowContent Flow, JsonObject Plan, byte[]? DiagramPng, IReadOnlyList<GuideOpenQuestion> OpenQuestions, bool IsDemo, byte[]? LogoPng = null,
    string? DiagramSvg = null, bool AiWritten = false, FlowDiagram? Diagram = null, IReadOnlyList<GuideVersionRow>? History = null)
{
    /// <summary>The request this document is about, for example NR-003 or CR-012 (null for documents made before requests were numbered).</summary>
    public string? RequestCode { get; init; }
    /// <summary>Pictures attached to the request, in printed order. Each is printed with its step, or with the attachments.</summary>
    public IReadOnlyList<GuideFigure>? Figures { get; init; }
    /// <summary>The keys of the sections to print (see <see cref="GuideSections"/>). Null prints every section that has something in it.</summary>
    public IReadOnlySet<string>? Sections { get; init; }
    /// <summary>Headings the person renamed for this PDF: section key to the heading to print instead of the standard one. Null prints the standard headings.</summary>
    public IReadOnlyDictionary<string, string>? SectionTitles { get; init; }
    /// <summary>Text the person wrote under a heading, printed instead of the generated text: section key to text. Null prints the generated text everywhere.</summary>
    public IReadOnlyDictionary<string, string>? SectionTexts { get; init; }
}

/// <summary>One heading of the document with what is under it, as shown to the person choosing what to print.</summary>
/// <param name="Html">The section exactly as it is printed (the page's own markup, with <c>\u0001</c> where its number goes), so the person can see it before choosing.</param>
public sealed record GuideSection(string Key, string Title, string Detail, string Html = "");

/// <summary>The sections a request's document can have, in printed order. The document control table and the sign-off belong to a document of its own only.</summary>
public static class GuideSections
{
    public static readonly IReadOnlyList<(string Key, string Title, bool OwnDocumentOnly)> All =
    [
        ("control", "Document control", true), ("purpose", "Purpose", false), ("scope", "Scope", false), ("requirements", "Functional requirements", false),
        ("roles", "User roles and responsibilities", false), ("usecase", "Use case description", false), ("diagram", "The flow at a glance", false),
        ("stages", "How it works, stage by stage", false), ("screens", "Screens and what they hold", false), ("pictures", "Attached pictures", false),
        ("touchpoints", "Where it touches the system", false), ("open", "Assumptions and open points", false), ("signoff", "Approval and sign-off", true),
    ];

    public static string TitleOf(string key) => All.First(s => s.Key == key).Title;
    public static bool IsKnown(string key) => All.Any(s => s.Key == key);
    public const int MaxTitleLength = 120;

    /// <summary>
    /// The renamed headings, checked: an unknown section is refused, a heading over <see cref="MaxTitleLength"/> letters is refused, spaces and line breaks are tidied,
    /// and a heading that is empty or the same as the standard one is simply dropped (it keeps its own name). Null when nothing was renamed.
    /// </summary>
    public static IReadOnlyDictionary<string, string>? ParseTitles(IReadOnlyDictionary<string, string>? renamed)
    {
        if (renamed is null || renamed.Count == 0) return null;
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (rawKey, rawTitle) in renamed)
        {
            var key = rawKey?.Trim() ?? "";
            if (!IsKnown(key)) throw new Domain.DomainException($"'{key}' is not a section of the document.", "unknown_section");
            var title = System.Text.RegularExpressions.Regex.Replace(rawTitle ?? "", @"\s+", " ").Trim();
            if (title.Length > MaxTitleLength) throw new Domain.DomainException($"A heading can have at most {MaxTitleLength} letters.", "heading_too_long");
            if (title.Length == 0 || title == TitleOf(key)) continue;
            result[key] = title;
        }
        return result.Count == 0 ? null : result;
    }

    /// <summary>Reads the titles stored with a document job (null when there are none).</summary>
    public static IReadOnlyDictionary<string, string>? ReadTitles(string? json) =>
        string.IsNullOrWhiteSpace(json) ? null : ParseTitles(System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json));

    /// <summary>The sections whose text can be replaced by the person's own: the ones that are written text, lists and tables. Not the document control, the drawing, the screens, the pictures or the sign-off.</summary>
    public static readonly IReadOnlySet<string> TextEditable = new HashSet<string> { "purpose", "scope", "requirements", "roles", "usecase", "stages", "touchpoints", "open" };
    public static bool IsTextEditable(string key) => TextEditable.Contains(key);
    public const int MaxTextLength = 8000;

    /// <summary>
    /// The text the person wrote, checked. A key is a section key (a request's own document) or "requestId/sectionKey" (a project or module PDF, where each request has its own
    /// text). An unknown or not editable section is refused, so is text over <see cref="MaxTextLength"/> letters; lines are tidied, and empty text is dropped (the section keeps
    /// its generated text). Null when nothing was written.
    /// </summary>
    public static IReadOnlyDictionary<string, string>? ParseTexts(IReadOnlyDictionary<string, string>? written)
    {
        if (written is null || written.Count == 0) return null;
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (rawKey, rawText) in written)
        {
            var slash = (rawKey ?? "").LastIndexOf('/');
            var key = (slash < 0 ? rawKey ?? "" : rawKey![(slash + 1)..]).Trim();
            Guid? request = null;
            if (slash >= 0)
            {
                if (!Guid.TryParse(rawKey![..slash].Trim(), out var id)) throw new Domain.DomainException($"'{rawKey}' is not a section of a request.", "unknown_section");
                request = id;
            }
            if (!IsKnown(key)) throw new Domain.DomainException($"'{key}' is not a section of the document.", "unknown_section");
            if (!IsTextEditable(key)) throw new Domain.DomainException($"The text of '{TitleOf(key)}' cannot be edited.", "section_not_editable");
            var text = CleanText(rawText);
            if (text.Length > MaxTextLength) throw new Domain.DomainException($"The text of a section can have at most {MaxTextLength} letters.", "text_too_long");
            if (text.Length == 0) continue;
            result[request is { } r ? $"{r:D}/{key}" : key] = text;
        }
        return result.Count == 0 ? null : result;
    }

    /// <summary>Line breaks made uniform, spaces tidied, empty lines and control characters removed. A line that starts with "- " is a bullet.</summary>
    internal static string CleanText(string? raw)
    {
        var lines = new string((raw ?? "").Where(c => !char.IsControl(c) || c == '\n' || c == '\r' || c == '\t').ToArray()).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
            .Select(l => System.Text.RegularExpressions.Regex.Replace(l, @"[ \t]+", " ").Trim()).Where(l => l.Length > 0);
        return string.Join("\n", lines);
    }

    /// <summary>Reads the texts stored with a document job (null when there are none).</summary>
    public static IReadOnlyDictionary<string, string>? ReadTexts(string? json) =>
        string.IsNullOrWhiteSpace(json) ? null : ParseTexts(System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json));

    /// <summary>The texts that belong to one request's document, by plain section key: the ones without a request in front of them for a request's own document, the ones of that request in a bigger one.</summary>
    public static IReadOnlyDictionary<string, string>? TextsOf(IReadOnlyDictionary<string, string>? texts, Guid? request)
    {
        if (texts is null) return null;
        var prefix = request is { } r ? $"{r:D}/" : null;
        var own = texts.Where(t => prefix is null ? !t.Key.Contains('/') : t.Key.StartsWith(prefix, StringComparison.Ordinal))
            .ToDictionary(t => prefix is null ? t.Key : t.Key[prefix.Length..], t => t.Value, StringComparer.Ordinal);
        return own.Count == 0 ? null : own;
    }

    /// <summary>The keys people may choose from: all of them for a request's own document, without control and sign-off for a chapter of a bigger one.</summary>
    public static IEnumerable<string> For(bool ownDocument) => All.Where(s => ownDocument || !s.OwnDocumentOnly).Select(s => s.Key);

    /// <summary>
    /// What was chosen, checked: nothing sent means every section; sending an empty choice or an unknown key is refused, so a mistake cannot
    /// silently print an empty document.
    /// </summary>
    public static IReadOnlySet<string>? Parse(IEnumerable<string>? chosen)
    {
        if (chosen is null) return null;
        var set = chosen.Select(k => k?.Trim() ?? "").Where(k => k.Length > 0).ToHashSet(StringComparer.Ordinal);
        if (set.Count == 0) throw new Domain.DomainException("Choose at least one section to put in the PDF.", "nothing_chosen");
        var bad = set.FirstOrDefault(k => !IsKnown(k));
        if (bad is not null) throw new Domain.DomainException($"'{bad}' is not a section of the document.", "unknown_section");
        return set;
    }
}

/// <summary>One line of the document control table: which version, when, by whom, from which version of the workflow.</summary>
public sealed record GuideVersionRow(int VersionNo, DateTime At, string By, int FlowVersionNo);

/// <summary>A request in a bigger document. <paramref name="Input"/> is null when the request has not been analysed yet (it is listed, not printed).</summary>
public sealed record CombinedRequest(string Anchor, string Code, string Title, GuideInput? Input);
public sealed record CombinedModule(string Name, IReadOnlyList<CombinedRequest> Requests);

/// <summary>What the document of a whole project, or of one module, is made from.</summary>
public sealed record CombinedGuideInput(
    string DocNumber, string Title, string ProjectName, string? ClientName, string? ModuleName, string PreparedBy, DateTime Created,
    IReadOnlyList<CombinedModule> Modules, bool IsDemo, byte[]? LogoPng = null);

/// <summary>
/// The implementation document as one printable page (A4): a cover like the company's requirement documents, then the purpose, who is
/// involved, the flow explained stage by stage, where it touches the system, the rules and requirements, and what is still open.
/// It explains the flow and its touch points; it is deliberately not a development plan. What the AI writes (the plain-language
/// stages and touch points) comes from the plan; everything else comes from the analysed workflow, and anything missing is simply left
/// out rather than shown as "To be confirmed". Everything is HTML-escaped, the page carries no script, and the only pictures are the
/// diagram and the optional logo.
/// </summary>
public static class GuideHtmlBuilder
{
    private static string E(string? s) => WebUtility.HtmlEncode(s?.Trim() ?? "");
    private static bool Has(string? s) => !string.IsNullOrWhiteSpace(s) && !IsUnknown(s);
    private static bool IsUnknown(string s) => s.Trim().Equals("To be confirmed", StringComparison.OrdinalIgnoreCase) || s.Trim().Equals("Not applicable", StringComparison.OrdinalIgnoreCase);
    private static string Or(string? s, string fallback) => Has(s) ? E(s) : fallback;

    public static string Build(GuideInput g)
    {
        var sb = new StringBuilder(64 * 1024);
        sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>").Append(E(g.FlowTitle)).Append(" - Implementation Document</title><style>")
          .Append(Css(g.DocNumber, g.LogoPng)).Append("</style></head><body>");

        Cover(sb, g);
        Body(sb, g, standalone: true);

        sb.Append("<footer>Prepared with bbflowbuild from the department's own notes. ")
          .Append(g.AiWritten ? "The wording is the AI's explanation of those notes" : "It was written from the analysed workflow")
          .Append(" and should be read by the consultant before it is shared.</footer></body></html>");
        return sb.ToString();
    }

    /// <summary>
    /// The sections of one request's document, from the purpose to the open points. A document of its own (<paramref name="standalone"/>) also has
    /// the document control table first and the sign-off last; as a chapter of a bigger document it has neither, because the bigger document has
    /// one cover and one control of its own. The sections are numbered from 1 in each case.
    /// </summary>
    private static void Body(StringBuilder sb, GuideInput g, bool standalone, List<GuideSection>? found = null, bool slim = false)
    {
        var f = g.Flow;
        var plan = g.Plan;
        var n = 0;
        bool Want(string key) => g.Sections is null || g.Sections.Contains(key);

        // A section is built first with a marker where its number goes, and only numbered and added when it is wanted, so the numbers of the
        // sections that are left run 1, 2, 3 without gaps. Every section that has something to show is also reported to the outline.
        void Put(string key, string title, string detail, StringBuilder part)
        {
            found?.Add(new GuideSection(key, title, detail, slim ? Slim(key, part.ToString()) : part.ToString()));
            if (!Want(key)) return;
            // text the person wrote instead of the generated text: the section keeps its heading and number, and is printed from their lines (only in what is printed, never in the outline)
            if (g.SectionTexts is not null && GuideSections.IsTextEditable(key) && g.SectionTexts.TryGetValue(key, out var written) && !string.IsNullOrWhiteSpace(written))
                part.Clear().Append(TextSection(title, written));
            // a heading the person renamed: each section writes its heading as </span>Title</h2>, so that is what is swapped (only in what is printed, never in the outline)
            if (g.SectionTitles is not null && g.SectionTitles.TryGetValue(key, out var custom) && !string.IsNullOrWhiteSpace(custom))
                part.Replace("</span>" + E(title) + "</h2>", "</span>" + E(custom) + "</h2>");
            sb.Append(part.Replace(Mark, (++n).ToString()));
        }
        void Make(string key, string title, Func<StringBuilder, string?> render)
        {
            var part = new StringBuilder();
            if (render(part) is { } detail) Put(key, title, detail, part);
        }

        // ---- document control
        if (standalone) Make("control", GuideSections.TitleOf("control"), p => DocumentControl(p, g, Mark));

        // ---- purpose
        Make("purpose", GuideSections.TitleOf("purpose"), p =>
        {
            var summary = Str(plan, "summary");
            p.Append("<section><h2><span>").Append(Mark).Append("</span>Purpose</h2>");
            Para(p, Has(summary) ? summary : f.Purpose);
            if (Has(f.Purpose) && Has(summary) && !SameText(summary, f.Purpose)) Para(p, f.Purpose);
            p.Append("</section>");
            return "What the flow is for";
        });

        // ---- scope
        Make("scope", GuideSections.TitleOf("scope"), p =>
        {
            var scopeIn = Strs(plan, "scopeIn");
            var scopeOut = Strs(plan, "scopeOut");
            if (scopeIn.Count + scopeOut.Count == 0 && !Has(f.Scope)) return null;
            p.Append("<section><h2><span>").Append(Mark).Append("</span>Scope</h2>");
            if (scopeIn.Count + scopeOut.Count > 0)
            {
                p.Append("<div class=\"two\">");
                if (scopeIn.Count > 0) p.Append("<div class=\"panel good\"><div class=\"label\">Covered</div>").Append(Bullets(scopeIn)).Append("</div>");
                if (scopeOut.Count > 0) p.Append("<div class=\"panel\"><div class=\"label\">Not covered</div>").Append(Bullets(scopeOut)).Append("</div>");
                p.Append("</div>");
            }
            else Para(p, f.Scope);
            p.Append("</section>");
            return scopeIn.Count + scopeOut.Count > 0 ? Detail((scopeIn.Count, "item covered", "items covered"), (scopeOut.Count, "item not covered", "items not covered")) : "What is covered";
        });

        // ---- functional requirements: what the solution must do and follow, the requirements, how they are accepted
        Make("requirements", GuideSections.TitleOf("requirements"), p => FunctionalRequirements(p, f, () => Mark));

        // ---- user roles and responsibilities
        Make("roles", GuideSections.TitleOf("roles"), p => UserRoles(p, f, plan, () => Mark));

        // ---- use case description
        Make("usecase", GuideSections.TitleOf("usecase"), p => UseCase(p, f, plan, () => Mark));

        // ---- the flow drawn
        Make("diagram", GuideSections.TitleOf("diagram"), p =>
        {
            if (g.Diagram is { } drawing && IsSafeSvg(drawing.Svg))
                DiagramPages(p, f, drawing, Mark);
            else
            {
                p.Append("<section class=\"diagram-page\"><h2><span>").Append(Mark).Append("</span>The flow at a glance</h2>");
                if (IsSafeSvg(g.DiagramSvg))
                    p.Append("<div class=\"diagram\">").Append(g.DiagramSvg).Append("</div>");     // a drawing the server made, checked once more here
                else if (g.DiagramPng is { Length: > 0 })
                    p.Append("<div class=\"diagram\"><img alt=\"Workflow diagram\" src=\"data:image/png;base64,").Append(Convert.ToBase64String(g.DiagramPng)).Append("\"></div>");
                else
                    p.Append("<p class=\"muted\">The diagram could not be drawn.</p>");
                p.Append("<p class=\"small muted\">Follow the arrows from the top. A diamond is a question: the arrow taken depends on the answer.</p></section>");
            }
            return "The workflow drawn on one page";
        });

        // ---- the flow told stage by stage
        var placed = new HashSet<int>();                                   // the pictures already printed with their step
        Make("stages", GuideSections.TitleOf("stages"), p =>
        {
            p.Append("<section><h2><span>").Append(Mark).Append("</span>How it works, stage by stage</h2>");
            var journey = Arr(plan, "journey");
            var stageCount = 0;
            if (journey.Count > 0)
            {
                var i = 0;
                foreach (var s in journey)
                {
                    p.Append("<div class=\"stage\"><div class=\"stage-no\">").Append(++i).Append("</div><div class=\"stage-body\"><h3>").Append(E(Str(s, "title"))).Append("</h3>");
                    var meta = new[] { Str(s, "who"), Str(s, "where") }.Where(Has).ToList();
                    if (meta.Count > 0) p.Append("<div class=\"meta\">").Append(string.Join(" <i>&middot;</i> ", meta.Select(E))).Append("</div>");
                    Para(p, Str(s, "description"));
                    var branches = Arr(s, "branches");
                    if (branches.Count > 0)
                    {
                        p.Append("<ul class=\"branches\">");
                        foreach (var b in branches) p.Append("<li><b>").Append(E(Str(b, "condition"))).Append("</b> &rarr; ").Append(E(Str(b, "result"))).Append("</li>");
                        p.Append("</ul>");
                    }
                    if (Has(Str(s, "outcome"))) p.Append("<div class=\"outcome\"><b>Result:</b> ").Append(E(Str(s, "outcome"))).Append("</div>");
                    FigureBlocks(p, g, Str(s, "stepId"), placed);
                    p.Append("</div></div>");
                }
                stageCount = journey.Count;
            }
            else
            {
                var i = 0;
                foreach (var s in f.Steps)
                {
                    p.Append("<div class=\"stage\"><div class=\"stage-no\">").Append(++i).Append("</div><div class=\"stage-body\"><h3>").Append(E(s.Action)).Append("</h3>");
                    var meta = new[] { s.Actor, s.Screen }.Where(Has).ToList();
                    if (meta.Count > 0) p.Append("<div class=\"meta\">").Append(string.Join(" <i>&middot;</i> ", meta.Select(E))).Append("</div>");
                    var gives = s.Outputs.Where(Has).ToList();
                    if (gives.Count > 0) p.Append("<div class=\"outcome\"><b>Result:</b> ").Append(E(string.Join(", ", gives))).Append("</div>");
                    FigureBlocks(p, g, s.Id, placed);
                    p.Append("</div></div>");
                }
                stageCount = f.Steps.Count;
                foreach (var d in f.Decisions.Where(d => Has(d.Condition)))
                    p.Append("<div class=\"callout\"><b>").Append(E(d.Condition)).Append("</b><br>Yes: ").Append(E(d.YesPath.Summary)).Append(" &nbsp; No: ").Append(E(d.NoPath.Summary)).Append("</div>");
            }
            var alternates = f.AlternateFlows.Where(a => Has(a.Name)).ToList();
            var exceptions = f.ExceptionFlows.Where(x => Has(x.Name)).ToList();
            foreach (var a in alternates)
                p.Append("<div class=\"callout\"><b>Alternative: ").Append(E(a.Name)).Append(".</b> ").Append(Has(a.Condition) ? "When " + E(a.Condition.Trim().TrimEnd('.')) + ". " : "").Append(E(string.Join(" ", a.Steps.Where(Has)))).Append("</div>");
            foreach (var x in exceptions)
                p.Append("<div class=\"callout warn\"><b>If it goes wrong: ").Append(E(x.Name)).Append(".</b> ").Append(Has(x.Condition) ? "When " + E(x.Condition.Trim().TrimEnd('.')) + ". " : "").Append(E(string.Join(" ", x.Handling.Where(Has)))).Append(Has(x.Outcome) ? " Outcome: " + E(x.Outcome) + "." : "").Append("</div>");
            p.Append("</section>");
            return Detail((stageCount, "stage", "stages"), (alternates.Count, "alternative", "alternatives"), (exceptions.Count, "thing that can go wrong", "things that can go wrong"));
        });
        if (!Want("stages")) placed.Clear();                                // pictures that would have been printed with a step are then printed with the attachments

        // ---- screens and what they hold: each page with its own fields
        Make("screens", GuideSections.TitleOf("screens"), p => ScreenDetails(p, g, f, () => Mark));

        // ---- pictures that belong to no step (or to a step the document does not show)
        Make("pictures", GuideSections.TitleOf("pictures"), p =>
        {
            if (g.Figures is not { Count: > 0 } figures) return null;
            var loose = figures.Where(x => !placed.Contains(x.No)).ToList();
            if (loose.Count == 0) return null;
            p.Append("<section><h2><span>").Append(Mark).Append("</span>Attached pictures</h2>");
            foreach (var fig in loose) FigureBlock(p, fig);
            p.Append("</section>");
            return Detail((loose.Count, "picture", "pictures"));
        });

        // ---- touch points
        Make("touchpoints", GuideSections.TitleOf("touchpoints"), p =>
        {
            var touch = Arr(plan, "touchpoints");
            var affected = Arr(plan, "existingAreasAffected");
            var handoffs = Arr(plan, "handoffs");
            var reports = f.ReportsAndPrints.Where(r => Has(r.Name)).ToList();
            var integrations = f.Integrations.Where(i => Has(i.System)).ToList();
            if (touch.Count + f.Screens.Count + affected.Count + handoffs.Count + reports.Count + integrations.Count == 0) return null;

            p.Append("<section><h2><span>").Append(Mark).Append("</span>Where it touches the system</h2>");
            var screens = touch.Count > 0 ? touch : f.Screens.Select(s => new JsonObject { ["name"] = s.MenuName, ["type"] = "Screen", ["whatHappens"] = "", ["dataIn"] = new JsonArray(), ["dataOut"] = new JsonArray() }).ToList();
            if (screens.Count > 0)
            {
                var anyIn = screens.Any(t => Strs(t, "dataIn").Any(Has));
                var anyOut = screens.Any(t => Strs(t, "dataOut").Any(Has));
                var anyWhat = screens.Any(t => Has(Str(t, "whatHappens")));
                p.Append("<h3>Screens, menus and masters</h3><table><thead><tr><th>Where</th>").Append(anyWhat ? "<th>What happens there</th>" : "")
                  .Append(anyIn ? "<th>Information used</th>" : "").Append(anyOut ? "<th>Information produced</th>" : "").Append("</tr></thead><tbody>");
                foreach (var t in screens)
                {
                    p.Append("<tr><td class=\"strong\">").Append(E(Str(t, "name"))).Append(Has(Str(t, "type")) ? " <span class=\"tag\">" + E(Str(t, "type")) + "</span>" : "").Append("</td>");
                    if (anyWhat) p.Append("<td>").Append(E(Str(t, "whatHappens"))).Append("</td>");
                    if (anyIn) p.Append("<td>").Append(Lines(Strs(t, "dataIn"))).Append("</td>");
                    if (anyOut) p.Append("<td>").Append(Lines(Strs(t, "dataOut"))).Append("</td>");
                    p.Append("</tr>");
                }
                p.Append("</tbody></table>");
            }
            if (affected.Count > 0)
            {
                p.Append("<h3>Existing BACKBONE areas affected</h3><table><thead><tr><th>Area</th><th>How it is affected</th></tr></thead><tbody>");
                foreach (var a in affected) p.Append("<tr><td class=\"strong\">").Append(E(Str(a, "area"))).Append("</td><td>").Append(E(Str(a, "effect"))).Append("</td></tr>");
                p.Append("</tbody></table>");
            }
            if (handoffs.Count + integrations.Count > 0)
            {
                p.Append("<h3>Hand-offs to other departments and systems</h3><table><thead><tr><th>From &rarr; to, or with</th><th>What passes</th><th>When, or direction</th></tr></thead><tbody>");
                foreach (var h in handoffs)
                    p.Append("<tr><td class=\"strong\">").Append(E(Str(h, "from"))).Append(" &rarr; ").Append(E(Str(h, "to"))).Append("</td><td>").Append(E(Str(h, "whatPasses"))).Append("</td><td>").Append(E(Str(h, "when"))).Append("</td></tr>");
                foreach (var i in integrations)
                    p.Append("<tr><td class=\"strong\">").Append(E(i.System)).Append("</td><td>").Append(E(i.Description)).Append("</td><td>").Append(E(i.Direction)).Append("</td></tr>");
                p.Append("</tbody></table>");
            }
            if (reports.Count > 0)
            {
                p.Append("<h3>Reports and prints</h3><table><thead><tr><th>Name</th><th>Kind</th><th>What it shows</th></tr></thead><tbody>");
                foreach (var r in reports) p.Append("<tr><td class=\"strong\">").Append(E(r.Name)).Append("</td><td>").Append(Or(r.Kind, "-")).Append("</td><td>").Append(E(r.Description)).Append("</td></tr>");
                p.Append("</tbody></table>");
            }
            p.Append("</section>");
            return Detail((screens.Count, "screen, menu or master", "screens, menus and masters"), (affected.Count, "existing area affected", "existing areas affected"),
                (handoffs.Count + integrations.Count, "hand-off", "hand-offs"), (reports.Count, "report or print", "reports and prints"));
        });

        // ---- assumptions and open points
        Make("open", GuideSections.TitleOf("open"), p =>
        {
            var assumptions = Strs(plan, "assumptions");
            var questions = Strs(plan, "openQuestions").Concat(g.OpenQuestions.Select(q => q.Question)).Concat(f.Gaps.Where(x => Has(x.Description)).Select(x => x.Description)).Where(Has)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (assumptions.Count + questions.Count == 0) return null;
            p.Append("<section><h2><span>").Append(Mark).Append("</span>Assumptions and open points</h2>");
            if (assumptions.Count > 0) p.Append("<h3>What this document assumes</h3>").Append(Bullets(assumptions));
            if (questions.Count > 0) p.Append("<h3>To confirm with the department</h3>").Append(Bullets(questions));
            p.Append("</section>");
            return Detail((assumptions.Count, "assumption", "assumptions"), (questions.Count, "question to confirm", "questions to confirm"));
        });

        // ---- sign-off
        if (standalone) Make("signoff", GuideSections.TitleOf("signoff"), p => { Approval(p, g, Mark); return "Names, signatures and dates"; });
    }

    /// <summary>
    /// A section for the preview of a big document: pictures are left out (their captions stay) and the flow drawing is replaced by a note, so that
    /// the preview of a whole project stays small and quick to open.
    /// </summary>
    private static string Slim(string key, string html) => key == "diagram"
        ? "<section class=\"diagram-page\"><h2><span>" + Mark + "</span>The flow at a glance</h2><p class=\"muted\">The workflow is drawn here as one picture.</p></section>"
        : System.Text.RegularExpressions.Regex.Replace(html, @"<img\b[^>]*>", "");
    /// <summary>Stands where a section's number goes until the section is put in the page.</summary>
    private const string Mark = "\u0001";

    /// <summary>"3 stages, 1 alternative": the parts that are not zero, in words.</summary>
    private static string Detail(params (int Count, string One, string Many)[] parts) =>
        string.Join(", ", parts.Where(x => x.Count > 0).Select(x => x.Count + " " + (x.Count == 1 ? x.One : x.Many)));

    /// <summary>The headings a request's document would have, and what is under each, so a person can choose which to print. Nothing is made.</summary>
    public static IReadOnlyList<GuideSection> Outline(GuideInput g, bool standalone, bool slim = false)
    {
        var found = new List<GuideSection>();
        Body(new StringBuilder(), g with { Sections = null }, standalone, found, slim);
        return found;
    }

    /// <summary>The cover of a request's own document, as it is printed, for the preview.</summary>
    public static string PreviewCover(GuideInput g)
    {
        var sb = new StringBuilder();
        Cover(sb, g);
        return sb.ToString();
    }

    /// <summary>The look of the printed document, for showing a preview of it on screen: the same styles, with each page drawn as a sheet of paper.</summary>
    public static string PreviewStyle() => Css("", null) + """
        body { background: #e9edf5; padding: 14px; }
        .paper { background: #fff; max-width: 840px; margin: 0 auto 16px; padding: 26px 34px 20px; box-shadow: 0 1px 6px rgba(18, 32, 61, .18); border-radius: 4px; }
        .paper h2:first-child, .paper > section:first-child h2 { margin-top: 0; }
        .cover { min-height: 0; padding: 18px 20px 18px 34px; break-after: auto; }
        .diagram-page { break-before: auto; break-after: auto; }
        .chapter { break-before: auto; }
        .coverinfo { width: 100%; max-width: 120mm; }
        img, svg { max-width: 100%; height: auto; }
        @media (max-width: 640px) { body { padding: 8px; } .paper { padding: 16px 14px 12px; } .cover { padding: 12px 10px 12px 26px; } .product { font-size: 22pt; } .two { grid-template-columns: 1fr; } table { display: block; overflow-x: auto; } }
        """;

    // ------------------------------------------------------------------ one big document, or one per module

    /// <summary>
    /// A document of many requests: one cover, a contents page that lists every module and request (a request without an analysis is listed as
    /// not included), then each analysed request as a chapter that starts on a new page. The same sections as a request's own document, made
    /// from the same analysed workflows, so no AI is used. The whole project and a single module use the same layout.
    /// </summary>
    public static string BuildCombined(CombinedGuideInput c)
    {
        var sb = new StringBuilder(256 * 1024);
        var included = c.Modules.SelectMany(m => m.Requests).Count(r => r.Input is not null);
        sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>").Append(E(c.Title)).Append(" - Implementation Document</title><style>")
          .Append(Css(c.DocNumber, c.LogoPng)).Append("</style></head><body>");

        // cover
        sb.Append("<section class=\"cover\">");
        if (c.LogoPng is { Length: > 0 }) sb.Append("<img class=\"logo\" alt=\"\" src=\"data:image/png;base64,").Append(Convert.ToBase64String(c.LogoPng)).Append("\">");
        sb.Append("<div class=\"company\">AOSTA INDIA PRIVATE LIMITED</div><div class=\"product\">BACKBONE</div><div class=\"rule\"></div>")
          .Append("<div class=\"doctitle\">Implementation Document</div><div class=\"flowtitle\">").Append(E(c.Title)).Append("</div>");
        if (c.IsDemo) sb.Append("<div class=\"demo\">Demo document: the AI was in demo mode, so the explanations are fixed examples, not an analysis of real notes.</div>");
        sb.Append("<table class=\"coverinfo\"><tbody>").Append(Row("Document ID", c.DocNumber));
        if (Has(c.ClientName)) sb.Append(Row("Client", c.ClientName!));
        sb.Append(Row("Project", c.ProjectName));
        if (Has(c.ModuleName)) sb.Append(Row("Module", c.ModuleName!));
        else sb.Append(Row("Modules", c.Modules.Count.ToString()));
        sb.Append(Row("Requests included", included.ToString())).Append(Row("Prepared by", c.PreparedBy)).Append(Row("Created date", c.Created.ToString("dd-MM-yyyy")))
          .Append("</tbody></table></section>");

        // contents
        sb.Append("<section class=\"toc\"><h2>Contents</h2>");
        foreach (var m in c.Modules)
        {
            sb.Append("<h3>").Append(E(m.Name)).Append("</h3><table class=\"tocrows\"><tbody>");
            foreach (var r in m.Requests)
            {
                sb.Append("<tr><td class=\"id\">").Append(E(r.Code)).Append("</td><td>");
                if (r.Input is not null) sb.Append("<a href=\"#").Append(r.Anchor).Append("\">").Append(E(r.Title)).Append("</a></td><td class=\"small muted\">Included</td></tr>");
                else sb.Append(E(r.Title)).Append("</td><td class=\"small muted\">Not analysed yet, so not included</td></tr>");
            }
            sb.Append("</tbody></table>");
        }
        sb.Append("</section>");

        // one chapter for each analysed request
        foreach (var m in c.Modules)
            foreach (var r in m.Requests.Where(r => r.Input is not null))
            {
                var g = r.Input!;
                sb.Append("<section class=\"chapter\" id=\"").Append(r.Anchor).Append("\"><div class=\"chapter-head\"><div class=\"chapter-code\">").Append(E(r.Code)).Append("</div><h1>")
                  .Append(E(r.Title)).Append("</h1><div class=\"meta\">").Append(E(m.Name)).Append(" <i>&middot;</i> workflow version ").Append(g.FlowVersionNo).Append("</div></div>");
                Body(sb, g, standalone: false);
                sb.Append("</section>");
            }

        sb.Append("<footer>Prepared with bbflowbuild from the departments' own notes. It was written from the analysed workflows and should be read by the consultant before it is shared.</footer></body></html>");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ the cover

    private static void Cover(StringBuilder sb, GuideInput g)
    {
        sb.Append("<section class=\"cover\">");
        if (g.LogoPng is { Length: > 0 }) sb.Append("<img class=\"logo\" alt=\"\" src=\"data:image/png;base64,").Append(Convert.ToBase64String(g.LogoPng)).Append("\">");
        sb.Append("<div class=\"company\">AOSTA INDIA PRIVATE LIMITED</div><div class=\"product\">BACKBONE</div><div class=\"rule\"></div>")
          .Append("<div class=\"doctitle\">Implementation Document</div><div class=\"flowtitle\">").Append(E(g.FlowTitle)).Append("</div>");
        if (g.IsDemo) sb.Append("<div class=\"demo\">Demo document: the AI was in demo mode, so the explanation below is a fixed example, not an analysis of real notes.</div>");
        sb.Append("<table class=\"coverinfo\"><tbody>")
          .Append(Row("Document ID", g.DocNumber)).Append(Row("Project", g.ProjectName));
        if (Has(g.Department) && !g.Department.Equals("General", StringComparison.OrdinalIgnoreCase)) sb.Append(Row("Module", g.Department));
        if (Has(g.RequestCode)) sb.Append(Row("Request", RequestText(g.RequestCode!)));
        sb.Append(Row("Prepared by", g.PreparedBy)).Append(Row("Created date", g.Created.ToString("dd-MM-yyyy"))).Append(Row("Workflow version", g.FlowVersionNo.ToString()))
          .Append("</tbody></table></section>");
    }

    /// <summary>
    /// The whole diagram on ONE page. A tall drawing is laid out in side-by-side columns (cut between two rows of boxes, so no box is cut) on a
    /// landscape page, which makes it far larger than one narrow column on a portrait page. The drawing is tried several widths wide and with
    /// one to five columns, and the combination that can be shown largest is used. It is a vector drawing, so it stays sharp when zoomed in the PDF.
    /// </summary>
    private static void DiagramPages(StringBuilder sb, FlowContent f, FlowDiagram first, string number)
    {
        // the diagram page is an ordinary A4 portrait page, the same size as every other page of the document
        const double portW = 668, portH = 765, gap = 12;                              // CSS pixels left on the page after the margins, the heading and the caption

        List<(int Top, int Bottom)> Pieces(FlowDiagram d, int k)
        {
            var list = new List<(int, int)>();
            var top = 0;
            for (var i = 1; i <= k; i++)
            {
                int bottom;
                if (i == k) bottom = d.Height;
                else
                {
                    var target = d.Height * (double)i / k;
                    var cuts = d.CutPoints.Where(c => c > top + 40 && c < d.Height - 40).ToList();
                    bottom = cuts.Count > 0 ? cuts.MinBy(c => Math.Abs(c - target)) : (int)target;
                    if (bottom <= top) bottom = (int)target;
                }
                list.Add((top, bottom));
                top = bottom;
            }
            return list;
        }

        // (drawing, columns, landscape, size of the drawing on the page)
        var best = (D: first, Pieces: Pieces(first, 1), Landscape: false, Scale: 0.0);
        foreach (var across in new[] { 2, 3, 4, 5, 6 })
        {
            var d = across == 4 ? first : FlowDiagramBuilder.Build(f, across);
            for (var k = 1; k <= 3; k++)
            {
                var pieces = Pieces(d, k);
                var tallest = pieces.Max(x => x.Bottom - x.Top);
                var scale = Math.Min(1.0, Math.Min((portW - (k - 1) * gap) / (k * d.Width), portH / tallest));
                var score = scale / (1 + 0.15 * (k - 1));                                    // a column is only worth it when it makes the picture clearly larger
                if (score > best.Scale + 0.015) best = (d, pieces, false, score);
            }
        }
        var (drawing, cols, isLandscape, _) = best;
        var fit = Math.Min(1.0, Math.Min((portW - (cols.Count - 1) * gap) / (cols.Count * drawing.Width), portH / cols.Max(x => x.Bottom - x.Top)));
        var inline = cols.Count == 1 && drawing.Height * fit < 480;                     // a small picture does not need a page of its own
        var width = (drawing.Width * fit).ToString("0", System.Globalization.CultureInfo.InvariantCulture);

        sb.Append("<section class=\"diagram-page").Append(isLandscape ? "" : " portrait").Append(inline ? " inline" : "").Append("\">");
        sb.Append("<h2><span>").Append(number).Append("</span>The flow at a glance</h2>");
        sb.Append("<div class=\"diagram-cols\">");
        for (var i = 0; i < cols.Count; i++)
        {
            sb.Append("<div class=\"col\">");
            if (cols.Count > 1) sb.Append("<div class=\"colnote\">").Append(i == 0 ? "Start" : "&larr; continued from the column before").Append("</div>");
            sb.Append("<div class=\"diagram\" style=\"--w:").Append(width).Append("px\">").Append(drawing.Slice(cols[i].Top, cols[i].Bottom)).Append("</div>");
            if (cols.Count > 1) sb.Append("<div class=\"colnote\">").Append(i < cols.Count - 1 ? "continues in the next column &rarr;" : "End").Append("</div>");
            sb.Append("</div>");
        }
        sb.Append("</div>");
        sb.Append("<p class=\"small muted\">Follow the arrows from the top. The numbers are the stages explained in the next section. A diamond is a question: the route taken depends on the answer.");
        if (fit < 0.45) sb.Append(" This is a large flow shown on one page: zoom in to read the boxes.");
        sb.Append("</p></section>");
    }
    /// <summary>An SVG may go into the page as markup only if it is a plain drawing: no script, no event handlers, no outside references, no embedded pages.</summary>
    internal static bool IsSafeSvg(string? svg) =>
        !string.IsNullOrWhiteSpace(svg) && svg.TrimStart().StartsWith("<svg", StringComparison.Ordinal)
        && !System.Text.RegularExpressions.Regex.IsMatch(svg, @"<\s*(script|iframe|object|embed|foreignObject|image|use)\b|\son[a-z]+\s*=|javascript:|(href|src)\s*=\s*[""']\s*(https?:|//|data:)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    // ------------------------------------------------------------------ the requirement gathering layout: requirements, roles, use case

    /// <summary>What the solution must do: the rules, the checks on entered information, the requirement table and how each is accepted.</summary>
    private static string? FunctionalRequirements(StringBuilder sb, FlowContent f, Func<string> num)
    {
        var rules = f.BusinessRules.Where(r => Has(r.Rule)).ToList();
        var checks = f.Validations.Where(v => Has(v.Rule)).ToList();
        var reqs = f.Requirements.Where(r => Has(r.Description)).ToList();
        if (rules.Count + checks.Count + reqs.Count == 0) return null;

        sb.Append("<section><h2><span>").Append(num()).Append("</span>Functional requirements</h2>");
        if (rules.Count > 0)
        {
            sb.Append("<h3>What the solution must do and follow</h3><table><thead><tr><th>Rule</th><th>Applies to</th></tr></thead><tbody>");
            foreach (var r in rules)
                sb.Append("<tr><td>").Append(E(r.Rule)).Append("</td><td>").Append(Chips(r.AppliesTo.Where(Has).Select(id => RuleTarget(f, id)).Distinct().ToList())).Append("</td></tr>");
            sb.Append("</tbody></table>");
        }
        if (checks.Count > 0)
        {
            sb.Append("<h3>Checks on entered information</h3><table><thead><tr><th>Field</th><th>Check</th><th>Message shown</th></tr></thead><tbody>");
            foreach (var v in checks) sb.Append("<tr><td class=\"strong\">").Append(Or(v.Field, "-")).Append("</td><td>").Append(E(v.Rule)).Append("</td><td>").Append(Or(v.ErrorMessage, "-")).Append("</td></tr>");
            sb.Append("</tbody></table>");
        }
        if (reqs.Count > 0)
        {
            sb.Append("<h3>Requirements</h3><table><thead><tr><th>Req. ID</th><th>Requirement description</th><th>Category</th><th>Priority</th></tr></thead><tbody>");
            foreach (var r in reqs)
                sb.Append("<tr><td class=\"id\">").Append(E(r.ReqId)).Append("</td><td>").Append(E(r.Description)).Append("</td><td>").Append(Or(Kind(r.Category), "-")).Append("</td><td>").Append(Or(r.Priority, "Not stated")).Append("</td></tr>");
            sb.Append("</tbody></table>");
            var ac = f.AcceptanceCriteria.Where(a => Has(a.Then)).ToList();
            if (ac.Count > 0)
            {
                sb.Append("<h3>How it will be accepted</h3>");
                foreach (var a in ac)
                    sb.Append("<div class=\"accept\"><span class=\"id\">").Append(E(a.ReqId)).Append("</span> Given ").Append(E(a.Given)).Append(", when ").Append(E(a.When)).Append(", then ").Append(E(a.Then)).Append(".</div>");
            }
        }
        sb.Append("</section>");
        return Detail((rules.Count, "rule", "rules"), (checks.Count, "check on entered information", "checks on entered information"), (reqs.Count, "requirement", "requirements"));
    }

    /// <summary>Who uses it: the role, what they do here, how often, in which module and from which menu or screen.</summary>
    private static string? UserRoles(StringBuilder sb, FlowContent f, JsonObject plan, Func<string> num)
    {
        var rows = new List<(string Role, string Does, string Frequency, string Module, List<string> Where)>();
        var userRoles = f.UserRoles.Where(u => Has(u.UserRole)).ToList();
        string Frequency(string role) => userRoles.FirstOrDefault(u => u.UserRole.Trim().Equals(role.Trim(), StringComparison.OrdinalIgnoreCase) || role.Contains(u.UserRole.Trim(), StringComparison.OrdinalIgnoreCase))?.Frequency ?? "";
        var module = Has(f.ModuleCode) && !f.ModuleCode.Equals("GEN", StringComparison.OrdinalIgnoreCase) ? f.ModuleCode.Trim() : "";

        foreach (var r in Arr(plan, "roles").Where(r => Has(Str(r, "role"))))
            rows.Add((Str(r, "role"), Str(r, "responsibility"), Frequency(Str(r, "role")), module, Strs(r, "usesAreas")));
        foreach (var u in userRoles.Where(u => !rows.Any(r => r.Role.Equals(u.UserRole.Trim(), StringComparison.OrdinalIgnoreCase))))
            rows.Add((u.UserRole.Trim(), "", u.Frequency ?? "", Has(u.Module) ? u.Module.Trim() : module, Has(u.MenuName) ? [u.MenuName.Trim()] : []));
        if (rows.Count == 0) return null;

        var anyDoes = rows.Any(r => Has(r.Does));
        var anyFreq = rows.Any(r => Has(r.Frequency));
        var anyModule = rows.Any(r => Has(r.Module));
        sb.Append("<section><h2><span>").Append(num()).Append("</span>User roles and responsibilities</h2><table><thead><tr><th>User / role</th>")
          .Append(anyDoes ? "<th>What they do in this flow</th>" : "").Append(anyFreq ? "<th>Frequency</th>" : "").Append(anyModule ? "<th>Module</th>" : "").Append("<th>Menu / screen</th></tr></thead><tbody>");
        foreach (var r in rows)
        {
            sb.Append("<tr><td class=\"strong\">").Append(E(r.Role)).Append("</td>");
            if (anyDoes) sb.Append("<td>").Append(E(r.Does)).Append("</td>");
            if (anyFreq) sb.Append("<td>").Append(Or(r.Frequency, "-")).Append("</td>");
            if (anyModule) sb.Append("<td>").Append(Or(r.Module, "-")).Append("</td>");
            sb.Append("<td>").Append(Chips(r.Where)).Append("</td></tr>");
        }
        sb.Append("</tbody></table></section>");
        return Detail((rows.Count, "role", "roles"));
    }

    /// <summary>The use case in one table, as in the requirement gathering document: actors, trigger, conditions, priority and the main scenario.</summary>
    private static string? UseCase(StringBuilder sb, FlowContent f, JsonObject plan, Func<string> num)
    {
        var rows = new List<(string Label, string Html)>();
        var actors = f.UseCase.PrimaryActors.Where(Has).Select(a => a.Trim()).ToList();
        if (actors.Count == 0) actors = Arr(plan, "roles").Select(r => Str(r, "role")).Where(Has).ToList();
        if (actors.Count > 0) rows.Add(("Primary actor(s)", E(string.Join(", ", actors))));
        if (Has(f.Trigger)) rows.Add(("Trigger", E(f.Trigger)));
        var pre = f.PreConditions.Where(c => Has(c.Text)).Select(c => c.Text).ToList();
        if (pre.Count > 0) rows.Add(("Pre-conditions", pre.Count == 1 ? E(pre[0]) : Bullets(pre)));
        var post = f.PostConditions.Where(c => Has(c.Text)).Select(c => c.Text).ToList();
        if (post.Count > 0) rows.Add(("Post-conditions", post.Count == 1 ? E(post[0]) : Bullets(post)));
        if (Has(f.UseCase.Priority)) rows.Add(("Priority", E(f.UseCase.Priority)));
        if (Has(f.UseCase.MainScenario)) rows.Add(("Main scenario", E(f.UseCase.MainScenario)));
        if (rows.Count == 0) return null;

        sb.Append("<section><h2><span>").Append(num()).Append("</span>Use case description</h2><table class=\"keyvalue\"><tbody>");
        foreach (var (label, html) in rows) sb.Append("<tr><td>").Append(E(label)).Append("</td><td>").Append(html).Append("</td></tr>");
        sb.Append("</tbody></table></section>");
        return string.Join(", ", rows.Select(r => r.Label.ToLowerInvariant().Replace("(s)", "s")));
    }
    // ------------------------------------------------------------------ document control, screens, sign-off

    /// <summary>The pictures attached to this step, printed under it (each picture is printed once).</summary>
    private static void FigureBlocks(StringBuilder sb, GuideInput g, string? stepId, HashSet<int> placed)
    {
        if (g.Figures is not { Count: > 0 } || !Has(stepId)) return;
        foreach (var fig in g.Figures.Where(x => string.Equals(x.StepId, stepId, StringComparison.OrdinalIgnoreCase) && !placed.Contains(x.No)))
        {
            placed.Add(fig.No);
            FigureBlock(sb, fig);
        }
    }

    private static void FigureBlock(StringBuilder sb, GuideFigure fig)
    {
        // only the three picture types the attachments accept are ever written into the page
        if (fig.ContentType is not ("image/jpeg" or "image/png" or "image/webp") || fig.Bytes.Length == 0) return;
        sb.Append("<figure class=\"figure\"><img alt=\"").Append(E(Has(fig.Caption) ? fig.Caption : "Figure " + fig.No)).Append("\" src=\"data:").Append(fig.ContentType).Append(";base64,")
          .Append(Convert.ToBase64String(fig.Bytes)).Append("\"><figcaption><b>Figure ").Append(fig.No).Append(".</b> ").Append(E(fig.Caption)).Append("</figcaption></figure>");
    }

    /// <summary>"NR-003" becomes "NR-003 (New Requirement)", "CR-012" becomes "CR-012 (Change Request)", "BUG-004" becomes "BUG-004 (Bug)".</summary>
    internal static string RequestText(string code) =>
        code.StartsWith("CR", StringComparison.OrdinalIgnoreCase) ? code + " (Change Request)"
        : code.StartsWith("NR", StringComparison.OrdinalIgnoreCase) ? code + " (New Requirement)"
        : code.StartsWith("BUG", StringComparison.OrdinalIgnoreCase) ? code + " (Bug)" : code;

    private static string DocumentControl(StringBuilder sb, GuideInput g, string number)
    {
        var rows = (g.History is { Count: > 0 } h ? h : [new GuideVersionRow(1, g.Created, g.PreparedBy, g.FlowVersionNo)]).OrderBy(r => r.VersionNo).ToList();
        sb.Append("<section><h2><span>").Append(number).Append("</span>Document control</h2>");
        sb.Append("<table class=\"keyvalue\"><tbody>")
          .Append(Row("Document ID", g.DocNumber)).Append(Row("Project", g.ProjectName));
        if (Has(g.Department) && !g.Department.Equals("General", StringComparison.OrdinalIgnoreCase)) sb.Append(Row("Module", g.Department));
        if (Has(g.RequestCode)) sb.Append(Row("Request", RequestText(g.RequestCode!)));
        sb.Append(Row("Workflow", g.FlowTitle)).Append("</tbody></table>");
        sb.Append("<h3>Versions of this document</h3><table><thead><tr><th>Version</th><th>Date</th><th>Prepared by</th><th>Based on workflow version</th></tr></thead><tbody>");
        foreach (var r in rows)
            sb.Append("<tr><td class=\"strong\">").Append(r.VersionNo).Append("</td><td>").Append(r.At.ToString("dd-MM-yyyy")).Append("</td><td>").Append(Or(r.By, "-")).Append("</td><td>").Append(r.FlowVersionNo).Append("</td></tr>");
        sb.Append("</tbody></table></section>");
        return "Document ID, versions and who prepared it";
    }

    /// <summary>Each page, tab or screen the flow uses, and the fields it holds, with type, whether it is mandatory and its default when those are known.</summary>
    private static string? ScreenDetails(StringBuilder sb, GuideInput g, FlowContent f, Func<string> num)
    {
        var screens = f.Screens.Where(s => Has(s.MenuName) && s.Fields.Any(x => Has(x.Name))).ToList();
        if (screens.Count == 0) return null;
        sb.Append("<section><h2><span>").Append(num()).Append("</span>Screens and what they hold</h2>")
          .Append("<p class=\"small muted\" style=\"break-after: avoid\">Each page of the flow with the fields the department named for it. Details the department did not give are listed as open points at the end.</p>");
        foreach (var s in screens)
        {
            var steps = f.Steps.Select((st, i) => (st, no: i + 1)).Where(t => Has(t.st.Screen) && t.st.Screen.Trim().Equals(s.MenuName.Trim(), StringComparison.OrdinalIgnoreCase)).Select(t => t.no).ToList();
            var fields = s.Fields.Where(x => Has(x.Name)).ToList();
            var anyType = fields.Any(x => Has(x.Type));
            var anyReq = fields.Any(x => Has(x.Mandatory));
            var anyDefault = fields.Any(x => Has(x.DefaultValue));
            var anyNote = fields.Any(x => Has(x.Source));
            sb.Append("<div class=\"screenbox\"><h3>").Append(E(s.MenuName)).Append(steps.Count > 0 ? " <span class=\"tag\">" + (steps.Count == 1 ? "step " : "steps ") + string.Join(", ", steps) + "</span>" : "").Append("</h3>");
            sb.Append("<table><thead><tr><th>Field</th>").Append(anyType ? "<th>Type</th>" : "").Append(anyReq ? "<th>Required</th>" : "").Append(anyDefault ? "<th>Default</th>" : "").Append(anyNote ? "<th>Notes</th>" : "").Append("</tr></thead><tbody>");
            foreach (var x in fields)
            {
                sb.Append("<tr><td class=\"strong\">").Append(E(x.Name)).Append("</td>");
                if (anyType) sb.Append("<td>").Append(Or(x.Type, "-")).Append("</td>");
                if (anyReq) sb.Append("<td>").Append(Or(x.Mandatory, "-")).Append("</td>");
                if (anyDefault) sb.Append("<td>").Append(Or(x.DefaultValue, "-")).Append("</td>");
                if (anyNote) sb.Append("<td>").Append(Or(x.Source, "-")).Append("</td>");
                sb.Append("</tr>");
            }
            sb.Append("</tbody></table></div>");
        }
        sb.Append("</section>");
        return Detail((screens.Count, "screen with its fields", "screens with their fields"));
    }

    private static void Approval(StringBuilder sb, GuideInput g, string number)
    {
        sb.Append("<section class=\"signoff\"><h2><span>").Append(number).Append("</span>Approval and sign-off</h2>")
          .Append("<table class=\"sign\"><thead><tr><th></th><th>Name</th><th>Role</th><th>Signature</th><th>Date</th></tr></thead><tbody>")
          .Append("<tr><td class=\"strong\">Prepared by</td><td>").Append(E(g.PreparedBy)).Append("</td><td></td><td></td><td>").Append(g.Created.ToString("dd-MM-yyyy")).Append("</td></tr>")
          .Append("<tr><td class=\"strong\">Reviewed by</td><td></td><td></td><td></td><td></td></tr>")
          .Append("<tr><td class=\"strong\">Approved by</td><td></td><td></td><td></td><td></td></tr>")
          .Append("</tbody></table></section>");
    }

    /// <summary>A step, decision or alternative named in words for the rules table: the whole sentence up to a readable length, cut at a word.</summary>
    private static string RuleTarget(FlowContent f, string id)
    {
        var step = f.Steps.Select((s, i) => (s, no: i + 1)).FirstOrDefault(t => t.s.Id == id);
        if (step.s is not null && Has(step.s.Action))
            return "Step " + step.no + ": " + (Has(step.s.Screen) ? step.s.Screen.Trim() : Cut(step.s.Action, 60));
        return Label(f, id);
    }

    private static string Cut(string s, int max)
    {
        s = s.Trim().TrimEnd('.');
        if (s.Length <= max) return s;
        var cut = s[..(max - 1)];
        var space = cut.LastIndexOf(' ');
        return (space > max / 2 ? cut[..space] : cut).TrimEnd(' ', ',', ';') + "\u2026";
    }

    private static string Row(string label, string value) => $"<tr><td>{E(label)}</td><td>{E(value)}</td></tr>";

    // ------------------------------------------------------------------ pieces

    private static string Kind(string? code) => code?.Trim().ToUpperInvariant() switch
    {
        "CR" => "Change request", "NR" => "New requirement", "ER" => "Enhancement", _ => code ?? "",
    };

    /// <summary>A reader cannot use "S2" or "D1": say what the step or decision is, in a few words.</summary>
    public static string Label(FlowContent f, string id)
    {
        static string Short(string s) { s = s.Trim().TrimEnd('.'); return s.Length <= 42 ? s : s[..39].TrimEnd() + "..."; }
        var step = f.Steps.FirstOrDefault(s => s.Id == id);
        if (step is not null && Has(step.Action)) return Short(step.Action);
        var decision = f.Decisions.FirstOrDefault(d => d.Id == id);
        if (decision is not null && Has(decision.Condition)) return Short(decision.Condition);
        var alt = f.AlternateFlows.FirstOrDefault(a => a.Id == id);
        if (alt is not null && Has(alt.Name)) return Short(alt.Name);
        var exc = f.ExceptionFlows.FirstOrDefault(x => x.Id == id);
        if (exc is not null && Has(exc.Name)) return Short(exc.Name);
        return id;
    }

    private static bool SameText(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A section made from the person's own lines: the heading as every section writes it, then a paragraph for each line, and a list for the lines that start with "- ".
    /// The web page builds the same markup for its preview, so what is seen there is what is printed.
    /// </summary>
    internal static string TextSection(string title, string text)
    {
        var sb = new StringBuilder("<section><h2><span>").Append(Mark).Append("</span>").Append(E(title)).Append("</h2>");
        var list = false;
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                if (!list) { sb.Append("<ul>"); list = true; }
                sb.Append("<li>").Append(E(line[2..])).Append("</li>");
            }
            else
            {
                if (list) { sb.Append("</ul>"); list = false; }
                sb.Append("<p>").Append(E(line)).Append("</p>");
            }
        }
        if (list) sb.Append("</ul>");
        return sb.Append("</section>").ToString();
    }

    private static void Para(StringBuilder sb, string? text)
    {
        if (!Has(text)) return;
        foreach (var p in text!.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) sb.Append("<p>").Append(E(p)).Append("</p>");
    }

    private static string Chips(IReadOnlyCollection<string> items) =>
        items.Count == 0 ? "<span class=\"muted\">-</span>" : string.Concat(items.Where(Has).Select(i => "<span class=\"chip\">" + E(i) + "</span>"));

    private static string Lines(IReadOnlyCollection<string> items) =>
        items.Count(Has) == 0 ? "<span class=\"muted\">-</span>" : string.Join("<br>", items.Where(Has).Select(E));

    private static string Bullets(IReadOnlyCollection<string> items) => "<ul>" + string.Concat(items.Select(i => "<li>" + E(i) + "</li>")) + "</ul>";

    // ------------------------------------------------------------------ reading the AI's plan (tolerant: missing parts are simply empty)

    internal static string Str(JsonNode? node, string name) => node?[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : "";

    internal static List<JsonObject> Arr(JsonNode? node, string name) =>
        (node?[name] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];

    internal static List<string> Strs(JsonNode? node, string name) =>
        (node?[name] as JsonArray)?.Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : "").Where(s => s.Length > 0).ToList() ?? [];

    // ------------------------------------------------------------------ the look

    private static string Css(string docNumber, byte[]? logo = null)
    {
        var running = docNumber.Replace("\\", "").Replace("\"", "").Replace("'", "");
        var css = """
        @page { size: A4; margin: 20mm 16mm 18mm; @top-left { content: "Implementation Document"; font: 8.5pt "Segoe UI", Arial, sans-serif; color: #6a7690; } @top-right { content: "
        """ + running + """
        "; font: 8.5pt "Segoe UI", Arial, sans-serif; color: #6a7690; } @bottom-right { content: "Page " counter(page); font: 8.5pt "Segoe UI", Arial, sans-serif; color: #6a7690; } }
        @page :first { margin: 0; @top-left { content: none; } @top-right { content: none; } @bottom-right { content: none; } }
        * { box-sizing: border-box; }
        body { font-family: "Segoe UI", Arial, Helvetica, sans-serif; font-size: 10.5pt; line-height: 1.5; color: #1b2438; margin: 0; }
        h2 { display: flex; align-items: center; gap: 10px; font-size: 15pt; margin: 26px 0 10px; color: #12203d; break-after: avoid; }
        h2 span { display: inline-flex; align-items: center; justify-content: center; width: 26px; height: 26px; border-radius: 50%; background: #1f4fd8; color: #fff; font-size: 10.5pt; font-weight: 700; flex: none; }
        h3 { font-size: 11.5pt; margin: 16px 0 6px; color: #20304f; break-after: avoid; }
        p { margin: 0 0 8px; }
        section { break-inside: auto; }
        .cover { min-height: 297mm; padding: 38mm 24mm 24mm; background: #fff; break-after: page; position: relative; }
        .cover::before { content: ""; position: absolute; left: 0; top: 0; bottom: 0; width: 9mm; background: linear-gradient(180deg, #1f4fd8, #12203d); }
        .logo { display: block; max-width: 70mm; max-height: 38mm; margin: 0 0 16mm; }
        .company { font-size: 13pt; font-weight: 700; letter-spacing: .06em; color: #2a4466; }
        .product { font-size: 30pt; font-weight: 300; letter-spacing: .12em; color: #2a4466; margin-top: 6mm; }
        .rule { height: 2px; background: #2a4466; margin: 4mm 0 8mm; }
        .doctitle { font-size: 21pt; font-style: italic; color: #12203d; }
        .flowtitle { font-size: 17pt; font-weight: 700; color: #1f4fd8; margin: 6mm 0 14mm; line-height: 1.25; }
        .coverinfo { width: 120mm; border-collapse: collapse; font-size: 10.5pt; }
        .coverinfo td { border: 1px solid #1b2438; padding: 5px 10px; }
        .coverinfo td:first-child { width: 42mm; background: #f2f5fb; }
        .demo { background: #fff4d6; border: 1px solid #f0cf7a; padding: 8px 10px; border-radius: 6px; margin: 0 0 10mm; font-size: 9.5pt; max-width: 140mm; }
        .two { display: grid; grid-template-columns: 1fr 1fr; gap: 10px; margin: 8px 0; }
        .panel { border: 1px solid #d9e0ee; border-radius: 10px; padding: 8px 12px; background: #f8faff; break-inside: avoid; }
        .panel.good { border-color: #b9dcc4; background: #f3faf5; }
        .label { font-size: 8.5pt; text-transform: uppercase; letter-spacing: .08em; color: #5a6782; font-weight: 700; margin-bottom: 2px; }
        .callout { border-left: 4px solid #1f4fd8; background: #f2f6ff; padding: 8px 12px; margin: 10px 0; border-radius: 0 8px 8px 0; break-inside: avoid; }
        .callout.warn { border-left-color: #d98a14; background: #fff8ec; }
        .diagram-page { break-before: page; break-after: page; }
        .diagram-page.portrait { page: auto; }
        .diagram-page.inline { break-before: auto; break-after: auto; break-inside: avoid; }
        .diagram-page h2, .diagram-page h3.continued { margin-top: 0; }
        .diagram-page .diagram svg { width: var(--w, 100%); max-width: 100%; height: auto; max-height: none; }
        .diagram-page .diagram { border: 0; padding: 0; text-align: left; }
        .figure { margin: 8px 0 6px; break-inside: avoid; }
        .figure img { display: block; max-width: 100%; max-height: 105mm; object-fit: contain; border: 1px solid #d9e0ee; border-radius: 6px; }
        .figure figcaption { font-size: 9pt; color: #51607f; margin-top: 3px; }
        .diagram-cols { display: flex; gap: 12px; align-items: flex-start; }
        .diagram-cols .col { flex: none; }
        .colnote { font-size: 7.5pt; color: #6a7690; line-height: 1.2; height: 12px; margin: 0 0 2px; white-space: nowrap; }
        .diagram { text-align: center; border: 1px solid #d9e0ee; border-radius: 10px; padding: 8px; }
        .diagram img, .diagram svg { max-width: 100%; max-height: 150mm; height: auto; object-fit: contain; }
        .stage { display: flex; gap: 12px; margin: 0 0 12px; break-inside: avoid; }
        .stage-no { flex: none; width: 30px; height: 30px; border-radius: 50%; background: #12203d; color: #fff; font-weight: 700; display: flex; align-items: center; justify-content: center; margin-top: 2px; }
        .stage-body { flex: 1; border-bottom: 1px solid #e3e8f2; padding-bottom: 10px; }
        .stage-body h3 { margin: 0 0 2px; font-size: 12pt; }
        .meta { font-size: 9pt; color: #5a6782; margin-bottom: 4px; } .meta i { font-style: normal; margin: 0 3px; }
        .branches { margin: 4px 0 6px 18px; padding: 0; } .branches li { margin: 2px 0; }
        .outcome { font-size: 9.5pt; background: #f3faf5; border: 1px solid #cfe6d6; border-radius: 6px; padding: 4px 9px; margin-top: 4px; }
        table { width: 100%; border-collapse: collapse; margin: 6px 0 12px; font-size: 9.5pt; }
        th { text-align: left; background: #12203d; color: #fff; padding: 6px 8px; font-weight: 600; }
        td { padding: 6px 8px; border-bottom: 1px solid #e3e8f2; vertical-align: top; }
        tr { break-inside: avoid; } thead { display: table-header-group; }
        td.strong { font-weight: 600; } td.id, .id { font-weight: 700; color: #1f4fd8; white-space: nowrap; }
        .chip { display: inline-block; margin: 1px 4px 1px 0; padding: 1px 8px; border-radius: 999px; background: #eaf0fd; color: #1f3f9a; font-size: 8.5pt; font-weight: 600; }
        .tag { font-size: 7.5pt; background: #eef1f7; color: #5a6782; border-radius: 10px; padding: 1px 6px; font-weight: 600; margin-left: 4px; }
        .accept { font-size: 9.5pt; padding: 5px 10px; margin: 4px 0; border: 1px solid #e3e8f2; border-radius: 8px; break-inside: avoid; }
        ul { margin: 4px 0 8px 18px; padding: 0; } li { margin: 2px 0; }
        .small { font-size: 9pt; } .muted { color: #7a869e; }
        table.keyvalue td:first-child { width: 42mm; background: #f2f5fb; font-weight: 600; }
        .screenbox { break-inside: avoid; border: 1px solid #d9e0ee; border-left: 4px solid #1f4fd8; border-radius: 10px; padding: 6px 12px 2px; margin: 10px 0; }
        .screenbox h3 { margin: 4px 0 2px; }
        table.sign td { height: 15mm; } table.sign td.strong { width: 32mm; } .signoff { break-inside: avoid; }
        footer { margin-top: 24px; padding-top: 8px; border-top: 1px solid #d9e0ee; font-size: 8.5pt; color: #6a7690; }
        .toc { break-after: page; } .toc h2 { margin-top: 0; } .toc h3 { margin-top: 14px; border-bottom: 1px solid #d9e0ee; padding-bottom: 2px; }
        table.tocrows td { border-bottom: 0; padding: 2px 8px; } table.tocrows td.id { width: 26mm; } .toc a { color: #1f4fd8; text-decoration: none; }
        .chapter { break-before: page; }
        .chapter-head { border-left: 6px solid #1f4fd8; padding: 2px 0 2px 14px; margin: 0 0 6px; }
        .chapter-code { font-size: 10pt; font-weight: 700; letter-spacing: .08em; color: #1f4fd8; }
        .chapter-head h1 { font-size: 21pt; margin: 2px 0 4px; color: #12203d; line-height: 1.2; }
        """;
        if (logo is not { Length: > 0 }) return css;

        // The company logo at the top right of every page, as in the requirement gathering document; the document number moves to the middle.
        // The logo is wrapped in a small SVG so it has a fixed size (a margin box would otherwise show it at its full size).
        var svg = "<svg xmlns='http://www.w3.org/2000/svg' width='120' height='44' viewBox='0 0 120 44'><image href='data:image/png;base64," + Convert.ToBase64String(logo)
                  + "' x='0' y='0' width='120' height='44' preserveAspectRatio='xMaxYMid meet'/></svg>";
        var plain = "@top-right { content: \"" + running + "\"; font: 8.5pt \"Segoe UI\", Arial, sans-serif; color: #6a7690; }";
        var withLogo = "@top-right { content: url(\"data:image/svg+xml;utf8," + Uri.EscapeDataString(svg) + "\"); vertical-align: middle; } @top-center { content: \"" + running + "\"; font: 8.5pt \"Segoe UI\", Arial, sans-serif; color: #6a7690; }";
        return css.Replace(plain, withLogo).Replace("@page :first { margin: 0; @top-left { content: none; } @top-right { content: none; }", "@page :first { margin: 0; @top-left { content: none; } @top-right { content: none; } @top-center { content: none; }");
    }
}
