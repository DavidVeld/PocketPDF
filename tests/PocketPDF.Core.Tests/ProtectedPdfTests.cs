using PdfSharp.Pdf;

namespace PocketPDF.Core.Tests;

public class ProtectedPdfTests
{
    [Fact]
    public void File_that_needs_a_password_to_open_gives_a_clear_error()
    {
        using var protectedPdf = Create(userPassword: "open-me", ownerPassword: "owner");
        Assert.Throws<PasswordProtectedPdfException>(() => MarkupFile.Load(protectedPdf.FilePath));
    }

    [Fact]
    public void File_protected_only_against_changes_opens_view_only()
    {
        using var protectedPdf = Create(userPassword: null, ownerPassword: "owner");

        var loaded = MarkupFile.Load(protectedPdf.FilePath);

        Assert.Equal("the file is protected against changes", loaded.ViewOnlyReason);
        Assert.Single(loaded.Pages);
    }

    [Fact]
    public void Damaged_file_gives_a_clear_error()
    {
        using var damaged = new TestPdf();
        File.WriteAllText(damaged.FilePath, "%PDF-1.6\n% cut off after the header\n");

        var error = Assert.Throws<InvalidDataException>(() => MarkupFile.Load(damaged.FilePath));
        Assert.Contains("damaged", error.Message);
    }

    static TestPdf Create(string? userPassword, string ownerPassword)
    {
        var testPdf = new TestPdf();
        using var document = new PdfDocument();
        document.AddPage();
        if (userPassword != null) document.SecuritySettings.UserPassword = userPassword;
        document.SecuritySettings.OwnerPassword = ownerPassword;
        document.SecuritySettings.PermitModifyDocument = false;
        document.SecuritySettings.PermitAnnotations = false;
        document.Save(testPdf.FilePath);
        return testPdf;
    }
}
