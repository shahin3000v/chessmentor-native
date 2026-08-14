using System.Security.Cryptography;
using System.Text;

namespace ChessMentor.Core;

public static class StableId
{
    public static string Create(string prefix, params object?[] parts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        var payload = string.Join('\u001f', parts.Select(static part => part?.ToString() ?? string.Empty));
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return $"{prefix}_{Convert.ToHexString(bytes.AsSpan(0, 12)).ToLowerInvariant()}";
    }
}
