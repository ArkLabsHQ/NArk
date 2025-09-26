using System.Diagnostics.CodeAnalysis;
using BTCPayServer.Plugins.ArkPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.Data.Entities;
using BTCPayServer.Plugins.ArkPayServer.Models.Events;
using ExchangeSharp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

namespace BTCPayServer.Plugins.ArkPayServer.Cache;

public class ActiveContractsCache(ArkPluginDbContextFactory arkPluginDbContextFactory, EventAggregator eventAggregator): BackgroundService
{
    private readonly SemaphoreSlim _updateTrigger = new(0, 1);

    public IReadOnlySet<ArkWalletContract> Contracts = new HashSet<ArkWalletContract>(comparer: new ContractScriptComparer());

    public void TriggerUpdate() => _updateTrigger.Release();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var dbContext = arkPluginDbContextFactory.CreateContext();

                var allContracts = await dbContext.WalletContracts
                    .Where(c => c.Active)
                    .ToListAsync(stoppingToken);

                var newActiveContracts =
                    allContracts.ToHashSet(comparer: new ContractScriptComparer());

                if (!newActiveContracts.SetEquals(Contracts))
                {
                    Contracts = newActiveContracts;
                    eventAggregator.Publish(new ArkCacheUpdated(nameof(ActiveContractsCache)));
                }

                await _updateTrigger.WaitAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
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