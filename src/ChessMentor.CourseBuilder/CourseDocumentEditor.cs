using ChessMentor.Core;

namespace ChessMentor.CourseBuilder;

public sealed class CourseDocumentEditor
{
    private readonly Stack<CourseBuilderDocument> _undo = new();
    private readonly Stack<CourseBuilderDocument> _redo = new();
    private long _identityCounter;

    public CourseDocumentEditor(CourseBuilderDocument document) => Current = document.Normalize();

    public event EventHandler? Changed;

    public CourseBuilderDocument Current { get; private set; }
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public CourseBlock Add(CourseBlockKind kind, CourseSourceReference? source = null, string text = "", string? fen = null)
    {
        var block = new CourseBlock(
            NextId(),
            kind,
            Title: DefaultTitle(kind),
            Text: text,
            Fen: fen,
            Source: source).Normalize();
        Mutate(document => document with { Blocks = [.. document.Blocks, block] });
        return block;
    }

    public bool Delete(string blockId) => MutateIfChanged(document =>
    {
        if (!document.Blocks.Any(block => block.Id == blockId))
        {
            return document;
        }

        var blocks = document.Blocks
            .Where(block => block.Id != blockId)
            .Select(block => block with
            {
                AttachedToBlockId = block.AttachedToBlockId == blockId ? null : block.AttachedToBlockId,
                StageMemberIds = block.StageMemberIds!.Where(member => member != blockId).ToArray(),
            })
            .ToArray();
        return document with { Blocks = blocks };
    });

    public CourseBlock Duplicate(string blockId)
    {
        var source = RequireBlock(blockId);
        var index = IndexOf(blockId);
        var copy = source with
        {
            Id = NextId(),
            Title = string.IsNullOrWhiteSpace(source.Title) ? "کپی" : $"{source.Title} — کپی",
            AttachedToBlockId = null,
            StageMemberIds = Array.Empty<string>(),
        };
        Mutate(document => document with
        {
            Blocks = document.Blocks.Take(index + 1).Append(copy).Concat(document.Blocks.Skip(index + 1)).ToArray(),
        });
        return copy;
    }

    public bool Move(string blockId, int newIndex)
    {
        var oldIndex = IndexOf(blockId);
        if (oldIndex < 0)
        {
            return false;
        }

        newIndex = Math.Clamp(newIndex, 0, Current.Blocks.Count - 1);
        if (oldIndex == newIndex)
        {
            return false;
        }

        return MutateIfChanged(document =>
        {
            var blocks = document.Blocks.ToList();
            var block = blocks[oldIndex];
            blocks.RemoveAt(oldIndex);
            blocks.Insert(newIndex, block);
            return document with { Blocks = blocks };
        });
    }

    public bool Replace(CourseBlock block) => MutateIfChanged(document =>
    {
        var index = IndexOf(block.Id);
        if (index < 0)
        {
            return document;
        }

        var blocks = document.Blocks.ToArray();
        blocks[index] = block.Normalize();
        return document with { Blocks = blocks };
    });

    public bool Rename(string title) => MutateIfChanged(document => document with { Title = title });

    public bool SetAutoAdvance(string blockId, double? seconds = 2) =>
        Update(blockId, block => block with { AutoAdvanceSeconds = seconds });

    public bool AttachText(string textBlockId, string targetBlockId)
    {
        var text = RequireBlock(textBlockId);
        var target = RequireBlock(targetBlockId);
        if (text.Kind != CourseBlockKind.Text || target.Kind == CourseBlockKind.Text || text.Id == target.Id)
        {
            return false;
        }

        return Update(textBlockId, block => block with { AttachedToBlockId = targetBlockId });
    }

    public bool DetachText(string textBlockId) =>
        Update(textBlockId, block => block with { AttachedToBlockId = null });

    public bool SetStageMembers(string stageBlockId, IEnumerable<string> memberIds)
    {
        var stage = RequireBlock(stageBlockId);
        if (stage.Kind != CourseBlockKind.Stage)
        {
            return false;
        }

        var valid = memberIds
            .Where(id => id != stageBlockId && Current.Blocks.Any(block => block.Id == id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return Update(stageBlockId, block => block with { StageMemberIds = valid });
    }

    public bool Undo()
    {
        if (!_undo.TryPop(out var previous))
        {
            return false;
        }

        _redo.Push(Current);
        Current = previous;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool Redo()
    {
        if (!_redo.TryPop(out var next))
        {
            return false;
        }

        _undo.Push(Current);
        Current = next;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private bool Update(string blockId, Func<CourseBlock, CourseBlock> update)
    {
        var block = RequireBlock(blockId);
        return Replace(update(block));
    }

    private void Mutate(Func<CourseBuilderDocument, CourseBuilderDocument> update)
    {
        _ = MutateIfChanged(update);
    }

    private bool MutateIfChanged(Func<CourseBuilderDocument, CourseBuilderDocument> update)
    {
        var candidate = update(Current).Normalize();
        if (CourseBuilderJson.Serialize(candidate) == CourseBuilderJson.Serialize(Current))
        {
            return false;
        }

        var next = candidate with { UpdatedUtc = DateTimeOffset.UtcNow };
        _undo.Push(Current);
        _redo.Clear();
        Current = next;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private CourseBlock RequireBlock(string blockId) =>
        Current.Blocks.FirstOrDefault(block => block.Id == blockId) ??
        throw new KeyNotFoundException($"Course block '{blockId}' was not found.");

    private int IndexOf(string blockId)
    {
        for (var index = 0; index < Current.Blocks.Count; index++)
        {
            if (Current.Blocks[index].Id == blockId)
            {
                return index;
            }
        }

        return -1;
    }

    private string NextId() => StableId.Create("course-block", Current.Id, ++_identityCounter, Guid.NewGuid());

    private static string DefaultTitle(CourseBlockKind kind) => kind switch
    {
        CourseBlockKind.Text => "متن",
        CourseBlockKind.Position => "موقعیت",
        CourseBlockKind.InteractiveMove => "حرکت تعاملی",
        CourseBlockKind.MoveSequence => "دنباله حرکات",
        CourseBlockKind.Variation => "شاخه",
        CourseBlockKind.Hint => "راهنما",
        CourseBlockKind.Audio => "صدا",
        CourseBlockKind.Stage => "Stage",
        CourseBlockKind.Checkpoint => "Checkpoint",
        _ => kind.ToString(),
    };
}
