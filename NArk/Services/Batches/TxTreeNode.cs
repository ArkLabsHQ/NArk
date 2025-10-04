using NBitcoin;

namespace NArk.Services.Batches;

/// <summary>
/// Node in the transaction tree for serialization
/// </summary>
public class TxTreeNode
{
    public required PSBT Tx { get; set; }
    public Dictionary<int, uint256> Children { get; set; } = new();
}