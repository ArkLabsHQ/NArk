using NBitcoin;
using NBitcoin.Secp256k1;

namespace NArk;



public class NofNMultisigTapScript : ScriptBuilder
{
    public NofNMultisigTapScript(ECXOnlyPubKey[] owners)
    {
        Owners = owners;
    }

    public static TapScript Create(params ECXOnlyPubKey[] owners) => new NofNMultisigTapScript(owners).Build();

    public ECXOnlyPubKey[] Owners { get; }
    public override IEnumerable<Op> BuildScript()
    {
        return Owners.SelectMany(key => new[] 
        { 
            Op.GetPushOp(key.ToBytes()), 
            OpcodeType.OP_CHECKSIGVERIFY 
        });
    }
        
}
//
//
// public class MultisigTapScript:ScriptBuilder
// {
//     public int Threshold { get; }
//     public ECXOnlyPubKey[] Owners { get; }
//
//     [Obsolete("Use NofNMultisigTapScript instead as thresholds are not supported in Ark ")]
//     public MultisigTapScript(int threshold, ECXOnlyPubKey[] owners)
//     {
//         Threshold = threshold;
//         Owners = owners;
//     }
//
//     public override IEnumerable<Op> BuildScript()
//     {
//         var ops = new List<Op>();
//         if (Owners.Length == 1 && Threshold == 1)
//         {
//             ops.AddRange(Op.GetPushOp(Owners[0].ToBytes()), OpcodeType.OP_CHECKSIGVERIFY);
//         }
//         else
//         {
//             for (var index = 0; index < Owners.Length; index++)
//             {
//                 var owner = Owners[index];
//                 ops.AddRange(Op.GetPushOp(owner.ToBytes()),index == 0 ? OpcodeType.OP_CHECKSIG : OpcodeType.OP_CHECKSIGADD);
//             }
//             ops.AddRange(Op.GetPushOp(Threshold), 
//             Owners.Length == Threshold ? OpcodeType.OP_NUMEQUAL : OpcodeType.OP_GREATERTHANOREQUAL);
//         }
//
//         return ops;
//     }
// }

