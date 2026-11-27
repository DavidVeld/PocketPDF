namespace PurpleStreamPDF.Core.Tests;

/// <summary>Markups made in other viewers must survive a save, whether they were left alone, changed or deleted.</summary>
public class ColleagueMarkupTests
{
    [Fact]
    public void Text_box_wrapped_by_another_viewer_reads_as_lines_that_fit_its_box()
    {
        const string contents = "Final column locations are subject to coordination with architects to avoid clash";
        using var source = TestPdf.CreateWithColleagueMarkup(contents); // a 150 pt wide box, 12 pt Helvetica

        var note = Assert.IsType<TextMarkup>(Assert.Single(MarkupFile.Load(source.FilePath).MarkupsByPage[0]));

        var lines = TextLayout.Lines(note.Text);
        Assert.True(lines.Length > 1);
        Assert.All(lines, line => Assert.True(TextLayout.LineWidth(line, 12) <= 151, line));
        Assert.Equal(contents, string.Join(' ', lines));
        Assert.True(note.IsUnchangedFromFile); // only read differently: saving leaves the colleague's note as it was
    }

    [Fact]
    public void Untouched_markup_keeps_everything_when_something_else_is_added()
    {
        using var source = TestPdf.CreateWithColleagueMarkup();
        using var saved = new TestPdf();
        var loaded = MarkupFile.Load(source.FilePath);
        var before = TestPdf.AnnotationsByName(source.FilePath, 0)["colleague-note"];

        loaded.MarkupsByPage[0].Add(new CloudMarkup { Vertices = [new(100, 100), new(200, 100), new(150, 200)] });
        MarkupFile.Save(loaded, loaded.MarkupsByPage, saved.FilePath);

        var after = TestPdf.AnnotationsByName(saved.FilePath, 0);
        Assert.Equal(before.Keys.Order(), after["colleague-note"].Keys.Order());
        Assert.Equal(before["/RC"], after["colleague-note"]["/RC"]);
        Assert.Equal(before["/BSIColumnData"], after["colleague-note"]["/BSIColumnData"]);
        Assert.Contains("colleague-popup", after.Keys);
        Assert.Contains("colleague-reply", after.Keys);
        Assert.Equal(4, after.Count); // note, pop-up, reply and the new cloud
    }

    [Fact]
    public void Changed_markup_keeps_its_id_author_and_extras_but_drops_the_old_rich_text()
    {
        using var source = TestPdf.CreateWithColleagueMarkup();
        using var saved = new TestPdf();
        var loaded = MarkupFile.Load(source.FilePath);

        var note = Assert.IsType<TextMarkup>(Assert.Single(loaded.MarkupsByPage[0]));
        note.Text = "Changed by me";
        note.MoveBy(10, 0);
        MarkupFile.Save(loaded, loaded.MarkupsByPage, saved.FilePath);

        var after = TestPdf.AnnotationsByName(saved.FilePath, 0);
        var savedNote = after["colleague-note"];
        Assert.Contains("Colleague", savedNote["/T"]);
        Assert.Contains("Text Box", savedNote["/Subj"]);
        Assert.True(savedNote.ContainsKey("/BSIColumnData"));
        Assert.False(savedNote.ContainsKey("/RC"));
        Assert.Contains("colleague-reply", after.Keys);

        var reloadedNote = Assert.IsType<TextMarkup>(Assert.Single(MarkupFile.Load(saved.FilePath).MarkupsByPage[0]));
        Assert.Equal("Changed by me", reloadedNote.Text);
    }

    [Fact]
    public void Deleted_markup_takes_its_popup_and_replies_with_it()
    {
        using var source = TestPdf.CreateWithColleagueMarkup();
        using var saved = new TestPdf();
        var loaded = MarkupFile.Load(source.FilePath);

        loaded.MarkupsByPage[0].Clear();
        MarkupFile.Save(loaded, loaded.MarkupsByPage, saved.FilePath);

        Assert.Empty(TestPdf.AnnotationsByName(saved.FilePath, 0));
    }
}
