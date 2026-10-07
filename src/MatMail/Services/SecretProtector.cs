using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace MatMail.Services;

/// <summary>
/// Encrypts secrets that must be readable again (the passwords of connected provider accounts). The keys live in the
/// data volume (<c>keys/</c>); without them stored provider passwords cannot be decrypted, so back the volume up.
/// </summary>
public sealed class SecretProtector
{
    private readonly IDataProtector _protector;

    public SecretProtector(IDataProtectionProvider provider) => _protector = provider.CreateProtector("MatMail.ProviderSecrets.v1");

    public string Protect(string plain) => _protector.Protect(plain);

    /// <summary>Returns the clear text, or null when the value is empty or was protected with different keys.</summary>
    public string? Unprotect(string? protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue))
        {
            return null;
        }

        try
        {
            return _protector.Unprotect(protectedValue);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
