namespace PurpleStreamPDF.Core.Tests;

public class UndoHistoryTests
{
    static CloudMarkup Cloud(double x) => new() { Vertices = [new(x, 0), new(x + 10, 0), new(x + 5, 10)] };

    [Fact]
    public void Clone_is_independent_and_stays_unchanged_from_file()
    {
        using var source = TestPdf.CreateWithColleagueMarkup();
        var note = Assert.Single(MarkupFile.Load(source.FilePath).MarkupsByPage[0]);
        var copy = note.Clone();
        Assert.True(copy.IsUnchangedFromFile);

        copy.MoveBy(5, 5);
        Assert.False(copy.IsUnchangedFromFile);
        Assert.True(note.IsUnchangedFromFile);

        var cloud = Cloud(0);
        var cloudCopy = (CloudMarkup)cloud.Clone();
        cloudCopy.MoveBy(1, 1);
        Assert.Equal(new PdfPoint(0, 0), cloud.Vertices[0]);
    }

    [Fact]
    public void A_change_to_several_pages_is_one_step()
    {
        var page0 = new List<Markup> { Cloud(0) };
        var page1 = new List<Markup> { Cloud(100) };
        var history = new UndoHistory();
        history.Reset(new Dictionary<int, List<Markup>> { [0] = page0, [1] = page1 });

        page0.Clear();
        page1.Clear();
        history.Commit([1, 0]);

        Assert.Equal(1, history.Undo()); // the first page listed
        Assert.Single(page0);
        Assert.Single(page1);
        Assert.False(history.CanUndo);
        history.Redo();
        Assert.Empty(page0);
        Assert.Empty(page1);
    }

    [Fact]
    public void An_arrowed_line_stops_inside_its_head()
    {
        var (from, tip) = (new ViewPoint(0, 0), new ViewPoint(100, 0));
        var end = LabelGeometry.ArrowLineEnd(from, tip, lineWidth: 3);
        var head = LabelGeometry.ArrowHead(from, tip, lineWidth: 3);
        Assert.True(end.X < tip.X && end.X > head[1].X); // between the head's base and its point
        Assert.Equal(from, LabelGeometry.ArrowLineEnd(from, new ViewPoint(1, 0), lineWidth: 3)); // shorter than the head: no line
    }

    [Fact]
    public void Undo_and_redo_go_back_and_forth_across_pages_changing_the_lists_in_place()
    {
        var page0 = new List<Markup>();
        var page1 = new List<Markup>();
        var history = new UndoHistory();
        history.Reset(new Dictionary<int, List<Markup>> { [0] = page0, [1] = page1 });

        page0.Add(Cloud(0));
        history.Commit(0);
        page1.Add(Cloud(100));
        history.Commit(1);
        page0[0].MoveBy(50, 0);
        history.Commit(0);

        Assert.Equal(0, history.Undo());
        Assert.Equal(new PdfPoint(0, 0), ((CloudMarkup)page0[0]).Vertices[0]);
        Assert.Equal(1, history.Undo());
        Assert.Empty(page1);
        Assert.Equal(0, history.Undo());
        Assert.Empty(page0);
        Assert.False(history.CanUndo);

        Assert.Equal(0, history.Redo());
        Assert.Equal(1, history.Redo());
        Assert.Equal(0, history.Redo());
        Assert.Equal(new PdfPoint(50, 0), ((CloudMarkup)page0[0]).Vertices[0]);
        Assert.Single(page1);
        Assert.False(history.CanRedo);
    }

    [Fact]
    public void Editing_a_restored_markup_does_not_change_the_stored_step()
    {
        var page = new List<Markup>();
        var history = new UndoHistory();
        history.Reset(new Dictionary<int, List<Markup>> { [0] = page });
        page.Add(Cloud(0));
        history.Commit(0);
        history.Undo();
        history.Redo();

        page[0].MoveBy(99, 0); // not committed yet, like a drag in progress
        history.Undo();
        history.Redo();
        Assert.Equal(new PdfPoint(0, 0), ((CloudMarkup)page[0]).Vertices[0]);
    }

    [Fact]
    public void A_new_change_clears_redo()
    {
        var page = new List<Markup>();
        var history = new UndoHistory();
        history.Reset(new Dictionary<int, List<Markup>> { [0] = page });
        page.Add(Cloud(0));
        history.Commit(0);
        history.Undo();
        page.Add(Cloud(20));
        history.Commit(0);
        Assert.False(history.CanRedo);
    }

    [Fact]
    public void Save_point_is_found_again_after_undo_and_redo_but_not_after_a_branch()
    {
        var page = new List<Markup>();
        var history = new UndoHistory();
        history.Reset(new Dictionary<int, List<Markup>> { [0] = page });
        Assert.True(history.IsAtSavePoint);

        page.Add(Cloud(0));
        history.Commit(0);
        history.MarkSaved();
        page.Add(Cloud(20));
        history.Commit(0);
        Assert.False(history.IsAtSavePoint);
        history.Undo();
        Assert.True(history.IsAtSavePoint);
        history.Undo();
        Assert.False(history.IsAtSavePoint);
        history.Redo();
        Assert.True(history.IsAtSavePoint);

        history.Undo(); // back to empty, then a different change: the saved state can't be reached any more
        page.Add(Cloud(40));
        history.Commit(0);
        history.Undo();
        Assert.False(history.IsAtSavePoint);
    }

    [Fact]
    public void Undoing_a_change_to_a_file_markup_saves_it_untouched()
    {
        using var source = TestPdf.CreateWithColleagueMarkup();
        using var saved = new TestPdf();
        var loaded = MarkupFile.Load(source.FilePath);
        var before = TestPdf.AnnotationsByName(source.FilePath, 0)["colleague-note"];
        var history = new UndoHistory();
        history.Reset(loaded.MarkupsByPage);

        ((TextMarkup)loaded.MarkupsByPage[0][0]).Text = "Changed";
        history.Commit(0);
        history.Undo();
        Assert.Empty(MarkupFile.AnnotationsToHide(loaded));

        MarkupFile.Save(loaded, loaded.MarkupsByPage, saved.FilePath);
        Assert.Equal(before, TestPdf.AnnotationsByName(saved.FilePath, 0)["colleague-note"]);
    }
}
