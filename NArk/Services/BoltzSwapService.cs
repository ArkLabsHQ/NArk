using BTCPayServer.Lightning;
using Microsoft.Extensions.Logging;
using NArk.Boltz.Client;
using NArk.Boltz.Models.Swaps.Reverse;
using NArk.Boltz.Models.Swaps.Submarine;
using NArk.Contracts;
using NBitcoin;
using NBitcoin.Crypto;
using NBitcoin.DataEncoders;
using NBitcoin.Secp256k1;

namespace NArk.Services;

public class BoltzSwapService
{
    private readonly BoltzClient _boltzClient;
    private readonly IOperatorTermsService _operatorTermsService;
    private readonly ILogger<BoltzSwapService> _logger;

    public BoltzSwapService(
        BoltzClient boltzClient,
        IOperatorTermsService operatorTermsService,
        ILogger<BoltzSwapService> logger)
    {
        _boltzClient = boltzClient;
        _operatorTermsService = operatorTermsService;
        _logger = logger;
    }

    public async Task<SubmarineSwapResult> CreateSubmarineSwap(BOLT11PaymentRequest invoice, ECXOnlyPubKey sender,
        CancellationToken cancellationToken = default)
    {
        var operatorTerms = await _operatorTermsService.GetOperatorTerms(cancellationToken);



        var response = await _boltzClient.CreateSubmarineSwapAsync(new SubmarineRequest()
        {
            Invoice = invoice.ToString(),
            RefundPublicKey = sender.ToCompressedEvenYHex(),
            From = "ARK",
            To = "BTC",
        });

        var hash = new uint160(Hashes.RIPEMD160(invoice.PaymentHash.ToBytes()));
        var receiver = response.ClaimPublicKey.ToECXOnlyPubKey();

        var vhtlcContract = new VHTLCContract(
            server: operatorTerms.SignerKey,
            sender: sender,
            receiver: receiver,
            hash: hash,
            refundLocktime: new LockTime(response.TimeoutBlockHeights.Refund),
            unilateralClaimDelay: new Sequence((uint) response.TimeoutBlockHeights.UnilateralClaim),
            unilateralRefundDelay: new Sequence((uint) response.TimeoutBlockHeights.UnilateralRefund),
            unilateralRefundWithoutReceiverDelay: new Sequence((uint) response.TimeoutBlockHeights
                .UnilateralRefundWithoutReceiver)
        );


        var address = vhtlcContract.GetArkAddress();
        if (response.Address != address.ToString(operatorTerms.Network.ChainName == ChainName.Mainnet))
            throw new Exception($"Address mismatch! Expected {address} got {response.Address}");

        return new SubmarineSwapResult()
        {
            Address = address,
            Swap = response,
            Contract = vhtlcContract
        };
    }

    public async Task<ReverseSwapResult> CreateReverseSwap(
        CreateInvoiceParams createInvoiceRequest,
        ECXOnlyPubKey receiver,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Creating reverse swap with invoice amount {InvoiceAmount} for receiver {Receiver}",
            createInvoiceRequest.Amount.ToUnit(LightMoneyUnit.BTC), receiver.ToHex());

        // Get operator terms 
        var operatorTerms = await _operatorTermsService.GetOperatorTerms(cancellationToken);

        // Generate preimage and compute preimage hash using SHA256 for Boltz
        var preimage = RandomUtils.GetBytes(32);
        var preimageHash = Hashes.SHA256(preimage);
        _logger.LogInformation("Generated preimage hash: {PreimageHash}", Encoders.Hex.EncodeData(preimageHash));

        // First make the Boltz request to get the swap details including timeout block heights
        var request = new ReverseRequest
        {
            From = "BTC",
            To = "ARK",
            InvoiceAmount = createInvoiceRequest.Amount.MilliSatoshi/1000,
            ClaimPublicKey = receiver.ToCompressedEvenYHex(), // Receiver will claim the VTXO
            PreimageHash = Encoders.Hex.EncodeData(preimageHash),
            AcceptZeroConf = true,
            DescriptionHash = createInvoiceRequest.DescriptionHash.ToString(),
            Description = createInvoiceRequest.Description,
            InvoiceExpirySeconds = Convert.ToInt32(createInvoiceRequest.Expiry.TotalSeconds),
        };

        _logger.LogDebug("Sending reverse swap request to Boltz");
        var response = await _boltzClient.CreateReverseSwapAsync(request);

        if (response == null)
        {
            _logger.LogError("Failed to create reverse swap - null response from Boltz");
            throw new InvalidOperationException("Failed to create reverse swap");
        }

        _logger.LogInformation("Received reverse swap response from Boltz with ID: {SwapId}", response.Id);

        // Extract the sender key from Boltz's response (refundPublicKey)
        if (string.IsNullOrEmpty(response.RefundPublicKey))
        {
            _logger.LogError("Boltz did not provide refund public key");
            throw new InvalidOperationException("Boltz did not provide refund public key");
        }
        
        var bolt11 = BOLT11PaymentRequest.Parse(response.Invoice, operatorTerms.Network);
        if (!bolt11.PaymentHash.ToBytes(false).SequenceEqual(preimageHash))
        {
            throw new InvalidOperationException("Boltz did not provide the correct preimage hash");
        }
        if(bolt11.MinimumAmount != LightMoney.Satoshis(request.InvoiceAmount))
        {
            throw new InvalidOperationException("Boltz did not provide the correct invoice amount");
        }
        
        
        var sender = response.RefundPublicKey.ToECXOnlyPubKey();
        _logger.LogDebug("Using sender key: {SenderKey}", response.RefundPublicKey);

        var vhtlcContract = new VHTLCContract(
            server: operatorTerms.SignerKey,
            sender: sender,
            receiver: receiver,
            preimage: preimage,
            refundLocktime: new LockTime(response.TimeoutBlockHeights.Refund),
            unilateralClaimDelay: new Sequence((uint) response.TimeoutBlockHeights.UnilateralClaim),
            unilateralRefundDelay: new Sequence((uint) response.TimeoutBlockHeights.UnilateralRefund),
            unilateralRefundWithoutReceiverDelay: new Sequence((uint) response.TimeoutBlockHeights
                .UnilateralRefundWithoutReceiver)
        );

        // Get the claim address and validate it matches Boltz's lockup address
        var arkAddress = vhtlcContract.GetArkAddress();
        var claimAddress = arkAddress.ToString(mainnet: operatorTerms.Network == Network.Main);
        _logger.LogDebug("Generated claim address: {ClaimAddress}", claimAddress);

        // Validate that our computed address matches what Boltz expects
        if (claimAddress != response.LockupAddress)
        {
            _logger.LogWarning("Address mismatch: computed {ComputedAddress}, Boltz expects {BoltzAddress}",
                claimAddress, response.LockupAddress);
            throw new InvalidOperationException(
                $"Address mismatch: computed {claimAddress}, Boltz expects {response.LockupAddress}");
        }

        _logger.LogInformation("Successfully created reverse swap with ID: {SwapId}, lockup address: {LockupAddress}",
            response.Id, response.LockupAddress);

        return new ReverseSwapResult
        {
            Contract = vhtlcContract,
            Swap = response,
            Hash = preimageHash
        };
    }
}