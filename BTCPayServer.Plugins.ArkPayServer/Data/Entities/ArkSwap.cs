﻿using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.ArkPayServer.Data.Entities;

public class ArkSwap
{
    public string SwapId { get; set; }
    public string WalletId { get; set; }
    
    public ArkSwapType SwapType { get; set; } 
    
    public string Invoice { get; set; }
    public long ExpectedAmount { get; set; }
    public string ContractScript { get; set; } // Foreign key to ArkWalletContract
    
    // Navigation property
    public ArkWalletContract Contract { get; set; }
    public ArkWallet Wallet { get; set; }
    
    public ArkSwapStatus Status { get; set; } 
    
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; }= DateTimeOffset.UtcNow;

    public string Hash { get; set; }


    public static void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ArkSwap>(entity =>
        {
            entity.HasKey(e => new { e.SwapId, e.WalletId});
            entity.Property(e => e.WalletId).IsRequired();
            entity.Property(e => e.SwapType).IsRequired();
            entity.Property(e => e.Invoice).IsRequired();
            entity.Property(e => e.Status).IsRequired();
            entity.Property(e => e.CreatedAt).IsRequired();
            
            // Configure foreign key relationship to ArkWalletContract
            entity.HasOne(e => e.Contract)
                .WithMany(wallet => wallet.Swaps)
                .HasForeignKey(e => new {e.ContractScript, e.WalletId});
            entity.HasOne(e => e.Wallet)
                .WithMany(wallet => wallet.Swaps)
                .HasForeignKey(e => e.WalletId);
        });
    }
    
    
}