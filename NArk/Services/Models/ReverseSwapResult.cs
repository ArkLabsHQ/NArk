using NArk.Boltz.Models.Swaps.Reverse;
using NArk.Boltz.Models.Swaps.Submarine;
using NArk.Contracts;

namespace NArk.Services;

public class ReverseSwapResult
{
    public required VHTLCContract Contract { get; set; }

    public required ReverseResponse Swap { get; set; }

    public byte[] Hash { get; set; }
}

public class SubmarineSwapResult
{
    
    public required VHTLCContract Contract { get; set; }
    public required SubmarineResponse Swap { get; set; }
    public required ArkAddress Address { get; set; }
    
}