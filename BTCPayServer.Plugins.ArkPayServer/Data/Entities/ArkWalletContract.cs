using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.ArkPayServer.Data.Entities;

public class ArkWalletContract
{
    public string Script { get; set; }
    // public string DescriptorTemplate { get; set; }
    public bool Active { get; set; }
    public string Type { get; set; }
    [Column(TypeName = "jsonb")]
    public Dictionary<string, string> ContractData { get; set; }

    public string WalletId { get; set; }

    internal static void OnModelCreating(ModelBuilder builder)
    {
        var entity = builder.Entity<ArkWalletContract>();
        
        entity.HasKey(c => c.Script);
    }
}
