
using BTCPayServer.Plugins.ArkPayServer.Data.Entities;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NArk.Services;
using System.Text.Json;
using Ark.V1;
using NArk.Delegator;
using NArk.Extensions;
using NArk.Services.Abstractions;
using NBitcoin.BIP322;
using GetInfoRequest = NArk.Delegator.GetInfoRequest;
using GetInfoResponse = NArk.Delegator.GetInfoResponse;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

/// <summary>
/// Service that allows users to delegate intent management by submitting signed intents
///

/// </summary>
public class ArkDelegationService : DelegationService.DelegationServiceBase
{
    private readonly IndexerService.IndexerServiceClient _indexerServiceClient;
    private readonly ArkIntentService _intentService;
    private readonly ArkadeSpender _arkadeSpender;
    private readonly ArkWalletService _walletService;
    private readonly IOperatorTermsService _operatorTermsService;
    private readonly ILogger<ArkDelegationService> _logger;
    private readonly string _delegationWalletId;

    
    public ArkDelegationService(
        IndexerService.IndexerServiceClient indexerServiceClient,
        ArkIntentService intentService,
        ArkadeSpender arkadeSpender,
        ArkWalletService walletService,
        IOperatorTermsService operatorTermsService,
        ILogger<ArkDelegationService> logger)
    {
        _indexerServiceClient = indexerServiceClient;
        _intentService = intentService;
        _arkadeSpender = arkadeSpender;
        _walletService = walletService;
        _operatorTermsService = operatorTermsService;
        _logger = logger;
        
        //TODO: switch away from grpc, or in the proto add a way to specify a dynamic wallet id
        _delegationWalletId = "delegation-service";
    }

    public override async Task<GetInfoResponse> GetInfo(GetInfoRequest request, ServerCallContext context)
    {
        var operatorTerms = await _operatorTermsService.GetOperatorTerms(context.CancellationToken);

        return new GetInfoResponse
        {
            ArkServer = operatorTerms.SignerKey.ToHex(),
            PublicKey = _delegationWalletId // Return delegation service identifier
        };
    }

    public override async Task<DelegateResponse> Delegate(DelegateRequest request, ServerCallContext context)
    {
        try
        {
            
            //1.) verify the psbt by checking it is fully valid and that the message is the same one inside the bip322 psbt
            //1.5) ensure delegator is the cosigner specified
            //2.) verify that psbt fields are present and valid
            //3.) extract the inputs of the psbt and convert them to coins. 
            //4.) verify against the api that coins are unspent
            //5.) verify a forfeit is provided for every non-recoverable coin
            //6.) extract the spending leaf of the forfeit and verify that it is only missing a signature from Server and Delegator
            
            
            //note: do not add these vtxos/contracts to the db, we can improve later.
            
            
            _logger.LogInformation("Received delegation request");
            var operatorTerms = await _operatorTermsService.GetOperatorTerms(context.CancellationToken);
            // Parse BIP322 signature (register intent proof)
            var registerProof = PSBT.Parse(request.Bip322Signature.Signature, operatorTerms.Network);
            var registerMessage = JsonSerializer.Deserialize<RegisterIntentMessage>(request.Bip322Signature.Message);
            if (registerMessage == null)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid register message format"));
            }
            // Parse forfeit transaction
            var forfeits = request.Forfeit.Select(s => PSBT.Parse(s, operatorTerms.Network))
                .ToDictionary(psbt => psbt.Inputs[0].PrevOut);

            var inputs = registerProof.Inputs.Where(input => input.Index != 0).ToDictionary(input => input.PrevOut);

            var vtxosRequest = new GetVtxosRequest()
            {
                SpendableOnly = false,
                RecoverableOnly = false
            };

            vtxosRequest.Outpoints.Add(inputs.Keys.Select(point => point.ToString()));
            var vtxosToFetch =
                await _indexerServiceClient.GetVtxosAsync(vtxosRequest, null, null, context.CancellationToken);
            
            
            //loop through, check they are unspent
            //any of them recoverable dont need a forfeit
            // those that are still ok, check that theere is a forfeit for it + the chosen tapleaf requires the delegate signature
            //check there is signature + condition if needed.
            throw new NotImplementedException();
            
            
            return new DelegateResponse
            {
                IntentId = _delegationWalletId
            };
        }
        catch (RpcException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing delegation request");
            throw new RpcException(new Status(StatusCode.Internal, $"Failed to process delegation: {ex.Message}"));
        }
    }

}
