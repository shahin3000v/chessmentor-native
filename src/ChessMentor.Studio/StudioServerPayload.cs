using System.Text;
using System.Text.Json;
using ChessMentor.Pgn;
using ChessMentor.Viewer;

namespace ChessMentor.Studio;

public static class StudioServerPayload
{
    public static StudioServerWorkspace Read(JsonElement workspacePayload)
    {
        if (!workspacePayload.TryGetProperty("games", out var games) || games.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Server workspace has no games array.");
        }

        var identities = new List<PgnExternalGameIdentity>();
        var links = new List<StudioTranslationLink>();
        var index = 0;
        foreach (var game in games.EnumerateArray())
        {
            if (!game.TryGetProperty("root", out var root) || root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("Server game has no root node.");
            }

            var rootIdentity = ReadNodeIdentity(root);
            var gameId = Text(game, "id");
            if (string.IsNullOrWhiteSpace(gameId))
            {
                gameId = $"server-game-{index}-{rootIdentity.NodeId}";
            }

            identities.Add(new PgnExternalGameIdentity(gameId, rootIdentity));
            ReadTranslationLinks(gameId, root, links);
            index++;
        }

        return new StudioServerWorkspace(ToPgn(workspacePayload), identities, links);
    }

    public static JsonElement Build(
        IReadOnlyList<ViewerGameItem> games,
        IReadOnlyDictionary<StudioCommentKey, StudioTranslationLink> translationLinks)
    {
        ArgumentNullException.ThrowIfNull(games);
        ArgumentNullException.ThrowIfNull(translationLinks);
        var payload = new
        {
            games = games.Select((item, index) => new
            {
                id = item.Game.Id,
                index,
                title = item.FullTitle,
                headers = item.Game.Headers
                    .GroupBy(static header => header.Name, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(static group => group.Last().Name, static group => group.Last().Value),
                headerEntries = item.Game.Headers.Select(static header => new
                {
                    name = header.Name,
                    value = header.Value,
                }).ToArray(),
                root = BuildNode(item.Game.Id, item.Game.Root, translationLinks),
                errorCount = 0,
                errors = Array.Empty<string>(),
            }).ToArray(),
            gameCount = games.Count,
            errors = Array.Empty<string>(),
        };
        return JsonSerializer.SerializeToElement(payload, SerializerOptions);
    }

    public static string ToPgn(JsonElement workspacePayload)
    {
        if (!workspacePayload.TryGetProperty("games", out var games) || games.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Server draft has no games array.");
        }

        var rendered = new List<string>();
        foreach (var game in games.EnumerateArray())
        {
            rendered.Add(RenderGame(game));
        }

        if (rendered.Count == 0)
        {
            throw new InvalidDataException("Server draft contains no games.");
        }

        return string.Join(Environment.NewLine + Environment.NewLine, rendered) + Environment.NewLine;
    }

    private static object BuildNode(
        string gameId,
        PgnMoveNode node,
        IReadOnlyDictionary<StudioCommentKey, StudioTranslationLink> links)
    {
        var startingComment = PgnTreeEditor.StartingCommentText(node);
        var comment = PgnTreeEditor.CommentText(node);
        _ = links.TryGetValue(new StudioCommentKey(gameId, node.StableId, "startingComment"), out var startingLink);
        _ = links.TryGetValue(new StudioCommentKey(gameId, node.StableId, "comment"), out var commentLink);
        var fields = (node.Fen ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return new
        {
            id = node.StableId,
            san = node.RawSan,
            uci = node.Uci ?? string.Empty,
            ply = node.Ply,
            isWhiteMove = node.IsWhiteMove,
            fullmoveNumber = node.FullmoveNumber,
            fen = node.Fen ?? string.Empty,
            turn = fields.Length > 1 && fields[1] == "b" ? "black" : "white",
            startingComment,
            startingCommentSourceHash = startingLink?.SourceHash ?? string.Empty,
            startingCommentSourceText = startingLink?.SourceText ?? string.Empty,
            comment,
            commentSourceHash = commentLink?.SourceHash ?? string.Empty,
            commentSourceText = commentLink?.SourceText ?? string.Empty,
            annotations = node.Annotations.ToArray(),
            nags = node.Nags.ToArray(),
            forceMoveNumber = node.ForceMoveNumber ||
                node.MoveNumberToken?.RawText.Contains("...", StringComparison.Ordinal) == true,
            children = node.Children.Select(child => BuildNode(gameId, child, links)).ToArray(),
        };
    }

    private static string RenderGame(JsonElement game)
    {
        if (!game.TryGetProperty("root", out var root) || root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Server game has no root node.");
        }

        var output = new StringBuilder();
        var result = "*";
        if (game.TryGetProperty("headerEntries", out var entries) && entries.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in entries.EnumerateArray())
            {
                var name = Text(entry, "name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var value = Text(entry, "value");
                if (string.Equals(name, "Result", StringComparison.OrdinalIgnoreCase))
                {
                    result = value;
                }

                output.Append('[').Append(name).Append(" \"")
                    .Append(EscapeHeader(value)).AppendLine("\"]");
            }
        }
        else if (game.TryGetProperty("headers", out var headers) && headers.ValueKind == JsonValueKind.Object)
        {
            foreach (var header in headers.EnumerateObject())
            {
                var value = header.Value.GetString() ?? string.Empty;
                if (string.Equals(header.Name, "Result", StringComparison.OrdinalIgnoreCase))
                {
                    result = value;
                }

                output.Append('[').Append(header.Name).Append(" \"")
                    .Append(EscapeHeader(value)).AppendLine("\"]");
            }
        }

        output.AppendLine();
        var tokens = new List<string>();
        AddComment(tokens, Text(root, "startingComment"));
        AddComment(tokens, Text(root, "comment"));
        RenderPosition(tokens, root, forceFirstNumber: true);
        tokens.Add(result);
        output.Append(string.Join(' ', tokens));
        return output.ToString();
    }

    private static void RenderPosition(List<string> tokens, JsonElement parent, bool forceFirstNumber)
    {
        if (!parent.TryGetProperty("children", out var children) ||
            children.ValueKind != JsonValueKind.Array ||
            children.GetArrayLength() == 0)
        {
            return;
        }

        var mainline = children[0];
        RenderMove(tokens, mainline, parent, forceFirstNumber);
        for (var index = 1; index < children.GetArrayLength(); index++)
        {
            var branchTokens = new List<string>();
            RenderMove(branchTokens, children[index], parent, forceNumber: true);
            RenderPosition(branchTokens, children[index], forceFirstNumber: false);
            tokens.Add("(" + string.Join(' ', branchTokens) + ")");
        }

        RenderPosition(tokens, mainline, children.GetArrayLength() > 1);
    }

    private static void RenderMove(
        List<string> tokens,
        JsonElement node,
        JsonElement parent,
        bool forceNumber)
    {
        AddComment(tokens, Text(node, "startingComment"));
        var ply = node.TryGetProperty("ply", out var plyValue) && plyValue.TryGetInt32(out var parsedPly)
            ? parsedPly
            : throw new InvalidDataException("Server move has no valid ply.");
        var isWhiteMove = node.TryGetProperty("isWhiteMove", out var isWhiteValue) &&
                          isWhiteValue.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? isWhiteValue.GetBoolean()
            : ply % 2 == 1;
        var number = node.TryGetProperty("fullmoveNumber", out var numberValue) &&
                     numberValue.TryGetInt32(out var parsedNumber) && parsedNumber > 0
            ? parsedNumber
            : Math.Max(1, (ply + 1) / 2);
        var parentFen = Text(parent, "fen").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (!(node.TryGetProperty("isWhiteMove", out _) &&
              node.TryGetProperty("fullmoveNumber", out _)) && parentFen.Length > 1)
        {
            isWhiteMove = string.Equals(parentFen[1], "w", StringComparison.Ordinal);
            if (parentFen.Length > 5 && int.TryParse(
                    parentFen[5],
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var parentNumber) && parentNumber > 0)
            {
                number = parentNumber;
            }
        }

        if (isWhiteMove)
        {
            tokens.Add($"{number}.");
        }
        else if (forceNumber || Bool(node, "forceMoveNumber"))
        {
            tokens.Add($"{number}...");
        }

        var san = Text(node, "san");
        if (string.IsNullOrWhiteSpace(san))
        {
            san = Text(node, "uci");
        }

        if (string.IsNullOrWhiteSpace(san))
        {
            throw new InvalidDataException("Server move has no SAN or UCI.");
        }

        tokens.Add(san.Replace("(", string.Empty, StringComparison.Ordinal)
            .Replace(")", string.Empty, StringComparison.Ordinal));
        var annotationNags = new HashSet<int>();
        if (node.TryGetProperty("annotations", out var annotations) && annotations.ValueKind == JsonValueKind.Array)
        {
            foreach (var annotation in annotations.EnumerateArray())
            {
                var value = annotation.GetString();
                if (value is "!" or "?" or "!!" or "??" or "!?" or "?!")
                {
                    tokens.Add(value);
                    annotationNags.Add(value switch
                    {
                        "!" => 1,
                        "?" => 2,
                        "!!" => 3,
                        "??" => 4,
                        "!?" => 5,
                        _ => 6,
                    });
                }
            }
        }

        if (node.TryGetProperty("nags", out var nags) && nags.ValueKind == JsonValueKind.Array)
        {
            foreach (var nag in nags.EnumerateArray())
            {
                if (nag.TryGetInt32(out var value) && value >= 0 && !annotationNags.Contains(value))
                {
                    tokens.Add($"${value}");
                }
            }
        }

        AddComment(tokens, Text(node, "comment"));
    }

    private static void AddComment(ICollection<string> tokens, string value)
    {
        var clean = value.Replace('{', '(').Replace('}', ')').Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (clean.Length > 0)
        {
            tokens.Add("{" + clean + "}");
        }
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;

    private static bool Bool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.True;

    private static string EscapeHeader(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace('\r', ' ')
            .Replace('\n', ' ');

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private static PgnExternalNodeIdentity ReadNodeIdentity(JsonElement node)
    {
        var nodeId = Text(node, "id");
        if (string.IsNullOrWhiteSpace(nodeId))
        {
            throw new InvalidDataException("Server node has no stable ID.");
        }

        var children = node.TryGetProperty("children", out var childArray) && childArray.ValueKind == JsonValueKind.Array
            ? childArray.EnumerateArray().Select(ReadNodeIdentity).ToArray()
            : Array.Empty<PgnExternalNodeIdentity>();
        return new PgnExternalNodeIdentity(nodeId, children);
    }

    private static void ReadTranslationLinks(
        string gameId,
        JsonElement node,
        ICollection<StudioTranslationLink> links)
    {
        var nodeId = Text(node, "id");
        foreach (var field in new[] { "startingComment", "comment" })
        {
            var sourceHash = Text(node, field + "SourceHash");
            var sourceText = Text(node, field + "SourceText");
            if (sourceHash.Length == 64 && !string.IsNullOrWhiteSpace(sourceText))
            {
                links.Add(new StudioTranslationLink(gameId, nodeId, field, sourceHash, sourceText));
            }
        }

        if (node.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in children.EnumerateArray())
            {
                ReadTranslationLinks(gameId, child, links);
            }
        }
    }
}

public sealed record StudioServerWorkspace(
    string PgnText,
    IReadOnlyList<PgnExternalGameIdentity> GameIdentities,
    IReadOnlyList<StudioTranslationLink> TranslationLinks);
