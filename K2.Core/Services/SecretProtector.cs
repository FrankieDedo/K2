using System;
using System.Security.Cryptography;
using System.Text;

namespace K2.Core.Services;

/// <summary>
/// Encrypts secrets (OAuth client secrets/access/refresh tokens) at rest using Windows DPAPI
/// (<see cref="DataProtectionScope.CurrentUser"/> — tied to the logged-in Windows account, no
/// password/key management needed, matches K2's "single-PC personal use" model). Used by
/// <see cref="TwitchStore"/>/<see cref="YouTubeStore"/> so their JSON files under
/// <c>%LocalAppData%\K2</c> never hold a token in plain text. Entropy is fixed (not per-secret)
/// since these stores hold one value per field, not a keyed collection — DPAPI's own
/// user-scoped key is the actual protection, entropy here just namespaces K2's blobs from any
/// other app's DPAPI-protected data for the same user.
/// </summary>
internal static class SecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("K2.Core.SecretProtector.v1");

    /// <summary>Encrypts <paramref name="plainText"/> for the current Windows user, returned as
    /// base64. Empty input round-trips to empty without invoking DPAPI.</summary>
    public static string Protect(string plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return "";
        byte[] cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(plainText), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(cipher);
    }

    /// <summary>Decrypts a value produced by <see cref="Protect"/>. If <paramref name="stored"/>
    /// isn't valid DPAPI-protected base64 (e.g. a pre-encryption plaintext value from before this
    /// was added), it's returned as-is — best-effort legacy fallback, never throws.</summary>
    public static string Unprotect(string stored)
    {
        if (string.IsNullOrEmpty(stored)) return "";
        try
        {
            byte[] cipher = Convert.FromBase64String(stored);
            byte[] plain = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception) { return stored; }
    }
}
