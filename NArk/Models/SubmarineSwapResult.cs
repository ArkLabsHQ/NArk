using NArk.Boltz.Models.Swaps.Submarine;
using NArk.Contracts;

namespace NArk.Models;

public record SubmarineSwapResult(VHTLCContract Contract, SubmarineResponse Swap, ArkAddress Address);
