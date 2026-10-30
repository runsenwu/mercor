namespace Shortener.Api;

public sealed class ShortenerOptions { public string PublicBaseUrl { get; set; } = "http://localhost:8080"; }

public sealed class CacheOptions
{
    public bool Enabled { get; set; } = true;
    public int TtlSeconds { get; set; } = 3600;
    public int NegativeTtlSeconds { get; set; } = 60;
}

public enum ClickMode { Queue, Sync, Off }

public sealed class ClickOptions
{
    public ClickMode Mode { get; set; } = ClickMode.Queue;
    public int BufferCapacity { get; set; } = 10000;
}

public sealed class RateLimitOptions
{
    public long AnonymousCreatesPerMinute { get; set; } = 20;
    public long AnonymousReadsPerMinute { get; set; } = 300;
}

public sealed class ApiKeyOptions
{
    public string Name { get; set; } = "";
    public string Key { get; set; } = "";
    public long PermitsPerMinute { get; set; }
}
