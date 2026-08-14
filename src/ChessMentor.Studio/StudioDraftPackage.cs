using ChessMentor.Pgn;

namespace ChessMentor.Studio;

public sealed record StudioDraftPackage(
    int SchemaVersion,
    string DraftId,
    string? SourceId,
    string Title,
    string PgnText,
    IReadOnlyList<string> SourceNames,
    string? ActiveGameId,
    string? ActiveNodeId,
    IReadOnlyList<StudioTranslationLink> TranslationLinks,
    IReadOnlyList<PgnExternalGameIdentity>? GameIdentities,
    long? ServerDraftId,
    string CategorySlug,
    string PublishSlug,
    int CreditPriceMinor,
    DateTimeOffset UpdatedUtc,
    string? FeaturedImagePath = null,
    string? FeaturedImageName = null,
    long? ServerCourseId = null,
    IReadOnlyList<PgnFlatGameIdentity>? FlatGameIdentities = null)
{
    public const int CurrentSchemaVersion = 4;
}

public sealed record StudioTranslationLink(
    string GameId,
    string NodeId,
    string Field,
    string SourceHash,
    string SourceText);
