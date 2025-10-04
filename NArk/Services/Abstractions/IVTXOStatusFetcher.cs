using NBitcoin;

namespace NArk.Services.Abstractions;

/// <summary>
/// Interface for checking VTXO status and freeze state
/// </summary>
public interface IVTXOStatusFetcher
{
    /// <summary>
    /// Check if a VTXO is frozen (locked by an active intent)
    /// </summary>
    /// <param name="outpoint">The VTXO outpoint</param>
    /// <returns>True if the VTXO is frozen, false otherwise</returns>
    Task<bool> IsVTXOFrozenAsync(OutPoint outpoint);
    
    /// <summary>
    /// Get the intent ID that has frozen a VTXO, if any
    /// </summary>
    /// <param name="outpoint">The VTXO outpoint</param>
    /// <returns>The intent ID if frozen, null otherwise</returns>
    Task<string?> GetFreezingIntentIdAsync(OutPoint outpoint);
    
    /// <summary>
    /// Get all frozen VTXOs
    /// </summary>
    /// <returns>List of frozen VTXO outpoints</returns>
    Task<List<OutPoint>> GetFrozenVTXOsAsync();
}
