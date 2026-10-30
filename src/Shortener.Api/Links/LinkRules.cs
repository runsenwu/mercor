using System.Buffers;
using System.Buffers.Binary;
using System.Buffers.Text;
using System.Collections.Frozen;
using System.Security.Cryptography;

namespace Shortener.Api.Links;

internal static class LinkRules
{
    public const int MaxUrlLength = 2048;
    public const int CodeLength = 7;
    public const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    private static readonly SearchValues<char> CodeChars =
        SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_-");

    // Paths the service serves itself; an alias must never shadow them (routing is case-insensitive).
    private static readonly FrozenSet<string> Reserved = new[]
        { "api", "docs", "openapi", "healthz", "readyz", "metrics", "favicon.ico", "robots.txt", "scalar" }
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static string NewCode() => RandomNumberGenerator.GetString(Alphabet, CodeLength);

    /// <summary>Cheap pre-check so junk paths (favicon.ico, scanners) never reach Redis or Postgres.</summary>
    public static bool IsWellFormedCode(string code) =>
        code.Length is >= 3 and <= 32 && !code.AsSpan().ContainsAnyExcept(CodeChars);

    public static bool IsReserved(string code) => Reserved.Contains(code);

    public static string? ValidateAlias(string alias) =>
        !IsWellFormedCode(alias) ? "Alias must be 3-32 characters of A-Z, a-z, 0-9, '_' or '-'."
        : IsReserved(alias) ? $"'{alias}' is a reserved word."
        : null;

    /// <summary>Returns an error message, or null and the normalized (ASCII-escaped) URL.</summary>
    public static string? ValidateUrl(string? input, string ownHost, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(input)) return "A URL is required.";
        if (input.Length > MaxUrlLength) return $"The URL must be at most {MaxUrlLength} characters.";
        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(uri.Host))
            return "The URL must be an absolute http or https URL.";
        if (string.Equals(uri.IdnHost, ownHost, StringComparison.OrdinalIgnoreCase))
            return "Links to this shortener itself are not allowed.";
        // AbsoluteUri escapes path/query; swap in the punycode host so the Location header stays ASCII.
        normalized = uri.Host == uri.IdnHost ? uri.AbsoluteUri
            : new UriBuilder(uri) { Host = uri.IdnHost }.Uri.AbsoluteUri;
        return normalized.Length > MaxUrlLength ? $"The URL must be at most {MaxUrlLength} characters." : null;
    }

    public static bool IsGone(DateTime? deletedAt, DateTime? expiresAt, DateTimeOffset now) =>
        deletedAt is not null || (expiresAt is { } e && e <= now.UtcDateTime);

    /// <summary>Positive cache TTL: never outlive the link itself. Null = too close to expiry to cache.</summary>
    public static TimeSpan? CacheTtl(DateTime? expiresAt, DateTimeOffset now, TimeSpan max)
    {
        if (expiresAt is null) return max;
        var remaining = expiresAt.Value - now.UtcDateTime;
        if (remaining < TimeSpan.FromSeconds(1)) return null;
        return remaining < max ? remaining : max;
    }

    // Opaque keyset cursor: version byte + last seen id, base64url.
    public static string EncodeCursor(long lastId)
    {
        Span<byte> bytes = stackalloc byte[9];
        bytes[0] = 1;
        BinaryPrimitives.WriteInt64BigEndian(bytes[1..], lastId);
        return Base64Url.EncodeToString(bytes);
    }

    public static bool TryDecodeCursor(string cursor, out long lastId)
    {
        lastId = 0;
        Span<byte> bytes = stackalloc byte[16];
        if (cursor.Length > 16 || !Base64Url.IsValid(cursor) || !Base64Url.TryDecodeFromChars(cursor, bytes, out var written)
            || written != 9 || bytes[0] != 1) return false;
        lastId = BinaryPrimitives.ReadInt64BigEndian(bytes[1..9]);
        return lastId > 0;
    }
}
