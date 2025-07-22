using System.Collections.Concurrent;
using System.Reflection;
using NBitcoin;

namespace NArk;

public static class TaprootConstants
{
	public static readonly string UnspendableKeyHex =
		"0250929b74c1a04954b78b4b6035e97a5e078a5a0f28ec96d547bfee9ace803ac0";

	public static readonly byte[] UnspendableKey = Convert.FromHexString(UnspendableKeyHex);

	private static T Property<T>(this object obj, string propertyName)
	{
		return (T) obj.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(obj);
	}

	public static List<TaprootNodeInfo?> Branch(this TaprootBuilder builder) =>
		builder.Property<List<TaprootNodeInfo>>("Branch");

	public static List<uint256> MerkleBranch(this TaprootScriptLeaf leaf) =>
		leaf.Property<List<uint256>>("MerkleBranch");

	public static ConcurrentDictionary<TapScript, List<List<uint256>>>
		ScriptToMerkleProofMap(this TaprootSpendInfo info) =>
		info.Property<ConcurrentDictionary<TapScript, List<List<uint256>>>>("ScriptToMerkleProofMap");

	/// <summary>
	/// Creates a TaprootBuilder using an algorithm similar to the AssembleTaprootScriptTree from the Bitcoin Core implementation.
	/// This builds a balanced binary tree by pairing leaves sequentially instead of using weight-based pairing.
	/// </summary>
	/// <param name="leaves">The TapScript leaves to include in the tree</param>
	/// <returns>A TaprootBuilder with the constructed tree</returns>
	public static TaprootBuilder WithTree(this TapScript[] leaves)
	{
        ArgumentNullException.ThrowIfNull(leaves);
        switch (leaves.Length)
        {
	        case 0:
		        throw new ArgumentException("Leaves has 0 length.", nameof(leaves));
	        // If there's only a single leaf, that becomes our root
	        case 1:
	        {
		        var singleLeafNode = TaprootNodeInfo.NewLeaf(leaves[0]);
		        var builder = new TaprootBuilder();
		        builder.Branch().Add(singleLeafNode);
		        return builder;
	        }
        }

        // Create initial branches by pairing sequential leaves
		var branches = new List<TaprootNodeInfo?>();
		for (var i = 0; i < leaves.Length; i += 2)
		{
			// If there's only a single leaf left, then we'll merge this
			// with the last branch we have.
			if (i == leaves.Length - 1)
			{
				branches[^1] += TaprootNodeInfo.NewLeaf(leaves[i]);
				continue;
			}
			// While we still have leaves left, we'll combine two of them
			// into a new branch node.
			branches.Add(TaprootNodeInfo.NewLeaf(leaves[i]) + TaprootNodeInfo.NewLeaf(leaves[i + 1]));
		}

		// In this second phase, we'll merge all the leaf branches we have one
		// by one until we have our final root.
		while (branches.Count != 0)
		{
			// When we only have a single branch left, then that becomes
			// our root.   
			if (branches.Count() == 1)
			{
				var builder = new TaprootBuilder();
				builder.Branch().Add(branches.Single()!);
				return builder;
			}

			var left = branches[0]!;
			var right = branches[1]!;
			branches = [..branches[2..], left+right];

		}

		throw new InvalidOperationException("This should never happen");
	}
}