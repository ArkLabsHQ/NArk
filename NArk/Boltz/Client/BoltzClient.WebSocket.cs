namespace NArk.Wallet.Boltz;

// This file's content has been moved to BoltzListener.cs
// It can be removed from the project if it's no longer needed as a partial class placeholder.
public partial class BoltzClient
{
    // WebSocket related methods (ConnectAsync, DisconnectAsync, SubscribeAsync, UnsubscribeAsync)
    // are now handled by the BoltzListener instance and exposed through BoltzClient.
    // See BoltzClient.cs for the new public methods that delegate to BoltzListener:
    // - ConnectWebSocketAsync
    // - DisconnectWebSocketAsync
    // - SubscribeToSwapUpdatesAsync
    // - UnsubscribeFromSwapUpdatesAsync
}

