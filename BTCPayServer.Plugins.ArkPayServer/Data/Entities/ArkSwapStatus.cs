namespace BTCPayServer.Plugins.ArkPayServer.Data.Entities;

public enum ArkSwapStatus
{
    Pending,
    Settled,
    Failed,
    Unknown
}

public static class ArkSwapStatusExtensions
{
    public static bool IsActive(this ArkSwapStatus status)
    {
        return status == ArkSwapStatus.Pending || status == ArkSwapStatus.Unknown;
    }
}