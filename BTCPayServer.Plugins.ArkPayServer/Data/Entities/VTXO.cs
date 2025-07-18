using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.ArkPayServer.Data.Entities;

public class VTXO
{
    public string Script { get; set; }
    public string TransactionId { get; set; }
    public int TransactionOutputIndex { get; set; }
    public string? SpentByTransactionId { get; set; }
    // public int? SpentByTransactionIdInputIndex { get; set; }
    public long Amount { get; set; }
    public DateTimeOffset SeenAt { get; set; }
    // public DateTimeOffset? SpentAt { get; set; }
    public bool IsNote { get; set; }
    // public ArkStoredTransaction? SpentByTransaction { get; set; }
    // public ArkStoredTransaction CreatedByTransaction { get; set; }
    
    internal static void OnModelCreating(ModelBuilder builder)
    {
        var entity = builder.Entity<VTXO>();
        entity.Property(vtxo => vtxo.SpentByTransactionId).HasDefaultValue(null);
        
        entity.HasKey(e => new { e.TransactionId, e.TransactionOutputIndex });
    }
}