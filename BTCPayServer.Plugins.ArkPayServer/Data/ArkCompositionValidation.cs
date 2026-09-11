using System.Globalization;
using System.Numerics;
using NArk.Abstractions;
using NBitcoin.Secp256k1;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;

namespace BTCPayServer.Plugins.ArkPayServer.Data;

internal static class ArkCompositionValidation
{
    private static readonly BigInteger MaximumUint256 = (BigInteger.One << 256) - 1;

    internal static string Hash(string value, bool canonical = false)
    {
        if (value is null || value.Length != 64 || !value.All(Uri.IsHexDigit) || canonical && value != value.ToLowerInvariant())
            throw new ArgumentException("Expected a 32-byte hexadecimal identifier.");
        return value.ToLowerInvariant();
    }

    internal static string EvmTransaction(string value)
    {
        if (value is null || !value.StartsWith("0x", StringComparison.Ordinal))
            throw new ArgumentException("Expected an EVM transaction hash.");
        return "0x" + Hash(value[2..]);
    }

    internal static string Integer(string value, bool allowZero = false)
    {
        if (value is null || value.Length is < 1 or > 78 || !value.All(char.IsAsciiDigit) ||
            value.Length > 1 && value[0] == '0' || !allowZero && value == "0" ||
            BigInteger.Parse(value, CultureInfo.InvariantCulture) > MaximumUint256)
            throw new ArgumentException("Expected a canonical uint256 integer.");
        return value;
    }

    internal static long Sats(string value)
    {
        Integer(value);
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var amount))
            throw new ArgumentException("Expected a supported satoshi amount.");
        return amount;
    }

    internal static string Address(string value)
    {
        if (!ArkEvmSettlementSettings.IsNonzeroAddress(value))
            throw new ArgumentException("Expected a nonzero EVM address.");
        return value.ToLowerInvariant();
    }

    internal static long Timestamp(long value)
    {
        if (value is <= 0 or > 253402300799) throw new ArgumentException("Expected Unix seconds.");
        return value;
    }

    internal static ArkCompositionQuote Quote(ArkCompositionQuote quote)
    {
        ArgumentNullException.ThrowIfNull(quote);
        var key = Hash(quote.SolverPubkey);
        if (!ECXOnlyPubKey.TryCreate(Convert.FromHexString(key), out _) ||
            quote.LockupScript is null || quote.LockupScript.Length != 68 || !quote.LockupScript.StartsWith("5120") ||
            quote.LockupAddress is null || quote.LockupAddress.Length > 512 ||
            !ArkAddress.TryParse(quote.LockupAddress, out var address) ||
            !string.Equals(quote.LockupScript, "5120" + Convert.ToHexString(address!.ToBytes()), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Expected a valid solver key and matching Arkade lock address/script.");
        if (Timestamp(quote.RefundLocktime) <= Timestamp(quote.ValidUntil))
            throw new ArgumentException("Refund must follow the quote funding deadline.");
        return quote with
        {
            RfqId = Hash(quote.RfqId, true),
            PaymentHash = Hash(quote.PaymentHash),
            SolverPubkey = key,
            FromAmount = Integer(quote.FromAmount),
            ToAmount = Integer(quote.ToAmount),
            LockupScript = quote.LockupScript.ToLowerInvariant(),
            LockupAddress = quote.LockupAddress.ToLowerInvariant(),
            PayoutScript = quote.PayoutScript?.ToLowerInvariant()
        };
    }

    internal static ArkCompositionEvmTerms Terms(ArkCompositionEvmTerms terms)
    {
        ArgumentNullException.ThrowIfNull(terms);
        return terms with
        {
            PaymentHash = Hash(terms.PaymentHash),
            Amount = Integer(terms.Amount),
            TokenAddress = Address(terms.TokenAddress),
            ClaimAddress = Address(terms.ClaimAddress),
            RefundAddress = Address(terms.RefundAddress),
            SwapContractAddress = Address(terms.SwapContractAddress),
            TimeoutBlock = Integer(terms.TimeoutBlock)
        };
    }
}
