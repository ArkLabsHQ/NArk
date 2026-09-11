using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.ArkPayServer.Data;

/// <summary>Store-scoped durable public route journal; never creates remote quotes.</summary>
/// <param name="factory">Plugin database context factory.</param>
public sealed class ArkInvoiceCompositionRepository(IDbContextFactory<ArkPluginDbContext> factory)
{
    /// <summary>Loads one owned route and both public RFQ journals.</summary>
    public async Task<ArkInvoiceComposition?> Get(string storeId, Guid routeId, CancellationToken cancellationToken = default)
    {
        ValidateStore(storeId);
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        return await Owned(context, storeId).AsNoTracking().SingleOrDefaultAsync(r => r.RouteId == routeId, cancellationToken);
    }
    /// <summary>Finds an owned prompt by its exact public hash.</summary>
    public async Task<ArkInvoiceComposition?> FindByPaymentHash(string storeId, string paymentHash, CancellationToken cancellationToken = default)
    {
        ValidateStore(storeId);
        var hash = ArkCompositionValidation.Hash(paymentHash);
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        return await Owned(context, storeId).AsNoTracking().SingleOrDefaultAsync(r => r.PaymentHash == hash, cancellationToken);
    }
    /// <summary>Lists at most 100 owned routes, optionally filtered by invoice and rail.</summary>
    public async Task<IReadOnlyList<ArkInvoiceComposition>> List(string storeId, string? invoiceId = null, string? paymentMethodId = null,
        int skip = 0, int take = 50, CancellationToken cancellationToken = default)
    {
        ValidateStore(storeId);
        if (skip < 0 || take is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(take), "Specify bounded pagination.");
        if (paymentMethodId is not (null or "ARKADE" or "BTC-LN" or "BTC-CHAIN"))
            throw new ArgumentException("Specify a supported source payment method.");
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        var query = Owned(context, storeId).AsNoTracking();
        if (invoiceId is not null) query = query.Where(r => r.InvoiceId == invoiceId);
        if (paymentMethodId is not null) query = query.Where(r => r.PaymentMethodId == paymentMethodId);
        return await query.OrderBy(r => r.RouteId).Skip(skip).Take(take).ToArrayAsync(cancellationToken);
    }
    /// <summary>Persists a route and reserved RFQ ids; an exact retry is a no-op.</summary>
    public async Task Add(string storeId, ArkInvoiceComposition route, CancellationToken cancellationToken = default)
    {
        ValidateOwnership(storeId, route);
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        var existing = await Owned(context, storeId).SingleOrDefaultAsync(r => r.RouteId == route.RouteId, cancellationToken);
        if (existing is not null)
        {
            if (!Identical(context, existing, route)) throw Conflict();
            return;
        }
        context.InvoiceCompositions.Add(route);
        await context.SaveChangesAsync(cancellationToken);
    }
    /// <summary>Atomically saves an extended journal using the caller's loaded revision.</summary>
    public async Task Save(string storeId, ArkInvoiceComposition route, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ValidateOwnership(storeId, route);
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        var existing = await Owned(context, storeId).SingleOrDefaultAsync(r => r.RouteId == route.RouteId, cancellationToken);
        if (existing is null) throw Conflict();
        if (Identical(context, existing, route)) return;
        if (expectedRevision < 0 || existing.Revision != expectedRevision || route.Revision <= expectedRevision) throw Conflict();
        EnsureAppendOnly(context, existing, route);
        context.Entry(existing).CurrentValues.SetValues(route);
        foreach (var leg in route.Legs)
        {
            var prior = existing.Legs.SingleOrDefault(l => l.RfqId == leg.RfqId);
            if (prior is null) context.InvoiceCompositionLegs.Add(leg);
            else context.Entry(prior).CurrentValues.SetValues(leg);
        }
        await context.SaveChangesAsync(cancellationToken);
    }

    private static IQueryable<ArkInvoiceComposition> Owned(ArkPluginDbContext context, string storeId) =>
        context.InvoiceCompositions.Include(r => r.Legs).Where(r => r.StoreId == storeId);

    private static void ValidateStore(string storeId)
    {
        if (string.IsNullOrWhiteSpace(storeId)) throw new ArgumentException("Specify an owning store.");
    }

    private static void ValidateOwnership(string storeId, ArkInvoiceComposition route)
    {
        ValidateStore(storeId);
        if (route is null || route.StoreId != storeId) throw new InvalidOperationException("The route does not belong to this store.");
    }

    private static bool Identical(ArkPluginDbContext context, ArkInvoiceComposition stored, ArkInvoiceComposition proposed) =>
        SameValues(context, stored, proposed) && stored.Legs.Count == proposed.Legs.Count &&
        stored.Legs.All(prior => proposed.Legs.SingleOrDefault(l => l.RfqId == prior.RfqId) is { } leg && SameValues(context, prior, leg));

    private static bool SameValues<T>(ArkPluginDbContext context, T stored, T proposed) where T : class =>
        context.Entry(stored).Metadata.GetProperties().All(property =>
            Equals(property.PropertyInfo!.GetValue(stored), property.PropertyInfo.GetValue(proposed)));

    private static void EnsureAppendOnly(ArkPluginDbContext context, ArkInvoiceComposition stored, ArkInvoiceComposition proposed)
    {
        string[] mutable = [nameof(ArkInvoiceComposition.Revision), nameof(ArkInvoiceComposition.Status), nameof(ArkInvoiceComposition.FailureCode)];
        foreach (var property in context.Entry(stored).Metadata.GetProperties().Where(p => !mutable.Contains(p.Name)))
        {
            var prior = property.PropertyInfo!.GetValue(stored);
            if (prior is not null && !Equals(prior, property.PropertyInfo.GetValue(proposed))) throw Conflict();
        }
        foreach (var prior in stored.Legs)
        {
            var leg = proposed.Legs.SingleOrDefault(l => l.RfqId == prior.RfqId) ?? throw Conflict();
            foreach (var property in context.Entry(prior).Metadata.GetProperties())
            {
                var value = property.PropertyInfo!.GetValue(prior);
                if (value is not null && !Equals(value, property.PropertyInfo.GetValue(leg))) throw Conflict();
            }
        }
    }

    private static DbUpdateConcurrencyException Conflict() => new("The public route journal conflicts with the stored revision.");
}
