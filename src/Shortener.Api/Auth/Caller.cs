using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Shortener.Api.Auth;

/// <summary>Who is calling: anonymous, a valid API key, or an invalid key (which never falls back to anonymous).</summary>
internal sealed class Caller : IBindableFromHttpContext<Caller>
{
    public const string HeaderName = "X-Api-Key";
    public static readonly Caller Anonymous = new(null, 0, false);
    public static readonly Caller Invalid = new(null, 0, true);
    private static readonly object ItemKey = new();

    public Caller(string? keyName, long permitsPerMinute, bool invalidKey)
    {
        KeyName = keyName;
        PermitsPerMinute = permitsPerMinute;
        HasInvalidKey = invalidKey;
    }

    public string? KeyName { get; }
    public long PermitsPerMinute { get; }
    public bool HasInvalidKey { get; }
    public bool IsAnonymous => KeyName is null;
    public string AuthTag => IsAnonymous ? "anonymous" : "api_key";

    public static Caller Resolve(HttpContext http)
    {
        if (http.Items.TryGetValue(ItemKey, out var cached) && cached is Caller c) return c;
        var caller = http.Request.Headers.TryGetValue(HeaderName, out var values)
            ? http.RequestServices.GetRequiredService<ApiKeyStore>().Authenticate(values.ToString())
            : Anonymous;
        http.Items[ItemKey] = caller;
        return caller;
    }

    public static ValueTask<Caller?> BindAsync(HttpContext context, ParameterInfo parameter) =>
        ValueTask.FromResult<Caller?>(Resolve(context));
}

internal sealed class ApiKeyStore
{
    private readonly (byte[] Hash, Caller Caller)[] _keys;

    public ApiKeyStore(IEnumerable<ApiKeyOptions> keys) =>
        _keys = keys.Where(k => k.Name != "" && k.Key != "")
            .Select(k => (Hash(k.Key), new Caller(k.Name, k.PermitsPerMinute, false))).ToArray();

    // Compare SHA-256 digests in constant time: fixed length, so neither content nor length leaks via timing.
    public Caller Authenticate(string presented)
    {
        var hash = Hash(presented);
        Caller? match = null;
        foreach (var (keyHash, caller) in _keys)
            if (CryptographicOperations.FixedTimeEquals(hash, keyHash)) match ??= caller;
        return match ?? Caller.Invalid;
    }

    private static byte[] Hash(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));
}

internal sealed class RequireApiKeyMetadata;

internal sealed class ApiKeyFilter : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var caller = Caller.Resolve(http);
        if (caller.HasInvalidKey)
            return ValueTask.FromResult<object?>(TypedResults.Problem(statusCode: 401, title: "Invalid API key",
                detail: $"The {Caller.HeaderName} header does not match any known key."));
        if (caller.IsAnonymous && http.GetEndpoint()?.Metadata.GetMetadata<RequireApiKeyMetadata>() is not null)
            return ValueTask.FromResult<object?>(TypedResults.Problem(statusCode: 401, title: "API key required",
                detail: $"Send a valid key in the {Caller.HeaderName} header."));
        return next(context);
    }
}
