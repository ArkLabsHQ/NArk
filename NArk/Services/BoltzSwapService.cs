using Microsoft.Extensions.Logging;
using NBitcoin;
using NBitcoin.Crypto;
using NBitcoin.DataEncoders;
using NBitcoin.Secp256k1;
using NArk.Wallet.Boltz;

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

    public async Task<ReverseSwapResult> CreateReverseSwap(
        long invoiceAmount,
        ECXOnlyPubKey receiver,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Creating reverse swap with invoice amount {InvoiceAmount} for receiver {Receiver}", 
            invoiceAmount, receiver.ToHex());
        
        // Get operator terms 
        var operatorTerms = await _operatorTermsService.GetOperatorTerms(cancellationToken);
        _logger.LogDebug("Retrieved operator terms with signer key {SignerKey}", operatorTerms.SignerKey.ToHex());
        
        // Generate preimage and compute preimage hash using SHA256 for Boltz
        var preimage = RandomUtils.GetBytes(32);
        var preimageHash = Hashes.SHA256(preimage);
        _logger.LogDebug("Generated preimage hash: {PreimageHash}", Encoders.Hex.EncodeData(preimageHash));

        // First make the Boltz request to get the swap details including timeout block heights
        var request = new ReverseRequest
        {
            From = "BTC",
            To = "ARK", 
            InvoiceAmount = invoiceAmount,
            ClaimPublicKey = Encoders.Hex.EncodeData(receiver.ToBytes()), // Receiver will claim the VTXO
            PreimageHash = Encoders.Hex.EncodeData(preimageHash),
            AcceptZeroConf = true
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
        
        var sender = response.RefundPublicKey.ToECXOnlyPubKey();
        _logger.LogDebug("Using sender key: {SenderKey}", response.RefundPublicKey);

        var vhtlcContract = new VHTLCContract(
            server: operatorTerms.SignerKey,
            sender: sender,
            receiver: receiver,
            preimage: preimage,
            refundLocktime: new LockTime(80 * 600), // TODO: Don't know
            unilateralClaimDelay: new Sequence((uint)response.TimeoutBlockHeight),
            unilateralRefundDelay: new Sequence((uint)response.TimeoutBlockHeight),
            unilateralRefundWithoutReceiverDelay: new Sequence((uint)response.TimeoutBlockHeight)
        );
        
        // Get the claim address and validate it matches Boltz's lockup address
        var arkAddress = vhtlcContract.GetArkAddress();
        var claimAddress = arkAddress.GetAddress(operatorTerms.Network).ToString();
        _logger.LogDebug("Generated claim address: {ClaimAddress}", claimAddress);
        
        // Validate that our computed address matches what Boltz expects
        if (claimAddress != response.LockupAddress)
        {
            _logger.LogWarning("Address mismatch: computed {ComputedAddress}, Boltz expects {BoltzAddress}", 
                claimAddress, response.LockupAddress);
            // TODO: Temporarily ignore this, since we use a mocked response from Boltz
            throw new InvalidOperationException($"Address mismatch: computed {claimAddress}, Boltz expects {response.LockupAddress}");
        }

        _logger.LogInformation("Successfully created reverse swap with ID: {SwapId}, lockup address: {LockupAddress}", 
            response.Id, response.LockupAddress);
        
        return new ReverseSwapResult
        {
            SwapId = response.Id,
            Invoice = response.Invoice,
            LockupAddress = response.LockupAddress,
            OnchainAmount = response.OnchainAmount,
            TimeoutBlockHeight = response.TimeoutBlockHeight,
            ClaimAddress = claimAddress,
            Preimage = preimage,
            PreimageHash = preimageHash,
            VHTLCContract = vhtlcContract
        };
    }


    public async Task<SubmarineClaimDetailsResponse?> GetClaimDetailsAsync(
        string swapId,
        string transactionHex,
        string preimage,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Getting claim details for swap ID: {SwapId}", swapId);
        
        var request = new SubmarineClaimDetailsRequest
        {
            Transaction = transactionHex,
            Preimage = preimage
        };

        var response = await _boltzClient.GetReverseSwapClaimDetailsAsync(swapId, request);
        
        if (response != null)
        {
            _logger.LogDebug("Successfully retrieved claim details for swap ID: {SwapId}", swapId);
        }
        else
        {
            _logger.LogWarning("Failed to get claim details for swap ID: {SwapId} - null response", swapId);
        }
        
        return response;
    }
}

public class ReverseSwapResult
{
    public string SwapId { get; set; } = null!;
    public string Invoice { get; set; } = null!;
    public string LockupAddress { get; set; } = null!;
    public long OnchainAmount { get; set; }
    public long TimeoutBlockHeight { get; set; }
    public string? ClaimAddress { get; set; }
    public byte[] Preimage { get; set; } = null!;
    public byte[] PreimageHash { get; set; } = null!;
    public VHTLCContract VHTLCContract { get; set; } = null!;
}