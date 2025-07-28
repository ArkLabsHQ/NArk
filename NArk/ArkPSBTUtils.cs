using System.Text;
using NBitcoin;
using NBitcoin.Protocol;

namespace NArk;

/**
* ArkPsbtFieldKey is the key values for ark psbt fields.
*/
public static class ArkPSBTUtils
{
    public const string VtxoTaprootTree = "taptree";

    public const string VtxoTreeExpiry = "expiry";
    public const string Cosigner = "cosigner";
    public const string ConditionWitness = "condition";

    public const byte ArkPsbtFieldKeyType = 255;

    
    private static bool StartsWith(this byte[] bytes, byte[] prefix) => bytes.Take(prefix.Length).SequenceEqual(prefix);
    
    public static Dictionary<string, object> GetPSBTArkFields(this SortedDictionary<byte[], byte[]> map)
    {
        var result = new Dictionary<string, object>();
        var fields = map.Where(pair => pair.Key[0] == ArkPsbtFieldKeyType)
            .ToDictionary(pair => pair.Key.Skip(1).ToArray(), pair => pair.Value);
        foreach (var field in fields)
        {
            if (!map.Remove(field.Key, out var val))
                continue;

            switch (field.Key)
            {
                case { } bytes1 when bytes1.StartsWith(Encoding.UTF8.GetBytes(VtxoTaprootTree)):
                    result[VtxoTaprootTree] = DecodeTaprootTree(val);
                    break;
                case { } bytes2 when bytes2.StartsWith(Encoding.UTF8.GetBytes(VtxoTreeExpiry)):
                    result[VtxoTreeExpiry] = DecodeVtxoTreeExpiry(val);
                    break;
                case { } bytes3 when bytes3.StartsWith(Encoding.UTF8.GetBytes(Cosigner)):
                    // Extract the index from the last byte of the key
                    byte cosignerIndex = bytes3[^1];
                    result[Cosigner] = new CosignerPublicKeyData
                    {
                        Index = cosignerIndex,
                        Key = val
                    };
                    break;
                case { } bytes4 when bytes4.StartsWith(Encoding.UTF8.GetBytes(ConditionWitness)):
                    result[ConditionWitness] = new WitScript(val);
                    break;
            }
        }
        return result;

    }
    
    public static void SetArkField(this SortedDictionary<byte[], byte[]> map, WitScript script)
    {
        map[Encoding.UTF8.GetBytes(ConditionWitness)] = script.ToBytes();
    }


    public static void SetArkField(this SortedDictionary<byte[], byte[]> map,
        CosignerPublicKeyData cosignerPublicKeyData)
    {
        map[ [..Encoding.UTF8.GetBytes(Cosigner) ,cosignerPublicKeyData.Index]] = cosignerPublicKeyData.Key;
    }
    
    public static void SetArkField(this SortedDictionary<byte[], byte[]> map, TapScript[] leaves)
    {
        map[Encoding.UTF8.GetBytes(VtxoTaprootTree)] = EncodeTaprootTree(leaves);
    }
    
    public static void SetArkField(this SortedDictionary<byte[], byte[]> map, Sequence expiry)
    {
        map[Encoding.UTF8.GetBytes(VtxoTreeExpiry)] = BitConverter.GetBytes(expiry.Value);
    }
    


    /// <summary>
    /// Encodes a collection of taproot script leaves into a byte array
    /// </summary>
    /// <param name="leaves">Array of tapscript byte arrays</param>
    /// <returns>Encoded taproot tree as byte array</returns>
    public static byte[] EncodeTaprootTree(TapScript[] leaves)
    {
        var chunks = new List<byte[]>();
        // Write number of leaves as compact size uint
        chunks.Add(new VarInt((ulong) leaves.Length).ToBytes());
        foreach (var tapscript in leaves)
        {
            // Write depth (always 1 for now)
            chunks.Add([1]);
            // Write leaf version (0xc0 for tapscript)
            chunks.Add([0xc0]);
            // Write script length and script
            chunks.Add(new VarInt((ulong) tapscript.Script.Length).ToBytes());
            chunks.Add(tapscript.Script.ToBytes());
        }

        // Concatenate all chunks
        int totalLength = chunks.Sum(chunk => chunk.Length);
        byte[] result = new byte[totalLength];
        int offset = 0;
        foreach (var chunk in chunks)
        {
            Buffer.BlockCopy(chunk, 0, result, offset, chunk.Length);
            offset += chunk.Length;
        }

        return result;
    }

    /// <summary>
    /// Decodes a byte array into a collection of taproot script leaves
    /// </summary>
    /// <param name="data">Encoded taproot tree byte array</param>
    /// <returns>Array of decoded TapScript objects</returns>
    public static TapScript[] DecodeTaprootTree(byte[]? data)
    {
        if (data == null || data.Length == 0)
            return new TapScript[0];

        var bitcoinStream = new BitcoinStream(data);
        uint leavesCount = 0;
        bitcoinStream.ReadWriteAsVarInt(ref leavesCount);
        var result = new TapScript[leavesCount];

        for (int i = 0; i < leavesCount; i++)
        {
            // Read depth (should be 1)
            byte depth = 0;
            bitcoinStream.ReadWrite(ref depth);
            if (depth != 1)
                throw new FormatException($"Unexpected depth value: {depth}. Expected 1.");

            // Read leaf version (should be 0xc0 for tapscript)
            byte leafVersion = 0;
            bitcoinStream.ReadWrite(ref leafVersion);
            if (leafVersion != 0xc0)
                throw new FormatException($"Unexpected leaf version: {leafVersion:X2}. Expected 0xC0.");

            uint scriptLength = 0;
            bitcoinStream.ReadWriteAsVarInt(ref scriptLength);
            // Read script
            byte[] scriptBytes = new byte[scriptLength];
            bitcoinStream.ReadWrite(scriptBytes, 0, (int) scriptLength);

            // Create TapScript object
            result[i] = new TapScript(Script.FromBytesUnsafe(scriptBytes), (TapLeafVersion) leafVersion);
        }

        return result;
    }

    public static Sequence DecodeVtxoTreeExpiry(byte[] data)
    {
        if (data.Length == 0)
            return Sequence.Final;

        // Decode the ScriptNum value
        long scriptNumValue = DecodeScriptNum(data);
        
        return  new Sequence((uint) scriptNumValue);
    }
    
    private static long DecodeScriptNum(byte[] data)
    {
        // Simple implementation of ScriptNum decoding
        // This is a basic implementation - may need to be expanded based on Bitcoin's ScriptNum rules
        if (data.Length == 0)
            return 0;
            
        long result = 0;
        for (int i = 0; i < data.Length; i++)
        {
            result |= ((long)data[i]) << (8 * i);
        }
        
        // Handle negative numbers (if the most significant byte has the high bit set)
        if ((data[data.Length - 1] & 0x80) != 0)
        {
            result &= ~((long)0x80 << (8 * (data.Length - 1)));
            result = -result;
        }
        
        return result;
    }
    
   
}