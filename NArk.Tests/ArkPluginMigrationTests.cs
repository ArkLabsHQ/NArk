using BTCPayServer.Plugins.ArkPayServer.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace NArk.Tests;

public class ArkPluginMigrationTests
{
    [Fact]
    public void PinnedSdkModelHasAMigrationBeforeServerStartup()
    {
        using var context = new DesignTimeDbContextFactory().CreateDbContext([]);

        Assert.False(context.Database.HasPendingModelChanges());
    }
}
