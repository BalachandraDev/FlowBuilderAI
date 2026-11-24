using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace FlowCapture.Infrastructure.Documents;

/// <summary>
/// Builds the default Word template, laid out like the sample RGD-MRD-003: a cover page with the company name, product, document
/// title and a details table, then pages with a "Requirement Gathering Document" header with the logo and a page-number footer.
/// The generator fills content controls named DocNumber, Module, AssignedBy and CreatedDate on the cover and replaces the
/// control named Body with the document. Branding (logo, fonts, colours, header, footer) lives only in this file, so a
/// different template can be uploaded without changing code.
/// </summary>
public static class RgdTemplateFactory
{
    private const long LogoWidthEmu = 2_286_000;       // 2.5 in
    private const int PageWidth = 12240, PageHeight = 15840, Margin = 1440;         // US Letter, 1 inch margins, like the sample

    public static byte[] CreateDefault() => Create(LoadPlaceholderLogo(), 600, 240);

    public static byte[] Create(byte[] logoPng, int logoPixelWidth, int logoPixelHeight)
    {
        using var stream = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(stream, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            var aspect = logoPixelHeight / (double)logoPixelWidth;

            var coverLogo = main.AddImagePart(ImagePartType.Png);
            using (var ms = new MemoryStream(logoPng)) coverLogo.FeedData(ms);
            var coverRel = main.GetIdOfPart(coverLogo);

            var headerPart = main.AddNewPart<HeaderPart>();
            var headerLogo = headerPart.AddImagePart(ImagePartType.Png);
            using (var ms = new MemoryStream(logoPng)) headerLogo.FeedData(ms);
            var headerLogoCx = 1_005_840L;                                             // 1.1 in
            headerPart.Header = new Header(HeaderXml(headerPart.GetIdOfPart(headerLogo), headerLogoCx, (long)(headerLogoCx * aspect)));

            var footerPart = main.AddNewPart<FooterPart>();
            footerPart.Footer = new Footer(FooterXml());

            main.AddNewPart<StyleDefinitionsPart>().Styles = new Styles(StylesXml());
            main.AddNewPart<NumberingDefinitionsPart>().Numbering = new Numbering(NumberingXml());
            main.Document = new Document(DocumentXml(coverRel, LogoWidthEmu, (long)(LogoWidthEmu * aspect),
                main.GetIdOfPart(headerPart), main.GetIdOfPart(footerPart)));

            doc.PackageProperties.Title = "Requirement Gathering Document (template)";
            doc.PackageProperties.Creator = "bbflowbuild";
        }
        return stream.ToArray();
    }

    private static byte[] LoadPlaceholderLogo()
    {
        using var s = typeof(RgdTemplateFactory).Assembly.GetManifestResourceStream("logo-placeholder.png")
                      ?? throw new InvalidOperationException("The placeholder logo is missing from the build.");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private static string DocumentXml(string coverRel, long coverCx, long coverCy, string headerRel, string footerRel) => $"""
        <w:document {WordXml.Namespaces}><w:body>
          <w:p><w:pPr><w:pStyle w:val="CoverLogo"/></w:pPr><w:r>{WordXml.InlinePicture(coverRel, coverCx, coverCy, 1, "Logo", "Company logo")}</w:r></w:p>
          <w:p><w:pPr><w:pStyle w:val="CoverCompany"/></w:pPr><w:r><w:t>AOSTA INDIA PRIVATE LIMITED</w:t></w:r></w:p>
          <w:p><w:pPr><w:pStyle w:val="CoverProduct"/></w:pPr><w:r><w:t>BACKBONE</w:t></w:r></w:p>
          <w:p><w:pPr><w:pStyle w:val="CoverTitle"/></w:pPr><w:r><w:t>Requirement Gathering Document</w:t></w:r></w:p>
          <w:tbl>
            <w:tblPr><w:tblStyle w:val="RgdTable"/><w:tblW w:w="7200" w:type="dxa"/><w:jc w:val="center"/><w:tblLayout w:type="fixed"/></w:tblPr>
            <w:tblGrid><w:gridCol w:w="2600"/><w:gridCol w:w="4600"/></w:tblGrid>
            {CoverRow("Requirement ID", "DocNumber", "MRD-000")}
            {CoverRow("Module", "Module", "Module")}
            {CoverRow("Assigned by", "AssignedBy", "Name")}
            {CoverRow("Created date", "CreatedDate", "dd-MM-yyyy")}
          </w:tbl>
          <w:p><w:pPr><w:sectPr>
            <w:type w:val="nextPage"/><w:pgSz w:w="{PageWidth}" w:h="{PageHeight}"/>
            <w:pgMar w:top="{Margin}" w:right="{Margin}" w:bottom="{Margin}" w:left="{Margin}" w:header="720" w:footer="720" w:gutter="0"/>
          </w:sectPr></w:pPr></w:p>
          <w:sdt><w:sdtPr><w:alias w:val="Document content"/><w:tag w:val="Body"/></w:sdtPr>
            <w:sdtContent><w:p><w:r><w:t>The generated document is placed here.</w:t></w:r></w:p></w:sdtContent></w:sdt>
          <w:sectPr>
            <w:headerReference w:type="default" r:id="{headerRel}"/><w:footerReference w:type="default" r:id="{footerRel}"/>
            <w:pgSz w:w="{PageWidth}" w:h="{PageHeight}"/>
            <w:pgMar w:top="{Margin}" w:right="{Margin}" w:bottom="{Margin}" w:left="{Margin}" w:header="720" w:footer="720" w:gutter="0"/>
            <w:pgNumType w:start="1"/>
          </w:sectPr>
        </w:body></w:document>
        """;

    private static string CoverRow(string label, string tag, string sample) => $"""
        <w:tr><w:tc><w:tcPr><w:tcW w:w="2600" w:type="dxa"/><w:shd w:val="clear" w:color="auto" w:fill="DCE6F2"/></w:tcPr><w:p><w:pPr><w:pStyle w:val="TableText"/></w:pPr><w:r><w:rPr><w:b/></w:rPr><w:t>{label}</w:t></w:r></w:p></w:tc>
        <w:tc><w:tcPr><w:tcW w:w="4600" w:type="dxa"/></w:tcPr><w:p><w:pPr><w:pStyle w:val="TableText"/></w:pPr>
          <w:sdt><w:sdtPr><w:alias w:val="{label}"/><w:tag w:val="{tag}"/><w:text/></w:sdtPr><w:sdtContent><w:r><w:t>{sample}</w:t></w:r></w:sdtContent></w:sdt></w:p></w:tc></w:tr>
        """;

    private static string HeaderXml(string logoRel, long cx, long cy) => $"""
        <w:hdr {WordXml.Namespaces}><w:p><w:pPr><w:pStyle w:val="Header"/></w:pPr>
          <w:r><w:t>Requirement Gathering Document</w:t></w:r><w:r><w:tab/></w:r>
          <w:r>{WordXml.InlinePicture(logoRel, cx, cy, 2, "HeaderLogo", "Company logo")}</w:r></w:p></w:hdr>
        """;

    private static string FooterXml() => $"""
        <w:ftr {WordXml.Namespaces}><w:p><w:pPr><w:pStyle w:val="Footer"/></w:pPr>
          <w:r><w:t xml:space="preserve">Page </w:t></w:r><w:fldSimple w:instr=" PAGE "><w:r><w:t>1</w:t></w:r></w:fldSimple></w:p></w:ftr>
        """;

    private static string NumberingXml() => $"""
        <w:numbering {WordXml.Namespaces}>
          <w:abstractNum w:abstractNumId="0"><w:multiLevelType w:val="hybridMultilevel"/>
            <w:lvl w:ilvl="0"><w:start w:val="1"/><w:numFmt w:val="bullet"/><w:lvlText w:val="&#8226;"/><w:lvlJc w:val="left"/>
              <w:pPr><w:ind w:left="720" w:hanging="360"/></w:pPr><w:rPr><w:rFonts w:ascii="Calibri" w:hAnsi="Calibri"/></w:rPr></w:lvl>
          </w:abstractNum>
          <w:num w:numId="1"><w:abstractNumId w:val="0"/></w:num>
        </w:numbering>
        """;

    private static string StylesXml() => $"""
        <w:styles {WordXml.Namespaces}>
          <w:docDefaults>
            <w:rPrDefault><w:rPr><w:rFonts w:ascii="Calibri" w:hAnsi="Calibri" w:eastAsia="Calibri" w:cs="Calibri"/><w:sz w:val="22"/><w:szCs w:val="22"/><w:lang w:val="en-IN"/></w:rPr></w:rPrDefault>
            <w:pPrDefault><w:pPr><w:spacing w:after="120" w:line="264" w:lineRule="auto"/></w:pPr></w:pPrDefault>
          </w:docDefaults>
          <w:style w:type="paragraph" w:default="1" w:styleId="Normal"><w:name w:val="Normal"/><w:qFormat/></w:style>
          <w:style w:type="paragraph" w:styleId="DocTitle"><w:name w:val="Doc Title"/><w:basedOn w:val="Normal"/><w:next w:val="Normal"/><w:qFormat/>
            <w:pPr><w:keepNext/><w:spacing w:before="0" w:after="160"/></w:pPr><w:rPr><w:rFonts w:ascii="Cambria" w:hAnsi="Cambria"/><w:b/><w:sz w:val="34"/><w:szCs w:val="34"/><w:color w:val="1F3864"/></w:rPr></w:style>
          <w:style w:type="paragraph" w:styleId="Heading1"><w:name w:val="heading 1"/><w:basedOn w:val="Normal"/><w:next w:val="Normal"/><w:qFormat/>
            <w:pPr><w:keepNext/><w:keepLines/><w:spacing w:before="320" w:after="120"/><w:outlineLvl w:val="0"/></w:pPr><w:rPr><w:rFonts w:ascii="Cambria" w:hAnsi="Cambria"/><w:b/><w:sz w:val="28"/><w:szCs w:val="28"/><w:color w:val="1F3864"/></w:rPr></w:style>
          <w:style w:type="paragraph" w:styleId="Heading2"><w:name w:val="heading 2"/><w:basedOn w:val="Normal"/><w:next w:val="Normal"/><w:qFormat/>
            <w:pPr><w:keepNext/><w:keepLines/><w:spacing w:before="240" w:after="100"/><w:outlineLvl w:val="1"/></w:pPr><w:rPr><w:rFonts w:ascii="Cambria" w:hAnsi="Cambria"/><w:b/><w:sz w:val="24"/><w:szCs w:val="24"/><w:color w:val="2F5496"/></w:rPr></w:style>
          <w:style w:type="paragraph" w:styleId="Heading3"><w:name w:val="heading 3"/><w:basedOn w:val="Normal"/><w:next w:val="Normal"/><w:qFormat/>
            <w:pPr><w:keepNext/><w:keepLines/><w:spacing w:before="200" w:after="80"/><w:outlineLvl w:val="2"/></w:pPr><w:rPr><w:b/><w:sz w:val="22"/><w:szCs w:val="22"/><w:color w:val="2F5496"/></w:rPr></w:style>
          <w:style w:type="paragraph" w:styleId="ListBullet"><w:name w:val="List Bullet"/><w:basedOn w:val="Normal"/><w:qFormat/>
            <w:pPr><w:numPr><w:ilvl w:val="0"/><w:numId w:val="1"/></w:numPr><w:spacing w:after="60"/><w:contextualSpacing/></w:pPr></w:style>
          <w:style w:type="paragraph" w:styleId="TableText"><w:name w:val="Table Text"/><w:basedOn w:val="Normal"/><w:qFormat/>
            <w:pPr><w:spacing w:before="40" w:after="40" w:line="240" w:lineRule="auto"/></w:pPr><w:rPr><w:sz w:val="19"/><w:szCs w:val="19"/></w:rPr></w:style>
          <w:style w:type="paragraph" w:styleId="Caption"><w:name w:val="caption"/><w:basedOn w:val="Normal"/><w:next w:val="Normal"/><w:qFormat/>
            <w:pPr><w:jc w:val="center"/><w:spacing w:before="60" w:after="200"/></w:pPr><w:rPr><w:i/><w:sz w:val="18"/><w:szCs w:val="18"/><w:color w:val="595959"/></w:rPr></w:style>
          <w:style w:type="paragraph" w:styleId="Header"><w:name w:val="header"/><w:basedOn w:val="Normal"/>
            <w:pPr><w:pBdr><w:bottom w:val="single" w:sz="6" w:space="4" w:color="7F7F7F"/></w:pBdr><w:tabs><w:tab w:val="right" w:pos="9360"/></w:tabs><w:spacing w:after="0"/></w:pPr><w:rPr><w:sz w:val="20"/><w:szCs w:val="20"/></w:rPr></w:style>
          <w:style w:type="paragraph" w:styleId="Footer"><w:name w:val="footer"/><w:basedOn w:val="Normal"/>
            <w:pPr><w:tabs><w:tab w:val="right" w:pos="9360"/></w:tabs><w:jc w:val="center"/><w:spacing w:after="0"/></w:pPr><w:rPr><w:sz w:val="18"/><w:szCs w:val="18"/></w:rPr></w:style>
          <w:style w:type="paragraph" w:styleId="CoverLogo"><w:name w:val="Cover Logo"/><w:basedOn w:val="Normal"/><w:pPr><w:jc w:val="center"/><w:spacing w:before="1200" w:after="480"/></w:pPr></w:style>
          <w:style w:type="paragraph" w:styleId="CoverCompany"><w:name w:val="Cover Company"/><w:basedOn w:val="Normal"/><w:pPr><w:jc w:val="center"/><w:spacing w:after="120"/></w:pPr><w:rPr><w:rFonts w:ascii="Cambria" w:hAnsi="Cambria"/><w:b/><w:sz w:val="40"/><w:szCs w:val="40"/><w:color w:val="1F3864"/></w:rPr></w:style>
          <w:style w:type="paragraph" w:styleId="CoverProduct"><w:name w:val="Cover Product"/><w:basedOn w:val="Normal"/><w:pPr><w:jc w:val="center"/><w:spacing w:after="360"/></w:pPr><w:rPr><w:b/><w:spacing w:val="60"/><w:sz w:val="56"/><w:szCs w:val="56"/><w:color w:val="0B5CAD"/></w:rPr></w:style>
          <w:style w:type="paragraph" w:styleId="CoverTitle"><w:name w:val="Cover Title"/><w:basedOn w:val="Normal"/><w:pPr><w:jc w:val="center"/><w:spacing w:after="720"/></w:pPr><w:rPr><w:rFonts w:ascii="Cambria" w:hAnsi="Cambria"/><w:sz w:val="36"/><w:szCs w:val="36"/></w:rPr></w:style>
          <w:style w:type="table" w:default="1" w:styleId="TableNormal"><w:name w:val="Normal Table"/><w:uiPriority w:val="99"/><w:semiHidden/>
            <w:tblPr><w:tblInd w:w="0" w:type="dxa"/><w:tblCellMar><w:top w:w="0" w:type="dxa"/><w:left w:w="108" w:type="dxa"/><w:bottom w:w="0" w:type="dxa"/><w:right w:w="108" w:type="dxa"/></w:tblCellMar></w:tblPr></w:style>
          <w:style w:type="table" w:styleId="RgdTable"><w:name w:val="Rgd Table"/><w:basedOn w:val="TableNormal"/><w:qFormat/>
            <w:tblPr><w:tblBorders>
              <w:top w:val="single" w:sz="4" w:space="0" w:color="7F7F7F"/><w:left w:val="single" w:sz="4" w:space="0" w:color="7F7F7F"/><w:bottom w:val="single" w:sz="4" w:space="0" w:color="7F7F7F"/>
              <w:right w:val="single" w:sz="4" w:space="0" w:color="7F7F7F"/><w:insideH w:val="single" w:sz="4" w:space="0" w:color="7F7F7F"/><w:insideV w:val="single" w:sz="4" w:space="0" w:color="7F7F7F"/>
            </w:tblBorders></w:tblPr></w:style>
        </w:styles>
        """;
}
