using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.ArkPayServer.Data.Entities;

public class ArkWalletContract
{
    [Key]
    public string Script { get; set; }
    
    public bool Active { get; set; }
    public string Type { get; set; }
    [Column(TypeName = "jsonb")]
    public Dictionary<string, string> ContractData { get; set; }
    
    public ArkWallet Wallet { get; set; }
    public string WalletId { get; set; }

    internal static void OnModelCreating(ModelBuilder builder)
    {
        var entity = builder.Entity<ArkWalletContract>();
        entity.HasKey(w => new {w.Script, w.WalletId});
        
        entity.HasOne(w => w.Wallet)
            .WithMany(w => w.Contracts)
            .HasForeignKey(w => w.WalletId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
