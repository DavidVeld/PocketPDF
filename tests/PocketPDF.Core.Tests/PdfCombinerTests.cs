using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PocketPDF.Core.Tests;

public class PdfCombinerTests
{
    [Fact]
    public void Pages_come_in_the_given_order_with_rotation_and_markups_kept()
    {
        using var first = TestPdf.CreateWithText();             // 1 page, 400 x 300
        using var second = TestPdf.Create();                    // 2 pages: rotated 90 with two annotations, then 200 x 100
        using var third = TestPdf.CreateWithColleagueMarkup();  // 1 page, 400 x 400, with a text box, pop-up and reply
        using var combined = new TestPdf();
        File.Delete(combined.FilePath);

        var result = PdfCombiner.Combine([first.FilePath, second.FilePath, third.FilePath], combined.FilePath);

        Assert.Equal(4, result.PageCount);
        Assert.Empty(result.SkippedFiles);
        using var document = PdfReader.Open(combined.FilePath, PdfDocumentOpenMode.Import);
        Assert.Equal([300.0, 600, 100, 400], document.Pages.Cast<PdfPage>().Select(page => page.MediaBox.Height));
        Assert.Equal(90, document.Pages[1].Elements.GetInteger("/Rotate"));
        Assert.Equal(["/Square", "/Ink"], TestPdf.AnnotationSubtypes(File.ReadAllBytes(combined.FilePath), 1));
        Assert.Contains("colleague-note", TestPdf.AnnotationsByName(combined.FilePath, 3).Keys);

        // And PocketPDF reads the combined file's markups like any other.
        Assert.IsType<TextMarkup>(Assert.Single(MarkupFile.Load(combined.FilePath).MarkupsByPage[3]));
    }

    [Fact]
    public void Unreadable_and_password_protected_files_are_skipped_and_listed()
    {
        using var good = TestPdf.CreateWithText();
        using var damaged = new TestPdf();
        File.WriteAllText(damaged.FilePath, "%PDF-1.6\n% cut off after the header\n");
        using var locked = new TestPdf();
        using (var document = new PdfDocument())
        {
            document.AddPage();
            document.SecuritySettings.UserPassword = "open-me";
            document.SecuritySettings.OwnerPassword = "owner";
            document.Save(locked.FilePath);
        }
        using var combined = new TestPdf();
        File.Delete(combined.FilePath);

        var result = PdfCombiner.Combine([damaged.FilePath, good.FilePath, locked.FilePath], combined.FilePath);

        Assert.Equal(1, result.PageCount);
        Assert.Equal([damaged.FilePath, locked.FilePath], result.SkippedFiles.Select(file => file.FilePath));
        Assert.Equal("it needs a password", result.SkippedFiles[1].Reason);
    }

    [Fact]
    public void File_protected_against_changes_is_skipped_rather_than_copied_without_its_protection()
    {
        using var issued = new TestPdf();
        using (var document = new PdfDocument())
        {
            document.AddPage();
            document.SecuritySettings.OwnerPassword = "owner";
            document.SecuritySettings.PermitModifyDocument = false;
            document.Save(issued.FilePath);
        }
        using var combined = new TestPdf();
        File.Delete(combined.FilePath);

        using var open = TestPdf.CreateWithText();

        var result = PdfCombiner.Combine([issued.FilePath, open.FilePath], combined.FilePath);

        Assert.Equal(1, result.PageCount);
        Assert.Contains("protected against changes", Assert.Single(result.SkippedFiles).Reason);
    }

    [Fact]
    public void Nothing_is_written_when_no_file_can_be_read()
    {
        using var damaged = new TestPdf();
        File.WriteAllText(damaged.FilePath, "not a pdf");
        using var combined = new TestPdf();
        File.Delete(combined.FilePath);

        Assert.Throws<InvalidDataException>(() => PdfCombiner.Combine([damaged.FilePath], combined.FilePath));
        Assert.False(File.Exists(combined.FilePath));
        Assert.False(File.Exists(combined.FilePath + ".combining"));
    }

    [Fact]
    public void An_existing_file_is_never_overwritten()
    {
        using var good = TestPdf.CreateWithText();
        using var existing = TestPdf.Create();
        var before = File.ReadAllBytes(existing.FilePath);

        Assert.Throws<IOException>(() => PdfCombiner.Combine([good.FilePath], existing.FilePath));
        Assert.Equal(before, File.ReadAllBytes(existing.FilePath));
    }

    [Fact]
    public void Binder_name_uses_the_date_and_counts_up_when_taken()
    {
        var folder = Directory.CreateTempSubdirectory("pocketpdf-binder-").FullName;
        try
        {
            var today = new DateTime(2026, 11, 20);
            Assert.Equal(Path.Combine(folder, "Binder_2026-11-20.pdf"), PdfCombiner.DefaultBinderPath(folder, today));
            File.WriteAllText(Path.Combine(folder, "Binder_2026-11-20.pdf"), "");
            File.WriteAllText(Path.Combine(folder, "Binder_2026-11-20 (2).pdf"), "");
            Assert.Equal(Path.Combine(folder, "Binder_2026-11-20 (3).pdf"), PdfCombiner.DefaultBinderPath(folder, today));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
