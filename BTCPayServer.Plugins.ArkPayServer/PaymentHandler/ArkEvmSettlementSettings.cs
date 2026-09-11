using System.Text.RegularExpressions;

namespace BTCPayServer.Plugins.ArkPayServer.PaymentHandler;

/// <summary>Persisted merchant settlement policy; never return this storage record from an API.</summary>
/// <param name="AssetId">Canonical CAIP-19 source of the EIP-155 chain and ERC20 token identity.</param>
/// <param name="Destination">Merchant's nonzero EVM destination.</param>
/// <param name="Enabled">Whether settlement is requested; execution additionally requires SDK support.</param>
public sealed record ArkEvmSettlementSettings(string AssetId, string Destination, bool Enabled = false)
{
    /// <summary>Explicit route bounds; absent on legacy settings, which cannot enable execution.</summary>
    public ArkEvmRoutePolicy? RoutePolicy { get; init; }
    /// <summary>Store-bound Data Protection ciphertext persisted by the configuration serializer, never an API field.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? ProtectedRpcUri { get; init; }

    private static readonly Regex AssetPattern = new(
        "\\Aeip155:[1-9][0-9]{0,31}/erc20:0x[0-9a-fA-F]{40}\\z", RegexOptions.CultureInvariant);
    private static readonly Regex AddressPattern = new("\\A0x[0-9a-fA-F]{40}\\z", RegexOptions.CultureInvariant);
    private const string ZeroAddress = "0x0000000000000000000000000000000000000000";

    /// <summary>Validates public values while preserving policy-absent legacy records for reading.</summary>
    public ArkEvmSettlementSettings Validate()
    {
        if (AssetId is null || !AssetPattern.IsMatch(AssetId) || AssetId.EndsWith(ZeroAddress, StringComparison.Ordinal))
            throw new ArgumentException("Settlement asset must identify a nonzero ERC20 contract and positive EVM chain.");
        if (!IsNonzeroAddress(Destination))
            throw new ArgumentException("Settlement destination must be a nonzero EVM address.");

        return this with
        {
            AssetId = AssetId.ToLowerInvariant(),
            Destination = Destination.ToLowerInvariant(),
            RoutePolicy = RoutePolicy?.Validate()
        };
    }

    internal static bool IsNonzeroAddress(string? value) =>
        value is not null && AddressPattern.IsMatch(value) && value != ZeroAddress;

    /// <inheritdoc />
    public override string ToString() => nameof(ArkEvmSettlementSettings);
}
