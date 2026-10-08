namespace MatMail.Api;

// The JSON the web mail client talks (camelCase on the wire). Enums travel as their names.

public sealed record AddressDto(string Name, string Address);

public sealed record FolderDto(long Id, string Name, string Path, string Kind, int Depth, int Unread, int Total);

public sealed record MailboxDto(
    long Id, string Name, string Type, bool IsOwn, string Access, bool CanEdit, bool CanSend, bool CanManage, List<FolderDto> Folders);

public sealed record IdentityDto(string Address, string Label, long MailboxId, bool IsPrimary, bool IsOwn);

public sealed record SignatureDto(long Id, string Name, string Html, bool IsDefault, string Scope, long? MailboxId);

public sealed record UserDto(long Id, string Name, string Login, bool IsAdmin);

public sealed record SettingsDto(int PageSize, int MaxUploadMb, string Hostname);

public sealed record BootstrapDto(UserDto User, List<MailboxDto> Mailboxes, List<IdentityDto> Identities, List<SignatureDto> Signatures, SettingsDto Settings);

public sealed record MessageListItemDto(
    long Id, long FolderId, long Uid, string Subject, string FromName, string FromAddress, string ToSummary, string Snippet, DateTime Date,
    bool IsRead, bool IsStarred, bool HasAttachments, bool IsDraft, bool IsAnswered, bool IsForwarded, string FolderKind);

public sealed record MessageListDto(int Total, int Page, int PageSize, List<MessageListItemDto> Items);

/// <summary>All messages a list request matches (the newest ones when there are very many), with the folder of each.</summary>
public sealed record MessageIdsDto(int Total, long[] Ids, long[] FolderIds, bool Capped);

public sealed record AttachmentDto(int Index, string FileName, string ContentType, long Size, string Url);

public sealed record MessageDetailDto(
    long Id, long FolderId, long MailboxId, string FolderKind, string Subject, AddressDto? From, List<AddressDto> To, List<AddressDto> Cc, List<AddressDto> Bcc,
    List<AddressDto> ReplyTo, DateTime Date, bool IsRead, bool IsStarred, bool IsDraft, bool CanEdit, bool CanSend, bool HasRemoteContent,
    string BodyUrl, List<AttachmentDto> Attachments, string? EnvelopeRecipients, string? ListUnsubscribe, long SizeBytes);

public sealed record FlagsRequest(long[] Ids, bool? IsRead, bool? IsStarred);

public sealed record MoveRequest(long[] Ids, long FolderId);

public sealed record DeleteRequest(long[] Ids, bool Permanent);

public sealed record SpamRequest(long[] Ids, bool NotSpam);

public sealed record CreateFolderRequest(long MailboxId, string Path);

public sealed record RenameFolderRequest(string Path);

public sealed record CountsDto(int Unread, int Total);

public sealed record ChangeResult(int Changed, Dictionary<long, CountsDto> Counts);
