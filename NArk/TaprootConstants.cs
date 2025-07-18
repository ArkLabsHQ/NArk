using System.Reflection;
using NBitcoin;

namespace NArk;

public static class TaprootConstants
{
    public static byte[] UnspendableKey =
        Convert.FromHexString("0250929b74c1a04954b78b4b6035e97a5e078a5a0f28ec96d547bfee9ace803ac0");

    public static List<TaprootNodeInfo> Branch(this TaprootBuilder builder)
    {
	    // Get the Branch property using reflection (it might be private/internal)
	    PropertyInfo branchProperty = typeof(TaprootBuilder).GetProperty("Branch",
		    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);

	    List<TaprootNodeInfo>? branch;
	    if (branchProperty == null)
	    {
		    // If it's not a property, try to get it as a field
		    FieldInfo branchField = typeof(TaprootBuilder).GetField("Branch",
			    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);

		    if (branchField == null)
		    {
			    throw new InvalidOperationException("Could not find Branch property or field");
		    }

		    branch = (List<TaprootNodeInfo>) branchField.GetValue(builder);


	    }
	    else
	    {

		    branch = (List<TaprootNodeInfo>) branchProperty.GetValue(builder);
	    }

	    return branch;
    }
    
    public static TaprootBuilder WithTree2(params TapScript[] leaves)
	{
		if (leaves == null) throw new ArgumentNullException(nameof(leaves));
		if (leaves.Length == 0) throw new ArgumentException("Leaves has 0 length.", nameof(leaves));

		// If there's only a single leaf, that becomes our root
		if (leaves.Length == 1)
		{
			var singleLeafNode = TaprootNodeInfo.NewLeaf(leaves[0]);
			var builder = new TaprootBuilder();
			builder.Branch().Add(singleLeafNode);
			return builder;
		}
		
		// Helper for generating binary tree from list, with weights
		// Convert TapScript array to weighted tree nodes
		var lst = leaves.Select(script => new WeightedTreeNode 
		{ 
			Script = script, 
			Weight = 1 // Default weight
		}).ToList();
		
		// We have at least 2 elements => can create branch
		while (lst.Count >= 2)
		{
			// Sort: elements with smallest weight are at the end of queue
			lst.Sort((a, b) => b.Weight.CompareTo(a.Weight));
			
			var b = lst[^1];
			lst.RemoveAt(lst.Count - 1);
			
			var a = lst[^1];
			lst.RemoveAt(lst.Count - 1);
			
			var weight = a.Weight + b.Weight;
			lst.Add(new WeightedTreeNode
			{
				Weight = weight,
				Children = [a, b]
			});
		}
		
		// At this point there is always 1 element in lst
		var root = lst[0];
		
		// Build TaprootNodeInfo from the weighted tree
		var rootNode = BuildTaprootNodeFromWeightedTree(root);
		
		// Create TaprootBuilder and add the root node
		var resultBuilder = new TaprootBuilder();
		resultBuilder.Branch().Add(rootNode);
		return resultBuilder;
	}
	
	private class WeightedTreeNode
	{
		public TapScript? Script { get; set; }
		public int Weight { get; set; }
		public List<WeightedTreeNode>? Children { get; set; }
	}
	
	private static TaprootNodeInfo BuildTaprootNodeFromWeightedTree(WeightedTreeNode node)
	{
		if (node.Script != null)
		{
			// Leaf node
			return TaprootNodeInfo.NewLeaf(node.Script);
		}
		else if (node.Children != null && node.Children.Count == 2)
		{
			// Branch node
			var leftNode = BuildTaprootNodeFromWeightedTree(node.Children[0]);
			var rightNode = BuildTaprootNodeFromWeightedTree(node.Children[1]);
			
			// Combine using the + operator like WithTree does
			return leftNode + rightNode;
		}
		
		throw new InvalidOperationException("Invalid tree node structure");
	}
    

    public static readonly LexicographicComparer uint256Comparer = new();
    public static readonly TapScriptComparer TapScriptComparer = new();
    
}

public class TapScriptComparer : IComparer<TapScript>
{
    public int Compare(TapScript? x, TapScript? y)
    {
        return TaprootConstants.uint256Comparer.Compare(x?.LeafHash, y?.LeafHash);
    }
}

public class LexicographicComparer : IComparer<uint256>
{
    public int Compare(uint256 x, uint256 y)
    {
        if (x == null && y == null) return 0;
        if (x == null) return -1;
        if (y == null) return 1;
        
        var bytesX = x.ToBytes();
        var bytesY = y.ToBytes();
        
        for (int i = 0; i < Math.Min(bytesX.Length, bytesY.Length); i++)
        {
            int comparison = bytesX[i].CompareTo(bytesY[i]);
            if (comparison != 0) return comparison;
        }
        
        return bytesX.Length.CompareTo(bytesY.Length);
    }
}