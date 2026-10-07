using DnsClient;
using DnsClient.Protocol;

namespace MatMail.MailServer.Outbound;

/// <summary>
/// The mail servers of a domain, best first. <see cref="Error"/> is set when none can be named; <see cref="IsPermanent"/> tells
/// whether asking again later can help (a domain that does not exist or accepts no mail) or not.
/// </summary>
public sealed record MxLookup(IReadOnlyList<string> Hosts, string? Error = null, bool IsPermanent = false);

/// <summary>Finds the mail servers of a domain (direct delivery).</summary>
public interface IMxResolver
{
    Task<MxLookup> ResolveAsync(string domain, CancellationToken cancel);
}

/// <summary>MX lookup through DNS: MX hosts by preference (equal ones shuffled), the domain itself when it has no MX (RFC 5321 5.1).</summary>
public sealed class DnsMxResolver : IMxResolver
{
    private readonly LookupClient _dns = new(new LookupClientOptions
    {
        Timeout = TimeSpan.FromSeconds(10),
        Retries = 2,
        UseCache = true,
        ThrowDnsErrors = false,
    });

    public async Task<MxLookup> ResolveAsync(string domain, CancellationToken cancel)
    {
        IDnsQueryResponse response;
        try
        {
            response = await _dns.QueryAsync(domain, QueryType.MX, QueryClass.IN, cancel);
        }
        catch (DnsResponseException ex)
        {
            return new MxLookup(Array.Empty<string>(), $"The DNS lookup for {domain} failed: {ex.Message}");
        }

        if (response.Header.ResponseCode == DnsHeaderResponseCode.NotExistentDomain)
        {
            return new MxLookup(Array.Empty<string>(), $"The domain {domain} does not exist.", IsPermanent: true);
        }

        if (response.HasError)
        {
            return new MxLookup(Array.Empty<string>(), $"The DNS lookup for {domain} failed: {response.ErrorMessage}");
        }

        return Order(domain, response.Answers.MxRecords().Select(r => ((int)r.Preference, r.Exchange.Value)));
    }

    /// <summary>Orders MX records: lowest preference first, equal preferences in random order. "." alone is a null MX (RFC 7505).</summary>
    internal static MxLookup Order(string domain, IEnumerable<(int Preference, string Exchange)> records)
    {
        List<(int Preference, string Host)> hosts = records.Select(r => (r.Preference, Host: r.Exchange.Trim().TrimEnd('.'))).ToList();
        if (hosts.Count == 0)
        {
            // No MX: the domain's own address record is the mail server (implicit MX).
            return new MxLookup(new[] { domain });
        }

        if (hosts.All(h => h.Host.Length == 0))
        {
            return new MxLookup(Array.Empty<string>(), $"The domain {domain} does not accept mail (null MX).", IsPermanent: true);
        }

        List<string> ordered = hosts
            .Where(h => h.Host.Length > 0)
            .GroupBy(h => h.Preference)
            .OrderBy(g => g.Key)
            .SelectMany(g => g.OrderBy(_ => Random.Shared.Next()))
            .Select(h => h.Host)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new MxLookup(ordered);
    }
}
