namespace BTCPayServer.Plugins.ArkPayServer.Models;

public class WalletCreationRequest
{
    public string Password { get; }
    public string? Mnemonic { get; }
    public string? Passphrase { get; }

    public WalletCreationRequest(string password, string? mnemonic = null, string? passphrase = null)
    {
        if (string.IsNullOrWhiteSpace(password))
            throw new ArgumentException("Password cannot be empty", nameof(password));

        if (passphrase != null && mnemonic == null)
            throw new ArgumentException("Passphrase cannot be provided without a mnemonic", nameof(passphrase));

        Password = password;
        Mnemonic = mnemonic;
        Passphrase = passphrase;
    }
}