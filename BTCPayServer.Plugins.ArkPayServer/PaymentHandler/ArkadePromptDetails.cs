using NArk;
using NArk.Contracts;
using Newtonsoft.Json;

namespace BTCPayServer.Plugins.ArkPayServer.PaymentHandler;

public record ArkadePromptDetails(
    string WalletId,
    [property: JsonConverter(typeof(ArkContractJsonConverter))]
    ArkContract Contract);

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