using System.Text;
using System.Threading.Channels;
using MatMail.Configuration;
using MatMail.Data;
using MatMail.Messaging;
using MatMail.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MimeKit;

namespace MatMail.Api;

/// <summary>
/// The JSON interface of the web mail client under <c>/api/mail</c>. Every call needs a signed-in user with the mail permission;
/// changes need the anti-forgery header the page provides. Access to mailboxes is always checked against
/// <see cref="MailAccessService"/>, never trusted from the request.
/// </summary>
public static class MailApi
{
    private static readonly string[] SafeInlineTypes = { "image/png", "image/jpeg", "image/gif", "image/webp", "image/bmp", "application/pdf", "text/plain" };

    public static void MapMailApi(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder api = app.MapGroup("/api/mail").RequireAuthorization(Permissions.MailUse).AddEndpointFilter(RequireAntiforgery);

        api.MapGet("/bootstrap", Bootstrap);
        api.MapGet("/counts", Counts);
        api.MapGet("/messages", ListMessages);
        api.MapGet("/messages/{id:long}", GetMessage);
        api.MapGet("/messages/{id:long}/body", GetBody);
        api.MapGet("/messages/{id:long}/attachment/{index:int}", GetAttachment);
        api.MapGet("/messages/{id:long}/cid/{cid}", GetInlinePart);
        api.MapGet("/messages/{id:long}/raw", GetRaw);
        api.MapPost("/messages/flags", ChangeFlags);
        api.MapPost("/messages/move", MoveMessages);
        api.MapPost("/messages/delete", DeleteMessages);
        api.MapPost("/messages/spam", MarkSpam);

        api.MapPost("/folders", CreateFolder);
        api.MapPatch("/folders/{id:long}", RenameFolder);
        api.MapDelete("/folders/{id:long}", DeleteFolder);
        api.MapPost("/folders/{id:long}/markread", MarkFolderRead);
        api.MapPost("/folders/{id:long}/empty", EmptyFolder);

        api.MapPost("/attachments", UploadAttachment);
        api.MapDelete("/attachments/{id}", DeleteAttachment);
        api.MapGet("/compose/reply", PrepareReply);
        api.MapGet("/compose/draft/{id:long}", OpenDraft);
        api.MapPost("/drafts", SaveDraft);
        api.MapDelete("/drafts/{id:long}", DiscardDraft);
        api.MapPost("/send", Send);
        api.MapGet("/contacts", Contacts);
        api.MapGet("/events", Events);
    }

    private static async ValueTask<object?> RequireAntiforgery(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        HttpContext http = context.HttpContext;
        if (!HttpMethods.IsGet(http.Request.Method) && !HttpMethods.IsHead(http.Request.Method))
        {
            try
            {
                await http.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(http);
            }
            catch (AntiforgeryValidationException)
            {
                return Results.Json(new { error = "The page is out of date. Reload it and try again." }, statusCode: StatusCodes.Status400BadRequest);
            }
        }

        return await next(context);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Start-up data
    // ---------------------------------------------------------------------------------------------------------------

    private static async Task<IResult> Bootstrap(
        MailAccessService access, MailStore store, FolderService folders, SignatureService signatures, CurrentUser current, AppConfig config, MatMailDbContext db, CancellationToken cancel)
    {
        MailUser? user = access.GetCurrentUser();
        if (user is null)
        {
            return Results.Unauthorized();
        }

        IReadOnlyList<AccessibleMailbox> boxes = await access.GetMailboxesAsync(user, cancel);
        var mailboxes = new List<MailboxDto>();
        foreach (AccessibleMailbox box in boxes)
        {
            IReadOnlyList<FolderInfo> infos = await folders.ListAsync(box.Mailbox.Id, cancel);
            Dictionary<long, (int Total, int Unread)> counts = await store.GetCountsAsync(infos.Select(i => i.Id), cancel);
            mailboxes.Add(new MailboxDto(
                box.Mailbox.Id,
                box.Mailbox.Type == MailboxType.Unassigned ? "Unassigned" : box.Mailbox.Name,
                box.Mailbox.Type.ToString(),
                box.IsOwn,
                box.Access.ToString(),
                box.Access >= MailboxAccess.Edit,
                box.Access >= MailboxAccess.Send,
                box.Access >= MailboxAccess.Manage,
                infos.Select(i => new FolderDto(i.Id, i.Folder.Name, i.Path, i.Kind.ToString(), i.Path.Count(c => c == FolderService.Separator), counts[i.Id].Unread, counts[i.Id].Total)).ToList()));
        }

        IReadOnlyList<SendIdentity> identities = await access.GetSendIdentitiesAsync(user, cancel);
        var identityDtos = identities.Select(i => new IdentityDto(
            i.Alias.Address, i.IsOwn ? i.Alias.Address : $"{i.Mailbox.Name} <{i.Alias.Address}>", i.Mailbox.Id, i.Alias.IsPrimary, i.IsOwn)).ToList();

        User? self = await db.Users.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(u => u.Id == user.UserId, cancel);
        string tenantName = current.TenantName ?? string.Empty;
        var signatureDtos = new List<SignatureDto>();
        var seen = new HashSet<long>();
        foreach (SendIdentity identity in identities)
        {
            foreach (Signature signature in await signatures.GetSelectableAsync(user.TenantId, identity.Mailbox.Id, user.UserId, cancel))
            {
                if (seen.Add(signature.Id))
                {
                    var context = new SignatureContext(user.DisplayName, identity.Alias.Address, self?.JobTitle, self?.Phone, tenantName);
                    signatureDtos.Add(new SignatureDto(signature.Id, signature.Name, SignatureService.Render(signature.Html, context, html: true), signature.IsDefault, signature.Scope.ToString(), signature.MailboxId));
                }
            }
        }

        return Results.Ok(new BootstrapDto(
            new UserDto(user.UserId, user.DisplayName, user.LoginName, current.CanAdminister),
            mailboxes,
            identityDtos,
            signatureDtos,
            new SettingsDto(50, config.Server.MaxUploadMb, config.Server.Hostname)));
    }

    private static async Task<IResult> Counts(long mailboxId, MailAccessService access, MailStore store, FolderService folders, CancellationToken cancel)
    {
        (MailUser? user, IResult? error) = await RequireMailboxAsync(access, mailboxId, MailboxAccess.Read, cancel);
        if (user is null)
        {
            return error!;
        }

        IReadOnlyList<FolderInfo> infos = await folders.ListAsync(mailboxId, cancel);
        Dictionary<long, (int Total, int Unread)> counts = await store.GetCountsAsync(infos.Select(i => i.Id), cancel);
        return Results.Ok(counts.ToDictionary(c => c.Key, c => new CountsDto(c.Value.Unread, c.Value.Total)));
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Lists and messages
    // ---------------------------------------------------------------------------------------------------------------

    private static async Task<IResult> ListMessages(
        long mailboxId, long? folderId, string? q, int? page, int? pageSize, MailAccessService access, MatMailDbContext db, CancellationToken cancel)
    {
        (MailUser? user, IResult? error) = await RequireMailboxAsync(access, mailboxId, MailboxAccess.Read, cancel);
        if (user is null)
        {
            return error!;
        }

        MailQuery query = MailQuery.Parse(mailboxId, folderId, q);
        int size = Math.Clamp(pageSize ?? 50, 10, 100);
        IQueryable<MailMessage> filtered = query.Apply(db.MailMessages.AsNoTracking(), db);
        int total = await filtered.CountAsync(cancel);
        int pageNumber = Math.Clamp(page ?? 1, 1, Math.Max(1, (int)Math.Ceiling(total / (double)size)));

        List<MessageListItemDto> items = await filtered
            .Skip((pageNumber - 1) * size)
            .Take(size)
            .Select(m => new MessageListItemDto(
                m.Id, m.FolderId, m.Uid, m.Subject, m.FromName, m.FromAddress, m.ToSummary, m.Preview, m.ReceivedDate,
                m.IsRead, m.IsStarred, m.HasAttachments, m.IsDraft, m.IsAnswered, m.IsForwarded, m.Folder!.Kind.ToString()))
            .ToListAsync(cancel);
        return Results.Ok(new MessageListDto(total, pageNumber, size, items));
    }

    private static async Task<IResult> GetMessage(
        long id, MailAccessService access, MailStore store, MailBodyRenderer renderer, MatMailDbContext db, CancellationToken cancel)
    {
        (MailUser? user, MailMessage? message, MailboxAccess accessLevel, IResult? error) = await RequireMessageAsync(access, db, id, MailboxAccess.Read, cancel);
        if (message is null)
        {
            return error!;
        }

        MimeMessage? mime = await LoadMimeAsync(store, id, cancel);
        if (mime is null)
        {
            return Results.NotFound();
        }

        RenderedBody body = renderer.Render(mime, id, allowRemoteImages: false);
        string folderKind = await db.MailFolders.AsNoTracking().Where(f => f.Id == message.FolderId).Select(f => f.Kind.ToString()).FirstAsync(cancel);
        string? unsubscribe = mime.Headers[HeaderId.ListUnsubscribe];

        List<AttachmentDto> attachments = renderer.GetAttachments(mime)
            .Select(a => new AttachmentDto(a.Index, a.FileName, a.ContentType, a.Size, $"/api/mail/messages/{id}/attachment/{a.Index}"))
            .ToList();

        return Results.Ok(new MessageDetailDto(
            id, message.FolderId, message.MailboxId, folderKind, mime.Subject ?? string.Empty,
            ToDto(mime.From.Mailboxes).FirstOrDefault(),
            ToDto(mime.To.Mailboxes), ToDto(mime.Cc.Mailboxes), ToDto(mime.Bcc.Mailboxes), ToDto(mime.ReplyTo.Mailboxes),
            message.ReceivedDate, message.IsRead, message.IsStarred, message.IsDraft,
            accessLevel >= MailboxAccess.Edit, accessLevel >= MailboxAccess.Send, body.HasRemoteContent,
            $"/api/mail/messages/{id}/body", attachments, message.EnvelopeRecipients, unsubscribe, message.SizeBytes));
    }

    /// <summary>The message text as a self-contained HTML document for a sandboxed iframe (no scripts, restricted resources).</summary>
    private static async Task<IResult> GetBody(
        long id, bool? images, MailAccessService access, MailStore store, MailBodyRenderer renderer, MatMailDbContext db, HttpContext http, CancellationToken cancel)
    {
        (_, MailMessage? message, _, IResult? error) = await RequireMessageAsync(access, db, id, MailboxAccess.Read, cancel);
        if (message is null)
        {
            return error!;
        }

        MimeMessage? mime = await LoadMimeAsync(store, id, cancel);
        if (mime is null)
        {
            return Results.NotFound();
        }

        bool allowImages = images == true;
        RenderedBody body = renderer.Render(mime, id, allowImages);
        string imageSources = allowImages ? "img-src 'self' data: https: http:" : "img-src 'self' data:";
        http.Response.Headers.ContentSecurityPolicy = $"default-src 'none'; {imageSources}; style-src 'unsafe-inline'; font-src data: https:; base-uri 'none'; form-action 'none'";
        http.Response.Headers.XContentTypeOptions = "nosniff";
        http.Response.Headers["Referrer-Policy"] = "no-referrer";
        http.Response.Headers.CacheControl = "private, max-age=0, must-revalidate";

        string document = "<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><base target=\"_blank\">"
            + "<style>html,body{margin:0;padding:0}body{font:14px/1.55 system-ui,-apple-system,'Segoe UI',Roboto,Arial,sans-serif;color:#202124;background:#fff;word-wrap:break-word;overflow-wrap:anywhere;padding:2px}"
            + "img{max-width:100%;height:auto}table{max-width:100%}pre{white-space:pre-wrap}blockquote{margin:.6em 0 .6em .4em;padding-left:.8em;border-left:3px solid #dadce0;color:#5f6368}a{color:#1a73e8}</style></head><body>"
            + body.Html + "</body></html>";
        return Results.Content(document, "text/html; charset=utf-8");
    }

    private static async Task<IResult> GetAttachment(
        long id, int index, bool? inline, MailAccessService access, MailStore store, MailBodyRenderer renderer, MatMailDbContext db, HttpContext http, CancellationToken cancel)
    {
        (_, MailMessage? message, _, IResult? error) = await RequireMessageAsync(access, db, id, MailboxAccess.Read, cancel);
        if (message is null)
        {
            return error!;
        }

        MimeMessage? mime = await LoadMimeAsync(store, id, cancel);
        MimeEntity? entity = mime is null ? null : renderer.FindAttachment(mime, index);
        if (entity is null)
        {
            return Results.NotFound();
        }

        return await SendEntityAsync(http, entity, MailBodyRenderer.FileNameOf(entity, index + 1), inline == true, cancel);
    }

    private static async Task<IResult> GetInlinePart(
        long id, string cid, MailAccessService access, MailStore store, MailBodyRenderer renderer, MatMailDbContext db, HttpContext http, CancellationToken cancel)
    {
        (_, MailMessage? message, _, IResult? error) = await RequireMessageAsync(access, db, id, MailboxAccess.Read, cancel);
        if (message is null)
        {
            return error!;
        }

        MimeMessage? mime = await LoadMimeAsync(store, id, cancel);
        MimePart? part = mime is null ? null : renderer.FindByContentId(mime, cid);
        return part is null ? Results.NotFound() : await SendEntityAsync(http, part, MailBodyRenderer.FileNameOf(part, 1), inline: true, cancel);
    }

    private static async Task<IResult> GetRaw(long id, MailAccessService access, MailStore store, MatMailDbContext db, CancellationToken cancel)
    {
        (_, MailMessage? message, _, IResult? error) = await RequireMessageAsync(access, db, id, MailboxAccess.Read, cancel);
        if (message is null)
        {
            return error!;
        }

        byte[]? raw = await store.GetRawAsync(id, cancel);
        return raw is null ? Results.NotFound() : Results.File(raw, "message/rfc822", $"message-{id}.eml");
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Changes
    // ---------------------------------------------------------------------------------------------------------------

    private static async Task<IResult> ChangeFlags(FlagsRequest request, MailAccessService access, MailStore store, MatMailDbContext db, CancellationToken cancel)
    {
        (MailUser? user, List<MailMessage> messages, IResult? error) = await AllowedMessagesAsync(access, db, request.Ids, MailboxAccess.Edit, cancel);
        if (user is null)
        {
            return error!;
        }

        int changed = await store.ChangeFlagsAsync(messages.Select(m => m.Id), new FlagChange { IsRead = request.IsRead, IsStarred = request.IsStarred }, cancel);
        return Results.Ok(new ChangeResult(changed, await CountsOfAsync(store, messages.Select(m => m.FolderId), cancel)));
    }

    private static async Task<IResult> MoveMessages(MoveRequest request, MailAccessService access, MailStore store, MatMailDbContext db, CancellationToken cancel)
    {
        (MailUser? user, List<MailMessage> messages, IResult? error) = await AllowedMessagesAsync(access, db, request.Ids, MailboxAccess.Edit, cancel);
        if (user is null)
        {
            return error!;
        }

        MailFolder? target = await access.GetFolderAsync(user, request.FolderId, MailboxAccess.Edit, cancel);
        if (target is null)
        {
            return Results.NotFound();
        }

        // Moving between mailboxes needs the same rights on both sides; the tenant filter keeps it inside the tenant.
        IReadOnlyList<MailMessage> moved = await store.MoveAsync(messages.Select(m => m.Id), target.Id, cancel);
        return Results.Ok(new ChangeResult(moved.Count, await CountsOfAsync(store, messages.Select(m => m.FolderId).Append(target.Id), cancel)));
    }

    private static async Task<IResult> DeleteMessages(DeleteRequest request, MailAccessService access, MailStore store, MatMailDbContext db, CancellationToken cancel)
    {
        (MailUser? user, List<MailMessage> messages, IResult? error) = await AllowedMessagesAsync(access, db, request.Ids, MailboxAccess.Edit, cancel);
        if (user is null)
        {
            return error!;
        }

        long[] folderIds = messages.Select(m => m.FolderId).Distinct().ToArray();
        int affected = await store.DeleteAsync(messages.Select(m => m.Id), request.Permanent, cancel);
        long[] trashIds = await db.MailFolders.AsNoTracking().Where(f => f.Kind == FolderKind.Trash && messages.Select(m => m.MailboxId).Contains(f.MailboxId)).Select(f => f.Id).ToArrayAsync(cancel);
        return Results.Ok(new ChangeResult(affected, await CountsOfAsync(store, folderIds.Concat(trashIds), cancel)));
    }

    private static async Task<IResult> MarkSpam(SpamRequest request, MailAccessService access, MailStore store, FolderService folders, MatMailDbContext db, CancellationToken cancel)
    {
        (MailUser? user, List<MailMessage> messages, IResult? error) = await AllowedMessagesAsync(access, db, request.Ids, MailboxAccess.Edit, cancel);
        if (user is null)
        {
            return error!;
        }

        int moved = 0;
        var touched = new List<long>();
        foreach (IGrouping<long, MailMessage> mailbox in messages.GroupBy(m => m.MailboxId))
        {
            MailFolder? target = await folders.FindByKindAsync(mailbox.Key, request.NotSpam ? FolderKind.Inbox : FolderKind.Junk, cancel);
            if (target is null)
            {
                continue;
            }

            moved += (await store.MoveAsync(mailbox.Select(m => m.Id), target.Id, cancel)).Count;
            touched.Add(target.Id);
        }

        return Results.Ok(new ChangeResult(moved, await CountsOfAsync(store, messages.Select(m => m.FolderId).Concat(touched), cancel)));
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Folders
    // ---------------------------------------------------------------------------------------------------------------

    private static async Task<IResult> CreateFolder(CreateFolderRequest request, MailAccessService access, FolderService folders, CancellationToken cancel)
    {
        (MailUser? user, IResult? error) = await RequireMailboxAsync(access, request.MailboxId, MailboxAccess.Manage, cancel);
        if (user is null)
        {
            return error!;
        }

        (MailFolder? folder, string? failure) = await folders.CreateAsync(request.MailboxId, request.Path, cancel);
        return folder is null ? Results.BadRequest(new { error = failure }) : Results.Ok(new { id = folder.Id });
    }

    private static async Task<IResult> RenameFolder(long id, RenameFolderRequest request, MailAccessService access, FolderService folders, CancellationToken cancel)
    {
        MailUser? user = access.GetCurrentUser();
        MailFolder? folder = user is null ? null : await access.GetFolderAsync(user, id, MailboxAccess.Manage, cancel);
        if (folder is null)
        {
            return Results.NotFound();
        }

        string? failure = await folders.RenameAsync(id, request.Path, cancel);
        return failure is null ? Results.Ok() : Results.BadRequest(new { error = failure });
    }

    private static async Task<IResult> DeleteFolder(long id, MailAccessService access, FolderService folders, CancellationToken cancel)
    {
        MailUser? user = access.GetCurrentUser();
        MailFolder? folder = user is null ? null : await access.GetFolderAsync(user, id, MailboxAccess.Manage, cancel);
        if (folder is null)
        {
            return Results.NotFound();
        }

        string? failure = await folders.DeleteAsync(id, cancel);
        return failure is null ? Results.Ok() : Results.BadRequest(new { error = failure });
    }

    private static async Task<IResult> MarkFolderRead(long id, MailAccessService access, MailStore store, CancellationToken cancel)
    {
        MailUser? user = access.GetCurrentUser();
        MailFolder? folder = user is null ? null : await access.GetFolderAsync(user, id, MailboxAccess.Edit, cancel);
        if (folder is null)
        {
            return Results.NotFound();
        }

        int changed = await store.MarkFolderReadAsync(id, cancel);
        return Results.Ok(new ChangeResult(changed, await CountsOfAsync(store, new[] { id }, cancel)));
    }

    private static async Task<IResult> EmptyFolder(long id, MailAccessService access, MailStore store, CancellationToken cancel)
    {
        MailUser? user = access.GetCurrentUser();
        MailFolder? folder = user is null ? null : await access.GetFolderAsync(user, id, MailboxAccess.Edit, cancel);
        if (folder is null)
        {
            return Results.NotFound();
        }

        if (folder.Kind is not (FolderKind.Trash or FolderKind.Junk))
        {
            return Results.BadRequest(new { error = "Only the trash and the spam folder can be emptied." });
        }

        int removed = await store.EmptyFolderAsync(id, cancel);
        return Results.Ok(new ChangeResult(removed, await CountsOfAsync(store, new[] { id }, cancel)));
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Writing
    // ---------------------------------------------------------------------------------------------------------------

    private static async Task<IResult> UploadAttachment(HttpContext http, MailAccessService access, AttachmentStaging staging, AppConfig config, CancellationToken cancel)
    {
        MailUser? user = access.GetCurrentUser();
        if (user is null)
        {
            return Results.Unauthorized();
        }

        IFormCollection form = await http.Request.ReadFormAsync(cancel);
        IFormFile? file = form.Files.FirstOrDefault();
        if (file is null)
        {
            return Results.BadRequest(new { error = "No file." });
        }

        try
        {
            await using Stream stream = file.OpenReadStream();
            StagedAttachment staged = await staging.SaveAsync(user.UserId, file.FileName, file.ContentType, stream, (long)config.Server.MaxUploadMb * 1024 * 1024, cancel);
            return Results.Ok(staged);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static IResult DeleteAttachment(string id, MailAccessService access, AttachmentStaging staging)
    {
        MailUser? user = access.GetCurrentUser();
        if (user is null)
        {
            return Results.Unauthorized();
        }

        staging.Delete(user.UserId, id);
        return Results.Ok();
    }

    private static async Task<IResult> PrepareReply(long messageId, string? mode, MailAccessService access, ComposeService compose, CancellationToken cancel)
    {
        MailUser? user = access.GetCurrentUser();
        ComposeModel? model = user is null ? null : await compose.PrepareReplyAsync(user, messageId, mode ?? "reply", cancel);
        return model is null ? Results.NotFound() : Results.Ok(model);
    }

    private static async Task<IResult> OpenDraft(long id, MailAccessService access, ComposeService compose, CancellationToken cancel)
    {
        MailUser? user = access.GetCurrentUser();
        ComposeModel? model = user is null ? null : await compose.OpenDraftAsync(user, id, cancel);
        return model is null ? Results.NotFound() : Results.Ok(model);
    }

    private static async Task<IResult> SaveDraft(ComposeModel model, MailAccessService access, ComposeService compose, CancellationToken cancel)
    {
        MailUser? user = access.GetCurrentUser();
        if (user is null)
        {
            return Results.Unauthorized();
        }

        ComposeResult result = await compose.SaveDraftAsync(user, model, cancel);
        return result.Ok ? Results.Ok(new { ok = true, draftId = result.DraftId }) : Results.BadRequest(new { error = result.Error });
    }

    private static async Task<IResult> DiscardDraft(long id, MailAccessService access, ComposeService compose, CancellationToken cancel)
    {
        MailUser? user = access.GetCurrentUser();
        if (user is null)
        {
            return Results.Unauthorized();
        }

        await compose.DiscardDraftAsync(user, id, cancel);
        return Results.Ok();
    }

    private static async Task<IResult> Send(ComposeModel model, MailAccessService access, ComposeService compose, CancellationToken cancel)
    {
        MailUser? user = access.GetCurrentUser();
        if (user is null)
        {
            return Results.Unauthorized();
        }

        ComposeResult result = await compose.SendAsync(user, model, cancel);
        return result.Ok ? Results.Ok(new { ok = true }) : Results.BadRequest(new { error = result.Error });
    }

    private static async Task<IResult> Contacts(string? q, MailAccessService access, ContactService contacts, CancellationToken cancel)
    {
        MailUser? user = access.GetCurrentUser();
        return user is null ? Results.Unauthorized() : Results.Ok(await contacts.SuggestAsync(user, q, 8, cancel));
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Live updates (server-sent events)
    // ---------------------------------------------------------------------------------------------------------------

    private static async Task Events(HttpContext http, MailAccessService access, MailEventHub hub, CancellationToken cancel)
    {
        MailUser? user = access.GetCurrentUser();
        if (user is null)
        {
            http.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        HashSet<long> mailboxIds = (await access.GetMailboxesAsync(user, cancel)).Select(m => m.Mailbox.Id).ToHashSet();
        var channel = Channel.CreateBounded<MailEvent>(new BoundedChannelOptions(100) { FullMode = BoundedChannelFullMode.DropOldest });
        using IDisposable subscription = hub.Subscribe(e =>
        {
            if (mailboxIds.Contains(e.MailboxId))
            {
                channel.Writer.TryWrite(e);
            }
        });

        http.Response.Headers.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers["X-Accel-Buffering"] = "no";
        await http.Response.WriteAsync("retry: 5000\n\n", cancel);
        await http.Response.Body.FlushAsync(cancel);

        // The connection ends after a while; the browser reconnects by itself, which also refreshes the mailbox list of the user.
        DateTime until = DateTime.UtcNow.AddMinutes(10);
        try
        {
            while (!cancel.IsCancellationRequested && DateTime.UtcNow < until)
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                wait.CancelAfter(TimeSpan.FromSeconds(25));
                try
                {
                    MailEvent evt = await channel.Reader.ReadAsync(wait.Token);
                    string json = System.Text.Json.JsonSerializer.Serialize(
                        new { kind = evt.Kind.ToString(), mailboxId = evt.MailboxId, folderId = evt.FolderId, messageId = evt.MessageId },
                        new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
                    await http.Response.WriteAsync($"event: mail\ndata: {json}\n\n", cancel);
                }
                catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
                {
                    await http.Response.WriteAsync(": ping\n\n", cancel);
                }

                await http.Response.Body.FlushAsync(cancel);
            }
        }
        catch (OperationCanceledException)
        {
            // The browser went away.
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------------------------

    private static async Task<(MailUser? User, IResult? Error)> RequireMailboxAsync(MailAccessService access, long mailboxId, MailboxAccess minimum, CancellationToken cancel)
    {
        MailUser? user = access.GetCurrentUser();
        if (user is null)
        {
            return (null, Results.Unauthorized());
        }

        MailboxAccess? level = await access.GetAccessAsync(user, mailboxId, cancel);
        return level >= minimum ? (user, null) : (null, Results.NotFound());
    }

    private static async Task<(MailUser? User, MailMessage? Message, MailboxAccess Access, IResult? Error)> RequireMessageAsync(
        MailAccessService access, MatMailDbContext db, long id, MailboxAccess minimum, CancellationToken cancel)
    {
        MailUser? user = access.GetCurrentUser();
        if (user is null)
        {
            return (null, null, default, Results.Unauthorized());
        }

        MailMessage? message = await db.MailMessages.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, cancel);
        MailboxAccess? level = message is null ? null : await access.GetAccessAsync(user, message.MailboxId, cancel);
        return message is null || level is null || level < minimum
            ? (null, null, default, Results.NotFound())
            : (user, message, level.Value, null);
    }

    /// <summary>The messages of the request the user may change (others are silently left out).</summary>
    private static async Task<(MailUser? User, List<MailMessage> Messages, IResult? Error)> AllowedMessagesAsync(
        MailAccessService access, MatMailDbContext db, long[] ids, MailboxAccess minimum, CancellationToken cancel)
    {
        MailUser? user = access.GetCurrentUser();
        if (user is null)
        {
            return (null, new List<MailMessage>(), Results.Unauthorized());
        }

        long[] wanted = ids.Distinct().Take(1000).ToArray();
        List<MailMessage> messages = await db.MailMessages.AsNoTracking().Where(m => wanted.Contains(m.Id)).ToListAsync(cancel);
        var allowed = new List<MailMessage>();
        foreach (IGrouping<long, MailMessage> mailbox in messages.GroupBy(m => m.MailboxId))
        {
            if (await access.GetAccessAsync(user, mailbox.Key, cancel) >= minimum)
            {
                allowed.AddRange(mailbox);
            }
        }

        return allowed.Count == 0 ? (null, allowed, Results.NotFound()) : (user, allowed, null);
    }

    private static async Task<Dictionary<long, CountsDto>> CountsOfAsync(MailStore store, IEnumerable<long> folderIds, CancellationToken cancel)
    {
        Dictionary<long, (int Total, int Unread)> counts = await store.GetCountsAsync(folderIds.Distinct(), cancel);
        return counts.ToDictionary(c => c.Key, c => new CountsDto(c.Value.Unread, c.Value.Total));
    }

    private static async Task<MimeMessage?> LoadMimeAsync(MailStore store, long id, CancellationToken cancel)
    {
        byte[]? raw = await store.GetRawAsync(id, cancel);
        if (raw is null)
        {
            return null;
        }

        using var stream = new MemoryStream(raw, writable: false);
        return await MimeMessage.LoadAsync(ParserOptions.Default, stream, cancel);
    }

    private static List<AddressDto> ToDto(IEnumerable<MailboxAddress> addresses)
        => addresses.Select(a => new AddressDto(a.Name ?? string.Empty, a.Address)).ToList();

    /// <summary>Serves an attachment safely: only harmless types may open inside the browser, everything else downloads.</summary>
    private static async Task<IResult> SendEntityAsync(HttpContext http, MimeEntity entity, string fileName, bool inline, CancellationToken cancel)
    {
        using var stream = new MemoryStream();
        switch (entity)
        {
            case MimePart { Content: not null } part:
                await part.Content.DecodeToAsync(stream, cancel);
                break;
            case MessagePart messagePart:
                await messagePart.Message.WriteToAsync(stream, cancel);
                break;
        }

        string type = entity is MessagePart ? "message/rfc822" : entity.ContentType.MimeType.ToLowerInvariant();
        bool safe = inline && SafeInlineTypes.Contains(type);
        http.Response.Headers.XContentTypeOptions = "nosniff";
        http.Response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; sandbox";
        http.Response.Headers.CacheControl = "private, max-age=3600";

        var disposition = new Microsoft.Net.Http.Headers.ContentDispositionHeaderValue(safe ? "inline" : "attachment");
        disposition.SetHttpFileName(fileName);
        http.Response.Headers.ContentDisposition = disposition.ToString();
        return Results.File(stream.ToArray(), safe ? type : "application/octet-stream");
    }
}
