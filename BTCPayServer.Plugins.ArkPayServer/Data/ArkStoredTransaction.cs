namespace BTCPayServer.Plugins.ArkPayServer;

public class ArkStoredTransaction
{
    public string TransactionId { get; set; }
    public string Psbt { get; set; }
    public StoredTransactionState State { get; set; }
    
    public List<VTXO> CreatedVtxos { get; set; } = new List<VTXO>();
    public List<VTXO> SpentVtxos { get; set; } = new List<VTXO>();
}