using System.ComponentModel.DataAnnotations;

namespace Gateway.Proxy;

public sealed class ClientRateLimitOptions
{
    public const string SectionName = "RateLimiting";

    [Range(1, int.MaxValue)]
    public int PermitLimit { get; init; } = 100;

    [Range(1, int.MaxValue)]
    public int WindowSeconds { get; init; } = 10;

    [Range(0, int.MaxValue)]
    public int QueueLimit { get; init; }
}
