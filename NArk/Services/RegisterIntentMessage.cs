using System.Text.Json.Serialization;
using Ark.V1;
using NBitcoin.BIP322;

namespace NArk.Services;



public class DeleteIntentMessage
{
    // expire_at: nowSeconds + 2 * 60, // valid for 2 minutes
    
    [JsonPropertyName("type")]
    [JsonPropertyOrder(0)]
    public string Type { get; set; }
    
    [JsonPropertyName("expire_at")]
    [JsonPropertyOrder(1)]
    public long ExpireAt { get; set; }
}

public class RegisterIntentMessage
{
    // type: "register",
    // onchain_output_indexes: onchainOutputsIndexes,
    // valid_at: nowSeconds,
    // expire_at: nowSeconds + 2 * 60, // valid for 2 minutes
    // cosigners_public_keys: cosignerPubKeys,
    
    [JsonPropertyName("type")]
    [JsonPropertyOrder(0)]
    public string Type { get; set; }
    
    [JsonPropertyName("onchain_output_indexes")]
    [JsonPropertyOrder(1)]
    public int[] OnchainOutputsIndexes { get; set; }
    
    [JsonPropertyName("valid_at")]
    [JsonPropertyOrder(2)]
    public long ValidAt { get; set; }
    
    [JsonPropertyName("expire_at")]
    [JsonPropertyOrder(3)]
    public long ExpireAt { get; set; }
    
    [JsonPropertyName("cosigners_public_keys")]
    [JsonPropertyOrder(4)]
    public string[] CosignersPublicKeys { get; set; }
}