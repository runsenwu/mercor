using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Shortener.Shared;

namespace Shortener.Worker;

/// <summary>Turns a message body into a <see cref="ClickEvent"/>, or says why it is unusable.</summary>
internal static class ClickParser
{
    // links.code is varchar(32). A longer code would make the whole batch's INSERT fail on every retry,
    // so it has to be rejected here, per message.
    public const int MaxCodeLength = 32;

    public static bool TryParse(
        ReadOnlySpan<byte> body, [NotNullWhen(true)] out ClickEvent? click, [NotNullWhen(false)] out string? error)
    {
        click = null;
        ClickEvent? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize(body, SharedJsonContext.Default.ClickEvent);
        }
        catch (JsonException ex)
        {
            error = $"invalid JSON: {ex.Message}";
            return false;
        }

        error = parsed switch
        {
            null => "body is null",
            { Code: null or "" } => "missing code",
            { Code.Length: > MaxCodeLength } => $"code longer than {MaxCodeLength} characters",
            { At: var at } when at == default => "missing at",
            _ => null,
        };
        if (error is not null) return false;

        click = parsed!;
        return true;
    }
}
