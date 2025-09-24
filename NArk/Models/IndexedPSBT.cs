using NBitcoin;

namespace NArk.Models;

public class IndexedPSBT(PSBT psbt, int index) : IComparable
{
    public PSBT PSBT { get; } = psbt;
    public int Index { get; } = index;

    public int CompareTo(object? obj)
    {
        if (obj is not IndexedPSBT other) return -1;
        return Index.CompareTo(other.Index);
    }
}