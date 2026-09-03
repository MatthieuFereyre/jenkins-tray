using System.Security.Cryptography;
using System.Text;

namespace JenkinsTray.Services;

/// <summary>
/// Wraps DPAPI so API tokens never sit in clear text on disk. The ciphertext is bound to the
/// current Windows user account: copying settings.json to another machine or user simply yields
/// an empty token rather than a leak.
/// </summary>
public static class SecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("JenkinsTray/v1/token");

    public static string Protect(string? plainText)
    {
        if (string.IsNullOrEmpty(plainText))
            return string.Empty;

        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plainText), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    public static string Unprotect(string? cipherText)
    {
        if (string.IsNullOrEmpty(cipherText))
            return string.Empty;

        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(cipherText), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return string.Empty;
        }
    }
}
