using NBitcoin;
using NBitcoin.Crypto;
using NBitcoin.DataEncoders;
using NBitcoin.Secp256k1;

namespace NArk;



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
        : this(server, sender, receiver, Hashes.Hash160(preimage), refundLocktime, unilateralClaimDelay, unilateralRefundDelay, unilateralRefundWithoutReceiverDelay)
    {
        Preimage = preimage;
    }
    
    public VHTLCContract(ECXOnlyPubKey server, ECXOnlyPubKey sender, ECXOnlyPubKey receiver, uint160 hash, LockTime refundLocktime,  
        Sequence unilateralClaimDelay,
        Sequence unilateralRefundDelay,
        Sequence  unilateralRefundWithoutReceiverDelay)
        : base(server)
    {
        Sender = sender;
        Receiver = receiver;
        Hash = hash;
        RefundLocktime = refundLocktime;
        UnilateralClaimDelay = unilateralClaimDelay;
        UnilateralRefundDelay = unilateralRefundDelay;
        UnilateralRefundWithoutReceiverDelay = unilateralRefundWithoutReceiverDelay;
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
            { "server", Server.ToHex() },
            { "sender", Sender.ToHex() },
            { "receiver", Receiver.ToHex() },
            { "hash", Encoders.Hex.EncodeData(Hash.ToBytes()) },
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
        var hash = Convert.FromHexString(contractData["hash"]); 
        var refundLocktime = new LockTime(uint.Parse(contractData["refundLocktime"]));
        var unilateralClaimDelay = new Sequence(uint.Parse(contractData["unilateralClaimDelay"]));
        var unilateralRefundDelay = new Sequence(uint.Parse(contractData["unilateralRefundDelay"]));
        var unilateralRefundWithoutReceiverDelay = new Sequence(uint.Parse(contractData["unilateralRefundWithoutReceiverDelay"]));
        
        
        return new VHTLCContract(server, sender, receiver, hash, refundLocktime, unilateralClaimDelay, unilateralRefundDelay, unilateralRefundWithoutReceiverDelay);
    }
    

    public ScriptBuilder CreateClaimScript()
    {
        // claim (preimage + receiver)
        var hashLock = new HashLockTapScript(Hash);
        var receiverMultisig = new NofNMultisigTapScript([Receiver]);
        return new CollaborativePathArkTapScript(Server,
            new CompositeTapScript(hashLock, new VerifyTapScript() ,receiverMultisig));
    }

    public WitScript ClaimWitness(SecpSchnorrSignature server,byte[] preimage, SecpSchnorrSignature receiver)
    {
        var tapLeaf = CreateClaimScript().Build();
        
        return new WitScript(
            Op.GetPushOp(server.ToBytes()),
            Op.GetPushOp(preimage),
            Op.GetPushOp(receiver.ToBytes()),
            Op.GetPushOp(tapLeaf.Script.ToBytes()),
            Op.GetPushOp(GetTaprootSpendInfo().GetControlBlock(tapLeaf).ToBytes()));
    }

    public ScriptBuilder CreateCooperativeScript()
    {
        // refund (sender + receiver + server)
        var senderReceiverMultisig = new NofNMultisigTapScript([Sender, Receiver]);
        return new CollaborativePathArkTapScript(Server,
            new CompositeTapScript(senderReceiverMultisig));
    }

    public WitScript CooperativeWitness(SecpSchnorrSignature server, SecpSchnorrSignature sender, SecpSchnorrSignature receiver)
    {
        var tapLeaf = CreateCooperativeScript().Build();
        
        return new WitScript(
            Op.GetPushOp(server.ToBytes()),
            Op.GetPushOp(sender.ToBytes()),
            Op.GetPushOp(receiver.ToBytes()),
            Op.GetPushOp(tapLeaf.Script.ToBytes()),
            Op.GetPushOp(GetTaprootSpendInfo().GetControlBlock(tapLeaf).ToBytes()));
    }

    public ScriptBuilder CreateRefundWithoutReceiverScript()
    {
        // refundWithoutReceiver (at refundLocktime, sender  + server)
        var senderReceiverMultisig = new NofNMultisigTapScript([Sender]);
        var lockTime = new LockTimeTapScript(RefundLocktime);
        return new CollaborativePathArkTapScript(Server,
            new CompositeTapScript(lockTime, senderReceiverMultisig));
    }

    public WitScript RefundWithoutReceiverWitness(SecpSchnorrSignature server, SecpSchnorrSignature sender)
    {
        var tapLeaf = CreateRefundWithoutReceiverScript().Build();
        
        return new WitScript(
            Op.GetPushOp(server.ToBytes()),
            Op.GetPushOp(sender.ToBytes()),
            Op.GetPushOp(tapLeaf.Script.ToBytes()),
            Op.GetPushOp(GetTaprootSpendInfo().GetControlBlock(tapLeaf).ToBytes()));
    }

    public ScriptBuilder CreateUnilateralClaimScript()
    {
        // unilateralClaim (preimage + receiver after unilateralClaimDelay)
        var hashLock = new HashLockTapScript(Hash);
        var receiverMultisig = new NofNMultisigTapScript([Receiver]);
        return new UnilateralPathArkTapScript(UnilateralClaimDelay,
            receiverMultisig, hashLock);
    }

    public WitScript UnilateralClaimWitness(byte[] preimage, SecpSchnorrSignature receiver)
    {
        var tapLeaf = CreateUnilateralClaimScript().Build();
        
        return new WitScript(
            Op.GetPushOp(preimage),
            Op.GetPushOp(receiver.ToBytes()),
            Op.GetPushOp(tapLeaf.Script.ToBytes()),
            Op.GetPushOp(GetTaprootSpendInfo().GetControlBlock(tapLeaf).ToBytes()));
    }

    public ScriptBuilder CreateUnilateralRefundScript()
    {
        // unilateralRefund (sender + receiver after unilateralRefundDelay)
        var senderReceiverMultisig = new NofNMultisigTapScript([Sender, Receiver]);
        return new UnilateralPathArkTapScript(UnilateralRefundDelay, senderReceiverMultisig);
    }

    public WitScript UnilateralRefundWitness(SecpSchnorrSignature sender, SecpSchnorrSignature receiver)
    {
        var tapLeaf = CreateUnilateralRefundScript().Build();
        
        return new WitScript(
            Op.GetPushOp(sender.ToBytes()),
            Op.GetPushOp(receiver.ToBytes()),
            Op.GetPushOp(tapLeaf.Script.ToBytes()),
            Op.GetPushOp(GetTaprootSpendInfo().GetControlBlock(tapLeaf).ToBytes()));
    }

    public ScriptBuilder CreateUnilateralRefundWithoutReceiverScript()
    {
        // unilateralRefundWithoutReceiver (sender after unilateralRefundWithoutReceiverDelay)
        return new UnilateralPathArkTapScript(UnilateralRefundWithoutReceiverDelay,
            new NofNMultisigTapScript([Sender]));
    }

    public WitScript UnilateralRefundWithoutReceiverWitness(SecpSchnorrSignature sender)
    {
        var tapLeaf = CreateUnilateralRefundWithoutReceiverScript().Build();
        
        return new WitScript(
            Op.GetPushOp(sender.ToBytes()),
            Op.GetPushOp(tapLeaf.Script.ToBytes()),
            Op.GetPushOp(GetTaprootSpendInfo().GetControlBlock(tapLeaf).ToBytes()));
    }
}