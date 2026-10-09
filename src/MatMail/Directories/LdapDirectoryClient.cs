using System.Net;
using System.Net.Sockets;
using MatMail.Data;
using Novell.Directory.Ldap;

namespace MatMail.Directories;

/// <summary>Talks LDAP (Active Directory and the others) with a library that needs nothing but .NET (no native LDAP library in the container).</summary>
public sealed class LdapDirectoryClientFactory : IDirectoryClientFactory
{
    public IDirectoryClient Create(DirectorySettings settings) => new LdapDirectoryClient(settings);

    public async Task<bool> CheckPasswordAsync(DirectorySettings settings, string dn, string password, CancellationToken cancel)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrWhiteSpace(dn))
        {
            return false;
        }

        // A connection of its own: signing in as the person on the connection of the account that searches would change that one.
        using LdapConnection connection = await LdapDirectoryClient.OpenAsync(settings, cancel);
        try
        {
            await LdapDirectoryClient.WithTimeout(settings, cancel, token => connection.BindAsync(dn, password, token));
            return true;
        }
        catch (LdapException ex) when (ex.ResultCode is LdapException.InvalidCredentials or LdapException.NoSuchObject or LdapException.InvalidDnSyntax or LdapException.UnwillingToPerform)
        {
            return false;
        }
        catch (LdapException ex)
        {
            throw new DirectoryException($"The directory could not check the password: {Describe(ex)}", ex);
        }
    }

    internal static string Describe(LdapException ex) => string.IsNullOrWhiteSpace(ex.LdapErrorMessage) ? ex.Message : ex.LdapErrorMessage;
}

internal sealed class LdapDirectoryClient(DirectorySettings settings) : IDirectoryClient
{
    private LdapConnection? _connection;

    public async Task ConnectAsync(CancellationToken cancel)
    {
        LdapConnection connection = await OpenAsync(settings, cancel);
        try
        {
            if (!string.IsNullOrWhiteSpace(settings.BindDn))
            {
                await WithTimeout(settings, cancel, token => connection.BindAsync(settings.BindDn, settings.BindPassword ?? string.Empty, token));
            }
        }
        catch (LdapException ex) when (ex.ResultCode == LdapException.InvalidCredentials)
        {
            connection.Dispose();
            throw new DirectoryException($"The directory does not accept the account “{settings.BindDn}”: the name or the password is wrong.", ex);
        }
        catch (Exception ex) when (ex is LdapException)
        {
            connection.Dispose();
            throw new DirectoryException($"The directory refused the sign-in of “{settings.BindDn}”: {LdapDirectoryClientFactory.Describe((LdapException)ex)}", ex);
        }
        catch
        {
            connection.Dispose();
            throw;
        }

        _connection = connection;
    }

    public async Task<IReadOnlyList<DirectoryEntry>> SearchAsync(string baseDn, string filter, IReadOnlyCollection<string> attributes, int limit, CancellationToken cancel)
        => await Search(baseDn, LdapConnection.ScopeSub, filter, attributes, limit, cancel);

    public async Task<DirectoryEntry?> ReadAsync(string dn, IReadOnlyCollection<string> attributes, CancellationToken cancel)
    {
        try
        {
            return (await Search(dn, LdapConnection.ScopeBase, "(objectClass=*)", attributes, 1, cancel)).FirstOrDefault();
        }
        catch (DirectoryException ex) when (ex.InnerException is LdapException { ResultCode: LdapException.NoSuchObject })
        {
            return null;
        }
    }

    private async Task<IReadOnlyList<DirectoryEntry>> Search(string baseDn, int scope, string filter, IReadOnlyCollection<string> attributes, int limit, CancellationToken cancel)
    {
        LdapConnection connection = _connection ?? throw new InvalidOperationException("The directory was not connected.");
        var constraints = new LdapSearchConstraints { MaxResults = limit, ReferralFollowing = false, TimeLimit = settings.TimeoutSeconds * 1000 };
        var entries = new List<DirectoryEntry>();
        try
        {
            await WithTimeout(settings, cancel, async token =>
            {
                ILdapSearchResults results = await connection.SearchAsync(baseDn, scope, filter, attributes.ToArray(), false, constraints, token);
                while (entries.Count < limit && await results.HasMoreAsync(token))
                {
                    try
                    {
                        entries.Add(Convert(await results.NextAsync(token)));
                    }
                    catch (LdapReferralException)
                    {
                        // a pointer to another server (Active Directory has them): not an entry
                    }
                }
            });
        }
        catch (LdapException ex) when (ex.ResultCode == LdapException.SizeLimitExceeded)
        {
            // the server's own limit: what came is what there is for now
        }
        catch (LdapException ex) when (ex.ResultCode == LdapException.NoSuchObject)
        {
            throw new DirectoryException($"“{baseDn}” does not exist in the directory.", ex);
        }
        catch (LdapException ex)
        {
            throw new DirectoryException($"The search in “{baseDn}” failed: {LdapDirectoryClientFactory.Describe(ex)}", ex);
        }

        return entries;
    }

    private static DirectoryEntry Convert(LdapEntry entry)
    {
        var attributes = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (LdapAttribute attribute in entry.GetAttributeSet())
        {
            // The id of an entry in Active Directory is binary: a GUID.
            attributes[attribute.Name] = attribute.Name.Equals("objectGUID", StringComparison.OrdinalIgnoreCase) && attribute.ByteValue is { Length: 16 } bytes
                ? [new Guid(bytes).ToString()]
                : attribute.StringValueArray;
        }

        return new DirectoryEntry(entry.Dn, attributes);
    }

    public void Dispose() => _connection?.Dispose();

    // ---------------------------------------------------------------------------------------------------------------
    // Connections
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>A connection, with TLS the way the settings say (LDAPS from the start, STARTTLS before anything else is sent), not yet signed in.</summary>
    internal static async Task<LdapConnection> OpenAsync(DirectorySettings settings, CancellationToken cancel)
    {
        LdapConnection? connection = null;
        try
        {
            await WithTimeout(settings, cancel, async token =>
            {
                connection = await ConnectAsync(settings, token);
                if (settings.Security == DirectorySecurity.StartTls)
                {
                    await connection.StartTlsAsync(token);
                }
            });
            return connection!;
        }
        catch (Exception ex)
        {
            connection?.Dispose();
            throw ex switch
            {
                DirectoryException => ex,
                LdapException ldap => new DirectoryException($"No connection to {settings.Host}:{settings.Port}: {Reason(ldap)}", ex),
                SocketException or IOException or System.Security.Authentication.AuthenticationException
                    => new DirectoryException($"No connection to {settings.Host}:{settings.Port}: {ex.Message}", ex),
                _ => ex,
            };
        }
    }

    /// <summary>
    /// Connects to the first address of the host that answers. The library takes only the first address it is given and has no second try: with
    /// IPv6 listed first and not served (<c>localhost</c>, a name with an AAAA record that nobody routes), a host that is there would be "unreachable".
    /// The host name stays the one that is connected to, so that TLS checks the certificate against it.
    /// </summary>
    private static async Task<LdapConnection> ConnectAsync(DirectorySettings settings, CancellationToken cancel)
    {
        IPAddress[] addresses = IPAddress.TryParse(settings.Host, out IPAddress? literal) ? [literal] : await Dns.GetHostAddressesAsync(settings.Host, cancel);
        for (int i = 0; i < addresses.Length; i++)
        {
            IPAddress address = addresses[i];
            var options = new LdapConnectionOptions().ConfigureIpAddressFilter(candidate => candidate.Equals(address));
            if (settings.Security == DirectorySecurity.Ldaps)
            {
                options.UseSsl();
            }

            if (settings.Security != DirectorySecurity.None)
            {
                options.ConfigureRemoteCertificateValidationCallback((_, _, _, errors) => settings.AllowInvalidCertificate || errors == System.Net.Security.SslPolicyErrors.None);
            }

            var connection = new LdapConnection(options);
            bool last = i == addresses.Length - 1;
            try
            {
                // A black hole must not eat the whole time: the next address gets its share.
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                if (!last)
                {
                    attempt.CancelAfter(TimeSpan.FromSeconds(Math.Max(3, settings.TimeoutSeconds / 3)));
                }

                await connection.ConnectAsync(settings.Host, settings.Port, attempt.Token);
                return connection;
            }
            catch (Exception ex) when (!last && !cancel.IsCancellationRequested && ex is OperationCanceledException or SocketException or LdapException { InnerException: SocketException })
            {
                connection.Dispose();
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }

        throw new DirectoryException($"No connection to {settings.Host}:{settings.Port}: the name has no address.");
    }

    /// <summary>What went wrong, as precisely as the library lets us know: it says "Connect Error" for everything, what lies under it says refused, no route, the certificate or the handshake.</summary>
    private static string Reason(LdapException ex)
    {
        Exception root = ex;
        while (root.InnerException is not null)
        {
            root = root.InnerException;
        }

        return ReferenceEquals(root, ex) ? LdapDirectoryClientFactory.Describe(ex) : root.Message;
    }

    /// <summary>Runs an operation with the time the settings allow; a directory that does not answer is an error with a reason, not a wait without end.</summary>
    internal static async Task WithTimeout(DirectorySettings settings, CancellationToken cancel, Func<CancellationToken, Task> operation)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
        try
        {
            await operation(limit.Token);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new DirectoryException($"The directory at {settings.Host}:{settings.Port} did not answer in {settings.TimeoutSeconds} seconds.");
        }
    }
}
