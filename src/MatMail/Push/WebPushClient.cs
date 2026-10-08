using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using MatMail.Configuration;
using MatMail.Data;

namespace MatMail.Push;

public enum PushOutcome
{
    /// <summary>The push service took the message; it reaches the device when the device is online.</summary>
    Delivered,

    /// <summary>The subscription is gone (the user turned notifications off, or the browser dropped it): forget it.</summary>
    Gone,

    /// <summary>Something else went wrong; the subscription may be fine, try again with the next message.</summary>
    Failed,
}

/// <summary>How a message is to be treated by the push service.</summary>
/// <param name="TtlSeconds">How long the push service keeps it for a device that is offline.</param>
/// <param name="Urgency">"very-low", "low", "normal" or "high": the device may hold back what is not urgent to save battery.</param>
/// <param name="Topic">A new message with the same topic replaces one that has not been delivered yet (up to 32 characters of base64url).</param>
public sealed record PushOptions(int TtlSeconds = 86_400, string Urgency = "normal", string? Topic = null);

/// <summary>Sends a message to a device (what <see cref="WebPushClient"/> does; tests put their own in its place).</summary>
public interface IPushSender
{
    Task<PushOutcome> SendAsync(PushSubscription subscription, byte[] message, PushOptions options, CancellationToken cancel);
}

/// <summary>Posts an encrypted message to the push service of a subscription (RFC 8030), signed as this server (VAPID).</summary>
public sealed class WebPushClient(HttpClient http, PushKeys keys, AppConfig config, ILogger<WebPushClient> logger) : IPushSender
{
    /// <summary>Who the push services can contact about this server: the configured address, else postmaster@ the host name.</summary>
    public string Subject => !string.IsNullOrWhiteSpace(config.Push.Contact) ? config.Push.Contact! : "mailto:postmaster@" + config.Server.Hostname;

    public async Task<PushOutcome> SendAsync(PushSubscription subscription, byte[] message, PushOptions options, CancellationToken cancel)
    {
        byte[]? receiver = WebPushEncryption.FromBase64Url(subscription.P256dh);
        byte[]? auth = WebPushEncryption.FromBase64Url(subscription.Auth);
        if (receiver is not { Length: 65 } || auth is not { Length: 16 } || !Uri.TryCreate(subscription.Endpoint, UriKind.Absolute, out Uri? endpoint) || endpoint.Scheme != Uri.UriSchemeHttps)
        {
            return PushOutcome.Gone;   // nothing that could ever work
        }

        byte[] body = WebPushEncryption.Encrypt(message, receiver, auth);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Content.Headers.ContentEncoding.Add("aes128gcm");
        request.Headers.TryAddWithoutValidation("Authorization", keys.Authorization(subscription.Endpoint, Subject, DateTimeOffset.UtcNow));
        request.Headers.TryAddWithoutValidation("TTL", options.TtlSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("Urgency", options.Urgency);
        if (!string.IsNullOrEmpty(options.Topic))
        {
            request.Headers.TryAddWithoutValidation("Topic", options.Topic);
        }

        try
        {
            using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
            if (response.IsSuccessStatusCode)
            {
                return PushOutcome.Delivered;
            }

            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                return PushOutcome.Gone;
            }

            logger.LogWarning("The push service {Host} answered {Status}.", endpoint.Host, (int)response.StatusCode);
            return PushOutcome.Failed;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancel.IsCancellationRequested)
        {
            logger.LogWarning("The push service {Host} could not be reached: {Reason}", endpoint.Host, ex.Message);
            return PushOutcome.Failed;
        }
    }

    /// <summary>
    /// Opens the connection only to a public address. The endpoint is what a browser told us; without this a signed-in user could make the
    /// server post to its own network (an address like http://192.168.0.1/ or the metadata service of a cloud) by naming it as the endpoint.
    /// </summary>
    internal static async ValueTask<Stream> ConnectToPublicAddressAsync(SocketsHttpConnectionContext context, CancellationToken cancel)
    {
        IPAddress[] candidates = (await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancel)).Where(IsPublic).ToArray();
        if (candidates.Length == 0)
        {
            throw new HttpRequestException("The push address does not lead to a public server.");
        }

        // The public addresses one after the other (a host often has an IPv6 address that this network cannot reach).
        SocketException? last = null;
        foreach (IPAddress target in candidates)
        {
            var socket = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(target, context.DnsEndPoint.Port), cancel);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException ex)
            {
                socket.Dispose();
                last = ex;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        throw new HttpRequestException("The push service could not be reached.", last);
    }

    /// <summary>An address of the internet: not this machine, not a private network, not link-local, not multicast.</summary>
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.Broadcast))
        {
            return false;
        }

        byte[] b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return !(b[0] == 0 || b[0] == 10 || b[0] >= 224
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)      // shared address space of carriers
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 192 && b[1] == 0 && b[2] == 0)
                || (b[0] == 198 && (b[1] == 18 || b[1] == 19)));   // benchmarking
        }

        return address.AddressFamily == AddressFamily.InterNetworkV6
            && !(address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast || (b[0] & 0xFE) == 0xFC);   // fc00::/7 is private
    }
}
