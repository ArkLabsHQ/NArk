namespace NArk.Contracts;

public class GenericArkContractParser(string type, Func<Dictionary<string, string>, ArkContract?> parse)
    : IArkContractParser
{
    public string Type { get; } = type;

    public ArkContract? Parse(Dictionary<string, string> contractData)
    {
        return parse(contractData);
    }
}