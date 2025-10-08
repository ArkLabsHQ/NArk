using System.Diagnostics.CodeAnalysis;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.Data.Entities;
using BTCPayServer.Plugins.ArkPayServer.Models.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PayoutData = BTCPayServer.Data.PayoutData;

namespace BTCPayServer.Plugins.ArkPayServer.Cache;

public class TrackedContractsCache(ArkPluginDbContextFactory arkPluginDbContextFactory, ApplicationDbContextFactory dbContextFactory,  EventAggregator eventAggregator, ILogger<TrackedContractsCache> logger): BackgroundService
{
    public IReadOnlySet<ArkWalletContract> Contracts = new HashSet<ArkWalletContract>(comparer: new ContractScriptComparer());
    public IReadOnlySet<PayoutData> Payouts = new HashSet<PayoutData>(comparer: new PayoutDataComparer());
    
    public void TriggerUpdate()
    {
        eventAggregator.Publish(new TrackedContractsCacheInvalidated());
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                logger.LogInformation("[ARK]: Refreshing active contract cache");

                await using var arkDbContext = arkPluginDbContextFactory.CreateContext();
                await using var dbContext = dbContextFactory.CreateContext();
                
                var allContracts = await arkDbContext.WalletContracts
                    .Where(c => c.Active)
                    .ToListAsync(stoppingToken);

                var newActiveContracts =
                    allContracts.ToHashSet(comparer: new ContractScriptComparer());

                var activePayouts = await dbContext.Payouts
                    .Where(payout => payout.State == PayoutState.AwaitingPayment)
                    .Where(payout => payout.PayoutMethodId == ArkadePlugin.ArkadePayoutMethodId.ToString())
                    .Where(payout => payout.DedupId != null)
                    .ToListAsync(cancellationToken: stoppingToken);

                var newInProgressPayouts =
                    activePayouts.ToHashSet(comparer: new PayoutDataComparer());
                
                if (!newActiveContracts.SetEquals(Contracts) || !newInProgressPayouts.SetEquals(Payouts))
                {
                    Contracts = newActiveContracts;
                    Payouts = newInProgressPayouts;
                    eventAggregator.Publish(new ArkCacheUpdated(nameof(TrackedContractsCache)));
                }
                
                await eventAggregator.WaitNext<TrackedContractsCacheInvalidated>(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private class PayoutDataComparer : IEqualityComparer<PayoutData>
    {
        public bool Equals(PayoutData? x, PayoutData? y)
        {
            return x?.Id == y?.Id;
        }

        public int GetHashCode([DisallowNull] PayoutData obj)
        {
            return obj.Id.GetHashCode();
        }
    }
    private class ContractScriptComparer : IEqualityComparer<ArkWalletContract>
    {
        public bool Equals(ArkWalletContract? x, ArkWalletContract? y)
        {
            return x?.Script == y?.Script;
        }

        public int GetHashCode([DisallowNull] ArkWalletContract obj)
        {
            return obj.Script.GetHashCode();
        }
    }
}