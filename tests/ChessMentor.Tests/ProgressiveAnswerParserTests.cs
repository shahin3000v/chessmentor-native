using ChessMentor.Viewer;

namespace ChessMentor.Tests;

public sealed class ProgressiveAnswerParserTests
{
    [Fact]
    public void NestedAnswerMarkersPreserveEveryCharacterInOrder()
    {
        var parsed = ProgressiveAnswerParser.Parse("پرسش. پاسخ: جواب اول پاسخ : جواب دوم");

        Assert.Equal("پرسش. ", parsed.Before);
        Assert.Equal(2, parsed.Sections.Count);
        Assert.Equal("پاسخ:", parsed.Sections[0].Label);
        Assert.Equal(" جواب اول ", parsed.Sections[0].Content);
        Assert.Equal("پاسخ :", parsed.Sections[1].Label);
        Assert.Equal(" جواب دوم", parsed.Sections[1].Content);
    }

    [Fact]
    public void PersianWordStartingWithAnswerIsNotTreatedAsAMarker()
    {
        var parsed = ProgressiveAnswerParser.Parse("پاسخگویی یک مهارت است؛ پاسخ: نمونه");

        Assert.Equal("پاسخگویی یک مهارت است؛ ", parsed.Before);
        Assert.Single(parsed.Sections);
    }
}
