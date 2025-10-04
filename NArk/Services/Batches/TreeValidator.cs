using NBitcoin;
using NBitcoin.Secp256k1;

namespace NArk.Services.Batches;

/// <summary>
/// Validation errors for transaction trees
/// </summary>
public static class ValidationErrors
{
    public static InvalidOperationException InvalidSettlementTx(string tx) => 
        new($"invalid settlement transaction: {tx}");
    
    public static readonly InvalidOperationException InvalidSettlementTxOutputs = 
        new("invalid settlement transaction outputs");
    
    public static readonly InvalidOperationException EmptyTree = 
        new("empty tree");
    
    public static readonly InvalidOperationException NumberOfInputs = 
        new("invalid number of inputs");
    
    public static readonly InvalidOperationException WrongSettlementTxid = 
        new("wrong settlement txid");
    
    public static readonly InvalidOperationException InvalidAmount = 
        new("invalid amount");
    
    public static readonly InvalidOperationException NoLeaves = 
        new("no leaves");
    
    public static readonly InvalidOperationException InvalidTaprootScript = 
        new("invalid taproot script");
    
    public static readonly InvalidOperationException InvalidRoundTxOutputs = 
        new("invalid round transaction outputs");
    
    public static readonly InvalidOperationException WrongCommitmentTxid = 
        new("wrong commitment txid");
    
    public static readonly InvalidOperationException MissingCosignersPublicKeys = 
        new("missing cosigners public keys");
}

/// <summary>
/// Validates tree signatures
/// </summary>
public static class TreeValidator
{
    


    /// <summary>
    /// Validates the connectors transaction graph
    /// Matches TypeScript validateConnectorsTxGraph (lines 31-56)
    /// </summary>
    public static void ValidateConnectorsTxGraph(
        PSBT commitmentTransactionPSBT,
        TxTree connectorsGraph)
    {
        connectorsGraph.Validate();
        if (connectorsGraph.Root.GetGlobalTransaction().Inputs.Count != 1)
            throw ValidationErrors.NumberOfInputs;

        var rootInput = connectorsGraph.Root.Inputs[0];

        var commitmentTransaction = commitmentTransactionPSBT.GetGlobalTransaction();
        
        if (rootInput.PrevOut.Hash != commitmentTransaction.GetHash())
        {
            throw ValidationErrors.WrongSettlementTxid;
        }

        if (commitmentTransaction.Outputs.Count <= rootInput.Index)
        {
            throw ValidationErrors.InvalidSettlementTxOutputs;
            
        }
    }

    /// <summary>
    /// Validates the VTXO transaction graph
    /// Matches TypeScript validateVtxoTxGraph (lines 58-157)
    /// Validates:
    /// - the number of nodes
    /// - the number of leaves
    /// - children coherence with parent
    /// - every control block and taproot output scripts
    /// - input and output amounts
    /// </summary>
    public static void ValidateVtxoTxGraph(
        TxTree graph,
        Transaction roundTransaction,
        uint256 sweepTapTreeRoot)
    {
        if (graph.Root == null)
            throw ValidationErrors.EmptyTree;

        var rootInput = graph.Root.Inputs[0];
        var commitmentTxid = roundTransaction.GetHash();

        if (rootInput.PrevOut.Hash != commitmentTxid)
        {
            throw ValidationErrors.WrongCommitmentTxid;
        }

        var batchOutputIndex = rootInput.PrevOut.N;
        
        if (roundTransaction.Outputs.Count <= batchOutputIndex)
            throw ValidationErrors.InvalidRoundTxOutputs;

        var batchOutputAmount = roundTransaction.Outputs[(int)batchOutputIndex].Value;
        if (batchOutputAmount == null)
            throw ValidationErrors.InvalidRoundTxOutputs;

        // Validate sum of root outputs equals batch output amount
        var sumRootValue = Money.Zero;
        foreach (var output in graph.Root.GetGlobalTransaction().Outputs)
        {
            sumRootValue += output.Value;
        }

        if (sumRootValue != batchOutputAmount)
            throw ValidationErrors.InvalidAmount;

        // Validate leaves exist
        var leaves = graph.Leaves().ToList();
        if (leaves.Count == 0)
            throw ValidationErrors.NoLeaves;

        // Validate the graph structure
        graph.Validate();

        // Iterate over all nodes to verify cosigner public keys correspond to parent output
        // Matches TypeScript lines 119-156
        foreach (var g in graph)
        {
            foreach (var (childIndex, child) in g.Children)
            {
                var parentOutput = g.Root.GetGlobalTransaction().Outputs[childIndex];
                if (parentOutput?.ScriptPubKey == null)
                    throw new InvalidOperationException($"parent output {childIndex} not found");

                // Extract the 32-byte key from P2TR script (skip OP_1 and OP_PUSHBYTES_32)
                var scriptBytes = parentOutput.ScriptPubKey.ToBytes();
                if (scriptBytes.Length < 34 || scriptBytes[0] != 0x51 || scriptBytes[1] != 0x20)
                    throw new InvalidOperationException($"parent output {childIndex} has invalid taproot script");

                var previousScriptKey = scriptBytes.Skip(2).Take(32).ToArray();
                if (previousScriptKey.Length != 32)
                    throw new InvalidOperationException($"parent output {childIndex} has invalid script");

                // Get cosigner keys from child transaction
                var cosigners = child.Root.Inputs[0].GetArkFieldsCosigners();
                if (cosigners.Count == 0)
                    throw ValidationErrors.MissingCosignersPublicKeys;

                var cosignerKeys = cosigners
                    .OrderBy(c => c.Index)
                    .Select(c => c.Key)
                    .ToArray();

                // Aggregate keys with taproot tweak
                var aggregatedKey = ECPubKey.MusigAggregate(cosignerKeys);
                var taprootFinalKey = TaprootFullPubKey.Create(
                    new TaprootInternalPubKey(aggregatedKey.ToXOnlyPubKey().ToBytes()),
                    sweepTapTreeRoot);

                // Extract x-only key from final taproot key (skip OP_1 and OP_PUSHBYTES_32)
                var finalKeyBytes = taprootFinalKey.ScriptPubKey.ToBytes();
                if (finalKeyBytes.Length < 34)
                    throw ValidationErrors.InvalidTaprootScript;

                var finalKeyXOnly = finalKeyBytes.Skip(2).Take(32).ToArray();

                // Compare the keys
                if (!finalKeyXOnly.SequenceEqual(previousScriptKey))
                    throw ValidationErrors.InvalidTaprootScript;
            }
        }
    }
}