namespace RelayOS.Core.Models;

public sealed record RelaySendOptions
{
    public TimeSpan TimeToLive { get; init; } = TimeSpan.FromHours(12);

    public RelayPriority Priority { get; init; } = RelayPriority.Normal;

    public string ContentType { get; init; } = "application/octet-stream";
}
