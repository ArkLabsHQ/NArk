using NArk;
using Newtonsoft.Json;

namespace BTCPayServer.Plugins.ArkPayServer.PaymentHandler;

public class ArkadePromptDetails(string WalletId, ArkContract contract)
{
    public string WalletId { get; init; } = WalletId;
    [JsonConverter(typeof(ArkContractJsonConverter))]
    public ArkContract Contract { get; init; } = contract;

    public void Deconstruct(out string WalletId, out ArkContract contract)
    {
        WalletId = this.WalletId;
        contract = this.Contract;
    }
}

public class ArkContractJsonConverter : JsonConverter<ArkContract>
{
    public override void WriteJson(JsonWriter writer, ArkContract? value, JsonSerializer serializer)
    {
        writer.WriteValue(value?.ToString());
    }

    public override ArkContract? ReadJson(JsonReader reader, Type objectType, ArkContract? existingValue, bool hasExistingValue,
        JsonSerializer serializer)
    {
        var contractString = reader.Value?.ToString() ?? string.Empty;
        return string.IsNullOrEmpty(contractString) ? null : ArkContract.Parse(contractString);
    }
}