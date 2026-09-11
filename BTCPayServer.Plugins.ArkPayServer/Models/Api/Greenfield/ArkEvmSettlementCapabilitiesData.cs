namespace BTCPayServer.Plugins.ArkPayServer.Models.Api.Greenfield;

/// <summary>Configuration readiness is independent of signer availability and SDK execution.</summary>
/// <param name="WalletConfigured">Whether the store has an Arkade wallet binding.</param>
/// <param name="SignerAvailable">Whether the wallet has a signer; not required for watch-only configuration.</param>
/// <param name="ConfigurationEnabled">Whether settlement is requested by the store.</param>
/// <param name="ConfigurationComplete">Whether all configuration prerequisites are present.</param>
/// <param name="EnabledSourceRails">Independently enabled payment methods.</param>
/// <param name="RpcEndpointConfigured">Whether this store's protected RPC URI can be read.</param>
/// <param name="RpcEndpointOrigin">RPC scheme, host and port only.</param>
/// <param name="MissingConfiguration">Nonsecret codes for missing prerequisites.</param>
public sealed record ArkEvmSettlementCapabilitiesData(
    bool WalletConfigured, bool SignerAvailable, bool ConfigurationEnabled, bool ConfigurationComplete,
    string[] EnabledSourceRails, bool RpcEndpointConfigured, string? RpcEndpointOrigin, string[] MissingConfiguration)
{
    /// <summary>Execution remains disabled until SDK composition is integrated.</summary>
    public bool ExecutionAvailable => false;
    /// <summary>Execution blocker independent of configuration readiness.</summary>
    public string BlockedReason => "sdk-composition-unavailable";
    /// <summary>Ingress funding alone cannot complete a composed payment.</summary>
    public string PaymentCompletionCondition => "evm-settlement";
}
