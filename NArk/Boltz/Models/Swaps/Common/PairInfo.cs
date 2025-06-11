namespace NArk.Wallet.Boltz;

using System.Text.Json.Serialization;

public class PairInfo
{
    [JsonPropertyName("hash")]
    public string Hash { get; set; }

    [JsonPropertyName("rate")]
    public double Rate { get; set; }

    [JsonPropertyName("limits")]
    public PairLimits Limits { get; set; }

    [JsonPropertyName("fees")]
    public PairFees Fees { get; set; }

    [JsonPropertyName("hashes")]
    public PairHashes? Hashes { get; set; } // Optional, for Taproot
}

public class PairLimits
{
    [JsonPropertyName("minimal")]
    public long Minimal { get; set; }

    [JsonPropertyName("maximal")]
    public long Maximal { get; set; }

    [JsonPropertyName("maximalZeroConf")]
    public MaximalZeroConf? MaximalZeroConf { get; set; }
}

public class MaximalZeroConf
{
    [JsonPropertyName("baseAsset")]
    public long? BaseAsset { get; set; }

    [JsonPropertyName("quoteAsset")]
    public long? QuoteAsset { get; set; }
}

public class PairFees
{
    [JsonPropertyName("percentage")]
    public double Percentage { get; set; }

    [JsonPropertyName("percentageSwapIn")]
    public double? PercentageSwapIn { get; set; } // For reverse swaps

    [JsonPropertyName("percentageSwapOut")]
    public double? PercentageSwapOut { get; set; } // For submarine swaps

    [JsonPropertyName("minerFees")]
    public MinerFees MinerFees { get; set; }
}

public class MinerFees
{
    [JsonPropertyName("baseAsset")]
    public MinerFeeDetails BaseAsset { get; set; }

    [JsonPropertyName("quoteAsset")]
    public MinerFeeDetails QuoteAsset { get; set; }
}

public class MinerFeeDetails
{
    [JsonPropertyName("normal")]
    public long Normal { get; set; }

    [JsonPropertyName("reverse")]
    public ReverseMinerFee Reverse { get; set; }

    [JsonPropertyName("claim")]
    public long? Claim { get; set; } // For submarine swaps

    [JsonPropertyName("lockup")]
    public long? Lockup { get; set; } // For reverse swaps
}

public class ReverseMinerFee
{
    [JsonPropertyName("claim")]
    public long Claim { get; set; }

    [JsonPropertyName("lockup")]
    public long Lockup { get; set; }
}

public class PairHashes
{
    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; }

    [JsonPropertyName("ripemd160")]
    public string Ripemd160 { get; set; }
}
