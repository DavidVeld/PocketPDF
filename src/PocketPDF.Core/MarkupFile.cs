using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace PocketPDF.Core;

/// <summary>An opened PDF: its pages and its editable markups, which the user changes in <see cref="MarkupsByPage"/>.</summary>
public sealed record LoadedPdf(
    byte[] OriginalBytes,
    List<PageGeometry> Pages,
    Dictionary<int, List<Markup>> MarkupsByPage)
{
    /// <summary>Why the file can only be viewed, or null if markups can be edited and saved.</summary>
    public string? ViewOnlyReason { get; init; }

    /// <summary>
    /// The drawing scale of each page that has one, as real mm per point (see <see cref="DrawingScale"/>): read from the
    /// page's viewport (/VP, as Bluebeam writes it) or set by the user. Saving writes a viewport for pages that have none.
    /// </summary>
    public Dictionary<int, double> MmPerPointByPage { get; init; } = [];

    /// <summary>Every markup as read from the file, including ones the user has since deleted.</summary>
    internal List<Markup> MarkupsFromFile { get; } = MarkupsByPage.Values.SelectMany(markups => markups).ToList();

    readonly int[] _rotationsFromFile = Pages.Select(page => page.Rotation).ToArray();

    /// <summary>
    /// Turns a page by <paramref name="degrees"/> (a multiple of 90, positive clockwise). The markups stay on the drawing:
    /// they are kept in PDF space, which doesn't turn. Saving writes the page's new /Rotate.
    /// </summary>
    public void RotatePage(int pageIndex, int degrees) =>
        Pages[pageIndex] = Pages[pageIndex] with { Rotation = PageGeometry.NormalizeRotation(Pages[pageIndex].Rotation + degrees) };

    /// <summary>True when a page was turned since the file was read.</summary>
    public bool HasTurnedPages => Pages.Where((page, pageIndex) => page.Rotation != _rotationsFromFile[pageIndex]).Any();
}

/// <summary>The PDF needs a password just to be opened.</summary>
public sealed class PasswordProtectedPdfException() : Exception(
    "This PDF is password-protected. PocketPDF can't open password-protected files: remove the password in Bluebeam or Acrobat first.");

/// <summary>Loads and saves markups as standard PDF annotations. Annotations PocketPDF can't edit are left untouched.</summary>
public static class MarkupFile
{
    public static LoadedPdf Load(string filePath) => Load(File.ReadAllBytes(filePath));

    /// <summary>Loads a PDF held in memory, such as an overlay that hasn't been saved yet.</summary>
    public static LoadedPdf Load(byte[] originalBytes)
    {
        PdfDocument document;
        bool askedForPassword = false;
        try
        {
            document = OpenForEditing(originalBytes, () => askedForPassword = true);
        }
        catch (Exception exception)
        {
            // PDFsharp can't read every file PDFium can, and needs the owner password to change protected files.
            // Show the file anyway, without markup editing.
            string reason = askedForPassword ? "the file is protected against changes" : exception.Message;
            try
            {
                return LoadViewOnly(originalBytes, reason);
            }
            catch when (askedForPassword)
            {
                throw new PasswordProtectedPdfException(); // PDFium can't open it either: it needs the password to view
            }
        }
        using var _ = document;

        var pages = new List<PageGeometry>();
        var markupsByPage = new Dictionary<int, List<Markup>>();
        var scales = new Dictionary<int, double>();
        for (int pageIndex = 0; pageIndex < document.PageCount; pageIndex++)
        {
            var geometry = PageGeometry.FromPage(document.Pages[pageIndex]);
            pages.Add(geometry);
            markupsByPage[pageIndex] = ReadEditableMarkups(document.Pages[pageIndex], pageIndex, geometry);
            if (ViewportScale(document.Pages[pageIndex]) is { } scale) scales[pageIndex] = scale;
        }
        return new LoadedPdf(originalBytes, pages, markupsByPage) { MmPerPointByPage = scales };
    }

    /// <summary>
    /// Annotations the page must be drawn without, because the markup layer draws them instead:
    /// markups from the file that were changed or deleted, and every CAD entity (so the CAD layer can be switched off).
    /// Other unchanged ones keep their own appearance from the file.
    /// </summary>
    public static IReadOnlySet<string> AnnotationsToHide(LoadedPdf loaded)
    {
        // By annotation key, not by object: undo puts back copies of the markups, like Save, which also matches by key.
        var unchangedKeys = loaded.MarkupsByPage.Values.SelectMany(markups => markups)
            .Where(markup => markup.IsUnchangedFromFile && markup is not CadMarkup)
            .Select(markup => markup.Origin!.AnnotationKey)
            .ToHashSet();
        return loaded.MarkupsFromFile
            .Select(markup => markup.Origin!.AnnotationKey)
            .Where(key => !unchangedKeys.Contains(key))
            .ToHashSet();
    }

    /// <summary>The file's bytes for drawing the pages, turned as the user turned them, without the annotations in <paramref name="annotationsToHide"/>.</summary>
    public static byte[] RenderBytes(LoadedPdf loaded, IReadOnlySet<string> annotationsToHide)
    {
        if (annotationsToHide.Count == 0 && !loaded.HasTurnedPages) return loaded.OriginalBytes;
        using var document = OpenForEditing(loaded.OriginalBytes);
        for (int pageIndex = 0; pageIndex < document.PageCount; pageIndex++)
        {
            WriteRotation(document.Pages[pageIndex], loaded.Pages[pageIndex]);
            if (document.Pages[pageIndex].Elements.GetObject("/Annots") is not PdfArray annotations) continue;
            var removedAnnotations = new HashSet<PdfDictionary>();
            for (int i = annotations.Elements.Count - 1; i >= 0; i--)
            {
                if (!annotationsToHide.Contains(AnnotationKey(annotations.Elements[i], pageIndex, i))) continue;
                if (Resolve(annotations.Elements[i]) is { } annotation) removedAnnotations.Add(annotation);
                annotations.Elements.RemoveAt(i);
            }
            RemoveDependents(annotations, removedAnnotations);
        }
        return SaveToBytes(document);
    }

    static LoadedPdf LoadViewOnly(byte[] pdfBytes, string reason)
    {
        var renderer = new PdfRenderer(pdfBytes);
        var pages = Enumerable.Range(0, renderer.PageCount).Select(pageIndex =>
        {
            var (width, height) = renderer.PageSize(pageIndex);
            return new PageGeometry(0, 0, width, height, 0); // only used for display, so the real crop box and rotation don't matter
        }).ToList();
        var noMarkups = Enumerable.Range(0, renderer.PageCount).ToDictionary(pageIndex => pageIndex, _ => new List<Markup>());
        return new LoadedPdf(pdfBytes, pages, noMarkups) { ViewOnlyReason = reason };
    }

    /// <summary>
    /// Writes the original document with <paramref name="markupsByPage"/> as its editable markups:
    /// markups read from the file and not changed stay exactly as they were, changed ones are updated in place,
    /// deleted ones are removed (with their pop-ups and replies) and new ones are added.
    /// </summary>
    public static void Save(LoadedPdf loaded, IReadOnlyDictionary<int, List<Markup>> markupsByPage, string targetPath)
    {
        using var document = OpenForEditing(loaded.OriginalBytes);
        for (int pageIndex = 0; pageIndex < document.PageCount; pageIndex++)
        {
            var page = document.Pages[pageIndex];
            var geometry = loaded.Pages[pageIndex];
            var markups = markupsByPage.GetValueOrDefault(pageIndex) ?? [];
            // Reading the file's own annotations below needs the page as the file has it, so it is turned at the end.
            var geometryInFile = PageGeometry.FromPage(page);
            var markupsFromFile = markups.Where(markup => markup.Origin != null).ToDictionary(markup => markup.Origin!.AnnotationKey);

            if (page.Elements.GetObject("/Annots") is PdfArray annotations)
            {
                var removedAnnotations = new HashSet<PdfDictionary>();
                for (int i = annotations.Elements.Count - 1; i >= 0; i--)
                {
                    if (Resolve(annotations.Elements[i]) is not { } annotation) continue;
                    if (AnnotationReader.TryRead(annotation, geometryInFile) == null) continue; // not ours to touch

                    if (!markupsFromFile.TryGetValue(AnnotationKey(annotations.Elements[i], pageIndex, i), out var markup))
                    {
                        removedAnnotations.Add(annotation);
                        annotations.Elements.RemoveAt(i);
                    }
                    else if (markup.Fingerprint() != markup.Origin!.Fingerprint)
                    {
                        AnnotationWriter.Update(document, annotation, markup, geometry);
                    }
                }
                RemoveDependents(annotations, removedAnnotations);
            }

            foreach (var markup in markups.Where(markup => markup.Origin == null))
                AnnotationsOf(page).Elements.Add(AnnotationWriter.Create(document, markup, geometry).Reference!);

            // shortcut: a page that already has viewports (Bluebeam's) keeps them as they are, so a scale set here isn't saved on it; write into them if that matters.
            if (loaded.MmPerPointByPage.TryGetValue(pageIndex, out double mmPerPoint) && !page.Elements.ContainsKey("/VP"))
            {
                var viewport = new PdfDictionary(document)
                {
                    Elements =
                    {
                        ["/Type"] = new PdfName("/Viewport"),
                        ["/BBox"] = new PdfArray(document, new PdfReal(geometry.Left), new PdfReal(geometry.Bottom), new PdfReal(geometry.Right), new PdfReal(geometry.Top)),
                        ["/Name"] = new PdfString("Sheet"),
                        ["/Measure"] = MeasureDictionary.Create(document, mmPerPoint),
                    },
                };
                page.Elements["/VP"] = new PdfArray(document, viewport);
            }
            WriteRotation(page, geometry);
        }

        // Write next to the target and swap at the end, so a failed save never damages an existing file.
        string temporaryPath = targetPath + ".saving";
        try
        {
            document.Save(temporaryPath);
            File.Move(temporaryPath, targetPath, overwrite: true);
        }
        catch
        {
            File.Delete(temporaryPath);
            throw;
        }
    }

    /// <summary>Gives the page the rotation the user turned it to, if that isn't the file's.</summary>
    static void WriteRotation(PdfPage page, PageGeometry geometry)
    {
        if (PageGeometry.FromPage(page).Rotation != geometry.Rotation) page.Elements["/Rotate"] = new PdfInteger(geometry.Rotation);
    }

    /// <summary>
    /// The scale the page's viewports have, or null if they have none, or different ones (a 1:100 plan beside a 1:20
    /// detail): one page-wide scale would then measure one of them wrong, so the user sets the scale instead.
    /// </summary>
    static double? ViewportScale(PdfPage page)
    {
        if (page.Elements.GetObject("/VP") is not PdfArray viewports) return null;
        var scales = viewports.Elements.Select(item => Resolve(item) is PdfDictionary viewport ? MeasureDictionary.MmPerPoint(viewport.Elements["/Measure"]) : null)
            .OfType<double>().ToList();
        return scales.Count > 0 && scales.All(scale => Math.Abs(scale - scales[0]) <= scales[0] * 1e-6) ? scales[0] : null;
    }

    /// <summary>Reads the editable annotations on a page into markups. Each markup remembers which annotation it came from.</summary>
    static List<Markup> ReadEditableMarkups(PdfPage page, int pageIndex, PageGeometry geometry)
    {
        var markups = new List<Markup>();
        if (page.Elements.GetObject("/Annots") is not PdfArray annotations) return markups;

        for (int i = 0; i < annotations.Elements.Count; i++)
        {
            if (Resolve(annotations.Elements[i]) is not { } annotation) continue;
            if (AnnotationReader.TryRead(annotation, geometry) is not { } markup) continue;
            var rect = annotation.Elements.GetRectangle("/Rect");
            var bounds = new PdfRect(Math.Min(rect.X1, rect.X2), Math.Min(rect.Y1, rect.Y2), Math.Max(rect.X1, rect.X2), Math.Max(rect.Y1, rect.Y2));
            markup.Origin = new AnnotationOrigin(AnnotationKey(annotations.Elements[i], pageIndex, i), markup.Fingerprint(), bounds);
            markups.Add(markup);
        }
        return markups;
    }

    /// <summary>
    /// Identifies an annotation in the original file: its object number, or its place in the page's list if it has none.
    /// Load and Save both read the same original bytes, so the key finds the same annotation.
    /// </summary>
    static string AnnotationKey(PdfItem item, int pageIndex, int indexInPage) =>
        item is PdfReference reference ? $"object {reference.ObjectID}" : $"page {pageIndex} item {indexInPage}";

    /// <summary>Removes the pop-ups and replies of deleted annotations, and replies to those replies.</summary>
    static void RemoveDependents(PdfArray annotations, HashSet<PdfDictionary> removedAnnotations)
    {
        bool removedAny = true;
        while (removedAny)
        {
            removedAny = false;
            for (int i = annotations.Elements.Count - 1; i >= 0; i--)
            {
                if (Resolve(annotations.Elements[i]) is not { } annotation) continue;
                bool isPopupOfRemoved = annotation.Elements.GetObject("/Parent") is PdfDictionary parent && removedAnnotations.Contains(parent);
                bool isReplyToRemoved = annotation.Elements.GetObject("/IRT") is PdfDictionary repliedTo && removedAnnotations.Contains(repliedTo);
                if (!isPopupOfRemoved && !isReplyToRemoved) continue;
                removedAnnotations.Add(annotation);
                annotations.Elements.RemoveAt(i);
                removedAny = true;
            }
        }
    }

    static PdfArray AnnotationsOf(PdfPage page)
    {
        if (page.Elements.GetObject("/Annots") is PdfArray existing) return existing;
        var annotations = new PdfArray(page.Owner);
        page.Elements["/Annots"] = annotations;
        return annotations;
    }

    static PdfDictionary? Resolve(PdfItem item) => item switch
    {
        PdfReference reference => reference.Value as PdfDictionary,
        PdfDictionary dictionary => dictionary,
        _ => null,
    };

    /// <summary>Opens a PDF for changing. Never prompts for a password: if one is needed, it gives up and calls <paramref name="onPasswordNeeded"/>.</summary>
    static PdfDocument OpenForEditing(byte[] pdfBytes, Action? onPasswordNeeded = null)
    {
        var document = PdfReader.Open(new MemoryStream(pdfBytes), PdfDocumentOpenMode.Modify, passwordRequest =>
        {
            onPasswordNeeded?.Invoke();
            passwordRequest.Abort = true;
        });
        // PDFsharp returns null instead of throwing when the password request is aborted.
        return document ?? throw new InvalidOperationException("A password is needed to change this file.");
    }

    static byte[] SaveToBytes(PdfDocument document)
    {
        using var stream = new MemoryStream();
        document.Save(stream);
        return stream.ToArray();
    }
}
