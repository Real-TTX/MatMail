using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Pop3;
using MailKit.Net.Smtp;
using MailKit.Security;
using MatMail.Data;

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

    public ProviderConnector(SecretProtector secrets) => _secrets = secrets;

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

            await client.ConnectAsync(account.ReceiveHost, account.ReceivePort, ToSocketOptions(account.ReceiveSecurity), cancel);
            await client.AuthenticateAsync(account.ReceiveUsername, ReceivePassword(account) ?? string.Empty, cancel);
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

            await client.ConnectAsync(account.ReceiveHost, account.ReceivePort, ToSocketOptions(account.ReceiveSecurity), cancel);
            await client.AuthenticateAsync(account.ReceiveUsername, ReceivePassword(account) ?? string.Empty, cancel);
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

            await client.ConnectAsync(account.SendHost, account.SendPort, ToSocketOptions(account.SendSecurity), cancel);
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
            return new ConnectionTestResult(false, "No receiving server is configured.");
        }

        try
        {
            if (account.ReceiveProtocol == ReceiveProtocol.Imap)
            {
                using ImapClient client = await ConnectImapAsync(account, cancel);
                IMailFolder inbox = client.Inbox;
                await inbox.OpenAsync(FolderAccess.ReadOnly, cancel);
                int folders = (await client.GetFoldersAsync(client.PersonalNamespaces[0], cancellationToken: cancel)).Count;
                string text = $"IMAP: signed in at {account.ReceiveHost}; {folders} folders; the inbox holds {inbox.Count} messages ({inbox.Unread} unread).";
                await client.DisconnectAsync(true, cancel);
                return new ConnectionTestResult(true, text);
            }

            using Pop3Client pop = await ConnectPop3Async(account, cancel);
            int count = pop.Count;
            await pop.DisconnectAsync(true, cancel);
            return new ConnectionTestResult(true, $"POP3: signed in at {account.ReceiveHost}; {count} messages are waiting.");
        }
        catch (Exception ex)
        {
            return new ConnectionTestResult(false, Describe(ex, account.ReceiveHost));
        }
    }

    /// <summary>Opens the SMTP connection and signs in (nothing is sent).</summary>
    public async Task<ConnectionTestResult> TestSendAsync(MailAccount account, CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(account.SendHost))
        {
            return new ConnectionTestResult(false, "No sending server is configured.");
        }

        try
        {
            using SmtpClient client = await ConnectSmtpAsync(account, cancel);
            string text = client.IsAuthenticated
                ? $"SMTP: signed in at {account.SendHost}."
                : $"SMTP: connected to {account.SendHost} (without sign-in).";
            await client.DisconnectAsync(true, cancel);
            return new ConnectionTestResult(true, text);
        }
        catch (Exception ex)
        {
            return new ConnectionTestResult(false, Describe(ex, account.SendHost));
        }
    }

    /// <summary>A short, readable reason for a failed connection.</summary>
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
