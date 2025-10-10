using BTCPayServer.Plugins.ArkPayServer.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.ArkPayServer.Data;

public class ArkPluginDbContext(DbContextOptions<ArkPluginDbContext> options) : DbContext(options)
{
    public DbSet<ArkWallet> Wallets { get; set; }
    public DbSet<ArkWalletContract> WalletContracts { get; set; }
    public DbSet<VTXO> Vtxos { get; set; }
    public DbSet<ArkSwap> Swaps { get; set; }
    public DbSet<ArkIntent> Intents { get; set; }
    // public DbSet<BoardingAddress> BoardingAddresses { get; set; }
    // public DbSet<ArkStoredTransaction> Transactions { get; set; }
    
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.HasDefaultSchema("BTCPayServer.Plugins.Ark");
        SetupDbRelations(modelBuilder);
    }

    private static void SetupDbRelations(ModelBuilder modelBuilder)
    {
        // ArkStoredTransaction.OnModelCreating(modelBuilder);
        VTXO.OnModelCreating(modelBuilder);
        ArkWallet.OnModelCreating(modelBuilder);
        ArkWalletContract.OnModelCreating(modelBuilder);
        ArkSwap.OnModelCreating(modelBuilder);
        ArkIntent.OnModelCreating(modelBuilder);
        // BoardingAddress.OnModelCreating(modelBuilder);
    }
}


public class ArkIntent
{
    public string Id { get; set; }
    public string WalletId { get; set; }
    public ArkIntentState State { get; set; }
    
    public DateTimeOffset ValidFrom { get; set; }
    public DateTimeOffset ValidUntil { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public List<VTXO> LockedVtxos { get; set; }

    public string RegisterProof { get; set; }
    public string RegisterProofMessage { get; set; }
    public string DeleteProof { get; set; }
    public string DeleteProofMessage { get; set; }

    public string? BatchId { get; set; }
    public string? CommitmentTransactionId { get; set; }
    public string? CancellationReason { get; set; }

    public string[] PartialForfeits{ get; set; }

    internal static void OnModelCreating(ModelBuilder builder)
    {
        var entity = builder.Entity<ArkIntent>();
        entity.HasKey(e => e.Id);
        entity.Property(e => e.BatchId).HasDefaultValue(null);
        entity.Property(e => e.CommitmentTransactionId).HasDefaultValue(null);
        entity.Property(e => e.CancellationReason).HasDefaultValue(null);
        entity.HasMany(e => e.LockedVtxos)
            .WithOne()
            .HasForeignKey("ArkIntentId")
            .IsRequired(false);
    }
}

public enum ArkIntentState
{
    WaitingToSubmit,
    WaitingForBatch,
    BatchFailed,
    BatchSucceeded,
    Cancelled
}