namespace ChessMentor.Pgn;

public static class PgnSerializer
{
    public static string Serialize(PgnDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.Serialize();
    }
}
