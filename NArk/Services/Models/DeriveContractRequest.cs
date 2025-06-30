namespace NArk.Services.Models;

public record DeriveContractRequest(string Wallet, byte[]? Tweak = null);