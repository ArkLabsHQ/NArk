namespace NArk.Extensions;

public static class BytesExtensions
{
    public static string ToHex(this byte[] value)
    {
        return Convert.ToHexString(value).ToLowerInvariant();
    }
}