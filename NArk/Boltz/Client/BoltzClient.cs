namespace NArk.Wallet.Boltz;

using System;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;

public partial class BoltzClient
{
    private readonly HttpClient _httpClient;
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="BoltzClient"/> class.
    /// </summary>
    /// <param name="httpClient">The HttpClient to use for REST API requests.</param>
    public BoltzClient(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    /// <summary>
    /// Derives the WebSocket URI from the base HTTP URI.
    /// </summary>
    /// <param name="baseHttpUri">The base HTTP URI of the Boltz API.</param>
    /// <returns>The corresponding WebSocket URI.</returns>
    /// <exception cref="ArgumentNullException">Thrown when baseHttpUri is null.</exception>
    public Uri DeriveWebSocketUri()
    {
        var baseHttpUri = _httpClient.BaseAddress;
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

    /// <summary>
    /// Posts a value as JSON using the shared serialization options.
    /// </summary>
    private Task<HttpResponseMessage> PostAsJsonAsync<T>(string uri, T value, CancellationToken ct = default)
    {
        return _httpClient.PostAsJsonAsync(uri, value, _jsonOptions, ct);
    }
}
