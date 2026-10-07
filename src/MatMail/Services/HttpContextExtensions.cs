namespace MatMail.Services;

public static class HttpContextExtensions
{
    /// <summary>
    /// The client's address as text. IPv4 clients that arrive over a dual-stack socket show up as ::ffff:a.b.c.d; this maps them back to a.b.c.d.
    /// </summary>
    public static string? ClientAddress(this HttpContext http)
    {
        System.Net.IPAddress? address = http.Connection.RemoteIpAddress;
        if (address is null)
        {
            return null;
        }

        return (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
    }
}
