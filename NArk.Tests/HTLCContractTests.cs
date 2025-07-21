using NArk;
using NBitcoin;
using NBitcoin.Secp256k1;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using Xunit.Sdk;

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

        public class ValidTestCase : TestCase
        {
            public string Expected { get; set; } = "";
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
                default:
                    return new Sequence((int) tl.Value);
            }
        }

        [Fact]
        public void TestValidHTLCContractsFromFixtures()
        {
            var fixturesJson = File.ReadAllText("htlc_fixtures.json");
            var fixtures = Json.Deserialize<HTLCFixtures>(fixturesJson);
            Assert.NotNull(fixtures);

            foreach (var testCase in fixtures.Valid)
            {
                var server = ECPubKey.Create(Convert.FromHexString(testCase.Server)).ToXOnlyPubKey();
                var sender = ECPubKey.Create(Convert.FromHexString(testCase.Sender)).ToXOnlyPubKey();
                var receiver = ECPubKey.Create(Convert.FromHexString(testCase.Receiver)).ToXOnlyPubKey();
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

                var address = contract.GetArkAddress();
                Assert.Equal(testCase.Expected, address.ToString(false));
            }
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
