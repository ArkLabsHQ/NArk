using System;
using NArk;
using NBitcoin;

namespace BTCPayServer.Data
{
    public class ArkAddressClaimDestination : IClaimDestination
    {
        private readonly bool _mainnet;
        public ArkAddress Address { get; }

        public ArkAddressClaimDestination(ArkAddress address, bool mainnet)
        {
            _mainnet = mainnet;
            Address = address;
        }
        public string? Id => Address.ToString(_mainnet);

        public virtual decimal? Amount => null;
    }

}
