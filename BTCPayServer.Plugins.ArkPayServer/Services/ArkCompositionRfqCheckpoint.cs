using System.Text.Json;
using System.Text.Json.Nodes;
using NArk.ArkadeIntents.Rfq;
using NArk.ArkadeIntents.Rfq.Profiles.Evm;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

public sealed class ArkCompositionRfqCheckpoint(IRfqTransport remote, ArkCompositionRfqCheckpointState state,
    Func<CancellationToken, Task> persist) : IRfqTransport
{
    public Task<RfqQuote<EvmSendQuoteProfile>> RequestEvmSendQuoteAsync(EvmSendRfqRequest request,
        CancellationToken cancellationToken = default) => QuoteAsync<EvmSendRfqRequest, EvmSendQuoteProfile>(request,
        request.RfqId, request.Pair, remote.RequestEvmSendQuoteAsync, cancellationToken);

    public Task<RfqQuote<TQuoteProfile>> RequestQuoteAsync<TRequestProfile, TQuoteProfile>(RfqRequest<TRequestProfile> request,
        CancellationToken cancellationToken = default) => QuoteAsync<RfqRequest<TRequestProfile>, TQuoteProfile>(request,
        request.RfqId, request.Pair, remote.RequestQuoteAsync<TRequestProfile, TQuoteProfile>, cancellationToken);

    public Task<RfqStatus<TStatusProfile>?> GetStatusAsync<TStatusProfile>(string rfqId,
        CancellationToken cancellationToken = default) => remote.GetStatusAsync<TStatusProfile>(rfqId, cancellationToken);

    private async Task<RfqQuote<TProfile>> QuoteAsync<TRequest, TProfile>(TRequest request, string id, string pair,
        Func<TRequest, CancellationToken, Task<RfqQuote<TProfile>>> send, CancellationToken cancellationToken)
    {
        if (state.Request is null)
        {
            state.Request = JsonSerializer.Serialize(request, RfqProtocol.Json);
            await persist(cancellationToken);
        }
        var original = JsonNode.Parse(state.Request) ?? throw new InvalidOperationException("The RFQ checkpoint is unavailable.");
        if (original["rfq_id"]?.GetValue<string>() != id || original["pair"]?.GetValue<string>() != pair)
            throw new InvalidOperationException("The RFQ checkpoint belongs to another negotiation.");
        if (state.Response is null)
        {
            var response = await send(original.Deserialize<TRequest>(RfqProtocol.Json)!, cancellationToken);
            state.Response = JsonSerializer.Serialize(response, RfqProtocol.Json);
            await persist(CancellationToken.None);
        }
        return RfqProtocol.ExpectQuote<TProfile>(JsonNode.Parse(state.Response)!, id, pair);
    }
}

public sealed class ArkCompositionRfqCheckpointState
{
    public string? Request { get; set; }
    public string? Response { get; set; }
    public override string ToString() => nameof(ArkCompositionRfqCheckpointState);
}
