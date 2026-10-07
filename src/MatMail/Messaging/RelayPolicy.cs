using System.Net;
using MatMail.Data;
using MatMail.Services;
using Microsoft.EntityFrameworkCore;

namespace MatMail.Messaging;

/// <summary>
/// The smart-host rules: which clients may send through the server without signing in, and as whom. A client whose address lies in
/// the network of an enabled <see cref="RelayRule"/> is trusted for that rule's tenant.
/// </summary>
public sealed class RelayPolicy
{
    private readonly MatMailDbContext _db;

    public RelayPolicy(MatMailDbContext db) => _db = db;

    /// <summary>The first enabled rule whose network contains the address, or null (the client is not trusted).</summary>
    public async Task<RelayRule?> FindRuleAsync(IPAddress remote, CancellationToken cancel = default)
    {
        List<RelayRule> rules = await _db.RelayRules.IgnoreQueryFilters().AsNoTracking().Where(r => r.IsEnabled).OrderBy(r => r.Id).ToListAsync(cancel);
        return rules.FirstOrDefault(r => Matches(r.Network, remote));
    }

    /// <summary>
    /// May a trusted client of this rule send as this address? With "allowed sender domains" only those domains count; without, the
    /// domains registered for the rule's tenant.
    /// </summary>
    public async Task<bool> IsSenderAllowedAsync(RelayRule rule, string senderAddress, CancellationToken cancel = default)
    {
        string domain = MailAddresses.DomainOf(MailAddresses.Normalize(senderAddress));
        if (domain.Length == 0)
        {
            // The empty sender "<>" is only used for bounce messages; trusted networks may send them too.
            return string.IsNullOrWhiteSpace(senderAddress);
        }

        if (rule.AllowedSenderDomains.Length > 0)
        {
            return rule.AllowedSenderDomains.Any(d => string.Equals(d.Trim(), domain, StringComparison.OrdinalIgnoreCase));
        }

        return await _db.Domains.IgnoreQueryFilters().AnyAsync(d => d.TenantId == rule.TenantId && d.Name == domain && d.IsActive, cancel);
    }

    /// <summary>Does the address lie in the network ("192.168.1.5", "192.168.1.0/24", "fd00::/8")?</summary>
    public static bool Matches(string network, IPAddress address)
    {
        if (!TryParseNetwork(network, out IPNetwork parsed))
        {
            return false;
        }

        IPAddress candidate = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        return parsed.Contains(candidate);
    }

    /// <summary>Parses a single address or a CIDR range; a single address becomes /32 (IPv4) or /128 (IPv6).</summary>
    public static bool TryParseNetwork(string? text, out IPNetwork network)
    {
        network = default;
        string value = (text ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return false;
        }

        if (!value.Contains('/'))
        {
            if (!IPAddress.TryParse(value, out IPAddress? single))
            {
                return false;
            }

            value += single.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? "/32" : "/128";
        }

        return IPNetwork.TryParse(value, out network);
    }

    /// <summary>The canonical text of a valid network, or null.</summary>
    public static string? Normalize(string? text)
        => TryParseNetwork(text, out IPNetwork network) ? network.ToString() : null;
}
