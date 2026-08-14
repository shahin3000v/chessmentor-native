using ChessMentor.Pgn;

namespace ChessMentor.Studio;

public static class StudioAudioIdentity
{
    /// <summary>
    /// Resolves the current server game index from the stable game ID. The stored
    /// index is only a legacy fallback because deletion can reindex a multi-game PGN
    /// while an offline audio upload is waiting in the durable queue.
    /// </summary>
    public static int ResolveGameIndex(
        IReadOnlyList<PgnExternalGameIdentity>? gameIdentities,
        string? gameId,
        int fallbackIndex,
        IReadOnlyList<PgnFlatGameIdentity>? flatGameIdentities = null)
    {
        if (string.IsNullOrWhiteSpace(gameId))
        {
            return fallbackIndex;
        }

        if (flatGameIdentities is { Count: > 0 })
        {
            for (var index = 0; index < flatGameIdentities.Count; index++)
            {
                if (string.Equals(flatGameIdentities[index].GameId, gameId, StringComparison.Ordinal))
                {
                    return index;
                }
            }

            return -1;
        }

        if (gameIdentities is { Count: > 0 })
        {
            for (var index = 0; index < gameIdentities.Count; index++)
            {
                if (string.Equals(gameIdentities[index].GameId, gameId, StringComparison.Ordinal))
                {
                    return index;
                }
            }

            return -1;
        }

        return fallbackIndex;
    }
}
