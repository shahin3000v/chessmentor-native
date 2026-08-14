namespace ChessMentor.CourseBuilder;

public sealed record CourseStagePreview(
    string Id,
    IReadOnlyList<CourseBlock> Members,
    IReadOnlyList<CourseBlock> AttachedTexts,
    double? AutoAdvanceSeconds);

public static class CourseStageCompiler
{
    public static IReadOnlyList<CourseStagePreview> Compile(CourseBuilderDocument source)
    {
        var document = source.Normalize();
        var byId = document.Blocks.ToDictionary(static block => block.Id, StringComparer.Ordinal);
        var attached = document.Blocks
            .Where(static block => block.Kind == CourseBlockKind.Text && block.AttachedToBlockId is not null)
            .GroupBy(static block => block.AttachedToBlockId!, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => (IReadOnlyList<CourseBlock>)group.ToArray(), StringComparer.Ordinal);
        var stageMembers = document.Blocks
            .Where(static block => block.Kind == CourseBlockKind.Stage)
            .SelectMany(static block => block.StageMemberIds!)
            .ToHashSet(StringComparer.Ordinal);
        var stages = new List<CourseStagePreview>();

        foreach (var block in document.Blocks)
        {
            if (block.AttachedToBlockId is not null || stageMembers.Contains(block.Id))
            {
                continue;
            }

            if (block.Kind == CourseBlockKind.Stage)
            {
                var members = block.StageMemberIds!
                    .Select(id => byId.GetValueOrDefault(id))
                    .Where(static member => member is not null && member.AttachedToBlockId is null)
                    .Cast<CourseBlock>()
                    .ToArray();
                var texts = members
                    .SelectMany(member => attached.GetValueOrDefault(member.Id) ?? Array.Empty<CourseBlock>())
                    .ToArray();
                stages.Add(new CourseStagePreview(block.Id, members, texts, block.AutoAdvanceSeconds));
                continue;
            }

            stages.Add(new CourseStagePreview(
                block.Id,
                [block],
                attached.GetValueOrDefault(block.Id) ?? Array.Empty<CourseBlock>(),
                block.AutoAdvanceSeconds));
        }

        return stages;
    }
}
