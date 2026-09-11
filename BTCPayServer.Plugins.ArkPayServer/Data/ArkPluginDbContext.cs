using Microsoft.EntityFrameworkCore;
using NArk.Storage.EfCore;
using NArk.Storage.EfCore.Entities;

namespace BTCPayServer.Plugins.ArkPayServer.Data;

public class ArkPluginDbContext(DbContextOptions<ArkPluginDbContext> options) : DbContext(options)
{
    public DbSet<ArkWalletEntity> Wallets { get; set; }
    public DbSet<ArkWalletContractEntity> WalletContracts { get; set; }
    public DbSet<VtxoEntity> Vtxos { get; set; }
    public DbSet<ArkIntentEntity> Intents { get; set; }
    public DbSet<ArkIntentVtxoEntity> IntentVtxos { get; set; }
    
    public DbSet<ArkSwapEntity> Swaps { get; set; }// todo - will be replaced with bottom one 
    public DbSet<ArkadeSwapIntentEntity> ArkadeIntentSwaps { get; set; }
    public DbSet<ArkInvoiceComposition> InvoiceCompositions { get; set; }


    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ConfigureArkEntities(opts =>
        {
            opts.Schema = "BTCPayServer.Plugins.Ark";
        });
        modelBuilder.Entity<ArkInvoiceComposition>(entity =>
        {
            entity.ToTable("InvoiceCompositions", "BTCPayServer.Plugins.Ark");
            entity.HasKey(c => c.RouteId);
            entity.HasIndex(c => c.PaymentHash).IsUnique();
            entity.HasIndex(c => new { c.StoreId, c.InvoiceId, c.PaymentMethodId });
            entity.Property(c => c.PaymentHash).HasMaxLength(64).IsConcurrencyToken();
            entity.Property(c => c.InvoiceId).IsConcurrencyToken();
            entity.Property(c => c.PaymentMethodId).HasMaxLength(50);
            entity.Property(c => c.AssetId).HasMaxLength(88);
            entity.Property(c => c.Destination).HasMaxLength(42);
            entity.Property(c => c.Status).HasMaxLength(32);
        });
    }
}
