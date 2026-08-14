using ChessMentor.Chess;
using ChessMentor.CourseBuilder;
using ChessMentor.Persistence;

namespace ChessMentor.Tests;

public sealed class CourseBuilderTests
{
    [Fact]
    public void DeleteDuplicateReorderUndoAndRedoPreserveStableBlocks()
    {
        var editor = new CourseDocumentEditor(CourseBuilderDocument.Create("آموزش تست"));
        var text = editor.Add(CourseBlockKind.Text, text: "توضیح اول");
        var position = editor.Add(CourseBlockKind.Position, fen: FenPosition.Initial);

        var copy = editor.Duplicate(text.Id);
        Assert.NotEqual(text.Id, copy.Id);
        Assert.Equal(3, editor.Current.Blocks.Count);
        Assert.True(editor.Move(copy.Id, 0));
        Assert.Equal(copy.Id, editor.Current.Blocks[0].Id);
        Assert.True(editor.Delete(position.Id));
        Assert.DoesNotContain(editor.Current.Blocks, block => block.Id == position.Id);

        Assert.True(editor.Undo());
        Assert.Contains(editor.Current.Blocks, block => block.Id == position.Id);
        Assert.True(editor.Redo());
        Assert.DoesNotContain(editor.Current.Blocks, block => block.Id == position.Id);
    }

    [Fact]
    public void PlayAndLegoAttachmentSurviveRoundTripAndCompileAsOneStage()
    {
        var editor = new CourseDocumentEditor(CourseBuilderDocument.Create());
        var target = editor.Add(CourseBlockKind.InteractiveMove, fen: FenPosition.Initial);
        var firstText = editor.Add(CourseBlockKind.Text, text: "متن متصل اول");
        var secondText = editor.Add(CourseBlockKind.Text, text: "متن متصل دوم");
        Assert.True(editor.SetAutoAdvance(target.Id));
        Assert.True(editor.AttachText(firstText.Id, target.Id));
        Assert.True(editor.AttachText(secondText.Id, target.Id));

        var reopened = CourseBuilderJson.Deserialize(CourseBuilderJson.Serialize(editor.Current));
        var targetAfterReopen = Assert.Single(reopened.Blocks, block => block.Id == target.Id);
        Assert.Equal(2d, targetAfterReopen.AutoAdvanceSeconds);
        var stage = Assert.Single(CourseStageCompiler.Compile(reopened));
        Assert.Equal(target.Id, stage.Id);
        Assert.Equal(2, stage.AttachedTexts.Count);

        var reopenedEditor = new CourseDocumentEditor(reopened);
        Assert.True(reopenedEditor.DetachText(firstText.Id));
        var stagesAfterDetach = CourseStageCompiler.Compile(reopenedEditor.Current);
        Assert.Equal(2, stagesAfterDetach.Count);
        Assert.Single(stagesAfterDetach.Single(item => item.Id == target.Id).AttachedTexts);
        Assert.Contains(stagesAfterDetach, item => item.Id == firstText.Id);
    }

    [Fact]
    public void StageContainerRemovesMembersFromIndependentProgress()
    {
        var editor = new CourseDocumentEditor(CourseBuilderDocument.Create());
        var position = editor.Add(CourseBlockKind.Position, fen: FenPosition.Initial);
        var hint = editor.Add(CourseBlockKind.Hint, text: "راهنما");
        var stage = editor.Add(CourseBlockKind.Stage);
        Assert.True(editor.SetStageMembers(stage.Id, [position.Id, hint.Id]));

        var compiled = Assert.Single(CourseStageCompiler.Compile(editor.Current));
        Assert.Equal(stage.Id, compiled.Id);
        Assert.Equal([position.Id, hint.Id], compiled.Members.Select(static block => block.Id));
    }

    [Fact]
    public async Task SaveReopenAndRevisionsKeepPlayAndLegoData()
    {
        var token = TestContext.Current.CancellationToken;
        var path = TemporaryDatabasePath();
        await using var database = new AppDatabase(path);
        await database.InitializeAsync(token);
        var repository = new CourseBuilderRepository(database);
        var editor = new CourseDocumentEditor(CourseBuilderDocument.Create("دوره پایدار"));
        var target = editor.Add(CourseBlockKind.Position, fen: FenPosition.Initial);
        var text = editor.Add(CourseBlockKind.Text, text: "همزمان با برد");
        editor.AttachText(text.Id, target.Id);
        editor.SetAutoAdvance(target.Id, 3.5);

        Assert.Equal(1, await repository.SaveAsync(editor.Current, "explicit-save", cancellationToken: token));
        editor.Rename("دوره ویرایش‌شده");
        Assert.Equal(2, await repository.SaveAsync(editor.Current, "autosave", cancellationToken: token));

        var reopened = await repository.GetAsync(editor.Current.Id, token);
        Assert.NotNull(reopened);
        Assert.Equal("دوره ویرایش‌شده", reopened.Title);
        Assert.Equal(target.Id, reopened.Blocks.Single(block => block.Id == text.Id).AttachedToBlockId);
        Assert.Equal(3.5, reopened.Blocks.Single(block => block.Id == target.Id).AutoAdvanceSeconds);
        var revisions = await repository.RevisionsAsync(editor.Current.Id, token);
        Assert.Equal([2, 1], revisions.Select(static revision => revision.Revision));
        Assert.Equal(["autosave", "explicit-save"], revisions.Select(static revision => revision.Reason));
    }

    private static string TemporaryDatabasePath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ChessMentor.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "course-builder.db");
    }
}
