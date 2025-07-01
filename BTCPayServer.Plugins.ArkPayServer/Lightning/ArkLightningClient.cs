using System.ComponentModel.DataAnnotations;
using BTCPayServer.Lightning;
using BTCPayServer.Payments.Lightning;
using NArk.Wallet.Boltz;
using NBitcoin;
using NodeInfo = BTCPayServer.Lightning.NodeInfo;

namespace BTCPayServer.Plugins.ArkPayServer.Lightning;

/// <summary>
/// ARK Lightning client – talks to a Boltz service instead of a real LN node.
/// Ark->LN interactions are handled through Boltz swaps
/// </summary>
public class ArkLightningClient(string WalletId, BoltzClient BoltzClient) : IExtendedLightningClient
{
    public Task<LightningInvoice> GetInvoice(string invoiceId, CancellationToken cancellation = default)
    {
        throw new NotImplementedException();
    }

    public Task<LightningInvoice> GetInvoice(uint256 paymentHash, CancellationToken cancellation = default)
    {
        throw new NotImplementedException();
    }

    public Task<LightningInvoice[]> ListInvoices(CancellationToken cancellation = default)
    {
        throw new NotImplementedException();
    }

    public Task<LightningInvoice[]> ListInvoices(ListInvoicesParams request, CancellationToken cancellation = default)
    {
        throw new NotImplementedException();
    }

    public Task<LightningPayment> GetPayment(string paymentHash, CancellationToken cancellation = default)
    {
        throw new NotImplementedException();
    }

    public Task<LightningPayment[]> ListPayments(CancellationToken cancellation = default)
    {
        throw new NotImplementedException();
    }

    public Task<LightningPayment[]> ListPayments(ListPaymentsParams request, CancellationToken cancellation = default)
    {
        throw new NotImplementedException();
    }

    public Task<LightningInvoice> CreateInvoice(LightMoney amount, string description, TimeSpan expiry,
        CancellationToken cancellation = default)
    {
        throw new NotImplementedException();
    }

    public Task<LightningInvoice> CreateInvoice(CreateInvoiceParams createInvoiceRequest, CancellationToken cancellation = default)
    {
        throw new NotImplementedException();
    }

    public Task<ILightningInvoiceListener> Listen(CancellationToken cancellation = default)
    {
        throw new NotImplementedException();
    }

    public Task<LightningNodeInformation> GetInfo(CancellationToken cancellation = default)
    {
        throw new NotImplementedException();
    }

    public Task<LightningNodeBalance> GetBalance(CancellationToken cancellation = default)
    {
        throw new NotImplementedException();
    }

    public Task<PayResponse> Pay(PayInvoiceParams payParams, CancellationToken cancellation = default)
    {
        throw new NotImplementedException();
    }

    public Task<PayResponse> Pay(string bolt11, PayInvoiceParams payParams, CancellationToken cancellation = default)
    {
        throw new NotImplementedException();
    }

    public Task<PayResponse> Pay(string bolt11, CancellationToken cancellation = default)
    {
        throw new NotImplementedException();
    }

    public Task<OpenChannelResponse> OpenChannel(OpenChannelRequest openChannelRequest, CancellationToken cancellation = default)
    {
        throw new NotImplementedException();
    }

    public Task<BitcoinAddress> GetDepositAddress(CancellationToken cancellation = default)
    {
        throw new NotImplementedException();
    }

    public Task<ConnectionResult> ConnectTo(NodeInfo nodeInfo, CancellationToken cancellation = default)
    {
        throw new NotImplementedException();
    }

    public Task CancelInvoice(string invoiceId, CancellationToken cancellation = default)
    {
        throw new NotImplementedException();
    }

    public Task<LightningChannel[]> ListChannels(CancellationToken cancellation = default)
    {
        throw new NotImplementedException();
    }

    public Task<ValidationResult?> Validate()
    {
        throw new NotImplementedException();
    }

    public string? DisplayName { get; }
    public Uri? ServerUri { get; }
}