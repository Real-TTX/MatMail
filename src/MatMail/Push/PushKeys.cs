using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MatMail.Services;

namespace MatMail.Push;

/// <summary>
/// The key pair that proves to the push services who sends (VAPID, RFC 8292). It is made once, on first use, and kept in the data
/// volume (<c>config/push-keys.json</c>, the private half protected with the data protection keys). The public half is what
/// browsers are told when they subscribe: a subscription belongs to this key pair, so a new pair means subscribing again.
/// </summary>
public sealed class PushKeys
{
    private static readonly object CreateLock = new();

    private readonly ECDsa _key;

    /// <summary>The public key as the browsers want it (<c>applicationServerKey</c>): 65 bytes, base64url.</summary>
    public string PublicKey { get; }

    public PushKeys(SecretProtector protector, string? dataDir = null)
    {
        string path = Path.Combine(dataDir ?? AppInfo.DataDir, "config", "push-keys.json");
        lock (CreateLock)
        {
            _key = TryLoad(path, protector) ?? Create(path, protector);
        }

        PublicKey = WebPushEncryption.ToBase64Url(PublicPoint(_key));
    }

    /// <summary>The value of the Authorization header for a push to this endpoint (a signed token valid for 12 hours).</summary>
    public string Authorization(string endpoint, string subject, DateTimeOffset now)
    {
        var uri = new Uri(endpoint);
        string audience = uri.GetLeftPart(UriPartial.Authority);
        string header = WebPushEncryption.ToBase64Url("{\"typ\":\"JWT\",\"alg\":\"ES256\"}"u8);
        string claims = WebPushEncryption.ToBase64Url(JsonSerializer.SerializeToUtf8Bytes(new { aud = audience, exp = now.AddHours(12).ToUnixTimeSeconds(), sub = subject }));
        string signingInput = header + "." + claims;
        byte[] signature = _key.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return $"vapid t={signingInput}.{WebPushEncryption.ToBase64Url(signature)}, k={PublicKey}";
    }

    /// <summary>Whether the signature of a token was made with this key pair (for tests).</summary>
    internal bool Verify(string signingInput, byte[] signature)
        => _key.VerifyData(Encoding.ASCII.GetBytes(signingInput), signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    private static byte[] PublicPoint(ECDsa key)
    {
        ECParameters parameters = key.ExportParameters(false);
        byte[] point = new byte[65];
        point[0] = 0x04;
        parameters.Q.X!.CopyTo(point, 1 + 32 - parameters.Q.X.Length);
        parameters.Q.Y!.CopyTo(point, 33 + 32 - parameters.Q.Y.Length);
        return point;
    }

    private static ECDsa? TryLoad(string path, SecretProtector protector)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using JsonDocument file = JsonDocument.Parse(File.ReadAllText(path));
            byte[]? publicPoint = WebPushEncryption.FromBase64Url(file.RootElement.GetProperty("publicKey").GetString());
            string? privateText = protector.Unprotect(file.RootElement.GetProperty("privateKey").GetString());
            byte[]? d = WebPushEncryption.FromBase64Url(privateText);
            if (publicPoint is not { Length: 65 } || d is null)
            {
                return null;   // damaged, or protected with keys this installation no longer has: a new pair is made
            }

            return ECDsa.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                D = d,
                Q = new ECPoint { X = publicPoint[1..33], Y = publicPoint[33..65] },
            });
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or CryptographicException or IOException)
        {
            return null;
        }
    }

    private static ECDsa Create(string path, SecretProtector protector)
    {
        ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        ECParameters parameters = key.ExportParameters(true);
        string json = JsonSerializer.Serialize(new
        {
            publicKey = WebPushEncryption.ToBase64Url(PublicPoint(key)),
            privateKey = protector.Protect(WebPushEncryption.ToBase64Url(parameters.D!)),
        }, new JsonSerializerOptions { WriteIndented = true });

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, path, overwrite: true);
        return key;
    }
}
