using NArk.Extensions;
using NBitcoin;
using NBitcoin.DataEncoders;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace NArk.Tests
{
    /// <summary>
    /// Tests for VtxoScript taproot tree construction.
    /// Verifies that building a taproot tree from a set of scripts produces the expected output key.
    /// </summary>
    public class VtxoScriptTests
    {
        public class VtxoScriptFixture
        {
            public string Name { get; set; } = "";
            public List<string> Scripts { get; set; } = new List<string>();
            public string TaprootKey { get; set; } = "";
        }

        /// <summary>
        /// Tests that VtxoScript taproot trees are constructed correctly from fixture data.
        /// This test verifies that:
        /// 1. Scripts can be parsed from hex format
        /// 2. The WithTree extension correctly builds a balanced taproot tree
        /// 3. The final taproot output key matches the expected value
        /// </summary>
        [Fact]
        public void TestVtxoScriptFromFixtures()
        {
            var fixturesJson = File.ReadAllText("vtxoscript.json");
            var fixtures = Json.Deserialize<List<VtxoScriptFixture>>(fixturesJson);
            Assert.NotNull(fixtures);

            foreach (var fixture in fixtures)
            {
                // Convert hex script strings to TapScript objects with TapLeaf version C0
                var tapScripts = fixture.Scripts
                    .Select(scriptHex => new TapScript(Script.FromHex(scriptHex), TapLeafVersion.C0))
                    .ToArray();

                // Build the taproot tree using the WithTree extension
                // This uses the same algorithm as Bitcoin Core's AssembleTaprootScriptTree
                var builder = tapScripts.WithTree();
                
                // Finalize with the unspendable internal key (standard for Ark contracts)
                var internalPubKey = new TaprootInternalPubKey(TaprootConstants.UnspendableKey.ToECXOnlyPubKey().ToBytes());
                var spendInfo = builder.Finalize(internalPubKey);

                // Extract the taproot output public key
                // This is the key that appears in the scriptPubKey (OP_1 <32-byte-key>)
                var outputPubKey = spendInfo.OutputPubKey.ToBytes();
                var taprootKeyHex = Encoders.Hex.EncodeData(outputPubKey);

                // Assert that the computed taproot key matches the expected one from the fixture
                Assert.Equal(fixture.TaprootKey, taprootKeyHex, ignoreCase: true);
            }
        }
    }
}
