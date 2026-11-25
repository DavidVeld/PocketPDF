using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PocketPDF.Core;

/// <summary>A file left out of a combined PDF, and why.</summary>
public sealed record SkippedFile(string FilePath, string Reason);

public sealed record CombineResult(int PageCount, IReadOnlyList<SkippedFile> SkippedFiles);

/// <summary>Joins PDFs into one, all pages in the given order. Keeps page rotation and markups; drops bookmarks.</summary>
public static class PdfCombiner
{
    /// <summary>
    /// Writes the pages of <paramref name="inputPaths"/> to <paramref name="outputPath"/>, which must not exist yet.
    /// Files that can't be read are skipped and listed in the result; if none can be read, nothing is written.
    /// </summary>
    public static CombineResult Combine(IReadOnlyList<string> inputPaths, string outputPath)
    {
        using var output = new PdfDocument();
        var skipped = new List<SkippedFile>();
        foreach (var inputPath in inputPaths)
        {
            try
            {
                // PDFsharp returns null when a password is needed and the request is aborted.
                using var input = PdfReader.Open(inputPath, PdfDocumentOpenMode.Import, request => request.Abort = true)
                    ?? throw new PasswordProtectedPdfException();
                // Combining would hand out an unprotected copy, so the author's "no changes" restriction is respected.
                if (IsProtectedAgainstChanges(inputPath))
                {
                    skipped.Add(new(inputPath, "it's protected against changes"));
                    continue;
                }
                foreach (var page in input.Pages) output.AddPage(page);
            }
            catch (PasswordProtectedPdfException)
            {
                skipped.Add(new(inputPath, "it needs a password"));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                skipped.Add(new(inputPath, $"it couldn't be read ({ex.Message})"));
            }
        }
        int pageCount = output.PageCount; // PDFsharp refuses to answer once the document is saved
        if (pageCount == 0) throw new InvalidDataException("None of the files could be read, so nothing was combined.");

        // Write next to the target, then move it into place: a crash never leaves half a PDF under the real name.
        string temporaryPath = outputPath + ".combining";
        try
        {
            output.Save(temporaryPath);
            File.Move(temporaryPath, outputPath, overwrite: false);
        }
        catch
        {
            File.Delete(temporaryPath);
            throw;
        }
        return new CombineResult(pageCount, skipped);
    }

    /// <summary>
    /// True if changing the file needs its owner password. Opening for import decrypts it without asking,
    /// so ask for change access, the same way <see cref="MarkupFile.Load"/> finds view-only files.
    /// </summary>
    static bool IsProtectedAgainstChanges(string filePath)
    {
        bool askedForPassword = false;
        using var _ = PdfReader.Open(filePath, PdfDocumentOpenMode.Modify, request =>
        {
            askedForPassword = true;
            request.Abort = true;
        });
        return askedForPassword;
    }

    /// <summary>"Binder_2026-11-20.pdf" in the folder, or "Binder_2026-11-20 (2).pdf" and so on if that's taken.</summary>
    public static string DefaultBinderPath(string folder, DateTime today)
    {
        string name = $"Binder_{today:yyyy-MM-dd}";
        string path = Path.Combine(folder, name + ".pdf");
        for (int copy = 2; File.Exists(path); copy++) path = Path.Combine(folder, $"{name} ({copy}).pdf");
        return path;
    }
}
