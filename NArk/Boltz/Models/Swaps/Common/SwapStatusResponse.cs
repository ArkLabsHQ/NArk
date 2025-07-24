using System.Text.Json.Serialization;

namespace NArk.Boltz.Models.Swaps.Common;

public class SwapStatusResponse
{
    [JsonPropertyName("status")]
    public string Status { get; set; }

   
}