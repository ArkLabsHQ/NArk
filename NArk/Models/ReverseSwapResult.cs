using NArk.Boltz.Models.Swaps.Reverse;
using NArk.Contracts;

namespace NArk.Models;

public record ReverseSwapResult(VHTLCContract Contract, ReverseResponse Swap, byte[] Hash);