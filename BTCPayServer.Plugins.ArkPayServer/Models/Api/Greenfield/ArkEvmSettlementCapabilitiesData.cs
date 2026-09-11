namespace BTCPayServer.Plugins.ArkPayServer.Models.Api.Greenfield;

public sealed record ArkEvmSettlementCapabilitiesData(
    bool WalletConfigured, bool SignerAvailable, bool ConfigurationEnabled)
{
    public bool ExecutionAvailable => false;
    public string BlockedReason => "sdk-composition-unavailable";
}
