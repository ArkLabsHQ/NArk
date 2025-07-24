#nullable enable
using NArk;
using NBitcoin;
using NBitcoin.Payment;

namespace BTCPayServer.Data
{
    public class ArkUriClaimDestination : ArkAddressClaimDestination
    {
        private readonly BitcoinUrlBuilder _bitcoinUrl;

        public ArkUriClaimDestination(BitcoinUrlBuilder bitcoinUrl, bool mainnet) : base(ArkAddress.Parse(bitcoinUrl.UnknownParameters["ark"]), mainnet)
        {
            ArgumentNullException.ThrowIfNull(bitcoinUrl);
            if (bitcoinUrl.Address is null)
                throw new ArgumentException(nameof(bitcoinUrl));
            _bitcoinUrl = bitcoinUrl;
        }
        public BitcoinUrlBuilder BitcoinUrl => _bitcoinUrl;
        public override string ToString()
        {
            return _bitcoinUrl.ToString();
        }

        public string Id => Address.ToString();
        public decimal? Amount => _bitcoinUrl.Amount?.ToDecimal(MoneyUnit.BTC);
    }
}
