using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.ArkPayServer.Data.Entities;

public class ArkWallet
{
    public string Id { get; set; }
    public string Wallet { get; set; }
    public List<ArkWalletContract> Contracts { get; set; } = new();

    internal static void OnModelCreating(ModelBuilder builder)
    {
        var entity = builder.Entity<ArkWallet>();
        entity.HasKey(w => w.Id);
        entity.HasIndex(w => w.Wallet).IsUnique();
        entity.HasMany(w => w.Contracts)
            .WithOne()
            .HasForeignKey(c => c.WalletId);
    }
}
