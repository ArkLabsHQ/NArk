using BTCPayServer.Plugins.ArkPayServer.Data.Entities;
using NBitcoin;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

public class VTXOsUpdated
{
    public VTXO[] Vtxos { get; set; }
    
    override public string ToString()
    {
        return $"{Vtxos.Length} VTXOs updated: {string.Join(", ", Vtxos.Select(vtxo => $"{vtxo.Script} {Money.Satoshis(vtxo.Amount)} - {vtxo.TransactionId}:{vtxo.TransactionOutputIndex}"))}";
    }
}