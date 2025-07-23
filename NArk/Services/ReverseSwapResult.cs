using NArk.Boltz.Models.Swaps.Reverse;
using NArk.Contracts;

namespace NArk.Services;

public class ReverseSwapResult
{
    public required VHTLCContract Contract { get; set; }

    public required ReverseResponse Swap { get; set; }

    public required ArkAddress Address { get; set; }

    public byte[] Hash { get; set; }
}