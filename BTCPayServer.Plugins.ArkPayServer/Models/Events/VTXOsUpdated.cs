using System.Text;
using BTCPayServer.Plugins.ArkPayServer.Data.Entities;
using NBitcoin;

namespace BTCPayServer.Plugins.ArkPayServer.Models.Events;

public record VTXOsUpdated(VTXO[] Vtxos)
{
    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{Vtxos} VTXOs updated:");
        
        foreach (var v in Vtxos)
        {
            sb.AppendLine($"{v.TransactionId}:{v.TransactionOutputIndex}_{v.Script}_{Money.Satoshis(v.Amount)}");
        }

        return sb.ToString();
    }
}