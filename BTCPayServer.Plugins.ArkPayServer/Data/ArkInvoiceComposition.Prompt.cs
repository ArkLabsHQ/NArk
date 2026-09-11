namespace BTCPayServer.Plugins.ArkPayServer.Data;

public sealed partial class ArkInvoiceComposition
{
    /// <summary>Public customer payment address or BOLT11, never a secret-bearing request.</summary>
    public string? CustomerDestination { get; private set; }
    /// <summary>Checkout deadline bounded by both quotes and the invoice, in Unix seconds.</summary>
    public long? CheckoutExpiresAt { get; private set; }

    /// <summary>Publishes one immutable customer prompt without advancing observed-money state.</summary>
    public void RecordCustomerPrompt(string destination, long expiresAt)
    {
        Require(!string.IsNullOrWhiteSpace(destination) && destination.Length <= 8192 &&
                destination.All(c => char.IsAsciiLetterOrDigit(c)) &&
                ArkCompositionValidation.Timestamp(expiresAt) <= _legs.Min(l => l.ValidUntil ?? 0));
        Require(PaymentMethodId != "ARKADE" || destination == Leg("Outgoing").LockupAddress);
        if (CustomerDestination is not null)
        {
            Require(CustomerDestination == destination && CheckoutExpiresAt == expiresAt);
            return;
        }
        RequireStatus(PaymentMethodId == "ARKADE" ? "OutgoingQuoted" : "IngressQuoted");
        Revision = checked(Revision + 1);
        CustomerDestination = destination;
        CheckoutExpiresAt = expiresAt;
    }
}
