using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Ganss.Xss;
using MatMail.Data;
using MatMail.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using MimeKit;
using MimeKit.Text;
using MimeKit.Utils;

namespace MatMail.Messaging;

/// <summary>Everything the compose window edits.</summary>
public sealed class ComposeModel
{
    /// <summary>The saved draft this window continues (it is replaced on every save and removed on send).</summary>
    public long? DraftId { get; set; }

    /// <summary>The address to send from (one of the user's send identities).</summary>
    public string From { get; set; } = string.Empty;
    public List<string> To { get; set; } = new();
    public List<string> Cc { get; set; } = new();
    public List<string> Bcc { get; set; } = new();
    public string Subject { get; set; } = string.Empty;
    public string Html { get; set; } = string.Empty;
    public string? Text { get; set; }
    public string? InReplyTo { get; set; }
    public string? References { get; set; }

    /// <summary>When sent, the original is marked answered.</summary>
    public long? ReplyToMessageId { get; set; }

    /// <summary>When sent, the original is marked forwarded.</summary>
    public long? ForwardOfMessageId { get; set; }

    /// <summary>Forwarding: take the original's attachments along.</summary>
    public bool IncludeOriginalAttachments { get; set; }
    public List<string> AttachmentIds { get; set; } = new();

    /// <summary>What the server staged for a draft or a forward (shown as chips; the window sends back <see cref="AttachmentIds"/>).</summary>
    public List<StagedAttachment> Attachments { get; set; } = new();
}

public sealed record ComposeResult(bool Ok, string? Error, long? DraftId = null);

/// <summary>
/// Writing mail: builds the message from what the compose window sends (HTML + plain text, attachments, inline pictures), prepares
/// replies and forwards, keeps drafts and hands finished mail to <see cref="MailSubmission"/>.
/// </summary>
public sealed partial class ComposeService
{
    private readonly MailAccessService _access;
    private readonly MailSubmission _submission;
    private readonly MailStore _store;
    private readonly FolderService _folders;
    private readonly AttachmentStaging _staging;
    private readonly MailBodyRenderer _renderer;
    private readonly Configuration.AppConfig _config;
    private readonly MatMailDbContext _db;
    private readonly IStringLocalizer<SharedResource> _l;

    public ComposeService(
        MailAccessService access, MailSubmission submission, MailStore store, FolderService folders, AttachmentStaging staging,
        MailBodyRenderer renderer, Configuration.AppConfig config, MatMailDbContext db, IStringLocalizer<SharedResource> l)
    {
        _access = access;
        _submission = submission;
        _store = store;
        _folders = folders;
        _staging = staging;
        _renderer = renderer;
        _config = config;
        _db = db;
        _l = l;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Sending and drafts
    // ---------------------------------------------------------------------------------------------------------------

    public async Task<ComposeResult> SendAsync(MailUser user, ComposeModel model, CancellationToken cancel = default)
    {
        SendIdentity? identity = await _access.FindSendIdentityAsync(user, model.From, cancel);
        if (identity is null)
        {
            return new ComposeResult(false, "You are not allowed to send as this address.");
        }

        List<MailboxAddress> to = ParseAddresses(model.To);
        List<MailboxAddress> cc = ParseAddresses(model.Cc);
        List<MailboxAddress> bcc = ParseAddresses(model.Bcc);
        if (to.Count + cc.Count + bcc.Count == 0)
        {
            return new ComposeResult(false, "Add at least one recipient.");
        }

        if (to.Count + cc.Count + bcc.Count > _config.Smtp.MaxRecipients)
        {
            return new ComposeResult(false, "Too many recipients.");
        }

        MimeMessage message;
        try
        {
            message = await BuildAsync(user, identity, model, cancel);
        }
        catch (InvalidOperationException ex)
        {
            return new ComposeResult(false, ex.Message);
        }

        byte[] raw = MimeSerializer.ToBytes(message);
        if (raw.LongLength > (long)_config.Server.MaxUploadMb * 1024 * 1024)
        {
            return new ComposeResult(false, "The message is too large.");
        }

        SubmissionResult result = await _submission.SubmitAsync(new SubmissionRequest
        {
            Raw = raw,
            EnvelopeFrom = identity.Alias.Address,
            Recipients = to.Concat(cc).Concat(bcc).Select(a => a.Address).ToList(),
            TenantId = user.TenantId,
            MailboxId = identity.Mailbox.Id,
            SenderUserId = user.UserId,
            SaveToSent = true,
            Source = SubmissionSource.Web,
            Peer = user.LoginName,
        }, cancel);

        if (!result.Accepted)
        {
            return new ComposeResult(false, result.Error ?? "The message could not be sent.");
        }

        await AfterSendAsync(user, model, cancel);
        return new ComposeResult(true, null);
    }

    /// <summary>Saves the message into the Drafts folder of the sending mailbox (replacing the previous version).</summary>
    public async Task<ComposeResult> SaveDraftAsync(MailUser user, ComposeModel model, CancellationToken cancel = default)
    {
        SendIdentity? identity = await _access.FindSendIdentityAsync(user, model.From, cancel);
        if (identity is null)
        {
            return new ComposeResult(false, "You are not allowed to send as this address.");
        }

        MimeMessage message = await BuildAsync(user, identity, model, cancel, forDraft: true);
        MailFolder? drafts = await _folders.FindByKindAsync(identity.Mailbox.Id, FolderKind.Drafts, cancel);
        if (drafts is null)
        {
            return new ComposeResult(false, "The mailbox has no Drafts folder.");
        }

        MailMessage saved = await _store.AddAsync(drafts.Id, new NewMessage(MimeSerializer.ToBytes(message)) { IsDraft = true, IsRead = true }, cancel);
        await RemoveDraftAsync(user, model.DraftId, saved.Id, cancel);
        return new ComposeResult(true, null, saved.Id);
    }

    public async Task DiscardDraftAsync(MailUser user, long draftId, CancellationToken cancel = default)
        => await RemoveDraftAsync(user, draftId, null, cancel);

    /// <summary>Reads a saved draft back into the compose window (its attachments are staged again).</summary>
    public async Task<ComposeModel?> OpenDraftAsync(MailUser user, long messageId, CancellationToken cancel = default)
    {
        MailMessage? draft = await _db.MailMessages.AsNoTracking().FirstOrDefaultAsync(m => m.Id == messageId && m.IsDraft, cancel);
        if (draft is null || await _access.GetAccessAsync(user, draft.MailboxId, cancel) is not MailboxAccess access || access < MailboxAccess.Edit)
        {
            return null;
        }

        byte[]? raw = await _store.GetRawAsync(messageId, cancel);
        if (raw is null)
        {
            return null;
        }

        using var stream = new MemoryStream(raw);
        MimeMessage message = await MimeMessage.LoadAsync(stream, cancel);
        var model = new ComposeModel
        {
            DraftId = messageId,
            From = message.From.Mailboxes.FirstOrDefault()?.Address ?? string.Empty,
            To = message.To.Mailboxes.Select(Format).ToList(),
            Cc = message.Cc.Mailboxes.Select(Format).ToList(),
            Bcc = message.Bcc.Mailboxes.Select(Format).ToList(),
            Subject = message.Subject ?? string.Empty,
            // A draft can come from elsewhere (a mail program, another person with access to the mailbox): into the page only as clean as a reply.
            Html = EditorHtml.Clean(message.HtmlBody ?? TextToHtmlBody(message.TextBody), "data", "cid", "mailto", "tel"),
            InReplyTo = message.InReplyTo is null ? null : "<" + message.InReplyTo.Trim('<', '>') + ">",
            References = message.References.Count == 0 ? null : string.Join(' ', message.References.Select(r => "<" + r.Trim('<', '>') + ">")),
        };

        foreach (MimeEntity entity in message.Attachments)
        {
            if (entity is not MimePart part || part.Content is null)
            {
                continue;
            }

            using var content = new MemoryStream();
            part.Content.DecodeTo(content);
            content.Position = 0;
            StagedAttachment staged = await _staging.SaveAsync(
                user.UserId, MailBodyRenderer.FileNameOf(part, model.AttachmentIds.Count + 1), part.ContentType.MimeType, content, long.MaxValue, cancel);
            model.AttachmentIds.Add(staged.Id);
            model.Attachments.Add(staged);
        }

        return model;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Reply / forward
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>The compose window content for "reply", "replyall" or "forward" of a message.</summary>
    public async Task<ComposeModel?> PrepareReplyAsync(MailUser user, long messageId, string mode, CancellationToken cancel = default)
    {
        MailMessage? original = await _db.MailMessages.AsNoTracking().FirstOrDefaultAsync(m => m.Id == messageId, cancel);
        if (original is null || await _access.GetAccessAsync(user, original.MailboxId, cancel) is null)
        {
            return null;
        }

        byte[]? raw = await _store.GetRawAsync(messageId, cancel);
        if (raw is null)
        {
            return null;
        }

        using var stream = new MemoryStream(raw);
        MimeMessage message = await MimeMessage.LoadAsync(stream, cancel);

        IReadOnlyList<SendIdentity> identities = await _access.GetSendIdentitiesAsync(user, cancel);
        HashSet<string> mine = identities.Select(i => i.Alias.Address).ToHashSet(StringComparer.OrdinalIgnoreCase);
        mine.UnionWith(await OwnAddressesAsync(user, cancel));

        var addressed = message.To.Mailboxes.Concat(message.Cc.Mailboxes).Select(m => m.Address).ToHashSet(StringComparer.OrdinalIgnoreCase);
        SendIdentity? from = identities.FirstOrDefault(i => addressed.Contains(i.Alias.Address))
                             ?? identities.FirstOrDefault(i => i.Mailbox.Id == original.MailboxId)
                             ?? identities.FirstOrDefault();

        bool forward = mode.Equals("forward", StringComparison.OrdinalIgnoreCase);
        bool replyAll = mode.Equals("replyall", StringComparison.OrdinalIgnoreCase);
        var model = new ComposeModel
        {
            From = from?.Alias.Address ?? string.Empty,
            Subject = (forward ? "Fwd: " : "Re: ") + MessageParser.StripReplyPrefixes(message.Subject),
            InReplyTo = forward ? null : message.MessageId is null ? null : "<" + message.MessageId.Trim('<', '>') + ">",
            ReplyToMessageId = forward ? null : messageId,
            ForwardOfMessageId = forward ? messageId : null,
        };

        if (!forward)
        {
            IEnumerable<MailboxAddress> replyTo = message.ReplyTo.Mailboxes.Any() ? message.ReplyTo.Mailboxes : message.From.Mailboxes;
            model.To = replyTo.Select(Format).ToList();
            if (replyAll)
            {
                model.Cc = message.To.Mailboxes.Concat(message.Cc.Mailboxes)
                    .Where(m => !mine.Contains(m.Address) && !replyTo.Any(r => string.Equals(r.Address, m.Address, StringComparison.OrdinalIgnoreCase)))
                    .Select(Format).ToList();
            }

            model.References = string.Join(' ', message.References.Select(r => "<" + r.Trim('<', '>') + ">")
                .Concat(message.MessageId is null ? Array.Empty<string>() : new[] { "<" + message.MessageId.Trim('<', '>') + ">" }));
        }

        model.Html = forward ? ForwardedHtml(message) : QuotedHtml(message);
        if (forward)
        {
            // The original's attachments are staged like uploads, so the sender can still remove some.
            model.IncludeOriginalAttachments = false;
            foreach (AttachmentInfo info in _renderer.GetAttachments(message))
            {
                if (_renderer.FindAttachment(message, info.Index) is not MimeEntity entity)
                {
                    continue;
                }

                using var content = new MemoryStream();
                if (entity is MimePart { Content: not null } part)
                {
                    await part.Content.DecodeToAsync(content, cancel);
                }
                else if (entity is MessagePart { Message: not null } messagePart)
                {
                    await messagePart.Message.WriteToAsync(content, cancel);
                }
                else
                {
                    continue;
                }

                content.Position = 0;
                StagedAttachment staged = await _staging.SaveAsync(user.UserId, info.FileName, info.ContentType, content, long.MaxValue, cancel);
                model.AttachmentIds.Add(staged.Id);
                model.Attachments.Add(staged);
            }
        }

        return model;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Building
    // ---------------------------------------------------------------------------------------------------------------

    private async Task<MimeMessage> BuildAsync(MailUser user, SendIdentity identity, ComposeModel model, CancellationToken cancel, bool forDraft = false)
    {
        var message = new MimeMessage();
        string fromName = identity.IsOwn ? user.DisplayName : identity.Mailbox.Name;
        message.From.Add(new MailboxAddress(fromName, identity.Alias.Address));

        if (!identity.IsOwn)
        {
            // Sending as a shared mailbox or on behalf of somebody: the real sender stays visible, like "on behalf of".
            IReadOnlyList<SendIdentity> all = await _access.GetSendIdentitiesAsync(user, cancel);
            SendIdentity? own = all.FirstOrDefault(i => i.IsOwn);
            if (own is not null)
            {
                message.Sender = new MailboxAddress(user.DisplayName, own.Alias.Address);
            }
        }

        message.To.AddRange(ParseAddresses(model.To));
        message.Cc.AddRange(ParseAddresses(model.Cc));
        message.Bcc.AddRange(ParseAddresses(model.Bcc));
        message.Subject = model.Subject?.Trim() ?? string.Empty;
        message.Date = DateTimeOffset.Now;
        message.MessageId = MimeUtils.GenerateMessageId(MailAddresses.DomainOf(identity.Alias.Address) is { Length: > 0 } domain ? domain : _config.Server.Hostname);
        message.Headers.Add("X-Mailer", "MatMail");
        if (!string.IsNullOrWhiteSpace(model.InReplyTo))
        {
            message.InReplyTo = model.InReplyTo.Trim('<', '>', ' ');
        }

        if (!string.IsNullOrWhiteSpace(model.References))
        {
            foreach (string reference in model.References.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                message.References.Add(reference.Trim('<', '>'));
            }
        }

        var builder = new BodyBuilder();
        string html = SanitizeOutgoing(model.Html ?? string.Empty);
        html = ExtractInlineImages(html, builder);
        builder.HtmlBody = WrapHtml(html);
        builder.TextBody = string.IsNullOrWhiteSpace(model.Text) ? HtmlText.ToPlainText(html).Trim() : model.Text;

        foreach (string id in model.AttachmentIds.Distinct())
        {
            StagedAttachment? staged = _staging.Find(user.UserId, id);
            Stream? content = staged is null ? null : _staging.OpenRead(user.UserId, id);
            if (staged is null || content is null)
            {
                throw new InvalidOperationException("An attachment is no longer available. Please add it again.");
            }

            using (content)
            {
                builder.Attachments.Add(staged.FileName, content, ContentType.Parse(staged.ContentType));
            }
        }

        if (model.IncludeOriginalAttachments && model.ForwardOfMessageId is long forwardOf && !forDraft)
        {
            await AddOriginalAttachmentsAsync(user, builder, forwardOf, cancel);
        }

        message.Body = builder.ToMessageBody();
        return message;
    }

    private async Task AddOriginalAttachmentsAsync(MailUser user, BodyBuilder builder, long messageId, CancellationToken cancel)
    {
        MailMessage? original = await _db.MailMessages.AsNoTracking().FirstOrDefaultAsync(m => m.Id == messageId, cancel);
        if (original is null || await _access.GetAccessAsync(user, original.MailboxId, cancel) is null)
        {
            return;
        }

        byte[]? raw = await _store.GetRawAsync(messageId, cancel);
        if (raw is null)
        {
            return;
        }

        using var stream = new MemoryStream(raw);
        MimeMessage source = await MimeMessage.LoadAsync(stream, cancel);
        int number = 1;
        foreach (MimeEntity entity in _renderer.GetAttachments(source).Select(a => _renderer.FindAttachment(source, a.Index)).OfType<MimeEntity>())
        {
            if (entity is MimePart { Content: not null } part)
            {
                using var copy = new MemoryStream();
                part.Content.DecodeTo(copy);
                copy.Position = 0;
                builder.Attachments.Add(MailBodyRenderer.FileNameOf(part, number++), copy.ToArray(), part.ContentType);
            }
            else if (entity is MessagePart { Message: not null } messagePart)
            {
                using var copy = new MemoryStream();
                messagePart.Message.WriteTo(copy);
                builder.Attachments.Add(MailBodyRenderer.FileNameOf(messagePart, number++), copy.ToArray(), ContentType.Parse("message/rfc822"));
            }
        }
    }

    /// <summary>After sending: remove the draft, mark the original answered/forwarded, forget the uploads.</summary>
    private async Task AfterSendAsync(MailUser user, ComposeModel model, CancellationToken cancel)
    {
        await RemoveDraftAsync(user, model.DraftId, null, cancel);
        foreach (string id in model.AttachmentIds)
        {
            _staging.Delete(user.UserId, id);
        }

        if (model.ReplyToMessageId is long answered && await CanChangeAsync(user, answered, cancel))
        {
            await _store.ChangeFlagsAsync(new[] { answered }, new FlagChange { IsAnswered = true, IsRead = true }, cancel);
        }

        if (model.ForwardOfMessageId is long forwarded && await CanChangeAsync(user, forwarded, cancel))
        {
            await _store.ChangeFlagsAsync(new[] { forwarded }, new FlagChange { IsForwarded = true }, cancel);
        }
    }

    private async Task<bool> CanChangeAsync(MailUser user, long messageId, CancellationToken cancel)
    {
        long? mailboxId = await _db.MailMessages.AsNoTracking().Where(m => m.Id == messageId).Select(m => (long?)m.MailboxId).FirstOrDefaultAsync(cancel);
        return mailboxId is long id && await _access.GetAccessAsync(user, id, cancel) >= MailboxAccess.Edit;
    }

    private async Task RemoveDraftAsync(MailUser user, long? draftId, long? except, CancellationToken cancel)
    {
        if (draftId is not long id || id == except)
        {
            return;
        }

        MailMessage? draft = await _db.MailMessages.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id && m.IsDraft, cancel);
        if (draft is not null && await _access.GetAccessAsync(user, draft.MailboxId, cancel) >= MailboxAccess.Edit)
        {
            await _store.DeleteAsync(new[] { id }, permanent: true, cancel);
        }
    }

    private async Task<IEnumerable<string>> OwnAddressesAsync(MailUser user, CancellationToken cancel)
        => await _db.MailboxAliases.AsNoTracking().Where(a => a.Mailbox!.OwnerUserId == user.UserId).Select(a => a.Address).ToListAsync(cancel);

    // ---------------------------------------------------------------------------------------------------------------
    // Quoting
    // ---------------------------------------------------------------------------------------------------------------

    private string QuotedHtml(MimeMessage original)
    {
        string header = WebUtility.HtmlEncode(string.Format(
            _l["On {0}, {1} wrote:"].Value, original.Date.LocalDateTime.ToString("f", CultureInfo.CurrentCulture), Describe(original.From.Mailboxes.FirstOrDefault())));
        return "<p><br></p><div class=\"mm-quote-header\">" + header + "</div><blockquote class=\"mm-quote\">" + OriginalBodyForQuote(original) + "</blockquote>";
    }

    private string ForwardedHtml(MimeMessage original)
    {
        string Row(string label, string? value) => string.IsNullOrWhiteSpace(value) ? string.Empty : $"<div><b>{WebUtility.HtmlEncode(label)}:</b> {WebUtility.HtmlEncode(value)}</div>";
        string header =
            "<div class=\"mm-forward-header\">" + WebUtility.HtmlEncode(_l["---------- Forwarded message ----------"].Value)
            + Row(_l["From"].Value, string.Join(", ", original.From.Mailboxes.Select(Describe)))
            + Row(_l["Date"].Value, original.Date.LocalDateTime.ToString("f", CultureInfo.CurrentCulture))
            + Row(_l["Subject"].Value, original.Subject)
            + Row(_l["To"].Value, string.Join(", ", original.To.Mailboxes.Select(Describe)))
            + "</div>";
        return "<p><br></p>" + header + "<div>" + OriginalBodyForQuote(original) + "</div>";
    }

    private static string OriginalBodyForQuote(MimeMessage original)
    {
        string html = original.HtmlBody ?? TextToHtmlBody(original.TextBody);
        // The quoted message comes from anybody and goes into the page of the editor: see EditorHtml.
        HtmlSanitizer sanitizer = EditorHtml.CreateSanitizer("mailto");
        sanitizer.AllowedTags.Remove("img");
        sanitizer.AllowedTags.Remove("style");
        return sanitizer.Sanitize(html);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------------------------

    private static string TextToHtmlBody(string? text)
    {
        var converter = new TextToHtml { Header = string.Empty, Footer = string.Empty, OutputHtmlFragment = true };
        return "<div style=\"white-space:pre-wrap\">" + converter.Convert(text ?? string.Empty) + "</div>";
    }

    private static string SanitizeOutgoing(string html)
    {
        return EditorHtml.Clean(html, "data", "cid", "mailto", "tel");
    }

    private static string WrapHtml(string body)
        => "<html><body style=\"font-family:Segoe UI,Arial,sans-serif;font-size:14px\">" + body + "</body></html>";

    /// <summary>Pictures pasted into the editor arrive as data: URLs; they become inline parts the receiving clients can show.</summary>
    private static string ExtractInlineImages(string html, BodyBuilder builder)
    {
        return DataImage().Replace(html, match =>
        {
            string type = match.Groups[1].Value;
            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(Regex.Replace(match.Groups[2].Value, @"\s+", string.Empty));
            }
            catch (FormatException)
            {
                return match.Value;
            }

            string extension = type.Split('/')[1].Split('+')[0];
            MimeEntity resource = builder.LinkedResources.Add($"image-{builder.LinkedResources.Count + 1}.{extension}", bytes, ContentType.Parse(type));
            resource.ContentId = MimeUtils.GenerateMessageId();
            return $"src=\"cid:{resource.ContentId}\"";
        });
    }

    private static List<MailboxAddress> ParseAddresses(IEnumerable<string> values)
    {
        var result = new List<MailboxAddress>();
        foreach (string value in values.Where(v => !string.IsNullOrWhiteSpace(v)))
        {
            if (InternetAddressList.TryParse(value, out InternetAddressList? list))
            {
                result.AddRange(list.Mailboxes.Where(m => MailAddresses.IsValid(m.Address)));
            }
        }

        return result;
    }

    private static string Format(MailboxAddress address)
        => string.IsNullOrWhiteSpace(address.Name) ? address.Address : $"{address.Name} <{address.Address}>";

    private static string Describe(MailboxAddress? address)
        => address is null ? string.Empty : string.IsNullOrWhiteSpace(address.Name) ? address.Address : $"{address.Name} <{address.Address}>";

    [GeneratedRegex(@"src=""data:(image/[a-zA-Z0-9.+-]+);base64,([A-Za-z0-9+/=\s]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex DataImage();
}
