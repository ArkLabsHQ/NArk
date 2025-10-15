using System.Numerics;
using System.Text;
using System.Text.Json;
using NArk.Extensions;
using NArk.Services;
using NArk.Services.Abstractions;
using NBitcoin;
using NBitcoin.RPC;
using NBitcoin.Secp256k1;

namespace NArk.Helpers;

public class IntentUtils
{
    private static PSBT CreatePsbt(
        Script pkScript,
        Network network,
        string message,
        uint version = 0, uint lockTime = 0, uint sequence = 0, Coin[]? fundProofOutputs = null)
    {
        var messageHash = CreateMessageHash(message);

        var toSpend = network.CreateTransaction();
        toSpend.Version = 0;
        toSpend.LockTime = 0;
        toSpend.Inputs.Add(new TxIn(new OutPoint(uint256.Zero, 0xFFFFFFFF), new Script(OpcodeType.OP_0, Op.GetPushOp(messageHash)))
        {
            Sequence = 0,
            WitScript = WitScript.Empty,
        });
        toSpend.Outputs.Add(new TxOut(Money.Zero, pkScript));
        var toSpendTxId = toSpend.GetHash();
        var toSign = network.CreateTransaction();
        toSign.Version = version;
        toSign.LockTime = lockTime;
        toSign.Inputs.Add(new TxIn(new OutPoint(toSpendTxId, 0))
        {
            Sequence = sequence
        });

        fundProofOutputs ??= [];

        foreach (var input in fundProofOutputs)
        {
            toSign.Inputs.Add(new TxIn(input.Outpoint, Script.Empty)
            {
                Sequence = sequence,
            });
        }
        toSign.Outputs.Add(new TxOut(Money.Zero, new Script(OpcodeType.OP_RETURN)));
        var psbt = PSBT.FromTransaction(toSign, network);
        psbt.Settings.AutomaticUTXOTrimming = false;
        psbt.AddTransactions(toSpend);
        psbt.AddCoins(fundProofOutputs);
        return psbt;
    }

    private static byte[] CreateMessageHash(string message)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        using var sha = new NBitcoin.Secp256k1.SHA256();
        sha.InitializeTagged("ark-intent-proof-message");
        sha.Write(bytes);
        return sha.GetHash();
    }

    public static async Task<PSBT> CreateIntent(string message, Network network,  SpendableArkCoinWithSigner[] inputs,
        TxOut[]? outputs, CancellationToken cancellationToken = default)
    {
        var toSignTx = CreatePsbt(inputs[0].ScriptPubKey, network, message, 2U, 0U, 0U, inputs);

        var toSignGTx = toSignTx.GetGlobalTransaction();
        if (outputs is not null && outputs.Length != 0)
        {
            toSignGTx.Outputs.RemoveAt(0);
            toSignGTx.Outputs.AddRange(outputs);
        }

        inputs = [ new SpendableArkCoinWithSigner(inputs[0]),..inputs];
        inputs[0].TxOut = toSignTx.Inputs[0].GetTxOut();
        inputs[0].Outpoint = toSignTx.Inputs[0].PrevOut;
        
        var precomputedTransactionData = toSignGTx.PrecomputeTransactionData(inputs.Select(i => i.TxOut).ToArray());
        
        toSignTx = PSBT.FromTransaction(toSignGTx, network).UpdateFrom(toSignTx);
        
        foreach (var inCoin in inputs)
        {
            await inCoin.SignAndFillPSBT(toSignTx, precomputedTransactionData, cancellationToken);
        }
        
        return toSignTx;
    }
    
    
    public static async Task<(PSBT register, PSBT delete, string registerMessage, string deleteMessage)> CreateIntent(Network network,ECXOnlyPubKey[] cosigners, DateTimeOffset validAt, DateTimeOffset expireAt, SpendableArkCoinWithSigner[] ins,
        IntentTxOut[]? outs = null, CancellationToken cancellationToken = default)
    {
        var msg = new RegisterIntentMessage
        {
            Type = "register",
            OnchainOutputsIndexes = outs?.Select((x, i) => (x, i)).Where(o => o.x.Type == IntentTxOut.IntentOutputType.OnChain).Select((x, i) => i).ToArray() ?? [],
            ValidAt = validAt.ToUnixTimeSeconds(),
            ExpireAt = expireAt.ToUnixTimeSeconds(),
            CosignersPublicKeys = cosigners.Select(c => c.ToHex()).ToArray()
        };

        var deleteMsg = new DeleteIntentMessage()
        {
            Type = "delete",
            ExpireAt = expireAt.ToUnixTimeSeconds()
        };
        var message = JsonSerializer.Serialize(msg);
        Console.WriteLine(message);
        var deleteMessage = JsonSerializer.Serialize(deleteMsg);

        return (
            await CreateIntent(message, network, ins, outs, cancellationToken),
            await CreateIntent(deleteMessage, network, ins, null, cancellationToken),
            message,
            deleteMessage);
    }

    public static async Task VerifyIntent(PSBT psbt, string message)
    {
        // if(!psbt.IsAllFinalized())
        //     throw new InvalidOperationException("PSBT is not finalized");
        //
        //
        //
        // var tx = psbt.GetGlobalTransaction();
        //
        // foreach (PSBTInput psbtInput in psbt.Inputs.Skip(1))
        // {
        //     var coin = psbtInput.GetTxOut();
        //     if (coin is null)
        //         throw new InvalidOperationException("PSBT input is not finalized");
        //     
        //     
        //     
        // }
        //
        // var toSignTx = tx;
        // var toSignPrecompute = tx.Pre
        // var toSignTxOut = new TxOut(Money.Zero, new TaprootAddress(new TaprootPubKey(toSignInput.WitScript.GetTaprootPubKey()), tx.Network));
        // var toSignCoin = new Coin(toSignInput.PrevOut, toSignTxOut);
        // var toSignPrecompute = tx.PrecomputeTransactionData(coins);
    }
    
    
    
}