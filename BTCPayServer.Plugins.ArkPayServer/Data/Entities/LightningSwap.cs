using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.ArkPayServer.Data.Entities;

public class LightningSwap
{
    [Key]
    public string SwapId { get; set; } = null!;
    
    public string WalletId { get; set; } = null!;
    
    public string SwapType { get; set; } = null!; // "reverse" for LN->Ark, "submarine" for Ark->LN
    
    public string Invoice { get; set; } = null!;
    
    public string LockupAddress { get; set; } = null!;
    
    public long OnchainAmount { get; set; }
    
    public long TimeoutBlockHeight { get; set; }
    
    public string? PreimageHash { get; set; }
    
    public string? ClaimAddress { get; set; }
    
    public string? ContractData { get; set; } // Store the VHTLCContract data
    
    public string Status { get; set; } = "created";
    
    public string? TransactionId { get; set; }
    
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    
    public DateTimeOffset? SettledAt { get; set; }

    public static void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<LightningSwap>(entity =>
        {
            entity.HasKey(e => e.SwapId);
            entity.Property(e => e.WalletId).IsRequired();
            entity.Property(e => e.SwapType).IsRequired();
            entity.Property(e => e.Invoice).IsRequired();
            entity.Property(e => e.LockupAddress).IsRequired();
            entity.Property(e => e.Status).IsRequired().HasDefaultValue("created");
            entity.Property(e => e.CreatedAt).IsRequired();
        });
    }
}