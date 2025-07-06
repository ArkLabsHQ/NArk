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

    /// <summary>
    /// Initializes a new instance of the <see cref="BoltzClient"/> class.
    /// </summary>
    /// <param name="httpClient">The HttpClient to use for REST API requests.</param>
    public BoltzClient(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }
}
