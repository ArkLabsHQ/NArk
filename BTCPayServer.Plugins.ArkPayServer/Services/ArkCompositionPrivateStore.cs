using System.Security.Cryptography;
using System.Text.Json;
using BTCPayServer.Abstractions.Contracts;
using Microsoft.AspNetCore.DataProtection;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

public sealed class ArkCompositionPrivateStore(ISettingsRepository settings, IDataProtectionProvider protection)
{
    public async Task<T?> ReadAsync<T>(string storeId, Guid routeId) where T : class
    {
        var envelope = await settings.GetSettingAsync<Envelope>(Name(routeId));
        if (envelope is null) return null;
        var bytes = Protector(storeId, routeId).Unprotect(Convert.FromBase64String(envelope.Ciphertext));
        try { return JsonSerializer.Deserialize<T>(bytes) ?? throw new CryptographicException("Invalid route recovery state."); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public async Task WriteAsync<T>(string storeId, Guid routeId, T state) where T : class
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state);
        try
        {
            await settings.UpdateSetting(new Envelope(Convert.ToBase64String(Protector(storeId, routeId).Protect(bytes))), Name(routeId));
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private IDataProtector Protector(string storeId, Guid routeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeId);
        if (routeId == Guid.Empty) throw new ArgumentException("A route identity is required.");
        return protection.CreateProtector("BTCPayServer.Plugins.ArkPayServer.CompositionRecovery.v1", storeId, routeId.ToString("N"));
    }

    private static string Name(Guid routeId) => $"ArkCompositionRecovery-{routeId:N}";
    public sealed record Envelope(string Ciphertext)
    {
        public override string ToString() => nameof(Envelope);
    }
}
