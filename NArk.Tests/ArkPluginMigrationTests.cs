using BTCPayServer.Plugins.ArkPayServer.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit;

namespace NArk.Tests;

public class ArkPluginMigrationTests
{
    [Fact]
    public void RouteJournalMigrationIsAdditiveAndLeavesExistingRoutesInert()
    {
        using var context = new DesignTimeDbContextFactory().CreateDbContext([]);
        var assembly = context.GetService<IMigrationsAssembly>();
        var entry = assembly.Migrations.Single(m => m.Key.EndsWith("_AddCompositionRouteJournal"));
        var migration = assembly.CreateMigration(entry.Value, context.Database.ProviderName!);
        var additions = migration.UpOperations.OfType<AddColumnOperation>().ToArray();
        Assert.Equal(15, additions.Length);
        Assert.All(additions, operation => Assert.Equal("InvoiceCompositions", operation.Table));
        Assert.All(additions.Where(operation => operation.Name != "Revision"), operation => Assert.True(operation.IsNullable));
        Assert.Equal(0L, additions.Single(operation => operation.Name == "Revision").DefaultValue);
        var legs = Assert.Single(migration.UpOperations.OfType<CreateTableOperation>());
        Assert.Equal("InvoiceCompositionLegs", legs.Name);
        Assert.Equal(["RfqId"], legs.PrimaryKey!.Columns);
        Assert.Single(legs.ForeignKeys);
        Assert.All(migration.UpOperations, operation =>
            Assert.True(operation is AddColumnOperation or CreateTableOperation or CreateIndexOperation));
        var sql = context.GetService<IMigrator>().GenerateScript("20260911052408_AddInvoiceCompositions", entry.Key);
        Assert.Contains("CREATE TABLE", sql);
        Assert.DoesNotContain("DROP ", sql);
        Assert.DoesNotContain("UPDATE ", sql);
        Assert.DoesNotContain("preimage", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PinnedSdkModelHasAMigrationBeforeServerStartup()
    {
        using var context = new DesignTimeDbContextFactory().CreateDbContext([]);

        Assert.False(context.Database.HasPendingModelChanges());
    }
}
