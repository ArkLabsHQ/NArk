using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.ArkPayServer.Data.Entities;

public class ArkWallet
{
    public Guid Id { get; set; }
    
    public string PubKey { get; set; }
    
    public byte[] EncryptedPrvkey { get; set; }
    
    public byte[] PasswordHash { get; set; }
    
    public uint CurrentIndex { get; set; }
    public List<ArkWalletContract> Contracts { get; set; } = new();

    internal static void OnModelCreating(ModelBuilder builder)
    {
        var entity = builder.Entity<ArkWallet>();
        entity.HasKey(w => w.Id);
        entity.HasMany(w => w.Contracts)
            .WithOne();
    }
}
