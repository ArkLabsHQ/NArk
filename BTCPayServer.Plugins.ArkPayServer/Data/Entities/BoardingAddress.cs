using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.ArkPayServer.Data.Entities;

public class BoardingAddress
{
    public string OnchainAddress { get; set; }
    public Guid WalletId { get; set; }
    public ArkWallet Wallet { get; set; }
    
    /// <summary>
    /// The derivation index used to generate this address
    /// </summary>
    public uint DerivationIndex { get; set; }
    
    /// <summary>
    /// The boarding exit delay in sequence format
    /// </summary>
    public uint BoardingExitDelay { get; set; }
    
    /// <summary>
    /// The taproot descriptor or tapscripts returned by the operator's GetBoardingAddress call
    /// </summary>
    public string ContractData { get; set; }
    
    /// <summary>
    /// When this boarding address was created
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }

    internal static void OnModelCreating(ModelBuilder builder)
    {
        var entity = builder.Entity<BoardingAddress>();
        
        entity.HasKey(b => b.OnchainAddress);
        entity.HasOne(b => b.Wallet)
            .WithMany()
            .HasForeignKey(b => b.WalletId)
            .OnDelete(DeleteBehavior.Cascade);
        
        entity.HasIndex(b => new { b.WalletId, b.DerivationIndex }).IsUnique();
    }
}