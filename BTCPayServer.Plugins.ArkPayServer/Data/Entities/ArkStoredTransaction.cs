using BTCPayServer.Plugins.ArkPayServer.Data.Entities.Enums;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.ArkPayServer.Data.Entities;

public class ArkStoredTransaction
{
    public string TransactionId { get; set; }
    public string Psbt { get; set; }
    public StoredTransactionState State { get; set; }
    public List<VTXO> CreatedVtxos { get; set; } = [];
    public List<VTXO> SpentVtxos { get; set; } = [];

    internal static void OnModelCreating(ModelBuilder builder)
    {
        var entity = builder.Entity<ArkStoredTransaction>();
        
        entity.HasKey(e => e.TransactionId);

        entity.HasMany(e => e.CreatedVtxos)
            .WithOne(v => v.CreatedByTransaction)
            .HasForeignKey(v => v.TransactionId)
            .OnDelete(DeleteBehavior.Restrict);

        entity.HasMany(e => e.SpentVtxos)
            .WithOne(v => v.SpentByTransaction)
            .HasForeignKey(v => v.SpentByTransactionId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
