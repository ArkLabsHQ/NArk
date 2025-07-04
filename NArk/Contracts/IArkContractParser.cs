using NBitcoin;

namespace NArk;

public interface IArkContractParser
{
    string Type { get; }
    ArkContract? Parse( Dictionary<string, string> contractData);

    public static  Dictionary<string, string> GetContractData(string contract)
    {
            var parts = contract.Split('&');
        var data = new Dictionary<string, string>();
        foreach (var part in parts)
        {
            var kvp = part.Split('=');
            if (kvp.Length == 2)
            {
                data[kvp[0]] = kvp[1];
            }
        }
        return data;
    }
}

public class GenericArkContractParser(string type, Func<Dictionary<string, string>, ArkContract?> parse)
    : IArkContractParser
{
    public string Type { get; } = type;

    public ArkContract? Parse(Dictionary<string, string> contractData)
    {
        return parse(contractData);
    }
}