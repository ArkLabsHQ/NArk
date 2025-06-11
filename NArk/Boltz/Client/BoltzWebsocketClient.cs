using System.Text.Json.Nodes;

namespace NArk.Wallet.Boltz;

using System;
using System.IO;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Manages WebSocket communication with the Boltz API, including connection, subscriptions, and auto-reconnection.
/// </summary>
public class BoltzWebsocketClient : IAsyncDisposable
{
    private ClientWebSocket? _webSocket;
    private readonly Uri _webSocketUri;
    private CancellationTokenSource? _receiveLoopCts;
    private readonly SemaphoreSlim _operationSemaphore = new(1, 1);

    /// <summary>
    /// Occurs for any WebSocket event, providing a common event object.
    /// </summary>
    public event Func<WebSocketResponse, Task>? OnAnyEventReceived;

    /// <summary>
    /// Initializes a new instance of the <see cref="BoltzWebsocketClient"/> class.
    /// </summary>
    /// <param name="webSocketUri">The explicit URI for the WebSocket connection.</param>
    public BoltzWebsocketClient(Uri webSocketUri)
    {
        _webSocketUri = webSocketUri ?? throw new ArgumentNullException(nameof(webSocketUri));
    }

    public static async Task<BoltzWebsocketClient> CreateAndConnectAsync(Uri webSocketUri,
        CancellationToken cancellationToken = default)
    {
        var listener = new BoltzWebsocketClient(webSocketUri);
        await listener.ConnectAsync(cancellationToken);
        return listener;
    }

    /// <summary>
    /// Connects to the Boltz WebSocket API.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token to cancel the connection attempt.</param>
    /// <exception cref="InvalidOperationException">Thrown if already connected/connecting (unless it's a reconnect attempt).</exception>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _operationSemaphore.WaitAsync(cancellationToken); // Wait for the semaphore
        try
        {
            if (_webSocket is { State: WebSocketState.Open or WebSocketState.Connecting })
            {
                throw new InvalidOperationException(
                    "WebSocket is already connected or connecting. Call DisconnectAsync first.");
            }

            _webSocket?.Dispose();
            _webSocket = new ClientWebSocket();

            await _receiveLoopCts?.CancelAsync();
            _receiveLoopCts?.Dispose();
            _receiveLoopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            // Try-catch is removed here; if ConnectAsync fails, the exception will propagate to the caller.
            await _webSocket.ConnectAsync(_webSocketUri, _receiveLoopCts.Token);

            // Optionally, resubscribe if there are active subscriptions from a previous session (if desired behavior)
            // For now, we assume a clean connect. If ResubscribeAsync is needed on manual reconnect, user can call it.
            // if (_activeSubscriptions.Any()) await ResubscribeAsync(_receiveLoopCts.Token);

            _ = Task.Run(() => ReceiveLoopAsync(_receiveLoopCts.Token), _receiveLoopCts.Token);
        }
        finally
        {
            _operationSemaphore.Release(); // Release the semaphore
        }
    }

    private async Task<WebSocketResponse> SendRequest<T>(string op, string channel, T args,
        CancellationToken cancellationToken)
    {
        try
        {
            await _operationSemaphore.WaitAsync(cancellationToken); // Wait for the semaphore

            if (_webSocket is not { State: WebSocketState.Open })
            {
                throw new InvalidOperationException("WebSocket is not connected.");
            }

            var request = new WebSocketRequest
            {
                Operation = op,
                Channel = channel,
                Args = (JsonSerializer.SerializeToNode(args) as JsonArray)!
            };

            var message = JsonSerializer.SerializeToUtf8Bytes(request);
            var responseTask = WaitForResponse(op, channel, cancellationToken);

            await _webSocket.SendAsync(message, WebSocketMessageType.Text, true, cancellationToken);
            return await responseTask;
        }
        finally
        {
            _operationSemaphore.Release(); // Release the semaphore
        }
    }

    private async Task<WebSocketResponse> WaitForResponse(string op, string channel,
        CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<WebSocketResponse>();

        Task OnEvent(WebSocketResponse boltzWebSocketEvent)
        {
            if (boltzWebSocketEvent.Event == op && boltzWebSocketEvent.Channel == channel)
            {
                tcs.SetResult(boltzWebSocketEvent);
            }

            return Task.CompletedTask;
        }

        if (_webSocket is not { State: WebSocketState.Open })
        {
            throw new InvalidOperationException("WebSocket is not connected.");
        }

        OnAnyEventReceived += OnEvent;
        CancellationTokenRegistration registration = default;
        try
        {
            registration = _receiveLoopCts.Token.Register(() => tcs.SetCanceled(_receiveLoopCts.Token));
            return await tcs.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            await registration.DisposeAsync();
            OnAnyEventReceived -= OnEvent; // Ensure we unsubscribe after waiting for the response
        }
    }


    /// <summary>
    /// Unsubscribes from WebSocket updates for specific swap IDs.
    /// </summary>
    public async Task UnsubscribeAsync(string[] swapIds, CancellationToken cancellationToken = default)
    {
        _ = await SendRequest("subscribe", "swap.update", swapIds, cancellationToken);
    }

    
    /// <summary>
    /// Subscribes to WebSocket updates for specific swap IDs.
    /// </summary>
    public async Task SubscribeAsync(string[] swapIds, CancellationToken cancellationToken = default)
    {
        _ = await SendRequest("subscribe", "swap.update", swapIds, cancellationToken);
    }
    
    
    
    

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new ArraySegment<byte>(new byte[8192]);
        try
        {
            while (_webSocket is { State: WebSocketState.Open } && !cancellationToken.IsCancellationRequested)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    if (cancellationToken.IsCancellationRequested) break;
                    result = await _webSocket.ReceiveAsync(buffer, cancellationToken);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }

                    if (buffer.Array != null) ms.Write(buffer.Array, buffer.Offset, result.Count);
                } while (!result.EndOfMessage && !cancellationToken.IsCancellationRequested);

                if (cancellationToken.IsCancellationRequested) break;


                ms.Seek(0, SeekOrigin.Begin);
                var response =
                    await JsonSerializer.DeserializeAsync<WebSocketResponse>(ms, cancellationToken: cancellationToken);
                _ = OnAnyEventReceived?.Invoke(response);
            }

            await _receiveLoopCts.CancelAsync();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }


    /// <summary>
    /// Disposes the BoltzListener, disconnecting the WebSocket if connected.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await _operationSemaphore.WaitAsync();
            await _receiveLoopCts.CancelAsync();
            _webSocket?.Dispose(); // Dispose the WebSocket
        } // Wait for the semaphore (no CancellationToken for DisposeAsync signature)

        finally
        {
            _operationSemaphore.Release(); // Release the semaphore

            _operationSemaphore.Dispose();
        }
    }
}