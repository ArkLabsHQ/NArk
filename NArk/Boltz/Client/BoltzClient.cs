namespace NArk.Wallet.Boltz;

using System;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

public partial class BoltzClient
{
    private readonly HttpClient _httpClient;
    private readonly BoltzWebsocketClient _boltzWebsocketClient;
    private readonly Uri _webSocketUri; // Keep for reference if needed, or remove if BoltzListener handles all URI logic

    /// <summary>
    /// Initializes a new instance of the <see cref="BoltzClient"/> class with explicit WebSocket URI and auto-reconnect options.
    /// </summary>
    /// <param name="httpClient">The HttpClient to use for REST API requests.</param>
    /// <param name="webSocketUri">The explicit URI for the WebSocket connection.</param>
    public BoltzClient(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

    }
    public static Uri DeriveWebSocketUri(Uri? baseHttpUri)
    {
        if (baseHttpUri == null)
        {
            throw new ArgumentNullException(nameof(baseHttpUri), "HttpClient.BaseAddress cannot be null when WebSocket URI is not explicitly provided.");
        }

        var uriBuilder = new UriBuilder(baseHttpUri);
        uriBuilder.Scheme = baseHttpUri.Scheme == "https" ? "wss" : "ws";

        var path = uriBuilder.Path.TrimEnd('/');
        if (path.EndsWith("/v2"))
        {
            uriBuilder.Path = path + "/ws";
        }
        else
        {
            uriBuilder.Path = path + "/v2/ws";
        }
        return uriBuilder.Uri;
    }

}
