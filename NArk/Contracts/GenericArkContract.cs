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