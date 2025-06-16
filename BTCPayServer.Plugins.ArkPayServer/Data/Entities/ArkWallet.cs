using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.ArkPayServer.Data.Entities;

public class ArkWallet
{
    public string DescriptorTemplate { get; set; }
    public uint CurrentIndex { get; set; }
    public List<ArkWalletContract> Contracts { get; set; } = new();

    internal static void OnModelCreating(ModelBuilder builder)
    {
        var entity = builder.Entity<ArkWallet>();
        entity.HasKey(w => w.DescriptorTemplate);
        entity.HasMany(w => w.Contracts)
            .WithOne()
            .HasForeignKey(c => c.DescriptorTemplate);
    }
}
