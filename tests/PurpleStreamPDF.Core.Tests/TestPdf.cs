using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PurpleStreamPDF.Core.Tests;

/// <summary>Builds small PDF files for tests in a temp folder, deleted on dispose.</summary>
sealed class TestPdf : IDisposable
{
    public string FilePath { get; } = Path.Combine(Path.GetTempPath(), $"pocketpdf-test-{Guid.NewGuid()}.pdf");

    /// <summary>
    /// Page 1: 800 x 600 media box, crop box 100,50 to 500,350, rotated 90, with two annotations PurpleStreamPDF doesn't edit:
    /// a filled square and a two-stroke ink annotation.
    /// Page 2: plain 200 x 100.
    /// </summary>
    public static TestPdf Create()
    {
        var testPdf = new TestPdf();
        using var document = new PdfDocument();

        var rotatedPage = document.AddPage();
        rotatedPage.Elements["/MediaBox"] = Box(0, 0, 800, 600);
        rotatedPage.Elements["/CropBox"] = Box(100, 50, 500, 350);
        rotatedPage.Elements["/Rotate"] = new PdfInteger(90);
        var square = new PdfDictionary(document);
        square.Elements["/Type"] = new PdfName("/Annot");
        square.Elements["/Subtype"] = new PdfName("/Square");
        square.Elements["/Rect"] = Box(150, 100, 200, 150);
        square.Elements["/IC"] = new PdfArray(document, new PdfReal(1), new PdfReal(1), new PdfReal(0));
        square.Elements["/CA"] = new PdfReal(0.5); // see-through: not editable in PurpleStreamPDF
        document.Internals.AddObject(square);
        var penStroke = new PdfDictionary(document);
        penStroke.Elements["/Type"] = new PdfName("/Annot");
        penStroke.Elements["/Subtype"] = new PdfName("/Ink");
        penStroke.Elements["/Rect"] = Box(150, 200, 250, 250);
        // Two strokes in one annotation: not editable in PurpleStreamPDF.
        penStroke.Elements["/InkList"] = new PdfArray(document,
            new PdfArray(document, new PdfReal(150), new PdfReal(200), new PdfReal(250), new PdfReal(250)),
            new PdfArray(document, new PdfReal(150), new PdfReal(250), new PdfReal(250), new PdfReal(200)));
        document.Internals.AddObject(penStroke);
        rotatedPage.Elements["/Annots"] = new PdfArray(document, square.Reference!, penStroke.Reference!);

        var plainPage = document.AddPage();
        plainPage.Elements["/MediaBox"] = Box(0, 0, 200, 100);

        document.Save(testPdf.FilePath);
        return testPdf;
    }

    /// <summary>
    /// One 400 x 400 page with a text box written by "another viewer" the way a desktop viewer does it: with rich text, a subject,
    /// a custom column, a pop-up and a reply. PurpleStreamPDF knows none of those keys and must not lose them.
    /// </summary>
    public static TestPdf CreateWithColleagueMarkup()
    {
        var testPdf = new TestPdf();
        using var document = new PdfDocument();
        var page = document.AddPage();
        page.Elements["/MediaBox"] = Box(0, 0, 400, 400);

        var note = Annotation(document, "/FreeText", "colleague-note");
        note.Elements["/Rect"] = Box(50, 300, 200, 330);
        note.Elements["/Contents"] = new PdfString("Colleague's note");
        note.Elements["/DA"] = new PdfString("/Helv 12 Tf 1 0 0 rg");
        note.Elements["/T"] = new PdfString("Colleague");
        note.Elements["/Subj"] = new PdfString("Text Box");
        note.Elements["/RC"] = new PdfString("<body><p><b>Colleague's</b> note</p></body>");
        note.Elements["/BSIColumnData"] = new PdfArray(document, new PdfString("Status: Open"));

        var popup = Annotation(document, "/Popup", "colleague-popup");
        popup.Elements["/Rect"] = Box(200, 300, 300, 380);
        popup.Elements["/Parent"] = note.Reference!;
        note.Elements["/Popup"] = popup.Reference!;

        var reply = Annotation(document, "/Text", "colleague-reply");
        reply.Elements["/Rect"] = Box(50, 300, 70, 320);
        reply.Elements["/Contents"] = new PdfString("Agreed");
        reply.Elements["/IRT"] = note.Reference!;

        page.Elements["/Annots"] = new PdfArray(document, note.Reference!, popup.Reference!, reply.Reference!);
        document.Save(testPdf.FilePath);
        return testPdf;
    }

    /// <summary>
    /// One 400 x 300 page of Helvetica 12 text: "Hello world" at (50, 250), "Second line" 20 pt below it,
    /// and "Gap" and "Here" drawn separately on one line at y 200 with no space character between them.
    /// <paramref name="extraContent"/> is drawn after them, with the same font as /F1.
    /// </summary>
    public static TestPdf CreateWithText(int rotation = 0, string extraContent = "")
    {
        var testPdf = new TestPdf();
        using var document = new PdfDocument();
        var page = document.AddPage();
        page.Elements["/MediaBox"] = Box(0, 0, 400, 300);
        if (rotation != 0) page.Elements["/Rotate"] = new PdfInteger(rotation);

        var font = new PdfDictionary(document);
        font.Elements["/Type"] = new PdfName("/Font");
        font.Elements["/Subtype"] = new PdfName("/Type1");
        font.Elements["/BaseFont"] = new PdfName("/Helvetica");
        document.Internals.AddObject(font);
        var fonts = new PdfDictionary(document);
        fonts.Elements["/F1"] = font.Reference!;
        page.Resources.Elements["/Font"] = fonts;

        const string text = "BT /F1 12 Tf 50 250 Td (Hello world) Tj 0 -20 Td (Second line) Tj ET\n"
            + "BT /F1 12 Tf 50 200 Td (Gap) Tj ET BT /F1 12 Tf 120 200 Td (Here) Tj ET\n";
        page.Contents.AppendContent().CreateStream(System.Text.Encoding.ASCII.GetBytes(text + extraContent));

        document.Save(testPdf.FilePath);
        return testPdf;
    }

    /// <summary>
    /// One 400 x 300 page of line work: a line (10,20)-(110,20); a 20 x 30 rectangle at (50,50); a curve from (200,200)
    /// to (250,200); a line (0,0)-(0,50) moved by "cm" to x 300; and a form XObject scaled 2x and moved to (100,100)
    /// holding a line (0,0)-(10,0), so it lands at (100,100)-(120,100).
    /// </summary>
    /// <param name="extra">More content to draw after the standard line work.</param>
    public static TestPdf CreateWithLineWork(int rotation = 0, string extra = "")
    {
        var testPdf = new TestPdf();
        using var document = new PdfDocument();
        var page = document.AddPage();
        page.Elements["/MediaBox"] = Box(0, 0, 400, 300);
        if (rotation != 0) page.Elements["/Rotate"] = new PdfInteger(rotation);

        var form = new PdfDictionary(document);
        form.Elements["/Type"] = new PdfName("/XObject");
        form.Elements["/Subtype"] = new PdfName("/Form");
        form.Elements["/BBox"] = new PdfArray(document, new PdfReal(0), new PdfReal(0), new PdfReal(50), new PdfReal(50));
        form.Elements["/Matrix"] = new PdfArray(document, new PdfReal(2), new PdfReal(0), new PdfReal(0), new PdfReal(2), new PdfReal(100), new PdfReal(100));
        form.CreateStream(System.Text.Encoding.ASCII.GetBytes("0 0 m 10 0 l S\n"));
        document.Internals.AddObject(form);
        page.Resources.Elements["/XObject"] = new PdfDictionary(document) { Elements = { ["/Fm1"] = form.Reference! } };

        string drawing = "0 0 0 RG 1 w\n10 20 m 110 20 l S\n50 50 20 30 re S\n200 200 m 200 250 250 250 250 200 c S\n"
            + "q 1 0 0 1 300 0 cm 0 0 m 0 50 l S Q\n/Fm1 Do\n" + extra;
        page.Contents.AppendContent().CreateStream(System.Text.Encoding.ASCII.GetBytes(drawing));

        document.Save(testPdf.FilePath);
        return testPdf;
    }

    static PdfDictionary Annotation(PdfDocument document, string subtype, string name)
    {
        var annotation = new PdfDictionary(document);
        annotation.Elements["/Type"] = new PdfName("/Annot");
        annotation.Elements["/Subtype"] = new PdfName(subtype);
        annotation.Elements["/NM"] = new PdfString(name);
        document.Internals.AddObject(annotation);
        return annotation;
    }

    /// <summary>Every annotation on a page as key → value text, found by its /NM name.</summary>
    public static Dictionary<string, Dictionary<string, string>> AnnotationsByName(string filePath, int pageIndex)
    {
        using var document = PdfReader.Open(filePath, PdfDocumentOpenMode.Import);
        var result = new Dictionary<string, Dictionary<string, string>>();
        if (document.Pages[pageIndex].Elements.GetObject("/Annots") is not PdfArray annotations) return result;
        foreach (var item in annotations.Elements)
        {
            var annotation = (item as PdfSharp.Pdf.Advanced.PdfReference)?.Value as PdfDictionary ?? (PdfDictionary)item;
            result[annotation.Elements.GetString("/NM")] = annotation.Elements.Keys.ToDictionary(key => key, key => annotation.Elements[key]?.ToString() ?? "");
        }
        return result;
    }

    public static PdfRectangle Box(double left, double bottom, double right, double top) =>
        new(new XPoint(left, bottom), new XPoint(right, top));

    /// <summary>The /Subtype of every annotation on a page of a PDF file.</summary>
    public static List<string> AnnotationSubtypes(byte[] pdfBytes, int pageIndex)
    {
        using var document = PdfReader.Open(new MemoryStream(pdfBytes), PdfDocumentOpenMode.Import);
        if (document.Pages[pageIndex].Elements.GetObject("/Annots") is not PdfArray annotations) return [];
        return annotations.Elements
            .Select(item => (item as PdfSharp.Pdf.Advanced.PdfReference)?.Value as PdfDictionary ?? item as PdfDictionary)
            .Select(annotation => annotation!.Elements.GetName("/Subtype"))
            .ToList();
    }

    public void Dispose() => File.Delete(FilePath);
}
