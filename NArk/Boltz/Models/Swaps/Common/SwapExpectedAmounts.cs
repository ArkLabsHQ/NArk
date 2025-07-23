using System.Text.Json.Serialization;

namespace NArk.Boltz.Models.Swaps.Common;

public class SwapExpectedAmounts
{
    [JsonPropertyName("invoiceAmount")]
    public long? InvoiceAmount { get; set; }

    [JsonPropertyName("onchainAmount")]
    public long? OnchainAmount { get; set; }

    [JsonPropertyName("userSendAmount")]
    public long? UserSendAmount { get; set; } // For Chain Swaps

    [JsonPropertyName("serverSendAmount")]
    public long? ServerSendAmount { get; set; } // For Chain Swaps
}