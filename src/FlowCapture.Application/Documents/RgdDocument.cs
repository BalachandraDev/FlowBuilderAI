namespace FlowCapture.Application.Documents;

/// <summary>
/// The content of a requirement document, independent of file format. The Word writer lays it out in the branded template, and
/// tests check it directly. Every piece of text must be non-blank (see <see cref="RgdDocument.FindBlankText"/>).
/// </summary>
public abstract record Block;

/// <param name="Level">0 = document title, 1 = numbered section, 2 = sub-section.</param>
public sealed record HeadingBlock(int Level, string Text) : Block;
public sealed record ParagraphBlock(string Text, bool Bold = false, bool Italic = false) : Block;
public sealed record BulletsBlock(IReadOnlyList<string> Items) : Block;

/// <param name="Header">Column titles. Empty for a two-column label/value table (first column shown as labels).</param>
/// <param name="Widths">Relative column widths.</param>
public sealed record TableBlock(IReadOnlyList<string> Header, IReadOnlyList<IReadOnlyList<string>> Rows, IReadOnlyList<int> Widths) : Block;

/// <param name="WidthPx">Display size in CSS pixels (the PNG itself may have more pixels for sharpness).</param>
public sealed record ImageBlock(byte[] Png, int WidthPx, int HeightPx, string Caption) : Block;
public sealed record PageBreakBlock : Block;

public sealed record RgdCover(string DocNumber, string Module, string AssignedBy, string CreatedDate);

public sealed record RgdDocument(RgdCover Cover, string Title, IReadOnlyList<Block> Blocks)
{
    /// <summary>Every text in the document that is empty or whitespace. A finished document has none.</summary>
    public IReadOnlyList<string> FindBlankText()
    {
        var problems = new List<string>();
        void Check(string where, string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) problems.Add(where);
        }

        Check("cover.docNumber", Cover.DocNumber);
        Check("cover.module", Cover.Module);
        Check("cover.assignedBy", Cover.AssignedBy);
        Check("cover.createdDate", Cover.CreatedDate);
        Check("title", Title);

        for (var i = 0; i < Blocks.Count; i++)
        {
            switch (Blocks[i])
            {
                case HeadingBlock h: Check($"block[{i}] heading", h.Text); break;
                case ParagraphBlock p: Check($"block[{i}] paragraph", p.Text); break;
                case BulletsBlock b:
                    if (b.Items.Count == 0) problems.Add($"block[{i}] bullets is empty");
                    for (var k = 0; k < b.Items.Count; k++) Check($"block[{i}] bullet {k}", b.Items[k]);
                    break;
                case TableBlock t:
                    if (t.Rows.Count == 0) problems.Add($"block[{i}] table has no rows");
                    for (var c = 0; c < t.Header.Count; c++) Check($"block[{i}] table header {c}", t.Header[c]);
                    for (var r = 0; r < t.Rows.Count; r++)
                        for (var c = 0; c < t.Rows[r].Count; c++) Check($"block[{i}] table row {r} cell {c}", t.Rows[r][c]);
                    break;
                case ImageBlock img:
                    Check($"block[{i}] image caption", img.Caption);
                    if (img.Png.Length == 0) problems.Add($"block[{i}] image is empty");
                    break;
            }
        }
        return problems;
    }

    /// <summary>All text in reading order (cover first). Used by tests and to compare the Word and PDF output.</summary>
    public string PlainText()
    {
        var lines = new List<string> { Cover.DocNumber, Cover.Module, Cover.AssignedBy, Cover.CreatedDate };
        foreach (var block in Blocks)
        {
            switch (block)
            {
                case HeadingBlock h: lines.Add(h.Text); break;
                case ParagraphBlock p: lines.Add(p.Text); break;
                case BulletsBlock b: lines.AddRange(b.Items); break;
                case TableBlock t:
                    lines.AddRange(t.Header);
                    foreach (var row in t.Rows) lines.AddRange(row);
                    break;
                case ImageBlock i: lines.Add(i.Caption); break;
            }
        }
        return string.Join("\n", lines);
    }
}
