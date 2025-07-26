using NArk;

namespace BTCPayServer.Data
{
    public interface IArkClaimDestination : IClaimDestination
    {
        ArkAddress Address { get; }
    }
    
    public class ArkAddressClaimDestination : IArkClaimDestination
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
