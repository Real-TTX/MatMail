using System.Net;
using System.Text;
using MailKit.Net.Smtp;
using MailKit.Security;
using MatMail.Data;
using MatMail.MailServer.Smtp;
using MatMail.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;

namespace MatMail.Tests;

/// <summary>The byte level and the parsing of the SMTP server, without a database.</summary>
public class SmtpProtocolTests
{
    [Theory]
    [InlineData("FROM:<alice@example.test>", "alice@example.test", 0)]
    [InlineData("FROM: <alice@example.test> SIZE=100 BODY=8BITMIME", "alice@example.test", 2)]
    [InlineData("from:<>", "", 0)]
    [InlineData("FROM:alice@example.test SMTPUTF8", "alice@example.test", 1)]
    [InlineData("FROM:<@relay.test,@other.test:alice@example.test>", "alice@example.test", 0)]
    [InlineData("FROM:<\"odd>name\"@example.test>", "\"odd>name\"@example.test", 0)]
    public void Paths_are_read_with_their_parameters(string argument, string address, int parameters)
    {
        SmtpPath? path = SmtpCommandParser.ParsePath(argument, "FROM:");
        Assert.NotNull(path);
        Assert.Equal(address, path.Address);
        Assert.Equal(parameters, path.Parameters.Count);
    }

    [Theory]
    [InlineData("TO:<alice@example.test>")]
    [InlineData("FROM:<alice@example.test")]
    [InlineData("FROM:<a@b.test>SIZE=1")]
    [InlineData("FROM:")]
    [InlineData("FROM:<a@b.test> SIZE=1 size=2")]
    public void Broken_paths_are_refused(string argument) => Assert.Null(SmtpCommandParser.ParsePath(argument, "FROM:"));

    [Fact]
    public void Parameters_keep_their_values_with_upper_case_keys()
    {
        SmtpPath path = SmtpCommandParser.ParsePath("FROM:<a@b.test> size=1234 body=8BITMIME auth=<>", "FROM:")!;
        Assert.Equal("1234", path.Parameters["SIZE"]);
        Assert.Equal("8BITMIME", path.Parameters["BODY"]);
        Assert.Equal("<>", path.Parameters["AUTH"]);
    }

    [Fact]
    public async Task Data_is_unstuffed_normalised_and_ends_only_at_crlf_dot_crlf()
    {
        // One byte per read: every state of the reader meets a buffer boundary.
        string data = "Subject: x\r\n\r\n..dotted\r\nbare lf\n.\nstill data\n.\r\nmore\r\ncr\rinside\r\n.\r\nNOOP\r\n";
        await using var stream = new ScriptedStream(Encoding.ASCII.GetBytes(data), chunkSize: 1);
        var connection = new SmtpConnection(stream);
        using var target = new MemoryStream();

        SmtpDataStatus status = await connection.ReadDataAsync(target, 1_000_000, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(SmtpDataStatus.Complete, status);
        Assert.Equal("Subject: x\r\n\r\n.dotted\r\nbare lf\r\n\r\nstill data\r\n\r\nmore\r\ncr\rinside\r\n", Encoding.ASCII.GetString(target.ToArray()));

        // The connection is in sync: the next command follows the end of the data.
        SmtpLine next = await connection.ReadLineAsync(512, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal(new SmtpLine(SmtpLineStatus.Ok, "NOOP"), next);
    }

    [Fact]
    public async Task Data_beyond_the_limit_is_read_to_the_end_but_reported_too_big()
    {
        string data = string.Concat(Enumerable.Repeat(new string('x', 98) + "\r\n", 100)) + ".\r\nQUIT\r\n";
        await using var stream = new ScriptedStream(Encoding.ASCII.GetBytes(data), chunkSize: 4096);
        var connection = new SmtpConnection(stream);
        using var target = new MemoryStream();

        Assert.Equal(SmtpDataStatus.TooBig, await connection.ReadDataAsync(target, 5000, TimeSpan.FromSeconds(5), CancellationToken.None));
        Assert.True(target.Length <= 5000);
        Assert.Equal("QUIT", (await connection.ReadLineAsync(512, TimeSpan.FromSeconds(5), CancellationToken.None)).Text);
    }

    [Fact]
    public async Task A_connection_lost_during_data_is_reported()
    {
        await using var stream = new ScriptedStream(Encoding.ASCII.GetBytes("Subject: x\r\n\r\nno end"), chunkSize: 7);
        var connection = new SmtpConnection(stream);
        using var target = new MemoryStream();
        Assert.Equal(SmtpDataStatus.Closed, await connection.ReadDataAsync(target, 1000, TimeSpan.FromSeconds(5), CancellationToken.None));
    }

    [Fact]
    public async Task Command_lines_that_are_too_long_are_skipped_whole()
    {
        string input = "NOOP " + new string('x', 70_000) + "\r\nQUIT\r\n";
        await using var stream = new ScriptedStream(Encoding.ASCII.GetBytes(input), chunkSize: 1000);
        var connection = new SmtpConnection(stream);

        Assert.Equal(SmtpLineStatus.TooLong, (await connection.ReadLineAsync(12288, TimeSpan.FromSeconds(5), CancellationToken.None)).Status);
        Assert.Equal("QUIT", (await connection.ReadLineAsync(12288, TimeSpan.FromSeconds(5), CancellationToken.None)).Text);
        Assert.Equal(SmtpLineStatus.Closed, (await connection.ReadLineAsync(12288, TimeSpan.FromSeconds(5), CancellationToken.None)).Status);
    }

    [Fact]
    public async Task Replies_wait_until_the_client_has_to_be_answered()
    {
        await using var stream = new ScriptedStream(Encoding.ASCII.GetBytes("NOOP\r\nNOOP\r\n"), chunkSize: 100);
        var connection = new SmtpConnection(stream);

        await connection.ReadLineAsync(512, TimeSpan.FromSeconds(5), CancellationToken.None);
        connection.Write("250 2.0.0 first");
        await connection.ReadLineAsync(512, TimeSpan.FromSeconds(5), CancellationToken.None);

        // Both commands came in one packet (pipelining): the first reply was not sent yet ...
        Assert.Equal(string.Empty, stream.Written);
        connection.Write("250 2.0.0 second");

        // ... both go out together once the server waits for more input.
        await connection.ReadLineAsync(512, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal("250 2.0.0 first\r\n250 2.0.0 second\r\n", stream.Written);
    }

    [Fact]
    public void Trace_headers_replace_a_return_path_the_client_sent()
    {
        byte[] raw = Encoding.ASCII.GetBytes("Return-Path: <forged@evil.test>\r\n\tcontinued\r\nSubject: Hi\r\nreturn-path: <again@evil.test>\r\n\r\nReturn-Path: in the body stays\r\n");
        string stamped = Encoding.ASCII.GetString(SmtpMessageHandler.AddTraceHeaders(raw, "real@sender.test", "Received: from x ([127.0.0.1])"));

        Assert.Equal("Return-Path: <real@sender.test>\r\nReceived: from x ([127.0.0.1])\r\nSubject: Hi\r\n\r\nReturn-Path: in the body stays\r\n", stamped);
    }

    [Fact]
    public void Sign_in_failures_block_an_address_for_a_while()
    {
        var throttle = new SmtpAuthThrottle();
        IPAddress attacker = IPAddress.Parse("192.0.2.10");
        DateTime start = new(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);

        for (int i = 0; i < SmtpAuthThrottle.MaxFailures - 1; i++)
        {
            Assert.False(throttle.RecordFailure(attacker, start.AddMinutes(i)));
        }

        Assert.False(throttle.IsBlocked(attacker, start.AddMinutes(5)));
        Assert.True(throttle.RecordFailure(attacker, start.AddMinutes(5)));
        Assert.True(throttle.IsBlocked(attacker, start.AddMinutes(14)));
        Assert.False(throttle.IsBlocked(attacker, start.AddMinutes(16)));
        Assert.False(throttle.IsBlocked(IPAddress.Parse("192.0.2.11"), start.AddMinutes(6)));

        // Failures spread wider than the window never add up to a block.
        var slow = new SmtpAuthThrottle();
        for (int i = 0; i < 20; i++)
        {
            Assert.False(slow.RecordFailure(attacker, start.AddMinutes(i * 3)));
        }
    }

    /// <summary>A stream that hands out scripted input in small pieces and records what is written.</summary>
    private sealed class ScriptedStream : Stream
    {
        private readonly byte[] _input;
        private readonly int _chunkSize;
        private readonly MemoryStream _written = new();
        private int _position;

        public ScriptedStream(byte[] input, int chunkSize)
        {
            _input = input;
            _chunkSize = chunkSize;
        }

        public string Written => Encoding.ASCII.GetString(_written.ToArray());
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int take = Math.Min(Math.Min(count, _chunkSize), _input.Length - _position);
            Array.Copy(_input, _position, buffer, offset, take);
            _position += take;
            return take;
        }

        public override void Write(byte[] buffer, int offset, int count) => _written.Write(buffer, offset, count);
        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

/// <summary>The SMTP server over real sockets: capabilities, sign-in, the relay policy and delivery into the database.</summary>
public class SmtpServerTests : IAsyncLifetime
{
    private const string Password = TestMailClients.Password;

    private TestHost _host = null!;
    private Seed _seed = null!;
    private RunningSmtpServer _server = null!;

    public async Task InitializeAsync()
    {
        _host = await TestHost.CreateAsync();
        _seed = await _host.SeedAsync();
        _server = await RunningSmtpServer.StartAsync(_host);
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        await _host.DisposeAsync();
    }

    // -------------------------------------------------------------------------------------------------------------------
    // Capabilities and TLS
    // -------------------------------------------------------------------------------------------------------------------

    [DbFact]
    public async Task The_relay_port_offers_starttls_and_sign_in_only_after_tls()
    {
        (RawSmtpClient client, SmtpReplyLines greeting) = await RawSmtpClient.ConnectAsync(_server.RelayPort);
        await using (client)
        {
            Assert.Equal("220 mail.example.test ESMTP MatMail", greeting.ToString());

            IReadOnlyList<string> plain = await client.EhloAsync();
            Assert.Equal("mail.example.test", plain[0]);
            Assert.Contains("PIPELINING", plain);
            Assert.Contains("SIZE 52428800", plain);
            Assert.Contains("8BITMIME", plain);
            Assert.Contains("ENHANCEDSTATUSCODES", plain);
            Assert.Contains("SMTPUTF8", plain);
            Assert.Contains("STARTTLS", plain);
            Assert.DoesNotContain(plain, line => line.StartsWith("AUTH", StringComparison.Ordinal));

            await client.StartTlsAsync();
            IReadOnlyList<string> secure = await client.EhloAsync();
            Assert.Contains("AUTH PLAIN LOGIN", secure);
            Assert.DoesNotContain("STARTTLS", secure);
            Assert.Equal("503 5.5.1 Error: TLS already active", (await client.CommandAsync("STARTTLS")).ToString());
        }
    }

    [DbFact]
    public async Task Sign_in_before_starttls_is_refused_on_the_submission_port()
    {
        (RawSmtpClient client, _) = await RawSmtpClient.ConnectAsync(_server.SubmissionPort);
        await using (client)
        {
            await client.EhloAsync();
            Assert.Equal("530 5.7.0 Must issue a STARTTLS command first", (await client.CommandAsync("AUTH PLAIN " + Plain("alice", Password))).ToString());
        }
    }

    [DbFact]
    public async Task Port_465_speaks_tls_from_the_first_byte()
    {
        (RawSmtpClient client, SmtpReplyLines greeting) = await RawSmtpClient.ConnectAsync(_server.ImplicitTlsPort, implicitTls: true);
        await using (client)
        {
            Assert.True(client.IsEncrypted);
            Assert.Equal("220 mail.example.test ESMTP MatMail", greeting.ToString());
            IReadOnlyList<string> extensions = await client.EhloAsync();
            Assert.Contains("AUTH PLAIN LOGIN", extensions);
            Assert.DoesNotContain("STARTTLS", extensions);
            Assert.Equal(235, (await client.CommandAsync("AUTH PLAIN " + Plain("alice", Password))).Code);
        }
    }

    [DbFact]
    public async Task Without_the_tls_requirement_sign_in_is_offered_in_plain_text()
    {
        _host.Config.Smtp.RequireTlsForAuth = false;
        (RawSmtpClient client, _) = await RawSmtpClient.ConnectAsync(_server.SubmissionPort);
        await using (client)
        {
            Assert.Contains("AUTH PLAIN LOGIN", await client.EhloAsync());
            Assert.Equal("235 2.7.0 Authentication successful", (await client.CommandAsync("AUTH PLAIN " + Plain("alice", Password))).ToString());
        }
    }

    [DbFact]
    public async Task Without_a_certificate_there_is_neither_starttls_nor_port_465()
    {
        await using RunningSmtpServer plain = await RunningSmtpServer.StartAsync(_host, withTls: false);
        Assert.False(plain.Server.BoundPorts.ContainsKey(SmtpListenerKind.ImplicitTls));

        (RawSmtpClient client, _) = await RawSmtpClient.ConnectAsync(plain.SubmissionPort);
        await using (client)
        {
            IReadOnlyList<string> extensions = await client.EhloAsync();
            Assert.DoesNotContain("STARTTLS", extensions);
            Assert.DoesNotContain(extensions, line => line.StartsWith("AUTH", StringComparison.Ordinal));
            Assert.Equal("454 4.7.0 TLS not available", (await client.CommandAsync("STARTTLS")).ToString());
        }
    }

    // -------------------------------------------------------------------------------------------------------------------
    // Sign-in
    // -------------------------------------------------------------------------------------------------------------------

    [DbFact]
    public async Task Mail_clients_sign_in_with_plain_and_login()
    {
        using (SmtpClient client = await TestMailClients.ConnectAsync(_server.SubmissionPort, SecureSocketOptions.None))
        {
            Assert.True(client.Capabilities.HasFlag(SmtpCapabilities.StartTLS));
            Assert.Empty(client.AuthenticationMechanisms);
        }

        using (SmtpClient client = await TestMailClients.ConnectAsync(_server.SubmissionPort, SecureSocketOptions.StartTls))
        {
            Assert.Contains("PLAIN", client.AuthenticationMechanisms);
            Assert.Contains("LOGIN", client.AuthenticationMechanisms);
            await client.AuthenticateAsync(new SaslMechanismPlain("alice", Password));
            Assert.True(client.IsAuthenticated);
        }

        using (SmtpClient client = await TestMailClients.ConnectAsync(_server.ImplicitTlsPort, SecureSocketOptions.SslOnConnect))
        {
            await client.AuthenticateAsync(new SaslMechanismLogin("bob", Password));
            Assert.True(client.IsAuthenticated);
        }

        using (SmtpClient client = await TestMailClients.ConnectAsync(_server.SubmissionPort, SecureSocketOptions.StartTls))
        {
            await Assert.ThrowsAsync<AuthenticationException>(() => client.AuthenticateAsync(new SaslMechanismPlain("bob", "wrong-password")));
            Assert.False(client.IsAuthenticated);
        }
    }

    [DbFact]
    public async Task The_sign_in_exchange_follows_rfc_4954()
    {
        (RawSmtpClient client, _) = await RawSmtpClient.ConnectAsync(_server.ImplicitTlsPort, implicitTls: true);
        await using (client)
        {
            Assert.Equal("503 5.5.1 Error: send EHLO first", (await client.CommandAsync("AUTH LOGIN")).ToString());
            await client.EhloAsync();
            Assert.Equal("504 5.5.4 Unrecognized authentication type", (await client.CommandAsync("AUTH CRAM-MD5")).ToString());

            Assert.Equal("334 VXNlcm5hbWU6", (await client.CommandAsync("AUTH LOGIN")).ToString());
            Assert.Equal("501 5.7.0 Authentication cancelled", (await client.CommandAsync("*")).ToString());

            Assert.Equal("334 ", (await client.CommandAsync("AUTH PLAIN")).ToString());
            Assert.Equal("501 5.5.2 Cannot decode the response", (await client.CommandAsync("not base64!")).ToString());

            Assert.Equal("535 5.7.8 Authentication credentials invalid", (await client.CommandAsync("AUTH PLAIN " + Plain("nobody", "guess"))).ToString());

            Assert.Equal("334 VXNlcm5hbWU6", (await client.CommandAsync("AUTH LOGIN")).ToString());
            Assert.Equal("334 UGFzc3dvcmQ6", (await client.CommandAsync(Base64("alice"))).ToString());
            Assert.Equal("235 2.7.0 Authentication successful", (await client.CommandAsync(Base64(Password))).ToString());
            Assert.Equal("503 5.5.1 Error: already authenticated", (await client.CommandAsync("AUTH PLAIN " + Plain("alice", Password))).ToString());
        }
    }

    [DbFact]
    public async Task Repeated_sign_in_failures_block_the_address()
    {
        (RawSmtpClient client, _) = await RawSmtpClient.ConnectAsync(_server.ImplicitTlsPort, implicitTls: true);
        await using (client)
        {
            await client.EhloAsync();
            for (int i = 0; i < SmtpAuthThrottle.MaxFailures; i++)
            {
                Assert.Equal(535, (await client.CommandAsync("AUTH PLAIN " + Plain($"guess{i}", "nope"))).Code);
            }

            // Even the right password is refused now, without being checked.
            Assert.Equal(
                "454 4.7.0 Too many failed sign-ins from your address, try again later",
                (await client.CommandAsync("AUTH PLAIN " + Plain("alice", Password))).ToString());
        }

        (RawSmtpClient again, _) = await RawSmtpClient.ConnectAsync(_server.ImplicitTlsPort, implicitTls: true);
        await using (again)
        {
            await again.EhloAsync();
            Assert.Equal(454, (await again.CommandAsync("AUTH PLAIN " + Plain("alice", Password))).Code);
        }

        Assert.True(_server.Throttle.IsBlocked(IPAddress.Loopback));
        Assert.Contains(await ActivityAsync(), log => log.Category == ActivityCategory.Smtp && log.Message.Contains("blocked"));
    }

    // -------------------------------------------------------------------------------------------------------------------
    // Signed-in users
    // -------------------------------------------------------------------------------------------------------------------

    [DbFact]
    public async Task Alice_submits_a_message_for_bob_and_it_lands_in_his_inbox()
    {
        using (SmtpClient client = await TestMailClients.SignInAsync(_server.SubmissionPort, "alice"))
        {
            await client.SendAsync(Message("Alice <alice@example.test>", "bob@example.test", "Lunch?", "At noon?"));
            await client.DisconnectAsync(true);
        }

        MailMessage stored = Assert.Single(await MessagesAsync(_seed.BobMailbox.Id));
        Assert.Equal("Lunch?", stored.Subject);
        Assert.Equal("alice@example.test", stored.FromAddress);

        string raw = RawText(stored);
        Assert.StartsWith("Return-Path: <alice@example.test>\r\nReceived: from client.test ([127.0.0.1])\r\n\t(using TLSv1.", raw);
        Assert.Matches(@"by mail\.example\.test \(MatMail\) with ESMTPSA id [0-9A-F]{12}\r\n\tfor <bob@example\.test>;", raw);

        // Mail clients keep their own copy in "Sent".
        Assert.Empty(await MessagesAsync(_seed.AliceMailbox.Id, FolderKind.Sent));
    }

    [DbFact]
    public async Task A_message_for_an_external_address_is_queued_for_the_routed_provider_account()
    {
        long accountId = await AddProviderAccountAsync("alice@example.test");
        using (SmtpClient client = await TestMailClients.SignInAsync(_server.SubmissionPort, "alice"))
        {
            await client.SendAsync(Message("Alice <alice@example.test>", "friend@outside.test", "Hello outside", "Hi", cc: "bob@example.test"));
        }

        OutboundMessage queued = Assert.Single(await QueueAsync());
        Assert.Equal(accountId, queued.MailAccountId);
        Assert.Equal("alice@example.test", queued.EnvelopeFrom);
        Assert.Equal(new[] { "friend@outside.test" }, queued.Recipients);
        Assert.Equal(_seed.Tenant.Id, queued.TenantId);
        Assert.Equal(_seed.Alice.Id, queued.SenderUserId);
        Assert.Equal(_seed.AliceMailbox.Id, queued.MailboxId);
        Assert.Equal(OutboundStatus.Pending, queued.Status);
        Assert.Equal("Hello outside", queued.Subject);

        // The local Cc recipient got the message at once.
        Assert.Single(await MessagesAsync(_seed.BobMailbox.Id));
    }

    [DbFact]
    public async Task A_signed_in_user_cannot_use_somebody_elses_envelope_sender()
    {
        using SmtpClient client = await TestMailClients.SignInAsync(_server.SubmissionPort, "alice");
        MimeMessage message = Message("Bob <bob@example.test>", "friend@outside.test", "Fake", "x");

        var refused = await Assert.ThrowsAsync<SmtpCommandException>(
            () => client.SendAsync(message, MailboxAddress.Parse("bob@example.test"), new[] { MailboxAddress.Parse("friend@outside.test") }));

        Assert.Equal(SmtpErrorCode.SenderNotAccepted, refused.ErrorCode);
        Assert.Equal(553, (int)refused.StatusCode);
        Assert.Empty(await QueueAsync());
    }

    [DbFact]
    public async Task A_signed_in_user_cannot_put_somebody_elses_address_into_from()
    {
        using SmtpClient client = await TestMailClients.SignInAsync(_server.SubmissionPort, "alice");
        MimeMessage spoofed = Message("Bob <bob@example.test>", "info@example.test", "Fake", "x");

        var refused = await Assert.ThrowsAsync<SmtpCommandException>(
            () => client.SendAsync(spoofed, MailboxAddress.Parse("alice@example.test"), new[] { MailboxAddress.Parse("info@example.test") }));

        Assert.Equal(SmtpErrorCode.MessageNotAccepted, refused.ErrorCode);
        Assert.Equal(550, (int)refused.StatusCode);
        Assert.Contains("bob@example.test", refused.Message);
        Assert.Empty(await MessagesAsync(_seed.Info.Id));

        // The connection stays usable for honest mail.
        await client.SendAsync(Message("Alice <alice@example.test>", "info@example.test", "Real", "x"));
        Assert.Single(await MessagesAsync(_seed.Info.Id));
    }

    [DbFact]
    public async Task Sending_as_a_shared_mailbox_needs_send_rights()
    {
        await GrantAsync(_seed.Info.Id, _seed.Alice.Id, MailboxAccess.Read);
        using (SmtpClient client = await TestMailClients.SignInAsync(_server.SubmissionPort, "alice"))
        {
            var refused = await Assert.ThrowsAsync<SmtpCommandException>(() => client.SendAsync(Message("Info <info@example.test>", "bob@example.test", "As info", "x")));
            Assert.Equal(553, (int)refused.StatusCode);
        }

        await GrantAsync(_seed.Info.Id, _seed.Alice.Id, MailboxAccess.Send);
        using (SmtpClient client = await TestMailClients.SignInAsync(_server.SubmissionPort, "alice"))
        {
            await client.SendAsync(Message("Info <info@example.test>", "bob@example.test", "As info", "x"));
        }

        MailMessage stored = Assert.Single(await MessagesAsync(_seed.BobMailbox.Id));
        Assert.Equal("info@example.test", stored.FromAddress);
    }

    // -------------------------------------------------------------------------------------------------------------------
    // Anonymous clients (other mail servers)
    // -------------------------------------------------------------------------------------------------------------------

    [DbFact]
    public async Task Mail_from_another_server_for_a_local_address_lands_in_the_inbox()
    {
        (RawSmtpClient client, _) = await RawSmtpClient.ConnectAsync(_server.RelayPort);
        await using (client)
        {
            await client.EhloAsync("mx.sender.test");
            string message = "Return-Path: <forged@evil.test>\r\n" + Text(RawMail.Build("Max <max@sender.test>", "alice@example.test", "Hello Alice", "Body"));
            SmtpReplyLines reply = await client.SendMailAsync("max@sender.test", new[] { "alice@example.test" }, message);
            Assert.Equal(250, reply.Code);
            Assert.Matches("^2\\.0\\.0 OK queued as [0-9A-F]{12}$", reply.Text);
        }

        MailMessage stored = Assert.Single(await MessagesAsync(_seed.AliceMailbox.Id));
        Assert.Equal("Hello Alice", stored.Subject);
        Assert.Equal("alice@example.test", stored.EnvelopeRecipients);

        string raw = RawText(stored);
        Assert.StartsWith("Return-Path: <max@sender.test>\r\nReceived: from mx.sender.test ([127.0.0.1])\r\n\tby mail.example.test (MatMail) with ESMTP id ", raw);
        Assert.Contains("\r\n\tfor <alice@example.test>;\r\n\t", raw);
        Assert.DoesNotContain("forged@evil.test", raw);
    }

    [DbFact]
    public async Task Mail_for_an_unknown_address_of_a_local_domain_goes_to_unassigned()
    {
        (RawSmtpClient client, _) = await RawSmtpClient.ConnectAsync(_server.RelayPort);
        await using (client)
        {
            await client.EhloAsync("mx.sender.test");
            SmtpReplyLines reply = await client.SendMailAsync("max@sender.test", new[] { "nobody@example.test" }, Text(RawMail.Build("max@sender.test", "nobody@example.test", "Lost?", "x")));
            Assert.Equal(250, reply.Code);
        }

        MailMessage stored = Assert.Single(await MessagesAsync(_seed.UnassignedMailboxId));
        Assert.Equal("nobody@example.test", stored.EnvelopeRecipients);
    }

    [DbFact]
    public async Task Other_servers_cannot_relay_to_external_addresses()
    {
        (RawSmtpClient client, _) = await RawSmtpClient.ConnectAsync(_server.RelayPort);
        await using (client)
        {
            await client.EhloAsync("mx.spammer.test");
            Assert.Equal("250 2.1.0 Sender OK", (await client.CommandAsync("MAIL FROM:<spammer@spammer.test>")).ToString());
            Assert.Equal("554 5.7.1 <victim@outside.test>: Relay access denied", (await client.CommandAsync("RCPT TO:<victim@outside.test>")).ToString());
            Assert.Equal("554 5.7.1 <victim@outside.test>: Relay access denied", (await client.CommandAsync("RCPT TO:<VICTIM@Outside.test>")).ToString());
            Assert.Equal("503 5.5.1 Error: need RCPT command", (await client.CommandAsync("DATA")).ToString());
        }

        Assert.Empty(await QueueAsync());
        Assert.Contains(await ActivityAsync(), log => log.Message.Contains("relay access denied"));
    }

    [DbFact]
    public async Task Other_servers_cannot_claim_a_local_sender()
    {
        (RawSmtpClient client, _) = await RawSmtpClient.ConnectAsync(_server.RelayPort);
        await using (client)
        {
            await client.EhloAsync("mx.spammer.test");
            Assert.Equal(
                "550 5.7.1 <alice@example.test>: Sender address rejected: example.test is a local domain, please sign in",
                (await client.CommandAsync("MAIL FROM:<alice@example.test>")).ToString());
            Assert.Equal("250 2.1.0 Sender OK", (await client.CommandAsync("MAIL FROM:<>")).ToString());
        }
    }

    [DbFact]
    public async Task The_submission_ports_require_a_sign_in()
    {
        (RawSmtpClient client, _) = await RawSmtpClient.ConnectAsync(_server.SubmissionPort);
        await using (client)
        {
            await client.EhloAsync();
            await client.StartTlsAsync();
            await client.EhloAsync();
            Assert.Equal("530 5.7.0 Authentication required", (await client.CommandAsync("MAIL FROM:<max@sender.test>")).ToString());
        }
    }

    // -------------------------------------------------------------------------------------------------------------------
    // Trusted networks (smart host)
    // -------------------------------------------------------------------------------------------------------------------

    [DbFact]
    public async Task A_trusted_network_relays_for_the_domains_of_its_tenant()
    {
        await AddRelayRuleAsync("127.0.0.1");
        (RawSmtpClient client, _) = await RawSmtpClient.ConnectAsync(_server.RelayPort);
        await using (client)
        {
            await client.EhloAsync("printer.local");
            SmtpReplyLines reply = await client.SendMailAsync(
                "scanner@example.test", new[] { "friend@outside.test" }, Text(RawMail.Build("Scanner <scanner@example.test>", "friend@outside.test", "Scan", "PDF")));
            Assert.Equal(250, reply.Code);

            // A sender outside the tenant's domains is treated like any other client: local recipients only.
            Assert.Equal(250, (await client.CommandAsync("MAIL FROM:<someone@foreign.test>")).Code);
            Assert.Equal(554, (await client.CommandAsync("RCPT TO:<friend@outside.test>")).Code);
            Assert.Equal(250, (await client.CommandAsync("RCPT TO:<alice@example.test>")).Code);
        }

        OutboundMessage queued = Assert.Single(await QueueAsync());
        Assert.Equal(_seed.Tenant.Id, queued.TenantId);
        Assert.Equal("scanner@example.test", queued.EnvelopeFrom);
        Assert.Null(queued.SenderUserId);
        Assert.Contains(await ActivityAsync(), log => log.Message.Contains("relay rule 'Office'"));
    }

    [DbFact]
    public async Task A_trusted_network_with_allowed_sender_domains_relays_only_for_those()
    {
        await AddRelayRuleAsync("127.0.0.1/32", "printers.test");
        (RawSmtpClient client, _) = await RawSmtpClient.ConnectAsync(_server.RelayPort);
        await using (client)
        {
            await client.EhloAsync("printer.local");
            SmtpReplyLines reply = await client.SendMailAsync(
                "scanner@printers.test", new[] { "friend@outside.test" }, Text(RawMail.Build("scanner@printers.test", "friend@outside.test", "Scan", "PDF")));
            Assert.Equal(250, reply.Code);

            // The tenant's own domain is not on the list of this rule: not allowed, and as anonymous sender it is spoofing.
            Assert.Equal(550, (await client.CommandAsync("MAIL FROM:<alice@example.test>")).Code);
        }

        OutboundMessage queued = Assert.Single(await QueueAsync());
        Assert.Equal("scanner@printers.test", queued.EnvelopeFrom);
        Assert.Equal(_seed.Tenant.Id, queued.TenantId);
    }

    // -------------------------------------------------------------------------------------------------------------------
    // Limits, protocol details
    // -------------------------------------------------------------------------------------------------------------------

    [DbFact]
    public async Task Messages_above_the_size_limit_are_refused()
    {
        _host.Config.Smtp.MaxMessageSizeMb = 1;
        (RawSmtpClient client, _) = await RawSmtpClient.ConnectAsync(_server.RelayPort);
        await using (client)
        {
            Assert.Contains("SIZE 1048576", await client.EhloAsync());
            Assert.Equal(
                "552 5.3.4 Message size exceeds fixed maximum message size",
                (await client.CommandAsync("MAIL FROM:<max@sender.test> SIZE=2000000")).ToString());

            string body = string.Concat(Enumerable.Repeat(new string('x', 76) + "\r\n", 16_000));
            SmtpReplyLines reply = await client.SendMailAsync("max@sender.test", new[] { "alice@example.test" }, Text(RawMail.Build("max@sender.test", "alice@example.test", "Big", body)));
            Assert.Equal("552 5.3.4 Message size exceeds fixed maximum message size", reply.ToString());
            Assert.Equal("250 2.0.0 OK", (await client.CommandAsync("NOOP")).ToString());
        }

        Assert.Empty(await MessagesAsync(_seed.AliceMailbox.Id));
    }

    [DbFact]
    public async Task Recipients_beyond_the_limit_get_a_temporary_refusal()
    {
        _host.Config.Smtp.MaxRecipients = 2;
        (RawSmtpClient client, _) = await RawSmtpClient.ConnectAsync(_server.RelayPort);
        await using (client)
        {
            await client.EhloAsync("mx.sender.test");
            Assert.Equal(250, (await client.CommandAsync("MAIL FROM:<max@sender.test>")).Code);
            Assert.Equal(250, (await client.CommandAsync("RCPT TO:<alice@example.test>")).Code);
            Assert.Equal(250, (await client.CommandAsync("RCPT TO:<bob@example.test>")).Code);
            Assert.Equal("452 4.5.3 Error: too many recipients", (await client.CommandAsync("RCPT TO:<info@example.test>")).ToString());
            Assert.Equal(354, (await client.CommandAsync("DATA")).Code);
            await client.SendAsync(Text(RawMail.Build("max@sender.test", "alice@example.test, bob@example.test", "Two of three", "x")) + ".\r\n");
            Assert.Equal(250, (await client.ReadReplyAsync()).Code);
        }

        Assert.Single(await MessagesAsync(_seed.AliceMailbox.Id));
        Assert.Single(await MessagesAsync(_seed.BobMailbox.Id));
        Assert.Empty(await MessagesAsync(_seed.Info.Id));
    }

    [DbFact]
    public async Task Commands_out_of_order_are_refused_and_rset_noop_vrfy_quit_work()
    {
        (RawSmtpClient client, _) = await RawSmtpClient.ConnectAsync(_server.RelayPort);
        await using (client)
        {
            Assert.Equal("503 5.5.1 Error: send HELO/EHLO first", (await client.CommandAsync("MAIL FROM:<a@sender.test>")).ToString());
            Assert.Equal("501 5.5.4 Syntax: HELO hostname", (await client.CommandAsync("HELO")).ToString());
            Assert.Equal("250 mail.example.test", (await client.CommandAsync("HELO client.test")).ToString());
            Assert.Equal("503 5.5.1 Error: need MAIL command", (await client.CommandAsync("RCPT TO:<alice@example.test>")).ToString());
            Assert.Equal("503 5.5.1 Error: need MAIL command", (await client.CommandAsync("DATA")).ToString());
            Assert.Equal("555 5.5.4 Parameters need EHLO", (await client.CommandAsync("MAIL FROM:<a@sender.test> SIZE=10")).ToString());
            Assert.Equal("250 2.1.0 Sender OK", (await client.CommandAsync("MAIL FROM:<a@sender.test>")).ToString());
            Assert.Equal("503 5.5.1 Error: nested MAIL command", (await client.CommandAsync("MAIL FROM:<b@sender.test>")).ToString());
            Assert.Equal("503 5.5.1 Error: need RCPT command", (await client.CommandAsync("DATA")).ToString());
            Assert.Equal("250 2.0.0 OK", (await client.CommandAsync("RSET")).ToString());
            Assert.Equal("250 2.0.0 OK", (await client.CommandAsync("NOOP")).ToString());
            Assert.Equal("252 2.5.0 Cannot VRFY user, but will accept message and attempt delivery", (await client.CommandAsync("VRFY alice")).ToString());
            Assert.Equal("221 2.0.0 mail.example.test closing connection", (await client.CommandAsync("QUIT")).ToString());
            Assert.True(await client.IsClosedAsync());
        }
    }

    [DbFact]
    public async Task Bad_syntax_gets_501_and_too_many_errors_end_the_session()
    {
        (RawSmtpClient client, _) = await RawSmtpClient.ConnectAsync(_server.RelayPort);
        await using (client)
        {
            await client.EhloAsync();
            Assert.Equal("501 5.5.4 Syntax: MAIL FROM:<address>", (await client.CommandAsync("MAIL TO:<a@sender.test>")).ToString());
            Assert.Equal("501 5.1.7 Bad sender address syntax", (await client.CommandAsync("MAIL FROM:<not an address>")).ToString());
            Assert.Equal("555 5.5.4 Unsupported parameter RET", (await client.CommandAsync("MAIL FROM:<a@sender.test> RET=FULL")).ToString());
            Assert.Equal(250, (await client.CommandAsync("MAIL FROM:<a@sender.test>")).Code);
            Assert.Equal("501 5.1.3 Bad recipient address syntax", (await client.CommandAsync("RCPT TO:<no-domain>")).ToString());
            Assert.Equal("500 5.5.2 Error: line too long", (await client.CommandAsync("NOOP " + new string('x', 13_000))).ToString());

            for (int i = 0; i < 4; i++)
            {
                Assert.Equal("500 5.5.2 Error: command not recognized", (await client.CommandAsync("HELLO")).ToString());
            }

            // The tenth refused command ends the session.
            Assert.Equal(500, (await client.CommandAsync("HELLO")).Code);
            Assert.Equal("421 4.7.0 mail.example.test Error: too many errors", (await client.ReadReplyAsync()).ToString());
            Assert.True(await client.IsClosedAsync());
        }
    }

    [DbFact]
    public async Task Pipelined_commands_get_their_replies_in_order()
    {
        (RawSmtpClient client, _) = await RawSmtpClient.ConnectAsync(_server.RelayPort);
        await using (client)
        {
            await client.EhloAsync("mx.sender.test");
            await client.SendAsync("MAIL FROM:<max@sender.test>\r\nRCPT TO:<alice@example.test>\r\nRCPT TO:<victim@outside.test>\r\nRCPT TO:<bob@example.test>\r\nDATA\r\n");
            Assert.Equal(250, (await client.ReadReplyAsync()).Code);
            Assert.Equal(250, (await client.ReadReplyAsync()).Code);
            Assert.Equal(554, (await client.ReadReplyAsync()).Code);
            Assert.Equal(250, (await client.ReadReplyAsync()).Code);
            Assert.Equal(354, (await client.ReadReplyAsync()).Code);

            await client.SendAsync(Text(RawMail.Build("max@sender.test", "alice@example.test, bob@example.test", "Pipelined", "x")) + ".\r\nNOOP\r\n");
            Assert.Equal(250, (await client.ReadReplyAsync()).Code);
            Assert.Equal("250 2.0.0 OK", (await client.ReadReplyAsync()).ToString());
        }

        Assert.Single(await MessagesAsync(_seed.AliceMailbox.Id));
        Assert.Single(await MessagesAsync(_seed.BobMailbox.Id));
    }

    [DbFact]
    public async Task Dot_stuffed_lines_and_utf8_content_arrive_unchanged()
    {
        string message =
            "From: Jörg <jörg@sender.test>\r\nTo: alice@example.test\r\nSubject: Grüße aus Köln\r\nMessage-ID: <utf8@sender.test>\r\n" +
            "MIME-Version: 1.0\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: 8bit\r\n\r\n" +
            "..a line that starts with a dot\r\nÄÖÜ äöü ß € 漢字\r\n...\r\n";

        (RawSmtpClient client, _) = await RawSmtpClient.ConnectAsync(_server.RelayPort);
        await using (client)
        {
            await client.EhloAsync("mx.sender.test");
            Assert.Equal(250, (await client.CommandAsync("MAIL FROM:<jörg@sender.test> SMTPUTF8 BODY=8BITMIME")).Code);
            Assert.Equal(250, (await client.CommandAsync("RCPT TO:<alice@example.test>")).Code);
            Assert.Equal(354, (await client.CommandAsync("DATA")).Code);
            await client.SendAsync(message + ".\r\n");
            Assert.Equal(250, (await client.ReadReplyAsync()).Code);
        }

        MailMessage stored = Assert.Single(await MessagesAsync(_seed.AliceMailbox.Id));
        Assert.Equal("Grüße aus Köln", stored.Subject);
        string raw = RawText(stored);
        Assert.StartsWith("Return-Path: <jörg@sender.test>\r\n", raw);
        Assert.EndsWith("\r\n\r\n.a line that starts with a dot\r\nÄÖÜ äöü ß € 漢字\r\n..\r\n", raw);
    }

    [DbFact]
    public async Task Only_crlf_dot_crlf_ends_the_data_so_nothing_can_be_smuggled()
    {
        string message = "From: max@sender.test\r\nTo: alice@example.test\r\nSubject: Smuggle\r\n\r\n" +
                         "first\n.\nMAIL FROM:<x@sender.test>\r\nsecond\n.\r\nRCPT TO:<victim@outside.test>\r\nthird\r\n";

        (RawSmtpClient client, _) = await RawSmtpClient.ConnectAsync(_server.RelayPort);
        await using (client)
        {
            await client.EhloAsync("mx.sender.test");
            SmtpReplyLines reply = await client.SendMailAsync("max@sender.test", new[] { "alice@example.test" }, message);
            Assert.Equal(250, reply.Code);
            Assert.Equal("250 2.0.0 OK", (await client.CommandAsync("NOOP")).ToString());
        }

        MailMessage stored = Assert.Single(await MessagesAsync(_seed.AliceMailbox.Id));
        string raw = RawText(stored);
        Assert.Contains("first\r\n\r\nMAIL FROM:<x@sender.test>\r\nsecond\r\n\r\nRCPT TO:<victim@outside.test>\r\nthird\r\n", raw);
        Assert.Empty(await QueueAsync());
    }

    [DbFact]
    public async Task Idle_connections_are_closed_after_the_command_timeout()
    {
        await using RunningSmtpServer quick = await RunningSmtpServer.StartAsync(_host, commandTimeout: TimeSpan.FromSeconds(1));
        (RawSmtpClient client, _) = await RawSmtpClient.ConnectAsync(quick.RelayPort);
        await using (client)
        {
            Assert.Equal("421 4.4.2 mail.example.test Error: timeout exceeded", (await client.ReadReplyAsync()).ToString());
            Assert.True(await client.IsClosedAsync());
        }
    }

    [DbFact]
    public async Task Connections_per_address_are_limited()
    {
        _host.Config.Smtp.MaxConnectionsPerIp = 2;
        (RawSmtpClient first, SmtpReplyLines firstGreeting) = await RawSmtpClient.ConnectAsync(_server.RelayPort);
        (RawSmtpClient second, SmtpReplyLines secondGreeting) = await RawSmtpClient.ConnectAsync(_server.RelayPort);
        (RawSmtpClient third, SmtpReplyLines thirdGreeting) = await RawSmtpClient.ConnectAsync(_server.RelayPort);
        await using (first)
        await using (second)
        await using (third)
        {
            Assert.Equal(220, firstGreeting.Code);
            Assert.Equal(220, secondGreeting.Code);
            Assert.Equal("421 4.7.0 mail.example.test Too many connections from your address, try again later", thirdGreeting.ToString());
            Assert.True(await third.IsClosedAsync());
        }
    }

    // -------------------------------------------------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------------------------------------------------

    private static string Plain(string login, string password) => Base64($"\0{login}\0{password}");

    private static string Base64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    private static string Text(byte[] raw) => Encoding.UTF8.GetString(raw);

    private static string RawText(MailMessage message) => Encoding.UTF8.GetString(message.Content!.Raw!);

    private static MimeMessage Message(string from, string to, string subject, string body, string? cc = null)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(from));
        message.To.Add(MailboxAddress.Parse(to));
        if (cc is not null)
        {
            message.Cc.Add(MailboxAddress.Parse(cc));
        }

        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = body };
        return message;
    }

    private async Task<List<MailMessage>> MessagesAsync(long mailboxId, FolderKind kind = FolderKind.Inbox)
    {
        using IServiceScope scope = _host.Scope();
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        return await db.MailMessages.AsNoTracking().Include(m => m.Content)
            .Where(m => m.MailboxId == mailboxId && m.Folder!.Kind == kind)
            .OrderBy(m => m.Uid)
            .ToListAsync();
    }

    private async Task<List<OutboundMessage>> QueueAsync()
    {
        using IServiceScope scope = _host.Scope();
        return await scope.ServiceProvider.GetRequiredService<MatMailDbContext>().OutboundMessages.AsNoTracking().OrderBy(o => o.Id).ToListAsync();
    }

    private async Task<List<ActivityLog>> ActivityAsync()
    {
        using IServiceScope scope = _host.Scope();
        return await scope.ServiceProvider.GetRequiredService<MatMailDbContext>().ActivityLogs.AsNoTracking().OrderBy(l => l.Id).ToListAsync();
    }

    private async Task<long> AddProviderAccountAsync(string address)
    {
        using IServiceScope scope = _host.Scope();
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        var account = new MailAccount
        {
            TenantId = _seed.Tenant.Id, Name = "Provider", Address = address, SendHost = "smtp.provider.test", ReceiveProtocol = ReceiveProtocol.None,
        };
        db.MailAccounts.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }

    private async Task GrantAsync(long mailboxId, long userId, MailboxAccess access)
    {
        using IServiceScope scope = _host.Scope();
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        MailboxPermission? existing = await db.MailboxPermissions.FirstOrDefaultAsync(p => p.MailboxId == mailboxId && p.UserId == userId);
        if (existing is null)
        {
            db.MailboxPermissions.Add(new MailboxPermission { TenantId = _seed.Tenant.Id, MailboxId = mailboxId, UserId = userId, Access = access });
        }
        else
        {
            existing.Access = access;
        }

        await db.SaveChangesAsync();
    }

    private async Task AddRelayRuleAsync(string network, params string[] allowedSenderDomains)
    {
        using IServiceScope scope = _host.Scope();
        var db = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();
        db.RelayRules.Add(new RelayRule { TenantId = _seed.Tenant.Id, Name = "Office", Network = network, AllowedSenderDomains = allowedSenderDomains });
        await db.SaveChangesAsync();
    }
}
