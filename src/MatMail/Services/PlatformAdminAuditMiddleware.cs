using MatMail.Data;

namespace MatMail.Services;

/// <summary>
/// A platform administrator who went into another tenant works there with every permission (to help). Whatever they change there – every
/// request that is not a read, in the administration or the mail client – is written into the activity log of that tenant, so that its
/// administrators can see it. Switching itself is logged by <see cref="SignInService.SwitchTenantAsync"/>.
/// </summary>
public sealed class PlatformAdminAuditMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext http, CurrentUser current, ActivityLogger log)
    {
        await next(http);

        if (!IsInForeignTenant(current, out long tenantId) || IsRead(http.Request.Method))
        {
            return;
        }

        PathString path = http.Request.Path;
        if (!path.StartsWithSegments("/Admin") && !path.StartsWithSegments("/api/mail"))
        {
            return;
        }

        await log.InfoAsync(
            ActivityCategory.Admin,
            $"Platform administrator '{current.Username}' in this tenant: {http.Request.Method} {path}{http.Request.QueryString} ({http.Response.StatusCode}).",
            tenantId: tenantId,
            userId: current.UserId,
            remoteIp: http.ClientAddress());
    }

    /// <summary>A platform administrator working in a tenant that is not their own.</summary>
    public static bool IsInForeignTenant(CurrentUser current, out long tenantId)
    {
        tenantId = current.TenantId ?? 0;
        return current.IsSystemAdmin && tenantId > 0 && current.HomeTenantId is long home && home != tenantId;
    }

    private static bool IsRead(string method) => HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method);
}
