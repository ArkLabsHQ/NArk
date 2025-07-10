using NBitcoin;
using NBitcoin.Crypto;
using NBitcoin.DataEncoders;
using NBitcoin.Secp256k1;
using NArk.Wallet.Boltz;

namespace NArk.Services;

public class BoltzSwapService(BoltzClient boltzClient, IOperatorTermsService operatorTermsService)
{
    public async Task<ReverseSwapResult> CreateReverseSwap(
        long invoiceAmount,
        ECXOnlyPubKey receiver,
        CancellationToken cancellationToken = default)
    {
        // Get operator terms 
        var operatorTerms = await operatorTermsService.GetOperatorTerms(cancellationToken);
        
        // Generate preimage and compute preimage hash using SHA256 for Boltz
        var preimage = RandomUtils.GetBytes(32);
        var preimageHash = Hashes.SHA256(preimage);

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

        var response = await boltzClient.CreateReverseSwapAsync(request);

        if (response == null)
        {
            throw new InvalidOperationException("Failed to create reverse swap");
        }

        // Extract the sender key from Boltz's response (refundPublicKey)
        if (string.IsNullOrEmpty(response.RefundPublicKey))
        {
            throw new InvalidOperationException("Boltz did not provide refund public key");
        }
        
        var senderKeyBytes = Encoders.Hex.DecodeData(response.RefundPublicKey);
        var sender = ECXOnlyPubKey.Create(senderKeyBytes);

        // Extract timeout block heights from Boltz response
        // If Boltz provides Ark-specific timeout block heights, use those; otherwise calculate defaults
        long unilateralClaim, unilateralRefund, unilateralRefundWithoutReceiver;
        
        if (response.TimeoutBlockHeights != null)
        {
            // Use Ark-specific timeout block heights from Boltz
            unilateralClaim = response.TimeoutBlockHeights.UnilateralClaim;
            unilateralRefund = response.TimeoutBlockHeights.UnilateralRefund;
            unilateralRefundWithoutReceiver = response.TimeoutBlockHeights.UnilateralRefundWithoutReceiver;
        }
        else
        {
            // Fallback to single timeout value with calculated offsets
            unilateralClaim = response.TimeoutBlockHeight;
            unilateralRefund = unilateralClaim + 144; // Add ~24 hours (144 blocks)
            unilateralRefundWithoutReceiver = unilateralRefund + 144; // Add another ~24 hours
        }

        // Now create VHTLC contract with the correct timeout values and using Hash160 like arkade
        var vhtlcContract = new VHTLCContract(
            server: operatorTerms.SignerKey,
            sender: sender,
            receiver: receiver,
            hash: Hashes.Hash160(preimage), // Use Hash160 like arkade implementation
            refundLocktime: new LockTime(80 * 600), // Use same refund locktime as arkade (80 * 600 seconds)
            unilateralClaimDelay: new Sequence((uint)unilateralClaim),
            unilateralRefundDelay: new Sequence((uint)unilateralRefund),
            unilateralRefundWithoutReceiverDelay: new Sequence((uint)unilateralRefundWithoutReceiver)
        );
        
        // Get the claim address and validate it matches Boltz's lockup address
        var arkAddress = vhtlcContract.GetArkAddress();
        var claimAddress = arkAddress.GetAddress(operatorTerms.Network).ToString();
        
        // Validate that our computed address matches what Boltz expects
        if (claimAddress != response.LockupAddress)
        {
            throw new InvalidOperationException($"Address mismatch: computed {claimAddress}, Boltz expects {response.LockupAddress}");
        }

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

    public async Task<SwapStatusResponse?> GetSwapStatusAsync(string swapId, CancellationToken cancellationToken = default)
    {
        return await boltzClient.GetSwapStatusAsync(swapId);
    }

    public async Task<SubmarineClaimDetailsResponse?> GetClaimDetailsAsync(
        string swapId,
        string transactionHex,
        string preimage,
        CancellationToken cancellationToken = default)
    {
        var request = new SubmarineClaimDetailsRequest
        {
            Transaction = transactionHex,
            Preimage = preimage
        };

        return await boltzClient.GetReverseSwapClaimDetailsAsync(swapId, request);
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