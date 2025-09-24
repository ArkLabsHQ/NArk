using NArk.Extensions;
using NArk.Scripts;
using NBitcoin;
using NBitcoin.Crypto;
using NBitcoin.DataEncoders;
using NBitcoin.Secp256k1;

namespace NArk.Contracts;

public class VHTLCContract : ArkContract
{
    public byte[]? Preimage { get; }
    public ECXOnlyPubKey Sender { get; }
    public ECXOnlyPubKey Receiver { get; }
    public uint160 Hash { get; }
    public LockTime RefundLocktime { get; }
    public Sequence UnilateralClaimDelay { get; }
    public Sequence UnilateralRefundDelay { get; }
    public Sequence UnilateralRefundWithoutReceiverDelay { get; }

    public VHTLCContract(ECXOnlyPubKey server, ECXOnlyPubKey sender, ECXOnlyPubKey receiver, byte[] preimage,
        LockTime refundLocktime,
        Sequence unilateralClaimDelay,
        Sequence unilateralRefundDelay,
        Sequence unilateralRefundWithoutReceiverDelay)
        : this(server, sender, receiver, new uint160(Hashes.Hash160(preimage).ToBytes(false)), refundLocktime, unilateralClaimDelay, unilateralRefundDelay, unilateralRefundWithoutReceiverDelay)
    {
        Preimage = preimage;
    }
    
    public VHTLCContract(ECXOnlyPubKey server, ECXOnlyPubKey sender, ECXOnlyPubKey receiver, uint160 hash, LockTime refundLocktime,  
        Sequence unilateralClaimDelay,
        Sequence unilateralRefundDelay,
        Sequence  unilateralRefundWithoutReceiverDelay)
        : base(server)
    {
        if(refundLocktime.Value == 0)
            throw new ArgumentException("refundLocktime must be greater than 0");

        ValidTimeLock(unilateralClaimDelay, nameof(unilateralClaimDelay));
        ValidTimeLock(unilateralRefundDelay, nameof(unilateralRefundDelay));
        ValidTimeLock(unilateralRefundWithoutReceiverDelay, nameof(unilateralRefundWithoutReceiverDelay));
        Sender = sender;
        Receiver = receiver;
        Hash = hash;
        RefundLocktime = refundLocktime;
        UnilateralClaimDelay = unilateralClaimDelay;
        UnilateralRefundDelay = unilateralRefundDelay;
        UnilateralRefundWithoutReceiverDelay = unilateralRefundWithoutReceiverDelay;
    }

    private void ValidTimeLock(Sequence sequence, string fieldName)
    {
        if(sequence.Value == 0)
            throw new ArgumentException($"{fieldName} timelock must be greater than 0");
        if (sequence.LockType == SequenceLockType.Time && sequence.LockPeriod.TotalSeconds % 512 != 0 || sequence.LockType == SequenceLockType.Time && sequence.LockPeriod.TotalSeconds < 512)
            throw new ArgumentException($"{fieldName} timelock in seconds must be a multiple of 512 and greater than 512");
    }

    public override string Type => ContractType;
    public const string ContractType = "HTLC";
    

    public override IEnumerable<ScriptBuilder> GetScriptBuilders()
    {
        // VHTLC is a Hashed Timelock Contract VtxoScript implementation
        yield return CreateClaimScript();
        yield return CreateCooperativeScript();
        yield return CreateRefundWithoutReceiverScript();
        yield return CreateUnilateralClaimScript();
        yield return CreateUnilateralRefundScript();
        yield return CreateUnilateralRefundWithoutReceiverScript();
    }

    public override Dictionary<string, string> GetContractData()
    {
        var data = new Dictionary<string, string>
        {
            { "server", Server!.ToHex() },
            { "sender", Sender.ToHex() },
            { "receiver", Receiver.ToHex() },
            { "hash", Hash.ToString() },
            { "refundLocktime", RefundLocktime.Value.ToString() },
            { "unilateralClaimDelay", UnilateralClaimDelay.Value.ToString() },
            { "unilateralRefundDelay", UnilateralRefundDelay.Value.ToString() },
            { "unilateralRefundWithoutReceiverDelay", UnilateralRefundWithoutReceiverDelay.Value.ToString() }
        };
        if(Preimage is not null)
            data.Add("preimage", Encoders.Hex.EncodeData(Preimage));
        return data;
    }
    
    public static ArkContract? Parse(Dictionary<string, string> contractData)
    {
        var server = ECXOnlyPubKey.Create(Convert.FromHexString(contractData["server"]));
        var sender = ECXOnlyPubKey.Create(Convert.FromHexString(contractData["sender"]));
        var receiver = ECXOnlyPubKey.Create(Convert.FromHexString(contractData["receiver"]));
        var hash = new uint160(contractData["hash"]); 
        var refundLocktime = new LockTime(uint.Parse(contractData["refundLocktime"]));
        var unilateralClaimDelay = new Sequence(uint.Parse(contractData["unilateralClaimDelay"]));
        var unilateralRefundDelay = new Sequence(uint.Parse(contractData["unilateralRefundDelay"]));
        var unilateralRefundWithoutReceiverDelay = new Sequence(uint.Parse(contractData["unilateralRefundWithoutReceiverDelay"]));
        if (contractData.TryGetValue("preimage", out var preimage))
        {
            var preimageBytes = Convert.FromHexString(preimage);
            if (!hash.ToBytes().SequenceEqual(Hashes.Hash160(preimageBytes).ToBytes(false)))
            {
                throw new FormatException("preimage does not match hash");
            }
            return new VHTLCContract(server, sender, receiver, preimageBytes, refundLocktime, unilateralClaimDelay, unilateralRefundDelay, unilateralRefundWithoutReceiverDelay);
        }
        
        return new VHTLCContract(server, sender, receiver, hash, refundLocktime, unilateralClaimDelay, unilateralRefundDelay, unilateralRefundWithoutReceiverDelay);
    }
    

    public ScriptBuilder CreateClaimScript()
    {
        // claim (preimage + receiver)
        var hashLock = new HashLockTapScript(Hash);
        var receiverMultisig = new NofNMultisigTapScript([Receiver]);
        return new CollaborativePathArkTapScript(Server!,
            new CompositeTapScript(hashLock, new VerifyTapScript() ,receiverMultisig));
    }

    public ScriptBuilder CreateCooperativeScript()
    {
        // refund (sender + receiver + server)
        var senderReceiverMultisig = new NofNMultisigTapScript([Sender, Receiver]);
        return new CollaborativePathArkTapScript(Server!, senderReceiverMultisig);
    }
    public ScriptBuilder CreateRefundWithoutReceiverScript()
    {
        // refundWithoutReceiver (at refundLocktime, sender  + server)
        var senderReceiverMultisig = new NofNMultisigTapScript([Sender]);
        var lockTime = new LockTimeTapScript(RefundLocktime);
        return new CollaborativePathArkTapScript(Server!,
            new CompositeTapScript(lockTime, senderReceiverMultisig));
    }


    public ScriptBuilder CreateUnilateralClaimScript()
    {
        // unilateralClaim (preimage + receiver after unilateralClaimDelay)
        var hashLock = new HashLockTapScript(Hash);
        var receiverMultisig = new NofNMultisigTapScript([Receiver]);
        return new UnilateralPathArkTapScript(UnilateralClaimDelay,
            receiverMultisig, hashLock);
    }
    public ScriptBuilder CreateUnilateralRefundScript()
    {
        // unilateralRefund (sender + receiver after unilateralRefundDelay)
        var senderReceiverMultisig = new NofNMultisigTapScript([Sender, Receiver]);
        return new UnilateralPathArkTapScript(UnilateralRefundDelay, senderReceiverMultisig);
    }
    public ScriptBuilder CreateUnilateralRefundWithoutReceiverScript()
    {
        // unilateralRefundWithoutReceiver (sender after unilateralRefundWithoutReceiverDelay)
        return new UnilateralPathArkTapScript(UnilateralRefundWithoutReceiverDelay,
            new NofNMultisigTapScript([Sender]));
    }
}