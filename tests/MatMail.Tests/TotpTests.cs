using System.Text;
using MatMail.Services;

namespace MatMail.Tests;

public class Base32Tests
{
    // RFC 4648 section 10 (shown without the "=" padding, which this encoder leaves out on purpose).
    [Theory]
    [InlineData("", "")]
    [InlineData("f", "MY")]
    [InlineData("fo", "MZXQ")]
    [InlineData("foo", "MZXW6")]
    [InlineData("foob", "MZXW6YQ")]
    [InlineData("fooba", "MZXW6YTB")]
    [InlineData("foobar", "MZXW6YTBOI")]
    public void Encoding_follows_the_rfc_4648_vectors(string text, string expected)
        => Assert.Equal(expected, Base32.Encode(Encoding.ASCII.GetBytes(text)));

    [Theory]
    [InlineData("MY======", "f")]
    [InlineData("MZXQ====", "fo")]
    [InlineData("MZXW6===", "foo")]
    [InlineData("MZXW6YQ=", "foob")]
    [InlineData("MZXW6YTB", "fooba")]
    [InlineData("MZXW6YTBOI======", "foobar")]
    [InlineData("MZXW6YTBOI", "foobar")]
    public void Decoding_follows_the_rfc_4648_vectors(string encoded, string expected)
    {
        Assert.True(Base32.TryDecode(encoded, out byte[] bytes));
        Assert.Equal(expected, Encoding.ASCII.GetString(bytes));
    }

    [Fact]
    public void Decoding_ignores_case_spaces_and_hyphens_as_people_type_a_key()
    {
        Assert.True(Base32.TryDecode("mzxw 6ytb-oi", out byte[] bytes));
        Assert.Equal("foobar", Encoding.ASCII.GetString(bytes));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("MZXW6YTB0I")]
    [InlineData("MZXW6YTB1I")]
    [InlineData("MZXW6YTB8I")]
    [InlineData("MZXW6YTB!I")]
    public void Text_that_is_not_base32_is_refused(string text)
        => Assert.False(Base32.TryDecode(text, out _));

    [Fact]
    public void Every_secret_survives_a_round_trip()
    {
        for (int i = 0; i < 200; i++)
        {
            byte[] secret = Totp.NewSecret();
            string text = Base32.Encode(secret);
            Assert.Equal(32, text.Length);
            Assert.True(Base32.TryDecode(text, out byte[] back));
            Assert.Equal(secret, back);
        }
    }
}

public class TotpTests
{
    private static readonly byte[] RfcSecret = Encoding.ASCII.GetBytes("12345678901234567890");

    // RFC 4226 appendix D: the HOTP values of the first ten counters.
    [Theory]
    [InlineData(0, "755224")]
    [InlineData(1, "287082")]
    [InlineData(2, "359152")]
    [InlineData(3, "969429")]
    [InlineData(4, "338314")]
    [InlineData(5, "254676")]
    [InlineData(6, "287922")]
    [InlineData(7, "162583")]
    [InlineData(8, "399871")]
    [InlineData(9, "520489")]
    public void The_hotp_values_of_rfc_4226_are_reproduced(long counter, string expected)
        => Assert.Equal(expected, Totp.Compute(RfcSecret, counter));

    // RFC 6238 appendix B, SHA-1 (the RFC prints eight digits; the six-digit code is their tail).
    [Theory]
    [InlineData(59L, "94287082")]
    [InlineData(1111111109L, "07081804")]
    [InlineData(1111111111L, "14050471")]
    [InlineData(1234567890L, "89005924")]
    [InlineData(2000000000L, "69279037")]
    [InlineData(20000000000L, "65353130")]
    public void The_test_vectors_of_rfc_6238_are_reproduced(long unixTime, string expectedEightDigits)
    {
        long step = Totp.StepOf(DateTimeOffset.FromUnixTimeSeconds(unixTime));
        Assert.Equal(expectedEightDigits, Totp.Compute(RfcSecret, step, digits: 8));
        Assert.Equal(expectedEightDigits[2..], Totp.Compute(RfcSecret, step));
    }

    [Fact]
    public void A_step_lasts_thirty_seconds()
    {
        Assert.Equal(1, Totp.StepOf(DateTimeOffset.FromUnixTimeSeconds(59)));
        Assert.Equal(2, Totp.StepOf(DateTimeOffset.FromUnixTimeSeconds(60)));
        Assert.Equal(0, Totp.StepOf(DateTimeOffset.FromUnixTimeSeconds(29)));
    }

    [Fact]
    public void The_current_step_and_one_step_either_way_are_accepted()
    {
        const long now = 1_000_000;
        foreach (long step in new[] { now - 1, now, now + 1 })
        {
            Assert.True(Totp.TryMatch(RfcSecret, Totp.Compute(RfcSecret, step), now, lastUsedStep: 0, out long matched));
            Assert.Equal(step, matched);
        }
    }

    [Fact]
    public void Codes_two_steps_away_are_refused()
    {
        const long now = 1_000_000;
        Assert.False(Totp.TryMatch(RfcSecret, Totp.Compute(RfcSecret, now - 2), now, 0, out _));
        Assert.False(Totp.TryMatch(RfcSecret, Totp.Compute(RfcSecret, now + 2), now, 0, out _));
    }

    [Fact]
    public void A_code_is_accepted_only_for_a_step_after_the_last_one_used()
    {
        const long now = 1_000_000;
        string code = Totp.Compute(RfcSecret, now);

        Assert.True(Totp.TryMatch(RfcSecret, code, now, lastUsedStep: now - 1, out long matched));
        Assert.Equal(now, matched);

        // Once step "now" was used, the same code - and the code of any earlier step - is spent.
        Assert.False(Totp.TryMatch(RfcSecret, code, now, lastUsedStep: now, out _));
        Assert.False(Totp.TryMatch(RfcSecret, Totp.Compute(RfcSecret, now - 1), now, lastUsedStep: now, out _));
        Assert.True(Totp.TryMatch(RfcSecret, Totp.Compute(RfcSecret, now + 1), now, lastUsedStep: now, out _));
    }

    [Fact]
    public void Codes_may_be_typed_with_a_space_in_the_middle()
    {
        const long now = 1_000_000;
        string code = Totp.Compute(RfcSecret, now);
        Assert.True(Totp.TryMatch(RfcSecret, code[..3] + " " + code[3..], now, 0, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("12345a")]
    [InlineData("abcdef")]
    [InlineData("123-456")]
    public void Input_that_is_not_a_six_digit_code_never_matches(string? input)
    {
        Assert.Null(Totp.NormalizeCode(input));
        Assert.False(Totp.TryMatch(RfcSecret, input, 1_000_000, 0, out _));
    }

    [Fact]
    public void A_wrong_code_does_not_match()
    {
        const long now = 1_000_000;
        var valid = Enumerable.Range(-1, 3).Select(i => Totp.Compute(RfcSecret, now + i)).ToHashSet();
        string wrong = Enumerable.Range(0, 10).Select(i => i.ToString("D6")).First(candidate => !valid.Contains(candidate));
        Assert.False(Totp.TryMatch(RfcSecret, wrong, now, 0, out _));
    }

    [Fact]
    public void Another_secret_produces_other_codes()
    {
        byte[] other = Encoding.ASCII.GetBytes("98765432109876543210");
        int equal = Enumerable.Range(0, 50).Count(i => Totp.Compute(RfcSecret, 5_000 + i) == Totp.Compute(other, 5_000 + i));
        Assert.True(equal < 3);
    }

    [Fact]
    public void The_key_uri_carries_issuer_account_and_parameters()
    {
        string uri = Totp.BuildUri("Eine Firma", "max@example.com", "JBSWY3DPEHPK3PXP");
        Assert.Equal(
            "otpauth://totp/Eine%20Firma:max%40example.com?secret=JBSWY3DPEHPK3PXP&issuer=Eine%20Firma&algorithm=SHA1&digits=6&period=30",
            uri);
    }

    [Fact]
    public void Digit_counts_outside_one_to_nine_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Totp.Compute(RfcSecret, 1, digits: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Totp.Compute(RfcSecret, 1, digits: 10));
    }
}
