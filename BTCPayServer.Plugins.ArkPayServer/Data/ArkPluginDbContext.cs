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
    /// <summary>Globally reserved composition RFQs and public leg facts.</summary>
    public DbSet<ArkInvoiceCompositionLeg> InvoiceCompositionLegs { get; set; }


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
            entity.Property(c => c.Revision).IsConcurrencyToken();
            entity.Property(c => c.SwapContractAddress).HasMaxLength(42);
            entity.Property(c => c.EvmAmount).HasMaxLength(78);
            entity.Property(c => c.EvmTokenAddress).HasMaxLength(42);
            entity.Property(c => c.EvmClaimAddress).HasMaxLength(42);
            entity.Property(c => c.EvmRefundAddress).HasMaxLength(42);
            entity.Property(c => c.EvmTimeoutBlock).HasMaxLength(78);
            entity.Property(c => c.IngressClaimTransactionId).HasMaxLength(64);
            entity.Property(c => c.EvmLockTransactionId).HasMaxLength(66);
            entity.Property(c => c.EvmObservedAtBlock).HasMaxLength(78);
            entity.Property(c => c.EvmProvenAtBlock).HasMaxLength(78);
            entity.Property(c => c.EvmClaimTransactionId).HasMaxLength(66);
            entity.Property(c => c.FailureCode).HasConversion<string>().HasMaxLength(32);
            entity.HasMany(c => c.Legs).WithOne().HasForeignKey(l => l.RouteId).OnDelete(DeleteBehavior.Cascade);
            entity.Navigation(c => c.Legs).HasField("_legs").UsePropertyAccessMode(PropertyAccessMode.Field);
        });
        modelBuilder.Entity<ArkInvoiceCompositionLeg>(entity =>
        {
            entity.ToTable("InvoiceCompositionLegs", "BTCPayServer.Plugins.Ark");
            entity.HasKey(l => l.RfqId);
            entity.HasIndex(l => new { l.RouteId, l.Kind }).IsUnique();
            entity.Property(l => l.RfqId).HasMaxLength(64);
            entity.Property(l => l.Kind).HasMaxLength(16);
            entity.Property(l => l.SolverPubkey).HasMaxLength(64);
            entity.Property(l => l.FromAmount).HasMaxLength(78);
            entity.Property(l => l.ToAmount).HasMaxLength(78);
            entity.Property(l => l.LockupScript).HasMaxLength(68);
            entity.Property(l => l.LockupAddress).HasMaxLength(512);
            entity.Property(l => l.PayoutScript).HasMaxLength(68);
            entity.Property(l => l.FundingTransactionId).HasMaxLength(64);
        });
    }
}
