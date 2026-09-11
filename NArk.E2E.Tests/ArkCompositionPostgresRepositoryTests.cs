using BTCPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Tests;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace NArk.E2E.Tests;

public sealed class ArkCompositionPostgresRepositoryTests(ITestOutputHelper output)
{
    [Fact]
    public async Task PreparedRouteCanBeSavedAfterPostgresRoundTrip()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await CompositionPostgresDatabase.Create(cancellationToken);
        var repository = new ArkInvoiceCompositionRepository(database);
        var handler = new ArkadePaymentMethodHandler(null!, null!, null!, null!, null!);
        var store = new StoreData { Id = "store" };
        store.SetPaymentMethodConfig(handler, new ArkadePaymentMethodConfig("wallet")
        {
            EvmSettlement = new ArkEvmSettlementSettings(
                "eip155:42161/erc20:0x1111111111111111111111111111111111111111",
                "0x2222222222222222222222222222222222222222", true)
            {
                RoutePolicy = new ArkEvmRoutePolicy
                {
                    EnabledSourceRails = ["ARKADE"],
                    OutgoingSolver = new("registry"),
                    SwapContractAddress = "0x3333333333333333333333333333333333333333",
                    FastestSecondsPerBlock = 1,
                    SlowestSecondsPerBlock = 2,
                    MinConfirmations = 3,
                    MinAgeSeconds = 5,
                    MinimumClaimWindowSeconds = 1800,
                    ArkadeRefundMarginSeconds = 7200
                }
            }
        });
        var createdAt = new DateTimeOffset(2026, 9, 11, 11, 0, 0, TimeSpan.FromHours(2)).AddTicks(1234567);
        var route = ArkInvoiceComposition.Create(store, null, new PaymentMethodHandlerDictionary([handler]), createdAt);
        await repository.Add(store.Id, route, cancellationToken);
        var stored = (await repository.Get(store.Id, route.RouteId, cancellationToken))!;
        output.WriteLine($"Created timestamp: {route.CreatedAt:O}; PostgreSQL timestamp: {stored.CreatedAt:O}");

        var revision = route.Revision;
        route.Prepare(1000, new string('a', 64), new string('b', 64), null);
        await repository.Save(store.Id, route, revision, cancellationToken);

        stored = (await repository.Get(store.Id, route.RouteId, cancellationToken))!;
        Assert.Equal("Prepared", stored.Status);
        Assert.Equal(1000, stored.BaseAmountSats);
        Assert.Equal(new string('a', 64), stored.PaymentHash);
        Assert.Equal(new string('b', 64), Assert.Single(stored.Legs).RfqId);
        Assert.Equal(new DateTimeOffset(2026, 9, 11, 9, 0, 0, TimeSpan.Zero).AddTicks(1234560), stored.CreatedAt);
        Assert.Equal(route.CreatedAt, stored.CreatedAt);
        await repository.Save(store.Id, route, revision, cancellationToken);
        await repository.Add(store.Id, route, cancellationToken);
    }

    private sealed class CompositionPostgresDatabase(string connectionString) : IDbContextFactory<ArkPluginDbContext>, IAsyncDisposable
    {
        internal static async Task<CompositionPostgresDatabase> Create(CancellationToken cancellationToken)
        {
            var configured = Environment.GetEnvironmentVariable("TESTS_POSTGRES");
            var connection = new NpgsqlConnectionStringBuilder(string.IsNullOrWhiteSpace(configured)
                ? ServerTester.DefaultConnectionString : configured)
            {
                Database = "nark_composition_" + Guid.NewGuid().ToString("N")
            };
            var database = new CompositionPostgresDatabase(connection.ConnectionString);
            await using var context = database.CreateDbContext();
            await context.Database.EnsureCreatedAsync(cancellationToken);
            return database;
        }

        public ArkPluginDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ArkPluginDbContext>()
            .UseNpgsql(connectionString).Options);

        public async ValueTask DisposeAsync()
        {
            await using var context = CreateDbContext();
            await context.Database.EnsureDeletedAsync(CancellationToken.None);
        }
    }
}
