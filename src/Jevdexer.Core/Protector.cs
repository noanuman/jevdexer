using System.Security.Cryptography;
using System.Text;

namespace Jevdexer.Core;

/// <summary>
/// Encryption at rest with DPAPI. The key belongs to the signed-in Windows user: another
/// account on the PC, or a copy of the files on another machine, cannot read them.
///
/// Protected text is `dpapi:` + base64. <see cref="Unprotect"/> passes anything without the prefix
/// through unchanged, so older plain-text data is read once and re-saved protected.
/// </summary>
public static class Protector
{
    private const string Prefix = "dpapi:";

    // Separates our blobs from other DPAPI users' blobs of the same account. Not a secret.
    private static readonly byte[] Entropy = "Jevdexer.v1"u8.ToArray();

    public static string Protect(string plain) =>
        Prefix + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser));

    /// <summary>Plain text for protected input, the input itself otherwise.</summary>
    /// <exception cref="CryptographicException">Protected by another user or machine.</exception>
    public static string Unprotect(string stored) =>
        IsProtected(stored)
            ? Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored[Prefix.Length..]), Entropy, DataProtectionScope.CurrentUser))
            : stored;

    public static bool IsProtected(string stored) => stored.StartsWith(Prefix, StringComparison.Ordinal);
}
