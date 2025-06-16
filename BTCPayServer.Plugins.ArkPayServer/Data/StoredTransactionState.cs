namespace BTCPayServer.Plugins.ArkPayServer;

public enum StoredTransactionState
{
    Virtual,
    Mempool,
    Replaced,
    Confirmed,
    Invalidated
}