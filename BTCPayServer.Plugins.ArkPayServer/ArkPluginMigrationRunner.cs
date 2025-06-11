using BTCPayServer.Abstractions.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.ArkPayServer;

public class ArkPluginMigrationRunner(
    ILogger<ArkPluginMigrationRunner> logger,
    ArkPluginDbContextFactory dbContextFactory,
    ISettingsRepository settingsRepository) : IStartupTask
{
    private class ArkPluginDataMigrationHistory
    {
        public bool InitialSetup { get; set; }
    }

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var settings =
            await settingsRepository.GetSettingAsync<ArkPluginDataMigrationHistory>() ??
            new ArkPluginDataMigrationHistory();

        await using var ctx = dbContextFactory.CreateContext();
        var pendingMigrations = (await ctx.Database.GetPendingMigrationsAsync(cancellationToken: cancellationToken)).ToList();
        if (pendingMigrations.Count != 0)
        {
            logger.LogInformation("Applying {Count} migrations", pendingMigrations.Count);
            await ctx.Database.MigrateAsync(cancellationToken: cancellationToken);
        }
        else
        {
            logger.LogInformation("No migrations to apply");
        }
        if (!settings.InitialSetup)
        {
            settings.InitialSetup = true;
            await settingsRepository.UpdateSetting(settings);
        }
    }
}