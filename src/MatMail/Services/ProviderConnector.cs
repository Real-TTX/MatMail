using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Pop3;
using MailKit.Net.Smtp;
using MailKit.Security;
using MatMail.Data;
using Microsoft.Extensions.Localization;

namespace MatMail.Services;

public sealed record ConnectionTestResult(bool Ok, string Message);

/// <summary>
/// Talks to the connected provider accounts (MailKit): opens IMAP / POP3 / SMTP connections with the stored settings and
/// tests them for the admin pages. The synchronisation and the outgoing queue use the same connect methods.
/// </summary>
public sealed class ProviderConnector
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly SecretProtector _secrets;
    private readonly IStringLocalizer<SharedResource> _l;

    /// <param name="localizer">Texts shown to administrators (connection test); without one they stay English.</param>
    public ProviderConnector(SecretProtector secrets, IStringLocalizer<SharedResource>? localizer = null)
    {
        _secrets = secrets;
        _l = localizer ?? new EnglishLocalizer();
    }

    public static SecureSocketOptions ToSocketOptions(ConnectionSecurity security) => security switch
    {
        ConnectionSecurity.Ssl => SecureSocketOptions.SslOnConnect,
        ConnectionSecurity.StartTls => SecureSocketOptions.StartTls,
        _ => SecureSocketOptions.None,
    };

    public string? ReceivePassword(MailAccount account) => _secrets.Unprotect(account.ReceivePasswordProtected);

    public (string? User, string? Password) SendCredentials(MailAccount account)
        => account.SendUsesReceiveCredentials
            ? (account.ReceiveUsername, _secrets.Unprotect(account.ReceivePasswordProtected))
            : (account.SendUsername, _secrets.Unprotect(account.SendPasswordProtected));

    public async Task<ImapClient> ConnectImapAsync(MailAccount account, CancellationToken cancel = default)
    {
        var client = new ImapClient { Timeout = (int)Timeout.TotalMilliseconds };
        try
        {
            if (account.AllowInvalidCertificate)
            {
                client.ServerCertificateValidationCallback = (_, _, _, _) => true;
            }

            await client.ConnectAsync(account.ReceiveHost!, account.ReceivePort, ToSocketOptions(account.ReceiveSecurity), cancel);
            await client.AuthenticateAsync(account.ReceiveUsername ?? string.Empty, ReceivePassword(account) ?? string.Empty, cancel);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public async Task<Pop3Client> ConnectPop3Async(MailAccount account, CancellationToken cancel = default)
    {
        var client = new Pop3Client { Timeout = (int)Timeout.TotalMilliseconds };
        try
        {
            if (account.AllowInvalidCertificate)
            {
                client.ServerCertificateValidationCallback = (_, _, _, _) => true;
            }

            await client.ConnectAsync(account.ReceiveHost!, account.ReceivePort, ToSocketOptions(account.ReceiveSecurity), cancel);
            await client.AuthenticateAsync(account.ReceiveUsername ?? string.Empty, ReceivePassword(account) ?? string.Empty, cancel);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public async Task<SmtpClient> ConnectSmtpAsync(MailAccount account, CancellationToken cancel = default)
    {
        var client = new SmtpClient { Timeout = (int)Timeout.TotalMilliseconds };
        try
        {
            if (account.AllowInvalidCertificate)
            {
                client.ServerCertificateValidationCallback = (_, _, _, _) => true;
            }

            await client.ConnectAsync(account.SendHost!, account.SendPort, ToSocketOptions(account.SendSecurity), cancel);
            (string? user, string? password) = SendCredentials(account);
            if (!string.IsNullOrEmpty(user))
            {
                await client.AuthenticateAsync(user, password ?? string.Empty, cancel);
            }

            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>Opens the receiving connection, signs in and reports what is there.</summary>
    public async Task<ConnectionTestResult> TestReceiveAsync(MailAccount account, CancellationToken cancel = default)
    {
        if (account.ReceiveProtocol == ReceiveProtocol.None || string.IsNullOrWhiteSpace(account.ReceiveHost))
        {
            return new ConnectionTestResult(false, _l["No receiving server is configured."]);
        }

        try
        {
            if (account.ReceiveProtocol == ReceiveProtocol.Imap)
            {
                using ImapClient client = await ConnectImapAsync(account, cancel);
                IMailFolder inbox = client.Inbox;
                await inbox.OpenAsync(FolderAccess.ReadOnly, cancel);
                int folders = (await client.GetFoldersAsync(client.PersonalNamespaces[0], cancellationToken: cancel)).Count;
                string text = _l["IMAP: signed in at {0}; {1} folders; the inbox holds {2} messages ({3} unread).", account.ReceiveHost!, folders, inbox.Count, inbox.Unread];
                await client.DisconnectAsync(true, cancel);
                return new ConnectionTestResult(true, text);
            }

            using Pop3Client pop = await ConnectPop3Async(account, cancel);
            int count = pop.Count;
            await pop.DisconnectAsync(true, cancel);
            return new ConnectionTestResult(true, _l["POP3: signed in at {0}; {1} messages are waiting.", account.ReceiveHost!, count]);
        }
        catch (Exception ex)
        {
            return new ConnectionTestResult(false, DescribeFailure(ex, account.ReceiveHost));
        }
    }

    /// <summary>Opens the SMTP connection and signs in (nothing is sent).</summary>
    public async Task<ConnectionTestResult> TestSendAsync(MailAccount account, CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(account.SendHost))
        {
            return new ConnectionTestResult(false, _l["No sending server is configured."]);
        }

        try
        {
            using SmtpClient client = await ConnectSmtpAsync(account, cancel);
            string text = client.IsAuthenticated
                ? _l["SMTP: signed in at {0}.", account.SendHost!]
                : _l["SMTP: connected to {0} (without sign-in).", account.SendHost!];
            await client.DisconnectAsync(true, cancel);
            return new ConnectionTestResult(true, text);
        }
        catch (Exception ex)
        {
            return new ConnectionTestResult(false, DescribeFailure(ex, account.SendHost));
        }
    }

    /// <summary>A short, readable reason for a failed connection, in the language of the current user.</summary>
    public string DescribeFailure(Exception ex, string? host) => ex switch
    {
        AuthenticationException => _l["{0}: the user name or password was refused.", host ?? string.Empty],
        SslHandshakeException => _l["{0}: the TLS connection failed (certificate or protocol problem): {1}", host ?? string.Empty, ex.Message],
        OperationCanceledException or TimeoutException => _l["{0}: the server did not answer in time.", host ?? string.Empty],
        System.Net.Sockets.SocketException socket => _l["{0}: cannot connect ({1}).", host ?? string.Empty, socket.Message],
        ServiceNotConnectedException => _l["{0}: the connection was closed.", host ?? string.Empty],
        _ => $"{host}: {ex.Message}",
    };

    /// <summary>The same in English, for the activity log and the stored status of a synchronisation.</summary>
    public static string Describe(Exception ex, string? host) => ex switch
    {
        AuthenticationException => $"{host}: the user name or password was refused.",
        SslHandshakeException => $"{host}: the TLS connection failed (certificate or protocol problem): {ex.Message}",
        OperationCanceledException => $"{host}: the server did not answer in time.",
        TimeoutException => $"{host}: the server did not answer in time.",
        System.Net.Sockets.SocketException socket => $"{host}: cannot connect ({socket.Message}).",
        ServiceNotConnectedException => $"{host}: the connection was closed.",
        _ => $"{host}: {ex.Message}",
    };
}

/// <summary>Stands in for the localizer where none is available (tests, the live-access fetcher): formats the English text.</summary>
internal sealed class EnglishLocalizer : IStringLocalizer<SharedResource>
{
    public LocalizedString this[string name] => new(name, name, resourceNotFound: true);

    public LocalizedString this[string name, params object[] arguments] => new(name, string.Format(name, arguments), resourceNotFound: true);

    public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => Array.Empty<LocalizedString>();
}
