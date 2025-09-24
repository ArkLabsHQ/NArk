﻿using NArk.Scripts;
using NBitcoin.Secp256k1;

namespace NArk.Contracts;

public class GenericArkContract(ECXOnlyPubKey server, IEnumerable<ScriptBuilder> scriptBuilders, Dictionary<string, string>? contractData = null) : ArkContract(server)
{
    public override string Type { get; } = "generic";
    public override IEnumerable<ScriptBuilder> GetScriptBuilders()
    {
        return scriptBuilders;
    }

    public override Dictionary<string, string> GetContractData()
    {
        return contractData ?? [];
    }
}