using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MatMail.Push;
using MatMail.Services;
using Microsoft.AspNetCore.DataProtection;

namespace MatMail.Tests;

/// <summary>The encryption of a push message: what RFC 8291 prints must come out, and what is encrypted must be readable by the device.</summary>
public class WebPushEncryptionTests
{
    private static byte[] B64(string text) => WebPushEncryption.FromBase64Url(text)!;

    /// <summary>The way a device reads a message (the other half of the protocol, only here for the tests).</summary>
    private static byte[] Decrypt(byte[] message, byte[] devicePrivate, byte[] devicePublic, byte[] auth)
    {
        byte[] salt = message[..16];
        Assert.Equal(WebPushEncryption.RecordSize, (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(message.AsSpan(16, 4)));
        int keyLength = message[20];
        byte[] senderPublic = message[21..(21 + keyLength)];
        byte[] cipher = message[(21 + keyLength)..];

        using ECDiffieHellman device = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = devicePrivate,
            Q = new ECPoint { X = devicePublic[1..33], Y = devicePublic[33..65] },
        });
        using ECDiffieHellman sender = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = senderPublic[1..33], Y = senderPublic[33..65] },
        });

        byte[] secret = device.DeriveRawSecretAgreement(sender.PublicKey);
        (byte[] key, byte[] nonce) = WebPushEncryption.DeriveKeys(secret, auth, salt, devicePublic, senderPublic);
        byte[] plain = new byte[cipher.Length - 16];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(nonce, cipher.AsSpan(0, plain.Length), cipher.AsSpan(plain.Length, 16), plain);

        Assert.Equal(0x02, plain[^1]);   // the mark of the last record
        return plain[..^1];
    }

    [Fact]
    public void The_example_of_the_rfc_comes_out_byte_for_byte()
    {
        // RFC 8291, section 5 and appendix A: the message, the keys and the salt are the ones printed there.
        const string expected = "DGv6ra1nlYgDCS1FRnbzlwAAEABBBP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A_yl95bQpu6cVPTpK4Mqgkf1CXztLVBSt2Ks3oZwbuwXPXLWyouBWLVWGNWQexSgSxsj_Qulcy4a-fN";
        byte[] plaintext = Encoding.UTF8.GetBytes("When I grow up, I want to be a watermelon");
        byte[] senderPrivate = B64("yfWPiYE-n46HLnH0KqZOF1fJJU3MYrct3AELtAQ-oRw");
        byte[] devicePublic = B64("BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4");
        byte[] auth = B64("BTBZMqHH6r4Tts7J_aSIgg");
        byte[] salt = B64("DGv6ra1nlYgDCS1FRnbzlw");

        // The public half of the server's key is the key id in the header of the printed message.
        byte[] senderPublic = B64(expected)![21..86];
        using ECDiffieHellman sender = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = senderPrivate,
            Q = new ECPoint { X = senderPublic[1..33], Y = senderPublic[33..65] },
        });

        byte[] message = WebPushEncryption.Encrypt(plaintext, devicePublic, auth, sender, salt);

        Assert.Equal(expected, WebPushEncryption.ToBase64Url(message));
    }

    [Fact]
    public void A_message_can_be_read_by_the_device_it_was_made_for_and_by_nobody_else()
    {
        using ECDiffieHellman device = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        byte[] devicePublic = WebPushEncryption.UncompressedPoint(device);
        byte[] devicePrivate = device.ExportParameters(true).D!;
        byte[] auth = RandomNumberGenerator.GetBytes(16);
        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(new { title = "Anna Berger", body = "Offer – für Sie", url = "/Mail#m=1" });

        byte[] message = WebPushEncryption.Encrypt(plaintext, devicePublic, auth);

        Assert.Equal(plaintext, Decrypt(message, devicePrivate, devicePublic, auth));
        Assert.DoesNotContain("Anna", Encoding.UTF8.GetString(message));
        Assert.ThrowsAny<CryptographicException>(() => Decrypt(message, devicePrivate, devicePublic, RandomNumberGenerator.GetBytes(16)));
        Assert.NotEqual(message, WebPushEncryption.Encrypt(plaintext, devicePublic, auth));   // every message has its own key and salt
    }

    [Fact]
    public void The_longest_message_fits_a_push_service_and_one_byte_more_is_refused()
    {
        using ECDiffieHellman device = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        byte[] devicePublic = WebPushEncryption.UncompressedPoint(device);
        byte[] auth = RandomNumberGenerator.GetBytes(16);

        byte[] longest = WebPushEncryption.Encrypt(new byte[WebPushEncryption.MaxPlaintextBytes], devicePublic, auth);

        Assert.Equal(4096, longest.Length);   // what a push service must accept
        Assert.Throws<ArgumentException>(() => WebPushEncryption.Encrypt(new byte[WebPushEncryption.MaxPlaintextBytes + 1], devicePublic, auth));
        Assert.Throws<ArgumentException>(() => WebPushEncryption.Encrypt("x"u8, new byte[65], auth));       // not a point of the curve's encoding
        Assert.Throws<ArgumentException>(() => WebPushEncryption.Encrypt("x"u8, devicePublic, new byte[15]));
    }

    [Fact]
    public void Keys_and_secrets_travel_as_base64url_without_padding()
    {
        Assert.Equal("-_8", WebPushEncryption.ToBase64Url([0xFB, 0xFF]));
        Assert.Equal(new byte[] { 0xFB, 0xFF }, WebPushEncryption.FromBase64Url("-_8"));
        Assert.Equal(new byte[] { 0xFB, 0xFF }, WebPushEncryption.FromBase64Url("-_8="));
        Assert.Null(WebPushEncryption.FromBase64Url("not base64!"));
        Assert.Null(WebPushEncryption.FromBase64Url(""));
        Assert.Null(WebPushEncryption.FromBase64Url(null));
    }
}

/// <summary>The key pair of the server and the token that proves it to the push services.</summary>
public class PushKeysTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "matmail-push-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // the temp folder is cleaned up by the system anyway
        }
    }

    private SecretProtector Protector(string? purposeDir = null)
        => new(DataProtectionProvider.Create(new DirectoryInfo(purposeDir ?? Path.Combine(_dir, "keys")), options => options.SetApplicationName("test")));

    [Fact]
    public void The_pair_is_made_once_and_found_again_after_a_restart()
    {
        SecretProtector protector = Protector();

        var first = new PushKeys(protector, _dir);
        var second = new PushKeys(protector, _dir);

        Assert.Equal(first.PublicKey, second.PublicKey);
        Assert.Equal(65, WebPushEncryption.FromBase64Url(first.PublicKey)!.Length);
        string file = File.ReadAllText(Path.Combine(_dir, "config", "push-keys.json"));
        Assert.Contains(first.PublicKey, file);
        // The private half is protected: not the 32 bytes themselves.
        Assert.DoesNotContain("\"privateKey\": \"" + first.PublicKey, file);
        Assert.NotNull(JsonDocument.Parse(file).RootElement.GetProperty("privateKey").GetString());
    }

    [Fact]
    public void Keys_that_cannot_be_read_any_more_give_a_new_pair()
    {
        var first = new PushKeys(Protector(), _dir);

        // The data protection keys of this installation are gone: the private half cannot be unprotected.
        var second = new PushKeys(Protector(Path.Combine(_dir, "other-keys")), _dir);

        Assert.NotEqual(first.PublicKey, second.PublicKey);
        Assert.Equal(second.PublicKey, new PushKeys(Protector(Path.Combine(_dir, "other-keys")), _dir).PublicKey);   // and it stays
    }

    [Fact]
    public void The_token_names_the_push_service_and_is_signed_by_the_server()
    {
        var keys = new PushKeys(Protector(), _dir);
        DateTimeOffset now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

        string header = keys.Authorization("https://fcm.googleapis.com/fcm/send/abc123", "mailto:postmaster@mail.example.test", now);

        Assert.StartsWith("vapid t=", header);
        Assert.EndsWith(", k=" + keys.PublicKey, header);
        string jwt = header["vapid t=".Length..header.IndexOf(", k=", StringComparison.Ordinal)];
        string[] parts = jwt.Split('.');
        Assert.Equal(3, parts.Length);

        using JsonDocument claims = JsonDocument.Parse(WebPushEncryption.FromBase64Url(parts[1])!);
        Assert.Equal("https://fcm.googleapis.com", claims.RootElement.GetProperty("aud").GetString());   // the push service, not the path
        Assert.Equal("mailto:postmaster@mail.example.test", claims.RootElement.GetProperty("sub").GetString());
        Assert.Equal(now.AddHours(12).ToUnixTimeSeconds(), claims.RootElement.GetProperty("exp").GetInt64());
        using JsonDocument head = JsonDocument.Parse(WebPushEncryption.FromBase64Url(parts[0])!);
        Assert.Equal("ES256", head.RootElement.GetProperty("alg").GetString());

        byte[] signature = WebPushEncryption.FromBase64Url(parts[2])!;
        Assert.Equal(64, signature.Length);   // r and s, 32 bytes each, as JWT wants it
        Assert.True(keys.Verify(parts[0] + "." + parts[1], signature));
        Assert.False(keys.Verify(parts[0] + "." + parts[1] + "x", signature));
    }
}

/// <summary>The push address comes from the browser, so the server must not post to its own network.</summary>
public class PushAddressTests
{
    [Theory]
    [InlineData("142.250.74.110", true)]       // somewhere on the internet
    [InlineData("2a00:1450:4001:81b::200e", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.16.0.5", false)]
    [InlineData("172.32.0.5", true)]           // just outside 172.16.0.0/12
    [InlineData("192.168.1.10", false)]
    [InlineData("169.254.169.254", false)]     // the metadata service of a cloud
    [InlineData("100.64.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("::", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fd12:3456::1", false)]        // unique local
    [InlineData("::ffff:10.0.0.1", false)]     // a private address in the clothes of IPv6
    [InlineData("::ffff:8.8.8.8", true)]
    public void Only_addresses_of_the_internet_are_reachable(string address, bool expected)
        => Assert.Equal(expected, WebPushClient.IsPublic(IPAddress.Parse(address)));
}

/// <summary>What a phone or a desktop reads to install MatMail like an app.</summary>
public class PwaManifestTests
{
    private static JsonElement Parse(IReadOnlyDictionary<string, object?> manifest) => JsonDocument.Parse(JsonSerializer.Serialize(manifest)).RootElement;

    [Fact]
    public void The_manifest_is_an_app_that_opens_the_mail_client_in_a_window_of_its_own()
    {
        JsonElement manifest = Parse(PwaManifest.Build("Acme Mail", "Compose", dark: false));

        Assert.Equal("Acme Mail", manifest.GetProperty("name").GetString());
        Assert.Equal("/Mail", manifest.GetProperty("start_url").GetString());
        Assert.Equal("/", manifest.GetProperty("scope").GetString());
        Assert.Equal("standalone", manifest.GetProperty("display").GetString());   // an iPhone cannot do fullscreen and letterboxes the page
        Assert.DoesNotContain(manifest.GetProperty("display_override").EnumerateArray(), v => v.GetString() == "fullscreen");
        Assert.Equal("/Mail?compose=1", manifest.GetProperty("shortcuts")[0].GetProperty("url").GetString());
    }

    [Fact]
    public void The_colours_are_those_of_the_header_in_the_theme_of_the_person()
    {
        JsonElement light = Parse(PwaManifest.Build("MatMail", "Compose", dark: false));
        JsonElement dark = Parse(PwaManifest.Build("MatMail", "Compose", dark: true));

        Assert.Equal(PwaManifest.LightColour, light.GetProperty("theme_color").GetString());
        Assert.Equal(PwaManifest.LightColour, light.GetProperty("background_color").GetString());
        Assert.Equal(PwaManifest.DarkColour, dark.GetProperty("theme_color").GetString());
        Assert.Equal(PwaManifest.DarkColour, dark.GetProperty("background_color").GetString());
    }

    [Fact]
    public void A_long_name_has_a_short_form_for_the_home_screen()
    {
        Assert.Equal("Acme Hosting", Parse(PwaManifest.Build("Acme Hosting GmbH Mail", "Compose", false)).GetProperty("short_name").GetString());
        Assert.Equal("Mail", Parse(PwaManifest.Build("Mail", "Compose", false)).GetProperty("short_name").GetString());
    }

    [Fact]
    public void Every_icon_of_the_manifest_is_a_file_of_the_application_with_the_size_it_claims()
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "MatMail.slnx")))
        {
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("The repository root was not found.");
        }

        JsonElement icons = Parse(PwaManifest.Build("MatMail", "Compose", false)).GetProperty("icons");
        Assert.Contains(icons.EnumerateArray(), i => i.GetProperty("sizes").GetString() == "192x192");
        Assert.Contains(icons.EnumerateArray(), i => i.GetProperty("purpose").GetString() == "maskable");
        foreach (JsonElement icon in icons.EnumerateArray())
        {
            byte[] png = File.ReadAllBytes(Path.Combine(root, "src", "MatMail", "wwwroot", icon.GetProperty("src").GetString()!.TrimStart('/')));
            int width = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4));
            int height = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4));
            Assert.Equal(icon.GetProperty("sizes").GetString(), $"{width}x{height}");
        }

        Assert.True(File.Exists(Path.Combine(root, "src", "MatMail", "wwwroot", "icons", "apple-touch-icon.png")));
        Assert.True(File.Exists(Path.Combine(root, "src", "MatMail", "wwwroot", "icons", "badge-96.png")));
    }
}
