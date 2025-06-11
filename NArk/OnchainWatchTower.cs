// namespace NArk;
//
// public class OnchainWatchTower : IAsyncDisposable
// {
//     private readonly IArkStore _arkStore;
//     private readonly IEnumerable<ITransactionSource> _transactionSources;
//
//     public OnchainWatchTower(IArkStore arkStore, IEnumerable<ITransactionSource> transactionSources)
//     {
//         _arkStore = arkStore;
//         _transactionSources = transactionSources;
//     }
//
//     private CancellationTokenSource _lifetimeCts;
//     private Task[] _streamTasks;
//
//     public async Task Start(CancellationToken startingCts, CancellationToken lifetimeCts)
//     {
//         _lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(lifetimeCts);
//         var toPoll = _arkStore.Transactions
//             .Where(transaction =>
//                 transaction.State == StoredTransactionState.Mempool ||
//                 transaction.State == StoredTransactionState.Virtual)
//             .Select(transaction => transaction.Psbt.ExtractTransaction().GetHash()).ToHashSet();
//
//
//         var transactions =
//             (await Task.WhenAll(_transactionSources.Select(source =>
//                 source.GetTransactions(toPoll.ToArray(), startingCts))))
//             .SelectMany(x => x).ToList();
//
//         var toSubmit = transactions.Where(transaction =>
//             transaction.State is not StoredTransactionState.Mempool and not StoredTransactionState.Virtual);
//         foreach (var transaction in toSubmit)
//         {
//             if (await OnTransactionReceived(transaction))
//             {
//                 transactions.Remove(transaction);
//             }
//         }
//
//
//         var subscriptionId = nameof(OnchainWatchTower);
//         _streamTasks = _transactionSources.Select(async source =>
//         {
//             while (!lifetimeCts.IsCancellationRequested)
//             {
//                 await foreach (var transaction in source.StreamTransactions(subscriptionId, lifetimeCts))
//                 {
//                     await OnTransactionReceived(transaction);
//                 }
//             }
//         }).ToArray();
//
//         foreach (var transactionSource in _transactionSources)
//         {
//             await transactionSource.Watch(subscriptionId, toPoll.ToArray(), null);
//         }
//
//         await foreach (var transaction in _arkStore.StreamTransactions(subscriptionId, lifetimeCts))
//         {
//             if (transaction.State is not StoredTransactionState.Mempool and not StoredTransactionState.Virtual)
//                 continue;
//             var hash = transaction.Psbt.ExtractTransaction().GetHash();
//             foreach (var transactionSource in _transactionSources)
//             {
//                 await transactionSource.Watch(subscriptionId, [hash], null);
//             }
//         }
//     }
//
//
//     private async Task<bool> OnTransactionReceived(StoredTransaction transaction)
//     {
//         //TODO: Handle
//         return true;
//     }
//
//
//     public async ValueTask DisposeAsync()
//     {
//         if (_lifetimeCts.CancelAsync() is { } t)
//         {
//             await t;
//         }
//     }
// }