using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using NArk;
using NBitcoin.Secp256k1;

namespace BTCPayServer.Plugins.ArkPayServer.Data.Entities;

public class ArkWallet
{
    public string Id { get; set; }
    public string Wallet { get; set; }
    public List<ArkWalletContract> Contracts { get; set; } = [];
    

    public ECXOnlyPubKey PublicKey => ArkExtensions.GetXOnlyPubKeyFromWallet(Wallet);

    public List<ArkSwap> Swaps { get; set; }

    internal static void OnModelCreating(ModelBuilder builder)
    {
        var entity = builder.Entity<ArkWallet>();
        entity.HasKey(w => w.Id);
        entity.HasIndex(w => w.Wallet).IsUnique();
        entity.HasMany(w => w.Contracts)
            .WithOne(contract => contract.Wallet)
            .HasForeignKey(contract => contract.WalletId);
        entity.HasMany(w => w.Swaps)
            .WithOne(contract => contract.Wallet)
            .HasForeignKey(contract => contract.WalletId);
    }
}
