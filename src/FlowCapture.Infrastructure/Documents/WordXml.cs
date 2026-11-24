namespace FlowCapture.Infrastructure.Documents;

/// <summary>Small pieces of WordprocessingML shared by the template factory and the document writer.</summary>
internal static class WordXml
{
    public const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    public const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    public const string Wp = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
    public const string A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    public const string Pic = "http://schemas.openxmlformats.org/drawingml/2006/picture";

    public const long EmuPerPixel = 9525;                       // 96 dpi

    /// <summary>Tags the template must contain, and the styles the writer uses. A template missing any of them is refused.</summary>
    public static readonly string[] RequiredTags = ["DocNumber", "Module", "AssignedBy", "CreatedDate", "Body"];
    public static readonly string[] RequiredStyles = ["Heading1", "Heading2", "Heading3", "DocTitle", "ListBullet", "TableText", "RgdTable", "Caption"];

    public static string Namespaces => $"xmlns:w=\"{W}\" xmlns:r=\"{R}\" xmlns:wp=\"{Wp}\" xmlns:a=\"{A}\" xmlns:pic=\"{Pic}\"";

    /// <summary>An inline picture. <paramref name="relId"/> is the relationship of the image part inside the part that holds this XML.</summary>
    public static string InlinePicture(string relId, long cx, long cy, int id, string name, string description) => $"""
        <w:drawing><wp:inline distT="0" distB="0" distL="0" distR="0"><wp:extent cx="{cx}" cy="{cy}"/>
          <wp:docPr id="{id}" name="{name}" descr="{System.Security.SecurityElement.Escape(description)}"/>
          <wp:cNvGraphicFramePr><a:graphicFrameLocks noChangeAspect="1"/></wp:cNvGraphicFramePr>
          <a:graphic><a:graphicData uri="{Pic}"><pic:pic><pic:nvPicPr><pic:cNvPr id="{id}" name="{name}"/><pic:cNvPicPr/></pic:nvPicPr>
            <pic:blipFill><a:blip r:embed="{relId}"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill>
            <pic:spPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="{cx}" cy="{cy}"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom></pic:spPr>
          </pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing>
        """;
}
