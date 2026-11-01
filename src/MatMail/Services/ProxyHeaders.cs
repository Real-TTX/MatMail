using Microsoft.AspNetCore.HttpOverrides;

namespace MatMail.Services;

/// <summary>
/// The way MatMail sits behind a reverse proxy that terminates TLS, the same in every Mat app (MatFile, MatDo, …) and without a setting: the
/// real client address (it guards the sign-in throttling and the smart-host rules) and the scheme (secure cookies, the addresses of the
/// sign-in through MatIdentity) come from <c>X-Forwarded-For</c> and <c>X-Forwarded-Proto</c>, from whichever proxy sends them (the proxy is
/// not known by address) and only the last hop counts (<c>ForwardLimit</c> 1). So the port belongs to the proxy: published to it alone, not to
/// the internet.
/// </summary>
public static class ProxyHeaders
{
    public static void Configure(ForwardedHeadersOptions options)
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
    }
}
