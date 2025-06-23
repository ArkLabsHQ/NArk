using System.Security.Cryptography;
using System.Text;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

/// <summary>
/// Cryptographic utilities for wallet operations, following the reference Go SDK implementation
/// </summary>
public static class CryptoUtils
{
    /// <summary>
    /// Hash password using SHA256, following the Go SDK utils.HashPassword implementation
    /// </summary>
    public static byte[] HashPassword(byte[] password)
    {
        return SHA256.HashData(password);
    }

    /// <summary>
    /// Hash password string using SHA256
    /// </summary>
    public static byte[] HashPassword(string password)
    {
        return HashPassword(Encoding.UTF8.GetBytes(password));
    }

    /// <summary>
    /// Encrypt data using AES-256-GCM, following the Go SDK utils.EncryptAES256 implementation
    /// </summary>
    public static byte[] EncryptAES256(byte[] data, byte[] password)
    {
        // Use PBKDF2 to derive a 32-byte key from the password
        var salt = RandomNumberGenerator.GetBytes(16); // 16-byte salt
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, 10000, HashAlgorithmName.SHA256, 32);
        
        using var aes = new AesGcm(key, AesGcm.TagByteSizes.MaxSize);
        var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize); // 12-byte nonce for GCM
        var ciphertext = new byte[data.Length];
        var tag = new byte[AesGcm.TagByteSizes.MaxSize]; // 16-byte authentication tag
        
        aes.Encrypt(nonce, data, ciphertext, tag);
        
        // Combine salt + nonce + tag + ciphertext
        var result = new byte[salt.Length + nonce.Length + tag.Length + ciphertext.Length];
        var offset = 0;
        
        Array.Copy(salt, 0, result, offset, salt.Length);
        offset += salt.Length;
        
        Array.Copy(nonce, 0, result, offset, nonce.Length);
        offset += nonce.Length;
        
        Array.Copy(tag, 0, result, offset, tag.Length);
        offset += tag.Length;
        
        Array.Copy(ciphertext, 0, result, offset, ciphertext.Length);
        
        return result;
    }

    /// <summary>
    /// Decrypt data using AES-256-GCM, following the Go SDK utils.DecryptAES256 implementation
    /// </summary>
    public static byte[] DecryptAES256(byte[] encryptedData, byte[] password)
    {
        if (encryptedData.Length < 16 + 12 + 16) // salt + nonce + tag minimum
            throw new ArgumentException("Invalid encrypted data length");
        
        var offset = 0;
        
        // Extract salt (16 bytes)
        var salt = new byte[16];
        Array.Copy(encryptedData, offset, salt, 0, salt.Length);
        offset += salt.Length;
        
        // Extract nonce (12 bytes)
        var nonce = new byte[AesGcm.NonceByteSizes.MaxSize];
        Array.Copy(encryptedData, offset, nonce, 0, nonce.Length);
        offset += nonce.Length;
        
        // Extract tag (16 bytes)
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];
        Array.Copy(encryptedData, offset, tag, 0, tag.Length);
        offset += tag.Length;
        
        // Extract ciphertext (remaining bytes)
        var ciphertext = new byte[encryptedData.Length - offset];
        Array.Copy(encryptedData, offset, ciphertext, 0, ciphertext.Length);
        
        // Derive the same key using PBKDF2
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, 10000, HashAlgorithmName.SHA256, 32);
        
        using var aes = new AesGcm(key, AesGcm.TagByteSizes.MaxSize);
        var plaintext = new byte[ciphertext.Length];
        
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        
        return plaintext;
    }
}