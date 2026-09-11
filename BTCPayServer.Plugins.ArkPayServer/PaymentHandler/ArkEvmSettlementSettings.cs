using System.Text.RegularExpressions;

namespace BTCPayServer.Plugins.ArkPayServer.PaymentHandler;

public sealed record ArkEvmSettlementSettings(string AssetId, string Destination, bool Enabled = false)
{
    private static readonly Regex AssetPattern = new(
        "\\Aeip155:[1-9][0-9]{0,31}/erc20:0x[0-9a-fA-F]{40}\\z", RegexOptions.CultureInvariant);
    private static readonly Regex AddressPattern = new("\\A0x[0-9a-fA-F]{40}\\z", RegexOptions.CultureInvariant);
    private const string ZeroAddress = "0x0000000000000000000000000000000000000000";

    public ArkEvmSettlementSettings Validate()
    {
        if (AssetId is null || !AssetPattern.IsMatch(AssetId) || AssetId.EndsWith(ZeroAddress, StringComparison.Ordinal))
            throw new ArgumentException("Settlement asset must identify a nonzero ERC20 contract and positive EVM chain.");
        if (Destination is null || !AddressPattern.IsMatch(Destination) || Destination == ZeroAddress)
            throw new ArgumentException("Settlement destination must be a nonzero EVM address.");

        return this with { AssetId = AssetId.ToLowerInvariant(), Destination = Destination.ToLowerInvariant() };
    }
}
