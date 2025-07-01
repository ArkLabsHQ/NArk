using Microsoft.Extensions.Hosting;

namespace NArk.Boltz;

public class BoltzSubscriptionService : IHostedService, IAsyncDisposable
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    public ValueTask DisposeAsync()
    {
        throw new NotImplementedException();
    }
}