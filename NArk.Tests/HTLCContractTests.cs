using NArk;
using NBitcoin;
using NBitcoin.Secp256k1;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NArk.Contracts;
using NBitcoin.DataEncoders;
using Xunit;
using Xunit.Sdk;
using NArk.Extensions;

namespace NArk.Tests
{
    public class HTLCContractTests
    {
        public class TimeLock
        {
            public string Type { get; set; } = "";
            public uint Value { get; set; }
        }

        public class TestCase
        {
            public string Description { get; set; } = "";
            public string PreimageHash { get; set; } = "";
            public string Receiver { get; set; } = "";
            public string Sender { get; set; } = "";
            public string Server { get; set; } = "";
            public uint RefundLocktime { get; set; }
            public TimeLock UnilateralClaimDelay { get; set; } = new TimeLock();
            public TimeLock UnilateralRefundDelay { get; set; } = new TimeLock();
            public TimeLock UnilateralRefundWithoutReceiverDelay { get; set; } = new TimeLock();
        }

        public class ExpectedTaptree
        {
            public string Root { get; set; }
            public Dictionary<string, ExpectedTaptreeValue> merkleProofs { get; set; }
        }

        public class ExpectedTaptreeValue
        {
            public string ControlBlock { get; set; }
        }

        public class ValidTestCase : TestCase
        {
            public string Expected { get; set; } = "";
            public ExpectedTaptree ExpectedTapTree { get; set; }
        }

        public class InvalidTestCase : TestCase
        {
            public string Error { get; set; } = "";
        }

        public class HTLCFixtures
        {
            public List<ValidTestCase> Valid { get; set; } = new List<ValidTestCase>();
            public List<InvalidTestCase> Invalid { get; set; } = new List<InvalidTestCase>();
        }

        private static Sequence ToSequence(TimeLock tl)
        {
            switch (tl.Type)
            {
                case "seconds":
                    if(tl.Value % 512 != 0 || tl.Value < 512)
                        throw new ArgumentException("TimeLock in seconds must be a multiple of 512 and greater than 512");
                    return new Sequence(TimeSpan.FromSeconds(tl.Value));
               case "blocks":
                    return new Sequence((int)tl.Value);
                default:
                    throw new ArgumentException("Unknown timelock type");
            }
        }

        private ECXOnlyPubKey FromTest(string s)
        {
             var bytes = Convert.FromHexString(s);
             if (bytes.Length != 33)
                 
             return ECXOnlyPubKey.Create(bytes);
             return ECPubKey.Create(bytes).ToXOnlyPubKey();
        }
        
        

        [Fact]
        public void TestValidHTLCContractsFromFixtures()
        {
            var fixturesJson = File.ReadAllText("htlc_fixtures.json");
            var fixtures = Json.Deserialize<HTLCFixtures>(fixturesJson);
            Assert.NotNull(fixtures);

            foreach (var testCase in fixtures.Valid)
            {
                var server = FromTest(testCase.Server);
                var sender =FromTest(testCase.Sender);
                var receiver =FromTest(testCase.Receiver);
                var hash = new uint160(new uint160(testCase.PreimageHash).ToBytes(false));

                var contract = new VHTLCContract(
                    server,
                    sender,
                    receiver,
                    hash,
                    new LockTime(testCase.RefundLocktime),
                    ToSequence(testCase.UnilateralClaimDelay),
                    ToSequence(testCase.UnilateralRefundDelay),
                    ToSequence(testCase.UnilateralRefundWithoutReceiverDelay)
                );
                var testCaseAddress = ArkAddress.Parse(testCase.Expected);

                var address = contract.GetArkAddress();
                var spendInfo = contract.GetTaprootSpendInfo();
                var tapscripts = contract.GetTapScriptList();
     
                Assert.True(address.ServerKey.ToBytes().SequenceEqual(testCaseAddress.ServerKey.ToBytes()));
                Assert.Equal(address.Version, testCaseAddress.Version);
                Assert.Equal(address.ScriptPubKey, testCaseAddress.ScriptPubKey);
                
                if(testCase.ExpectedTapTree is null )
                    continue;
                Assert.Equal(testCase.ExpectedTapTree.merkleProofs.Count, spendInfo.ScriptToMerkleProofMap().Count);
                for (int i = 0; i < testCase.ExpectedTapTree.merkleProofs.Count; i++)
                {
                    Assert.Equal(tapscripts[i].Script.ToHex(),testCase.ExpectedTapTree.merkleProofs.ElementAt(i).Key);
                }

                var testCaseTaprootSpendInfo = ComputeTaprootSpendInfo(testCase.ExpectedTapTree, TaprootInternalPubKey.Parse(TaprootConstants.UnspendableKeyHex[2..]));
                var testCaseTapScripts = testCaseTaprootSpendInfo.ScriptToMerkleProofMap().Keys.ToList();
                    
                var  tapscripts2 = spendInfo.ScriptToMerkleProofMap().Keys.ToArray();
                    
                //first verift the tapscripts are identical and in the same order
                Assert.Equal(testCaseTapScripts.Count, tapscripts2.Length);
                for (var i = 0; i < testCaseTapScripts.Count; i++)
                {
                    Assert.Equal(testCaseTapScripts[i].ToString(), tapscripts2[i].ToString());
                }
                    
                Assert.Equal(spendInfo.MerkleRoot.ToString(), testCase.ExpectedTapTree.Root);

               
                
            }
        }

        /// <summary>
        /// Computes a TaprootSpendInfo from an ExpectedTaptree.
        /// This is the reverse process of the WithTree3 method.
        /// </summary>
        /// <param name="tapTree">The expected taproot tree structure</param>
        /// <param name="internalPubKey">The taproot internal pubkey to use</param>
        /// <returns>A TaprootSpendInfo computed from the ExpectedTaptree</returns>
        public TaprootSpendInfo ComputeTaprootSpendInfo(ExpectedTaptree tapTree, TaprootInternalPubKey internalPubKey)
        {
            // Parse the merkle root from the expected taptree
            var merkleRoot = uint256.Parse(tapTree.Root);
            
            // Create the script to merkle branch dictionary
            var scriptToMerkleProofMap = new Dictionary<uint256, TaprootScriptLeaf>();
            foreach (var proofPair in tapTree.merkleProofs)
            {
                // Parse the script from the proof key (hex)
                var script = new TapScript(Script.FromHex(proofPair.Key), TapLeafVersion.C0);
                
                // Parse the control block and extract the merkle branch
                var controlBlock = ControlBlock.FromHex(proofPair.Value.ControlBlock);
                var leaf = new TaprootScriptLeaf(script);
                leaf.MerkleBranch( ).AddRange(controlBlock.MerkleBranch);
                scriptToMerkleProofMap.Add(leaf.LeafHash, leaf);
               
            }
            var ctor = typeof(TaprootNodeInfo).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single();
            var taprootNodeInfo = (TaprootNodeInfo)ctor.Invoke([merkleRoot, scriptToMerkleProofMap.Values.ToList(), false]);
            // Compute the output key based on the internal key and merkle root
            // var outputKey = internalPubKey.GetTaprootFullPubKey(merkleRoot);
            
            // Create and return the TaprootSpendInfo
            return TaprootSpendInfo.FromNodeInfo(internalPubKey, taprootNodeInfo);
        }

        /// <summary>
        /// Reconstructs a merkle tree from a TaprootSpendInfo by analyzing the leaves and their merkle branches.
        /// </summary>
        /// <param name="spendInfo">The TaprootSpendInfo containing script leaves and their merkle branches</param>
        /// <returns>A dictionary mapping node hashes to their child hashes in the tree</returns>
        public Dictionary<uint256, (uint256 Left, uint256 Right)> ReconstructMerkleTreeFromSpendInfo(TaprootSpendInfo spendInfo)
        {
            // Create a dictionary to store our tree structure - map from parent hash to its children
            var treeNodes = new Dictionary<uint256, (uint256 Left, uint256 Right)>();
        
            // Get the ScriptToMerkleProofMap using reflection since it's internal
            var mapProperty = spendInfo.GetType().GetProperty("ScriptToMerkleProofMap", 
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            var scriptMap = ( ConcurrentDictionary<TapScript, List<List<uint256>>>)mapProperty.GetValue(spendInfo);
     
            
            // Process each script and its merkle branches
            foreach (var kvp in scriptMap)
            {
                
                var script = kvp.Key;
                var branches = kvp.Value;
                
                foreach (var branch in branches)
                {
                    // For each branch, reconstruct the path from leaf to root
                    var leafHash = script.LeafHash;
                    var currentNodeHash = leafHash;
                    
                    
                    // Walk up the tree using the branch elements
                    foreach (var siblingHash in branch)
                    {
                        uint256 left;
                        uint256 right;
                        if (CompareLexicographic(currentNodeHash, siblingHash))
                        {
                            left = currentNodeHash;
                            right = siblingHash;
                        }
                        else
                        {
                            left = siblingHash;
                            right = currentNodeHash;
                        }
                        
                        // Compute parent hash
                        var parentHash = ComputeParentHash(left, right);
                        
                        // Add to tree structure
                        treeNodes[parentHash] = (left, right);
                        
                        // Move up the tree
                        currentNodeHash = parentHash;
                    }
                }
            }
            
            return treeNodes;
        }
        
        /// <summary>
        /// Computes the parent hash from two child hashes in a Taproot merkle tree
        /// </summary>
        private uint256 ComputeParentHash(uint256 left, uint256 right)
        {
            using SHA256 sha = new SHA256();
            sha.InitializeTagged("TapBranch");
            
            sha.Write(left.ToBytes());
            sha.Write(right.ToBytes());
           

            return new uint256(sha.GetHash());
        }
        static bool CompareLexicographic(uint256 a, uint256 b)
        {
            Span<byte> ab = stackalloc byte[32];
            Span<byte> bb = stackalloc byte[32];
            a.ToBytes(ab);
            b.ToBytes(bb);
            for (int i = 0; i < ab.Length && i < bb.Length; i++)
            {
                if (ab[i] < bb[i])
                    return true;
                if (bb[i] < ab[i])
                    return false;
            }
            return true;
        }
            
        

        /// <summary>
        /// Visualizes the merkle tree from a TaprootSpendInfo in a readable format
        /// </summary>
        /// <param name="spendInfo">The TaprootSpendInfo to visualize</param>
        /// <returns>A string representing the tree structure</returns>
        public string VisualizeMerkleTree(TaprootSpendInfo spendInfo)
        {
            // First reconstruct the tree
            var treeNodes = ReconstructMerkleTreeFromSpendInfo(spendInfo);
            
            // Start with the root node
            var rootHash = spendInfo.MerkleRoot;
            if (rootHash == null)
                return "Empty tree (key-path only spend)";
            
            var result = new System.Text.StringBuilder();
            result.AppendLine($"Root: {rootHash}");
            
            // Use a queue for breadth-first traversal
            var queue = new Queue<(uint256 Hash, int Depth)>();
            queue.Enqueue((rootHash, 0));
            
            int currentDepth = 0;
            while (queue.Count > 0)
            {
                var (nodeHash, depth) = queue.Dequeue();
                
                // If we're at a new depth, print a level indicator
                if (depth > currentDepth)
                {
                    currentDepth = depth;
                    result.AppendLine($"\nLevel {currentDepth}:");
                }
                
                // If this node is in our tree (has children), process them
                if (treeNodes.TryGetValue(nodeHash, out var children))
                {
                    var (left, right) = children;
                    result.Append($"  Node {nodeHash.ToString().Substring(0, 8)}... -> [{left.ToString().Substring(0, 8)}..., {right.ToString().Substring(0, 8)}...]");
                    
                    // Add children to the queue
                    queue.Enqueue((left, depth + 1));
                    queue.Enqueue((right, depth + 1));
                }
                else
                {
                    // This is a leaf node
                    result.Append($"  Leaf {nodeHash.ToString().Substring(0, 8)}...");
                    
                    // Try to find which script this leaf corresponds to
                    var scriptFound = false;
                    var mapProperty = spendInfo.GetType().GetProperty("ScriptToMerkleProofMap", 
                        BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                    var scriptMap = mapProperty.GetValue(spendInfo);
                    var getEnumerator = scriptMap.GetType().GetMethod("GetEnumerator");
                    var enumerator = getEnumerator.Invoke(scriptMap, null);
                    var moveNext = enumerator.GetType().GetMethod("MoveNext");
                    var current = enumerator.GetType().GetProperty("Current");
                    
                    // Search for the script with matching leaf hash
                    while ((bool)moveNext.Invoke(enumerator, null))
                    {
                        var kvp = current.GetValue(enumerator);
                        var keyProperty = kvp.GetType().GetProperty("Key");
                        var script = keyProperty.GetValue(kvp) as TapScript;
                        
                        if (script.LeafHash == nodeHash)
                        {
                            result.Append($" (Script: {script.ToString().Substring(0, Math.Min(script.ToString().Length, 30))}...)");
                            scriptFound = true;
                            break;
                        }
                    }
                    
                    if (!scriptFound)
                        result.Append(" (Unknown script)");
                }
                
                result.AppendLine();
            }
            
            return result.ToString();
        }

        [Fact]
        public void TestInvalidHTLCContractsFromFixtures()
        {
            var fixturesJson = File.ReadAllText("htlc_fixtures.json");
            var fixtures = Json.Deserialize<HTLCFixtures>(fixturesJson);
            Assert.NotNull(fixtures);

            foreach (var testCase in fixtures.Invalid)
            {
                var server = ECPubKey.Create(Convert.FromHexString(testCase.Server)).ToXOnlyPubKey();
                var sender = ECPubKey.Create(Convert.FromHexString(testCase.Sender)).ToXOnlyPubKey();
                var receiver = ECPubKey.Create(Convert.FromHexString(testCase.Receiver)).ToXOnlyPubKey();

                try
                {
                    var hash = new uint160(testCase.PreimageHash);
                    var contract = new VHTLCContract(
                        server,
                        sender,
                        receiver,
                        hash,
                        new LockTime(testCase.RefundLocktime),
                        ToSequence(testCase.UnilateralClaimDelay),
                        ToSequence(testCase.UnilateralRefundDelay),
                        ToSequence(testCase.UnilateralRefundWithoutReceiverDelay)
                    );

                    Assert.Fail($"Should have thrown exception because of {testCase.Error}");
                }
                catch (Exception e) when (e is not XunitException)
                {
                    // ignored
                }

                // Assert.Contains(testCase.Error, ex.Message);
            }
        }

    }
}
