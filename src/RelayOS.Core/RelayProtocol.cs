namespace RelayOS.Core;

public static class RelayProtocol
{
    public const int MaxPayloadBytes = 256 * 1024;
    public const int MaxNodeIdLength = 128;
    public const int MaxContentTypeLength = 128;
    public static readonly TimeSpan MaxTimeToLive = TimeSpan.FromDays(7);
}
