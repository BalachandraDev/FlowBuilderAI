using System.Globalization;
using System.Net;
using System.Text;
using FlowCapture.Shared.Content;

namespace FlowCapture.Application.Documents;

/// <summary>
/// The drawn workflow. <see cref="Svg"/> is one standalone picture; <see cref="Slice"/> cuts the same picture into page-sized pieces
/// (at <see cref="CutPoints"/>, which fall between rows so no box is ever cut through).
/// </summary>
public sealed class FlowDiagram
{
    internal string Head { get; init; } = "";
    internal string Body { get; init; } = "";
    public int Width { get; init; }
    public int Height { get; init; }
    /// <summary>Heights (in picture pixels, top to bottom) of the gaps between rows of boxes.</summary>
    public IReadOnlyList<int> CutPoints { get; init; } = [];
    public int NodeCount { get; init; }

    public string Svg => Slice(0, Height);

    /// <summary>The part of the picture from <paramref name="top"/> to <paramref name="bottom"/>, as its own SVG.</summary>
    public string Slice(int top, int bottom)
    {
        top = Math.Max(0, top);
        bottom = Math.Min(Height, Math.Max(top + 1, bottom));
        var h = bottom - top;
        return $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 {top} {Width} {h}\" width=\"{Width}\" height=\"{h}\" role=\"img\" aria-label=\"Workflow diagram\">{Head}{Body}</svg>";
    }
}

/// <summary>
/// Draws the workflow itself, from the analysed steps and decisions, as a clean top-to-bottom diagram a developer can read at a glance:
/// every step is numbered and says who does it and which screen it happens on, every question is a diamond with labelled Yes / No
/// routes, and a menu that offers several choices is one step with a labelled route to each. Nothing here depends on the AI's own
/// drawing, so the picture always matches the data. No browser is needed: it is plain SVG text, laid out in layers (Sugiyama style:
/// rows by distance from the start, boxes ordered to reduce crossings, then pulled towards the boxes they connect to).
/// </summary>
public static class FlowDiagramBuilder
{
    /// <summary>Changes when the drawing changes, so stored pictures are redrawn.</summary>
    public const string Version = "d1";

    private const double StepW = 236, PadX = 14, LineH = 17, CharW = 7.0, GapX = 44, GapY = 64, Margin = 28;
    private const int DefaultAcross = 4;
    private const int StepChars = 28, DecisionChars = 19, MaxStepLines = 5, MaxDecisionLines = 4;

    private enum K { Start, End, Step, Decision, Alt, Dummy }

    private sealed class N
    {
        public string Id = "";
        public K Kind;
        public List<string> Lines = [];
        public string? Top, Badge;
        public List<string> BottomLines = [];
        public double W, H, X, Y, RowTop, RowBottom;
        public int Rank;
        public double Order;
        public int Pre = int.MaxValue;
        public List<E> Out = [], In = [];
        public List<N> Up = [], Down = [];                  // neighbours one row above and below, through dummies
    }

    private sealed class E
    {
        public N From = null!, To = null!;
        public string? Label;
        public bool Dashed, Back;
        public List<N> Via = [];
    }

    /// <param name="maxAcross">How many boxes may stand side by side in one row before every second one moves half a row down. More across makes a wider, shorter picture.</param>
    public static FlowDiagram Build(FlowContent f, int maxAcross = DefaultAcross)
    {
        // ------------------------------------------------------------ the graph
        var nodes = new List<N>();
        var byId = new Dictionary<string, N>(StringComparer.Ordinal);
        N Add(string id, K kind)
        {
            var n = new N { Id = id, Kind = kind };
            nodes.Add(n);
            byId[id] = n;
            return n;
        }

        string Role(string actor) => f.Actors.FirstOrDefault(a => a.Id == actor)?.Role is { Length: > 0 } r ? r : actor;

        var start = Add("\u0001start", K.Start);
        var end = Add("\u0001end", K.End);
        start.Top = "START";
        start.Lines = Has(f.Trigger) ? Wrap(f.Trigger.Trim(), StepChars, 3) : [];
        start.W = start.Lines.Count > 0 ? StepW : 120;
        start.H = start.Lines.Count > 0 ? 34 + start.Lines.Count * 16 : 36;
        end.Top = "END";
        end.W = 120; end.H = 36;

        var number = 0;
        foreach (var s in f.Steps)
        {
            if (byId.ContainsKey(s.Id)) continue;
            var n = Add(s.Id, K.Step);
            n.Badge = (++number).ToString(CultureInfo.InvariantCulture);
            n.Top = Has(s.Actor) ? Role(s.Actor.Trim()) : null;
            n.Lines = Wrap(Has(s.Action) ? s.Action.Trim() : "(step)", StepChars, MaxStepLines);
            n.BottomLines = Has(s.Screen) ? Wrap("Screen: " + s.Screen.Trim(), StepChars, 2) : [];
            n.W = StepW;
            n.H = 30 + n.Lines.Count * LineH + (n.BottomLines.Count == 0 ? 0 : 8 + n.BottomLines.Count * 16) + 8;
        }
        foreach (var d in f.Decisions)
        {
            if (byId.ContainsKey(d.Id)) continue;
            var n = Add(d.Id, K.Decision);
            n.Lines = Wrap(Has(d.Condition) ? d.Condition.Trim() : "(question)", DecisionChars, MaxDecisionLines);
            var textH = n.Lines.Count * 16;
            n.W = n.Lines.Count > 3 ? 290 : 256;
            n.H = Math.Max(112, textH * 2.3 + 30);
        }
        foreach (var a in f.AlternateFlows)
        {
            if (byId.ContainsKey(a.Id) || !Has(a.Name) || !byId.TryGetValue(a.BranchFrom ?? "", out var from) || from.Kind is K.Alt or K.Start or K.End) continue;
            var n = Add(a.Id, K.Alt);
            n.Top = "ALTERNATIVE";
            n.Lines = Wrap(a.Name.Trim() + (Has(a.Condition) ? ": " + a.Condition.Trim().TrimEnd('.') : ""), StepChars, MaxStepLines);
            n.W = StepW;
            n.H = 30 + n.Lines.Count * LineH + 8;
        }

        N? Resolve(string? id) => string.IsNullOrWhiteSpace(id) || id == "END" ? end : byId.TryGetValue(id.Trim(), out var n) && n.Kind != K.Start ? n : null;

        E? Link(N from, string? targetId, string? label, bool dashed = false)
        {
            var to = Resolve(targetId);
            if (to is null || to == from) return null;
            if (from.Out.Any(e => e.To == to && e.Label == label)) return null;
            var e = new E { From = from, To = to, Label = label, Dashed = dashed };
            from.Out.Add(e);
            to.In.Add(e);
            return e;
        }

        if (f.Steps.Count > 0) Link(start, f.Steps[0].Id, null);

        foreach (var s in f.Steps)
        {
            if (!byId.TryGetValue(s.Id, out var n) || n.Kind != K.Step) continue;
            var targets = s.Next.Select(t => (Id: t, Node: Resolve(t))).Where(t => t.Node is not null).ToList();
            // a step that leads to several steps is a menu of choices: each route says which one it is
            var choice = targets.Count(t => t.Node!.Kind == K.Step) > 1;
            foreach (var t in targets)
                Link(n, t.Id, choice && t.Node!.Kind == K.Step ? RouteName(f, t.Id) : null);
            if (n.Out.Count == 0 && !f.AlternateFlows.Any(a => a.BranchFrom == s.Id)) Link(n, "END", null);
        }
        foreach (var d in f.Decisions)
        {
            if (!byId.TryGetValue(d.Id, out var n) || n.Kind != K.Decision) continue;
            Link(n, d.YesPath.Next, "Yes");
            Link(n, d.NoPath.Next, "No");
            if (n.Out.Count == 0) Link(n, "END", null, dashed: true);
        }
        foreach (var a in f.AlternateFlows)
        {
            if (!byId.TryGetValue(a.Id, out var n) || n.Kind != K.Alt) continue;
            Link(byId[a.BranchFrom], a.Id, null, dashed: true);
            Link(n, string.IsNullOrWhiteSpace(a.RejoinAt) ? "END" : a.RejoinAt, null, dashed: true);
            if (n.Out.Count == 0) Link(n, "END", null, dashed: true);
        }
        // a step whose only way on was an alternative still has to lead somewhere
        foreach (var n in nodes.Where(n => n.Kind == K.Step && n.Out.Count == 0)) Link(n, "END", null);
        if (f.Steps.Count == 0) Link(start, "END", null);

        // ------------------------------------------------------------ rows: distance from the start, ignoring loops
        var pre = 0;
        var visited = new HashSet<N>();
        var onStack = new HashSet<N>();
        void Dfs(N n)
        {
            visited.Add(n);
            onStack.Add(n);
            n.Pre = pre++;
            foreach (var e in n.Out)
            {
                if (onStack.Contains(e.To)) e.Back = true;
                else if (!visited.Contains(e.To)) Dfs(e.To);
            }
            onStack.Remove(n);
        }
        Dfs(start);
        foreach (var n in nodes.Where(n => !visited.Contains(n) && n != end).ToList()) Dfs(n);
        if (!visited.Contains(end)) Dfs(end);

        var forward = nodes.SelectMany(n => n.Out).Where(e => !e.Back).ToList();
        for (var round = 0; round < nodes.Count + 1; round++)
        {
            var changed = false;
            foreach (var e in forward)
                if (e.To.Rank < e.From.Rank + 1) { e.To.Rank = e.From.Rank + 1; changed = true; }
            if (!changed) break;
        }
        // a row with many boxes side by side (a menu with six choices) would make the picture too wide to read: every second box moves
        // half a row down, so the picture stays narrow and the text stays large
        foreach (var n in nodes) n.Rank *= 2;
        foreach (var row in nodes.Where(n => n != end).GroupBy(n => n.Rank).Where(g => g.Count() > maxAcross && g.Key % 2 == 0).ToList())
            foreach (var n in row.OrderBy(n => n.Pre).Where((_, i) => i % 2 == 1)) n.Rank++;
        var used = nodes.Where(n => n != end).Select(n => n.Rank).Distinct().OrderBy(r => r).ToList();
        foreach (var n in nodes.Where(n => n != end)) n.Rank = used.IndexOf(n.Rank);
        end.Rank = used.Count;

        // ------------------------------------------------------------ dummies: a long route passes through every row it crosses
        var layers = new Dictionary<int, List<N>>();
        void Place(N n) { if (!layers.TryGetValue(n.Rank, out var l)) layers[n.Rank] = l = []; l.Add(n); }
        foreach (var n in nodes.ToList()) Place(n);
        foreach (var e in forward)
        {
            var prev = e.From;
            for (var r = e.From.Rank + 1; r < e.To.Rank; r++)
            {
                var dummy = new N { Id = "\u0002" + Guid.NewGuid().ToString("N"), Kind = K.Dummy, W = 12, H = 12, Rank = r, Pre = e.To.Pre };
                e.Via.Add(dummy);
                Place(dummy);
                prev.Down.Add(dummy);
                dummy.Up.Add(prev);
                prev = dummy;
            }
            prev.Down.Add(e.To);
            e.To.Up.Add(prev);
        }

        var maxRank = layers.Keys.Max();
        for (var r = 0; r <= maxRank; r++) if (!layers.ContainsKey(r)) layers[r] = [];

        // ------------------------------------------------------------ order inside a row: Yes before No, then fewer crossings
        foreach (var l in layers.Values)
        {
            var i = 0;
            foreach (var n in l.OrderBy(n => n.Pre).ThenBy(n => n.Kind == K.Dummy ? 1 : 0)) n.Order = i++;
            l.Sort((a, b) => a.Order.CompareTo(b.Order));
        }
        for (var sweep = 0; sweep < 6; sweep++)
        {
            for (var r = 1; r <= maxRank; r++) Reorder(layers[r], n => n.Up);
            for (var r = maxRank - 1; r >= 0; r--) Reorder(layers[r], n => n.Down);
        }

        // ------------------------------------------------------------ across: pulled towards the boxes each one connects to
        for (var r = 0; r <= maxRank; r++)
        {
            var x = 0.0;
            foreach (var n in layers[r]) { n.X = x + n.W / 2; x += n.W + Gap(n, n); }
            var total = x - (layers[r].Count > 0 ? Gap(layers[r][^1], layers[r][^1]) : 0);
            foreach (var n in layers[r]) n.X -= total / 2;
        }
        for (var iter = 0; iter < 10; iter++)
        {
            for (var r = 1; r <= maxRank; r++) Pull(layers[r], n => n.Up);
            for (var r = maxRank - 1; r >= 0; r--) Pull(layers[r], n => n.Down);
        }
        var minLeft = layers.Values.SelectMany(l => l).Min(n => n.X - n.W / 2);
        var maxRight = layers.Values.SelectMany(l => l).Max(n => n.X + n.W / 2);
        // a way back to an earlier step goes round the outside, on the side nearer to where it starts, so it never passes behind a box
        var backs = nodes.SelectMany(n => n.Out).Where(e => e.Back).ToList();
        var centre = (minLeft + maxRight) / 2;
        var leftBacks = backs.Where(e => e.From.X < centre).ToList();
        var rightBacks = backs.Except(leftBacks).ToList();
        var extraLeft = leftBacks.Count > 0 ? 40 + leftBacks.Count * 22 : 0;
        var shift = Margin - minLeft + extraLeft;
        foreach (var n in layers.Values.SelectMany(l => l)) n.X += shift;

        // ------------------------------------------------------------ down: one row after another
        var y = Margin;
        var cuts = new List<int>();
        for (var r = 0; r <= maxRank; r++)
        {
            var h = layers[r].Count == 0 ? 0 : layers[r].Max(n => n.H);
            foreach (var n in layers[r]) { n.Y = y + h / 2; n.RowTop = y; n.RowBottom = y + h; }
            y += h;
            if (r < maxRank) { cuts.Add((int)(y + GapY / 2)); y += GapY; }
        }

        // ------------------------------------------------------------ drawing
        var real = nodes;
        var width = layers.Values.SelectMany(l => l).Max(n => n.X + n.W / 2) + Margin + rightBacks.Count * 22 + (rightBacks.Count > 0 ? 40 : 0);
        width = Math.Max(width, 380);                       // the key at the bottom needs this much, side by side when there is room (880), else one under the other
        var keyAcross = width >= 880;
        var legendY = y + 34;
        var height = legendY + (keyAcross ? 40 : 84);

        var body = new StringBuilder(32 * 1024);
        var labels = new StringBuilder();
        var sideExit = new Dictionary<E, int>();
        foreach (var d in real.Where(n => n.Kind == K.Decision)) ChooseExits(d, sideExit);

        foreach (var e in forward) DrawEdge(body, labels, e, sideExit);
        for (var i = 0; i < rightBacks.Count; i++) DrawBackEdge(body, labels, rightBacks[i], width - Margin - 14 - i * 22, left: false);
        for (var i = 0; i < leftBacks.Count; i++) DrawBackEdge(body, labels, leftBacks[i], Margin + 8 + i * 22, left: true);

        foreach (var n in real) DrawNode(body, n);
        body.Append(labels);
        DrawLegend(body, Margin, legendY, keyAcross);

        var head = "<defs><marker id=\"fcArrow\" viewBox=\"0 0 10 10\" refX=\"9\" refY=\"5\" markerWidth=\"8\" markerHeight=\"8\" orient=\"auto-start-reverse\"><path d=\"M0,0 L10,5 L0,10 z\" fill=\"#51607f\"/></marker></defs>"
                   + "<style>text{font-family:'Segoe UI','Helvetica Neue',Arial,sans-serif}.edge{fill:none;stroke:#51607f;stroke-width:1.6}.dash{stroke-dasharray:6 5;stroke:#8a94ab}</style>"
                   + $"<rect x=\"0\" y=\"0\" width=\"{F(width)}\" height=\"{F(height)}\" fill=\"#ffffff\"/>";

        return new FlowDiagram
        {
            Head = head, Body = body.ToString(), Width = (int)Math.Ceiling(width), Height = (int)Math.Ceiling(height),
            CutPoints = cuts, NodeCount = nodes.Count(n => n.Kind != K.Dummy),
        };
    }

    // ================================================================== layout helpers

    private static double Gap(N a, N b) => a.Kind == K.Dummy && b.Kind == K.Dummy ? 26 : GapX;

    private static void Reorder(List<N> row, Func<N, List<N>> neighbours)
    {
        var rank = row.Select((n, i) => (n, i)).ToDictionary(t => t.n, t => (double)t.i);
        double Bary(N n)
        {
            var list = neighbours(n);
            return list.Count == 0 ? rank[n] : list.Average(m => m.Order);
        }
        var sorted = row.Select(n => (n, key: Bary(n))).OrderBy(t => t.key).ThenBy(t => rank[t.n]).Select(t => t.n).ToList();
        row.Clear();
        row.AddRange(sorted);
        var i = 0;
        foreach (var n in row) n.Order = i++;
    }

    /// <summary>Moves each box towards the average position of its neighbours, keeping the order and the spacing.</summary>
    private static void Pull(List<N> row, Func<N, List<N>> neighbours)
    {
        if (row.Count == 0) return;
        var want = row.Select(n => neighbours(n) is { Count: > 0 } l ? l.Average(m => m.X) : n.X).ToArray();
        double Sep(int i) => (row[i].W + row[i + 1].W) / 2 + Gap(row[i], row[i + 1]);
        var left = (double[])want.Clone();
        for (var i = 1; i < row.Count; i++) left[i] = Math.Max(want[i], left[i - 1] + Sep(i - 1));
        var right = (double[])want.Clone();
        for (var i = row.Count - 2; i >= 0; i--) right[i] = Math.Min(want[i], right[i + 1] - Sep(i));
        for (var i = 0; i < row.Count; i++) row[i].X = (left[i] + right[i]) / 2;
        // the average of two valid placements is valid, but rounding can squeeze a pair: push apart once more
        for (var i = 1; i < row.Count; i++) row[i].X = Math.Max(row[i].X, row[i - 1].X + Sep(i - 1));
    }

    // ================================================================== text

    private static bool Has(string? s) => !string.IsNullOrWhiteSpace(s) && !s.Trim().Equals("To be confirmed", StringComparison.OrdinalIgnoreCase);

    private static string RouteName(FlowContent f, string id)
    {
        var step = f.Steps.FirstOrDefault(s => s.Id == id);
        if (step is null) return id;
        return Shorten(Has(step.Screen) ? step.Screen.Trim() : step.Action.Trim().TrimEnd('.'), 24);
    }

    private static string Shorten(string s, int max) => s.Length <= max ? s : s[..(max - 1)].TrimEnd() + "…";

    /// <summary>Breaks text into lines of at most <paramref name="max"/> characters at word boundaries; the last line ends in an ellipsis when text is left over.</summary>
    internal static List<string> Wrap(string text, int max, int maxLines)
    {
        var lines = new List<string>();
        var current = new StringBuilder();
        foreach (var raw in text.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var word = raw;
            while (word.Length > max)
            {
                if (current.Length > 0) { lines.Add(current.ToString()); current.Clear(); }
                lines.Add(word[..(max - 1)] + "-");
                word = word[(max - 1)..];
            }
            if (current.Length > 0 && current.Length + 1 + word.Length > max) { lines.Add(current.ToString()); current.Clear(); }
            if (current.Length > 0) current.Append(' ');
            current.Append(word);
        }
        if (current.Length > 0) lines.Add(current.ToString());
        if (lines.Count > maxLines)
        {
            lines = lines.Take(maxLines).ToList();
            lines[^1] = Shorten(lines[^1] + "…", max);
            if (!lines[^1].EndsWith('…')) lines[^1] += "…";
        }
        return lines.Count == 0 ? [""] : lines;
    }

    private static string F(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
    private static string X(string? s) => WebUtility.HtmlEncode(s ?? "");

    // ================================================================== drawing

    private static void DrawNode(StringBuilder sb, N n)
    {
        if (n.Kind == K.Dummy) return;
        var left = n.X - n.W / 2;
        var top = n.Y - n.H / 2;
        switch (n.Kind)
        {
            case K.Start:
                sb.Append($"<rect x=\"{F(left)}\" y=\"{F(top)}\" width=\"{F(n.W)}\" height=\"{F(n.H)}\" rx=\"{F(Math.Min(18, n.H / 2))}\" fill=\"#12203d\"/>");
                if (n.Lines.Count == 0)
                    sb.Append($"<text x=\"{F(n.X)}\" y=\"{F(n.Y + 5)}\" text-anchor=\"middle\" font-size=\"13\" font-weight=\"700\" letter-spacing=\"1.5\" fill=\"#ffffff\">START</text>");
                else
                {
                    sb.Append($"<text x=\"{F(left + PadX)}\" y=\"{F(top + 21)}\" font-size=\"11\" font-weight=\"700\" letter-spacing=\"1.5\" fill=\"#9db4f0\">STARTS WHEN</text>");
                    for (var i = 0; i < n.Lines.Count; i++)
                        sb.Append($"<text x=\"{F(left + PadX)}\" y=\"{F(top + 40 + i * 16)}\" font-size=\"12.5\" fill=\"#ffffff\">{X(n.Lines[i])}</text>");
                }
                break;
            case K.End:
                sb.Append($"<rect x=\"{F(left)}\" y=\"{F(top)}\" width=\"{F(n.W)}\" height=\"{F(n.H)}\" rx=\"18\" fill=\"#12203d\"/>");
                sb.Append($"<text x=\"{F(n.X)}\" y=\"{F(n.Y + 5)}\" text-anchor=\"middle\" font-size=\"13\" font-weight=\"700\" letter-spacing=\"1.5\" fill=\"#ffffff\">END</text>");
                break;
            case K.Step:
            case K.Alt:
                var alt = n.Kind == K.Alt;
                sb.Append($"<rect x=\"{F(left)}\" y=\"{F(top)}\" width=\"{F(n.W)}\" height=\"{F(n.H)}\" rx=\"10\" fill=\"{(alt ? "#ffffff" : "#f4f7ff")}\" stroke=\"{(alt ? "#8a94ab" : "#93aaea")}\" stroke-width=\"1.4\"{(alt ? " stroke-dasharray=\"6 4\"" : "")}/>");
                if (n.Badge is not null)
                {
                    sb.Append($"<circle cx=\"{F(left + 18)}\" cy=\"{F(top + 16)}\" r=\"11\" fill=\"#1f4fd8\"/>");
                    sb.Append($"<text x=\"{F(left + 18)}\" y=\"{F(top + 20.5)}\" text-anchor=\"middle\" font-size=\"12\" font-weight=\"700\" fill=\"#ffffff\">{X(n.Badge)}</text>");
                }
                if (n.Top is not null)
                    sb.Append($"<text x=\"{F(left + (n.Badge is null ? PadX : 36))}\" y=\"{F(top + 20)}\" font-size=\"11\" font-weight=\"{(alt ? 700 : 600)}\" {(alt ? "letter-spacing=\"1.2\" " : "")}fill=\"#5a6782\">{X(n.Top)}</text>");
                for (var i = 0; i < n.Lines.Count; i++)
                    sb.Append($"<text x=\"{F(left + PadX)}\" y=\"{F(top + 44 + i * LineH)}\" font-size=\"13\" fill=\"#1b2438\">{X(n.Lines[i])}</text>");
                for (var i = 0; i < n.BottomLines.Count; i++)
                    sb.Append($"<text x=\"{F(left + PadX)}\" y=\"{F(top + n.H - 10 - (n.BottomLines.Count - 1 - i) * 16)}\" font-size=\"11.5\" font-weight=\"600\" fill=\"#1f4fd8\">{X(n.BottomLines[i])}</text>");
                break;
            case K.Decision:
                sb.Append($"<polygon points=\"{F(n.X)},{F(top)} {F(n.X + n.W / 2)},{F(n.Y)} {F(n.X)},{F(top + n.H)} {F(left)},{F(n.Y)}\" fill=\"#fff6e3\" stroke=\"#dca33a\" stroke-width=\"1.5\"/>");
                var textTop = n.Y - n.Lines.Count * 16 / 2.0 + 12;
                for (var i = 0; i < n.Lines.Count; i++)
                    sb.Append($"<text x=\"{F(n.X)}\" y=\"{F(textTop + i * 16)}\" text-anchor=\"middle\" font-size=\"12.5\" font-weight=\"600\" fill=\"#4a3410\">{X(n.Lines[i])}</text>");
                break;
        }
    }

    /// <summary>Which corner of a question each answer leaves from: the side it is heading towards, else the bottom.</summary>
    private static void ChooseExits(N d, Dictionary<E, int> exits)
    {
        var taken = new HashSet<int>();
        foreach (var e in d.Out.Where(e => !e.Back).OrderByDescending(e => Math.Abs(Target(e).X - d.X)))
        {
            var dx = Target(e).X - d.X;
            var want = dx < -d.W / 2 + 10 ? -1 : dx > d.W / 2 - 10 ? 1 : 0;
            if (taken.Contains(want)) want = taken.Contains(0) ? (taken.Contains(-1) ? 1 : -1) : 0;
            taken.Add(want);
            exits[e] = want;
        }
    }

    /// <summary>Where the route first heads: the first box it passes through, or the target.</summary>
    private static N Target(E e) => e.Via.Count > 0 ? e.Via[0] : e.To;

    private static void DrawEdge(StringBuilder sb, StringBuilder labels, E e, Dictionary<E, int> sideExit)
    {
        var from = e.From;
        var side = from.Kind == K.Decision && sideExit.TryGetValue(e, out var s) ? s : 0;
        var points = new List<(double X, double Y)>();
        var sx = side == 0 ? from.X : from.X + side * from.W / 2;
        var sy = side == 0 ? from.Y + from.H / 2 : from.Y;
        points.Add((sx, sy));
        // a long route runs straight down through every row it crosses, in the gap between the boxes, never through one
        foreach (var v in e.Via) { points.Add((v.X, v.RowTop)); points.Add((v.X, v.RowBottom)); }
        var last = (X: e.To.X, Y: e.To.Y - e.To.H / 2);
        points.Add(last);

        var d = new StringBuilder($"M{F(points[0].X)},{F(points[0].Y)}");
        for (var i = 1; i < points.Count; i++)
        {
            var (x1, y1) = points[i - 1];
            var (x2, y2) = points[i];
            var run = Math.Max(24, Math.Min(70, Math.Abs(y2 - y1) / 2));
            if (i == 1 && side != 0)
                d.Append($" C{F(x1 + side * 80)},{F(y1)} {F(x2)},{F(y2 - run)} {F(x2)},{F(y2)}");
            else
                d.Append($" C{F(x1)},{F(y1 + run)} {F(x2)},{F(y2 - run)} {F(x2)},{F(y2)}");
        }
        sb.Append($"<path class=\"edge{(e.Dashed ? " dash" : "")}\" d=\"{d}\" marker-end=\"url(#fcArrow)\"/>");

        if (!string.IsNullOrEmpty(e.Label))
        {
            if (from.Kind == K.Decision)
            {
                // an answer is named next to the question it answers
                var lx = side == 0 ? sx + (points[1].X - sx) * 0.18 : sx + side * 28;
                var ly = side == 0 ? sy + 22 : sy - 9;
                Label(labels, e.Label, lx, ly);
            }
            else
                Label(labels, e.Label, last.X, last.Y - 26);          // a choice is named just above the box it leads to, where each one has room
        }
    }

    private static void DrawBackEdge(StringBuilder sb, StringBuilder labels, E e, double xOut, bool left)
    {
        var a = e.From;
        var b = e.To;
        var ax = left ? a.X - a.W / 2 : a.X + a.W / 2;
        var bx = left ? b.X - b.W / 2 : b.X + b.W / 2;
        sb.Append($"<path class=\"edge{(e.Dashed ? " dash" : "")}\" d=\"M{F(ax)},{F(a.Y)} C{F(xOut)},{F(a.Y)} {F(xOut)},{F(b.Y)} {F(bx)},{F(b.Y)}\" marker-end=\"url(#fcArrow)\"/>");
        Label(labels, string.IsNullOrEmpty(e.Label) ? "again" : e.Label + " ↺", xOut + (left ? 6 : -6), (a.Y + b.Y) / 2);
    }

    private static void Label(StringBuilder sb, string text, double cx, double cy)
    {
        var w = text.Length * 6.6 + 14;
        sb.Append($"<rect x=\"{F(cx - w / 2)}\" y=\"{F(cy - 10)}\" width=\"{F(w)}\" height=\"18\" rx=\"9\" fill=\"#ffffff\" stroke=\"#d3dbec\"/>");
        sb.Append($"<text x=\"{F(cx)}\" y=\"{F(cy + 3)}\" text-anchor=\"middle\" font-size=\"11\" font-weight=\"700\" fill=\"#3a4766\">{X(text)}</text>");
    }

    private static void DrawLegend(StringBuilder sb, double x, double y, bool across)
    {
        // each entry: an icon and a line of text; side by side when the picture is wide enough, otherwise one under the other
        (double X, double Y) At(int i) => across ? (x + (i == 0 ? 0 : i == 1 ? 318 : 640), y) : (x, y + i * 26);
        var (x0, y0) = At(0);
        sb.Append($"<rect x=\"{F(x0)}\" y=\"{F(y0 - 11)}\" width=\"26\" height=\"16\" rx=\"4\" fill=\"#f4f7ff\" stroke=\"#93aaea\"/><text x=\"{F(x0 + 34)}\" y=\"{F(y0 + 2)}\" font-size=\"11.5\" fill=\"#5a6782\">Step: who does what, and on which screen</text>");
        var (x1, y1) = At(1);
        sb.Append($"<polygon points=\"{F(x1 + 12)},{F(y1 - 12)} {F(x1 + 24)},{F(y1 - 3)} {F(x1 + 12)},{F(y1 + 6)} {F(x1)},{F(y1 - 3)}\" fill=\"#fff6e3\" stroke=\"#dca33a\"/><text x=\"{F(x1 + 34)}\" y=\"{F(y1 + 2)}\" font-size=\"11.5\" fill=\"#5a6782\">Question: the route taken depends on the answer</text>");
        var (x2, y2) = At(2);
        sb.Append($"<rect x=\"{F(x2)}\" y=\"{F(y2 - 11)}\" width=\"26\" height=\"16\" rx=\"4\" fill=\"#ffffff\" stroke=\"#8a94ab\" stroke-dasharray=\"4 3\"/><text x=\"{F(x2 + 34)}\" y=\"{F(y2 + 2)}\" font-size=\"11.5\" fill=\"#5a6782\">Alternative way</text>");
    }
}
