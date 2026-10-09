using MatMail.Data;
using Microsoft.Extensions.Localization;

namespace MatMail.Pages.Admin.Transfers;

/// <summary>The words of the transfer log: directions, doors and states in the language of the reader.</summary>
public sealed class TransferLabels(IStringLocalizer<SharedResource> l)
{
    public string Direction(TransferDirection direction) => direction switch
    {
        TransferDirection.Inbound => l["Incoming"].Value,
        TransferDirection.Outbound => l["Outgoing"].Value,
        _ => l["Internal"].Value,
    };

    public string DirectionIcon(TransferDirection direction) => direction switch
    {
        TransferDirection.Inbound => "arrow-down",
        TransferDirection.Outbound => "arrow-up",
        _ => "shuffle",
    };

    public string Channel(TransferChannel channel) => channel switch
    {
        TransferChannel.SmtpServer => l["Mail server (SMTP)"].Value,
        TransferChannel.SmtpSubmission => l["Mail program (SMTP)"].Value,
        TransferChannel.SmartHost => l["Smart host"].Value,
        TransferChannel.WebClient => l["Web client"].Value,
        TransferChannel.ProviderAccount => l["Connected account"].Value,
        TransferChannel.Rule => l["Mailbox rule"].Value,
        _ => l["Server"].Value,
    };

    public string Status(TransferStatus status) => status switch
    {
        TransferStatus.Delivered => l["Delivered"].Value,
        TransferStatus.Queued => l["Queued"].Value,
        TransferStatus.Deferred => l["Deferred"].Value,
        TransferStatus.Failed => l["Failed"].Value,
        TransferStatus.Rejected => l["Rejected"].Value,
        _ => l["Deleted by a rule"].Value,
    };

    public static string StatusKind(TransferStatus status) => status switch
    {
        TransferStatus.Delivered => "ok",
        TransferStatus.Queued => "info",
        TransferStatus.Deferred => "warn",
        TransferStatus.Discarded => "muted",
        _ => "danger",
    };
}
