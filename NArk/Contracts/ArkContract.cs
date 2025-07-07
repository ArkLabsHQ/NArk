using System.Text.Json.Serialization;
using NBitcoin;
using NBitcoin.Secp256k1;

namespace NArk;

public class GenericArkContract: ArkContract
{
    private readonly IEnumerable<ScriptBuilder> _scriptBuilders;
    private readonly Dictionary<string, string> _contractData;

    public GenericArkContract(ECXOnlyPubKey server, IEnumerable<ScriptBuilder> scriptBuilders, Dictionary<string, string> contractData = null) : base(server)
    {
        _scriptBuilders = scriptBuilders;
        _contractData = contractData;
    }

    public override string Type { get; } = "generic";
    public override IEnumerable<ScriptBuilder> GetScriptBuilders()
    {
        return _scriptBuilders;
    }

    public override Dictionary<string, string> GetContractData()
    {
       return _contractData;
    }
}

public abstract class ArkContract
{
    
    public static readonly List<IArkContractParser> Parsers = [];

    static ArkContract()
    {
        Parsers.Add(new GenericArkContractParser(ArkPaymentContract.ContractType, ArkPaymentContract.Parse));
        Parsers.Add(new GenericArkContractParser(TweakedArkPaymentContract.ContractType, TweakedArkPaymentContract.Parse));
        Parsers.Add(new GenericArkContractParser(VHTLCContract.ContractType, VHTLCContract.Parse));
        Parsers.Add(new GenericArkContractParser(ArkNoteContract.ContractType, ArkNoteContract.Parse));
        
    }


    public static ArkContract? Parse(string contract)
    {
        if (!contract.StartsWith("arkcontract"))
        {
            throw new ArgumentException("Invalid contract format. Must start with 'arkcontract'");
        }
        var contractData = IArkContractParser.GetContractData(contract);
        contractData.TryGetValue("arkcontract", out var contractType);
        if (string.IsNullOrEmpty(contractType))
        {
            throw new ArgumentException("Contract type is missing in the contract data");
        }
        return Parse(contractType, contractData);
        
    }
    public static ArkContract? Parse(string type, Dictionary<string, string> contractData)
    {
        return Parsers.FirstOrDefault(parser => parser.Type == type)?
            .Parse(contractData); // Ensure the Payment parser is registered
        
    }
    
    
    public abstract string Type { get; }

    public ECXOnlyPubKey Server { get; }
    protected ArkContract(ECXOnlyPubKey server)
    {
        Server = server;
    }

    public abstract IEnumerable<ScriptBuilder> GetScriptBuilders();

    
    public ArkAddress GetArkAddress()
    {
        var spendInfo = GetTaprootSpendInfo();

        return new ArkAddress(ECXOnlyPubKey.Create(spendInfo.OutputPubKey.ToBytes()), Server);
    }

    public TaprootSpendInfo GetTaprootSpendInfo()
    {
        var leaves = GetScriptBuilders().ToArray();
        if (!leaves.OfType<CollaborativePathArkTapScript>().Any())
            throw new ArgumentException("At least one collaborative path is required");
        if (!leaves.OfType<UnilateralPathArkTapScript>().Any())
            throw new ArgumentException("At least one unilateral path is required");
        if(leaves.Any(x => x is not CollaborativePathArkTapScript && x is not UnilateralPathArkTapScript))
            throw new ArgumentException("Only collaborative and unilateral paths are allowed");
        
        var spendInfo = TaprootSpendInfo.WithHuffmanTree(
            new TaprootInternalPubKey(TaprootConstants.UnspendableKey.ToHex().ToECXOnlyPubKey().ToBytes()), 
            leaves.Select(x => ((uint)0, x.Build())).ToArray());
        
        return spendInfo;
    }
    
    public override string ToString()
    {
        var contractData = GetContractData();
        contractData.Remove("arkcontract");
        var dataString = string.Join("&", contractData.Select(kvp => $"{kvp.Key}={kvp.Value}"));
       
        return $"arkcontract={Type}&{dataString}";
    }
    
    public abstract Dictionary<string, string> GetContractData();
}