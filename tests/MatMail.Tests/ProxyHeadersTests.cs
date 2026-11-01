using System.Net;
using MatMail.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MatMail.Tests;

/// <summary>Behind a reverse proxy without a setting (<see cref="ProxyHeaders"/>): the real client and the scheme come from <c>X-Forwarded-*</c>.</summary>
public class ProxyHeadersTests
{
    private static async Task<HttpContext> RunAsync(string remote, Action<IHeaderDictionary> headers)
    {
        var options = new ForwardedHeadersOptions();
        ProxyHeaders.Configure(options);
        var middleware = new Microsoft.AspNetCore.HttpOverrides.ForwardedHeadersMiddleware(_ => Task.CompletedTask, NullLoggerFactory.Instance, Options.Create(options));
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(remote);
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("mail.example.test");
        headers(context.Request.Headers);
        await middleware.Invoke(context);
        return context;
    }

    [Fact]
    public async Task The_client_and_the_scheme_come_from_the_proxy_of_any_address()
    {
        // a proxy in the Docker network, on the host, or far away: none has to be named
        foreach (string proxy in new[] { "172.18.0.2", "127.0.0.1", "10.0.0.5", "203.0.113.7" })
        {
            HttpContext context = await RunAsync(proxy, h => { h["X-Forwarded-For"] = "198.51.100.23"; h["X-Forwarded-Proto"] = "https"; });

            Assert.Equal("198.51.100.23", context.Connection.RemoteIpAddress!.ToString());
            Assert.Equal("https", context.Request.Scheme);
            Assert.True(context.Request.IsHttps);
        }
    }

    [Fact]
    public async Task Only_the_last_proxy_counts_and_the_host_stays_what_the_proxy_sent()
    {
        // somebody put a made-up address in front: the proxy appended the real one, which is the one that counts
        HttpContext context = await RunAsync("172.18.0.2", h => { h["X-Forwarded-For"] = "1.2.3.4, 198.51.100.23"; h["X-Forwarded-Host"] = "evil.example"; });

        Assert.Equal("198.51.100.23", context.Connection.RemoteIpAddress!.ToString());
        Assert.Equal("mail.example.test", context.Request.Host.Value);
        Assert.Equal("http", context.Request.Scheme);   // no X-Forwarded-Proto: nothing changes
    }

    [Fact]
    public async Task Without_headers_nothing_changes()
    {
        HttpContext context = await RunAsync("192.0.2.10", _ => { });

        Assert.Equal("192.0.2.10", context.Connection.RemoteIpAddress!.ToString());
        Assert.False(context.Request.IsHttps);
    }
}
