using BTCPayServer.Payments;
using BTCPayServer.Plugins.ArkPayServer.Data;
using BTCPayServer.Services.Invoices;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace NArk.Tests;

public class ArkCompositionRepositoryTests
{
    [Fact]
    public async Task TestNativeSqliteDoesNotLoadTheKnownVulnerableVersion()
    {
        await using var database = await CompositionDatabase.Create();
        await using var context = database.CreateDbContext();
        using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT sqlite_version()";
        var version = Version.Parse((string)(await command.ExecuteScalarAsync())!);
        Assert.True(version >= new Version(3, 50, 2), version.ToString());
    }

    [Fact]
    public async Task CompletePublicJournalRoundTripsWithBothIndependentLegs()
    {
        await using var database = await CompositionDatabase.Create();
        var route = CompositionFixture.Completed();
        route.AttachInvoice(new InvoiceEntity { Id = "invoice", StoreId = "store" }, PaymentMethodId.Parse("BTC-LN"), CompositionFixture.Hash);
        await database.Repository.Add("store", route);

        var stored = await database.Repository.Get("store", route.RouteId);

        Assert.NotSame(route, stored);
        var originalJson = JObject.FromObject(route);
        var storedJson = JObject.FromObject(stored!);
        originalJson.Remove("Legs");
        storedJson.Remove("Legs");
        Assert.True(JToken.DeepEquals(originalJson, storedJson));
        Assert.Equal(JsonConvert.SerializeObject(route.Legs.OrderBy(l => l.Kind)),
            JsonConvert.SerializeObject(stored!.Legs.OrderBy(l => l.Kind)));
        Assert.Equal(25, stored!.IngressFeeSats);
        Assert.True(stored.SettlementVerified);
        Assert.Equal(route.RouteId, (await database.Repository.FindByPaymentHash("store", CompositionFixture.Hash.ToUpperInvariant()))!.RouteId);
    }

    [Fact]
    public async Task PreparedIdsAreDurableBeforeQuotesAndEachStepSurvivesReload()
    {
        await using var database = await CompositionDatabase.Create();
        var route = CompositionFixture.Create();
        await database.Repository.Add("store", route);
        var revision = route.Revision;
        route.Prepare(1000, CompositionFixture.Hash, CompositionFixture.OutgoingRfq, CompositionFixture.IngressRfq);
        await database.Repository.Save("store", route, revision);
        route = (await database.Repository.Get("store", route.RouteId))!;
        Assert.Equal("Prepared", route.Status);
        Assert.Equal(2, route.Legs.Count);
        Assert.All(route.Legs, leg => Assert.Null(leg.FromAmount));
        revision = route.Revision;
        route.RecordOutgoingQuote(CompositionFixture.OutgoingQuote(), CompositionFixture.EvmTerms());
        await database.Repository.Save("store", route, revision);
        route = (await database.Repository.Get("store", route.RouteId))!;
        Assert.Equal("OutgoingQuoted", route.Status);
        revision = route.Revision;
        route.RecordIngressQuote(CompositionFixture.IngressQuote());
        await database.Repository.Save("store", route, revision);
        route = (await database.Repository.Get("store", route.RouteId))!;
        Assert.Equal("IngressQuoted", route.Status);
        Assert.False(route.SettlementVerified);
    }

    [Fact]
    public async Task ExactAddAndSaveRetriesAreIdempotent()
    {
        await using var database = await CompositionDatabase.Create();
        var route = CompositionFixture.Prepared();
        await database.Repository.Add("store", route);
        await database.Repository.Add("store", route);
        var revision = route.Revision;
        route.RecordOutgoingQuote(CompositionFixture.OutgoingQuote(), CompositionFixture.EvmTerms());
        await database.Repository.Save("store", route, revision);
        await database.Repository.Save("store", route, revision);
        Assert.Single(await database.Repository.List("store"));
        Assert.Equal(route.Revision, (await database.Repository.Get("store", route.RouteId))!.Revision);
    }

    [Fact]
    public async Task StaleWriterCannotReplaceFactsOrAttachAfterAnotherRevision()
    {
        await using var database = await CompositionDatabase.Create();
        var route = CompositionFixture.Prepared();
        await database.Repository.Add("store", route);
        var stale = (await database.Repository.Get("store", route.RouteId))!;
        var revision = route.Revision;
        route.RecordOutgoingQuote(CompositionFixture.OutgoingQuote(), CompositionFixture.EvmTerms());
        await database.Repository.Save("store", route, revision);
        stale.AttachInvoice(new InvoiceEntity { Id = "invoice", StoreId = "store" }, PaymentMethodId.Parse("BTC-LN"), CompositionFixture.Hash);

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => database.Repository.Save("store", stale, revision));

        var stored = (await database.Repository.Get("store", route.RouteId))!;
        Assert.Equal("OutgoingQuoted", stored.Status);
        Assert.Null(stored.InvoiceId);
        Assert.Equal("2000000", stored.EvmAmount);
    }

    [Fact]
    public async Task RootConcurrencyTokenProtectsCompetingDatabaseContexts()
    {
        await using var database = await CompositionDatabase.Create();
        var route = CompositionFixture.Prepared();
        await database.Repository.Add("store", route);
        await using var first = database.CreateDbContext();
        await using var second = database.CreateDbContext();
        var winner = await first.InvoiceCompositions.Include(r => r.Legs).SingleAsync();
        var loser = await second.InvoiceCompositions.Include(r => r.Legs).SingleAsync();
        winner.RecordOutgoingQuote(CompositionFixture.OutgoingQuote(), CompositionFixture.EvmTerms());
        loser.RecordFailure(ArkCompositionFailure.RemoteUnavailable);
        await first.SaveChangesAsync();

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());

        Assert.Equal("OutgoingQuoted", (await database.Repository.Get("store", route.RouteId))!.Status);
    }

    [Fact]
    public async Task LosingRootRevisionRollsBackItsDifferentLegQuoteAtomically()
    {
        await using var database = await CompositionDatabase.Create();
        var route = CompositionFixture.Prepared();
        await database.Repository.Add("store", route);
        await using var first = database.CreateDbContext();
        await using var second = database.CreateDbContext();
        var winner = await first.InvoiceCompositions.Include(r => r.Legs).SingleAsync();
        var loser = await second.InvoiceCompositions.Include(r => r.Legs).SingleAsync();
        winner.RecordOutgoingQuote(CompositionFixture.OutgoingQuote(), CompositionFixture.EvmTerms());
        loser.RecordOutgoingQuote(CompositionFixture.OutgoingQuote() with { ToAmount = "3000000" },
            CompositionFixture.EvmTerms() with { Amount = "3000000" });
        await first.SaveChangesAsync();

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());

        var stored = (await database.Repository.Get("store", route.RouteId))!;
        Assert.Equal("2000000", stored.EvmAmount);
        Assert.Equal("2000000", stored.Legs.Single(l => l.Kind == "Outgoing").ToAmount);
    }

    [Fact]
    public async Task CancelledOperationsDoNotInsertOrAdvanceRoutes()
    {
        await using var database = await CompositionDatabase.Create();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            database.Repository.Add("store", CompositionFixture.Prepared(), cancellation.Token));
        Assert.Empty(await database.Repository.List("store"));
    }

    [Fact]
    public async Task PaymentHashCannotBeReusedAcrossStoresOrRoutes()
    {
        await using var database = await CompositionDatabase.Create();
        await database.Repository.Add("store", CompositionFixture.Prepared());
        var duplicate = CompositionFixture.Create(storeId: "other");
        duplicate.Prepare(1000, CompositionFixture.Hash, new string('1', 64), new string('2', 64));

        await Assert.ThrowsAsync<DbUpdateException>(() => database.Repository.Add("other", duplicate));

        Assert.Empty(await database.Repository.List("other"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RfqCannotBeReusedEvenForTheOppositeLeg(bool oppositeLeg)
    {
        await using var database = await CompositionDatabase.Create();
        await database.Repository.Add("store", CompositionFixture.Prepared());
        var duplicate = CompositionFixture.Create();
        duplicate.Prepare(1000, new string('f', 64), oppositeLeg ? new string('1', 64) : CompositionFixture.OutgoingRfq,
            oppositeLeg ? CompositionFixture.OutgoingRfq : new string('2', 64));

        await Assert.ThrowsAsync<DbUpdateException>(() => database.Repository.Add("store", duplicate));

        Assert.Single(await database.Repository.List("store"));
    }

    [Fact]
    public async Task CrossStoreReadsWritesAndHashLookupsAreDenied()
    {
        await using var database = await CompositionDatabase.Create();
        var route = CompositionFixture.Prepared();
        await database.Repository.Add("store", route);
        Assert.Null(await database.Repository.Get("other", route.RouteId));
        Assert.Null(await database.Repository.FindByPaymentHash("other", CompositionFixture.Hash));
        Assert.Empty(await database.Repository.List("other"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => database.Repository.Add("other", route));
        await Assert.ThrowsAsync<InvalidOperationException>(() => database.Repository.Save("other", route, route.Revision));
        await Assert.ThrowsAsync<ArgumentException>(() => database.Repository.List(""));
    }

    [Fact]
    public async Task InvoiceRailRenewalsStayIndependentAndListsAreBounded()
    {
        await using var database = await CompositionDatabase.Create();
        for (var i = 0; i < 4; i++)
        {
            var route = CompositionFixture.Create(i == 0 ? "ARKADE" : "BTC-LN");
            route.AssignPaymentHash(new string((char)('a' + i), 64));
            route.AttachInvoice(new InvoiceEntity { Id = "invoice", StoreId = "store" }, PaymentMethodId.Parse(route.PaymentMethodId), route.PaymentHash!);
            await database.Repository.Add("store", route);
        }

        Assert.Equal(4, (await database.Repository.List("store", "invoice")).Count);
        Assert.Equal(3, (await database.Repository.List("store", "invoice", "BTC-LN")).Count);
        Assert.Single(await database.Repository.List("store", "invoice", "BTC-LN", 2, 1));
        Assert.Empty(await database.Repository.List("store", "unknown"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => database.Repository.List("store", take: 101));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => database.Repository.List("store", skip: -1));
    }

    [Fact]
    public async Task RelationalModelHasOneLegPerKindAndNoSecretColumns()
    {
        await using var database = await CompositionDatabase.Create();
        await using var context = database.CreateDbContext();
        var root = context.Model.FindEntityType(typeof(ArkInvoiceComposition))!;
        var leg = context.Model.FindEntityType(typeof(ArkInvoiceCompositionLeg))!;
        Assert.True(root.FindProperty(nameof(ArkInvoiceComposition.Revision))!.IsConcurrencyToken);
        Assert.Equal(["RfqId"], leg.FindPrimaryKey()!.Properties.Select(p => p.Name));
        Assert.Contains(leg.GetIndexes(), i => i.IsUnique && i.Properties.Select(p => p.Name).SequenceEqual(["RouteId", "Kind"]));
        Assert.Single(leg.GetForeignKeys());
        Assert.All(root.GetProperties().Concat(leg.GetProperties()), property =>
        {
            Assert.DoesNotContain("preimage", property.Name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secret", property.Name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("cipher", property.Name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("descriptor", property.Name, StringComparison.OrdinalIgnoreCase);
        });
        Assert.Equal(78, leg.FindProperty("ToAmount")!.GetMaxLength());
        Assert.Equal(78, root.FindProperty("EvmAmount")!.GetMaxLength());
    }
}

internal sealed class CompositionDatabase : IDbContextFactory<ArkPluginDbContext>, IAsyncDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    internal ArkInvoiceCompositionRepository Repository => new(this);

    internal static async Task<CompositionDatabase> Create()
    {
        var database = new CompositionDatabase();
        await database._connection.OpenAsync();
        await using var context = database.CreateDbContext();
        await context.Database.EnsureCreatedAsync();
        return database;
    }

    public ArkPluginDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ArkPluginDbContext>()
        .UseSqlite(_connection).Options);

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}
