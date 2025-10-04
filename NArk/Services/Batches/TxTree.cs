using NBitcoin;

namespace NArk.Services.Batches;

/// <summary>
/// Represents a graph of bitcoin transactions used in settlement sessions
/// </summary>
public class TxTree : IEnumerable<TxTree>
{
    public PSBT Root { get; }
    public Dictionary<int, TxTree> Children { get; }

    public TxTree(PSBT root, Dictionary<int, TxTree>? children = null)
    {
        Root = root;
        Children = children ?? new Dictionary<int, TxTree>();
    }

    public static TxTree Create(List<TxTreeNode> chunks)
    {
        if (chunks.Count == 0)
            throw new InvalidOperationException("empty chunks");

        // Decode all chunks
        var chunksByTxid = new Dictionary<uint256, (PSBT tx, Dictionary<int, uint256> children)>();
        foreach (var chunk in chunks)
        {
            var txid = chunk.Tx.GetGlobalTransaction().GetHash();
            chunksByTxid[txid] = (chunk.Tx, chunk.Children);
        }

        // Find root (not referenced as child)
        var rootTxids = new List<uint256>();
        foreach (var txid in chunksByTxid.Keys)
        {
            var isChild = chunksByTxid.Values.Any(c => c.children.Values.Contains(txid));
            if (!isChild)
                rootTxids.Add(txid);
        }

        if (rootTxids.Count == 0)
            throw new InvalidOperationException("no root chunk found");
        if (rootTxids.Count > 1)
            throw new InvalidOperationException($"multiple root chunks found: {string.Join(", ", rootTxids)}");

        var graph = BuildGraph(rootTxids[0], chunksByTxid);
        if (graph == null)
            throw new InvalidOperationException($"chunk not found for root txid: {rootTxids[0]}");

        if (graph.NodeCount() != chunks.Count)
            throw new InvalidOperationException($"number of chunks ({chunks.Count}) != nodes in graph ({graph.NodeCount()})");

        return graph;
    }

    private static TxTree? BuildGraph(uint256 rootTxid, Dictionary<uint256, (PSBT tx, Dictionary<int, uint256> children)> chunksByTxid)
    {
        if (!chunksByTxid.TryGetValue(rootTxid, out var chunk))
            return null;

        var children = new Dictionary<int, TxTree>();
        foreach (var (outputIndex, childTxid) in chunk.children)
        {
            var childGraph = BuildGraph(childTxid, chunksByTxid);
            if (childGraph != null)
                children[outputIndex] = childGraph;
        }

        return new TxTree(chunk.tx, children);
    }

    public int NodeCount()
    {
        return 1 + Children.Values.Sum(child => child.NodeCount());
    }

    public void Validate()
    {
        var tx = Root.GetGlobalTransaction();
        var nbOfInputs = tx.Inputs.Count;
        var nbOfOutputs = tx.Outputs.Count;

        if (nbOfInputs != 1)
            throw new InvalidOperationException($"unexpected number of inputs: {nbOfInputs}, expected 1");

        if (Children.Count > nbOfOutputs - 1)
            throw new InvalidOperationException($"unexpected number of children: {Children.Count}, expected maximum {nbOfOutputs - 1}");

        foreach (var (outputIndex, child) in Children)
        {
            if (outputIndex >= nbOfOutputs)
                throw new InvalidOperationException($"output index {outputIndex} is out of bounds (nb of outputs: {nbOfOutputs})");

            child.Validate();

            var childTx = child.Root.GetGlobalTransaction();
            var childInput = childTx.Inputs[0];
            var parentTxid = tx.GetHash();

            if (childInput.PrevOut.Hash != parentTxid || childInput.PrevOut.N != outputIndex)
                throw new InvalidOperationException($"input of child {outputIndex} is not the output of the parent");

            // Verify sum of child outputs equals parent output
            var childOutputsSum = childTx.Outputs.Sum(o => o.Value.Satoshi);
            var parentOutputAmount = tx.Outputs[outputIndex].Value.Satoshi;

            if (childOutputsSum != parentOutputAmount)
                throw new InvalidOperationException($"sum of child's outputs != parent output: {childOutputsSum} != {parentOutputAmount}");
        }
    }

    public IEnumerable<PSBT> Leaves()
    {
        if (Children.Count == 0)
        {
            yield return Root;
            yield break;
        }

        foreach (var leaf in Children.Values.SelectMany(child => child.Leaves()))
        {
            yield return leaf;
        }
    }

    public TxTree? Find(uint256 txid)
    {
        return txid == Root.GetGlobalTransaction().GetHash() ? this : Children.Values.Select(child => child.Find(txid)).OfType<TxTree>().FirstOrDefault();
    }

    public void Update(uint256 txid, Action<PSBT> fn)
    {
        if (txid == Root.GetGlobalTransaction().GetHash())
        {
            fn(Root);
            return;
        }

        foreach (var child in Children.Values)
        {
            try
            {
                child.Update(txid, fn);
                return;
            }
            catch
            {
                // ignored
            }
        }

        throw new InvalidOperationException($"tx not found: {txid}");
    }

    public IEnumerator<TxTree> GetEnumerator()
    {
        yield return this;
        foreach (var child in Children.Values)
        {
            foreach (var node in child)
                yield return node;
        }
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}