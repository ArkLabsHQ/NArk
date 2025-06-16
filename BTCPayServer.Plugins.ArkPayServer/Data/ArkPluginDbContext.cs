using Microsoft.EntityFrameworkCore;
using NBitcoin.WalletPolicies;

namespace BTCPayServer.Plugins.ArkPayServer;

public class ArkPluginDbContext(DbContextOptions<ArkPluginDbContext> options) : DbContext(options)
{

    public DbSet<ArkWallet> Wallets { get; set; }
    public DbSet<ArkWalletContract> WalletContracts { get; set; }
    
    public DbSet<ArkStoredTransaction> Transactions { get; set; }
    public DbSet<VTXO> Vtxos { get; set; }
    
    
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.HasDefaultSchema("BTCPayServer.Plugins.Ark");

        modelBuilder.Entity<ArkStoredTransaction>(entity =>
        {
            entity.HasKey(e => e.TransactionId);

            entity.HasMany(e => e.CreatedVtxos)
                .WithOne(v => v.CreatedByTransaction)
                .HasForeignKey(v => v.TransactionId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasMany(e => e.SpentVtxos)
                .WithOne(v => v.SpentByTransaction)
                .HasForeignKey(v => v.SpentByTransactionId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<VTXO>(entity =>
        {
            entity.HasKey(e => new { e.TransactionId, e.TransactionOutputIndex });
        });

        modelBuilder.Entity<ArkWallet>(entity =>
        {
            entity.HasKey(w => w.DescriptorTemplate);
            entity.HasMany(w => w.Contracts)
                .WithOne()
                .HasForeignKey(c => c.DescriptorTemplate);
        });

        modelBuilder.Entity<ArkWalletContract>(entity =>
        {
            entity.HasKey(c => c.Script);
        });
    }
}