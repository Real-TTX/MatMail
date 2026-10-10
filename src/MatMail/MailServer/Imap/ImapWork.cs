using MatMail.Data;
using MatMail.Messaging;

namespace MatMail.MailServer.Imap;

/// <summary>
/// The services one command works with, in a DI scope of its own that acts as the signed-in user (tenant filter, audit columns).
/// A fresh scope per command keeps the database context's change tracker current and returns pooled connections right away,
/// so a long-lived IMAP session never holds stale rows or a database connection while it is idle.
/// </summary>
internal sealed class ImapWork : IAsyncDisposable
{
    private readonly AsyncServiceScope _scope;

    public ImapWork(IServiceScopeFactory scopes, MailUser? user)
    {
        _scope = scopes.CreateAsyncScope();
        if (user is not null)
        {
            Access.Apply(user);
        }
    }

    public IServiceProvider Services => _scope.ServiceProvider;

    public MatMailDbContext Db => Services.GetRequiredService<MatMailDbContext>();

    public MailStore Store => Services.GetRequiredService<MailStore>();

    public FolderService Folders => Services.GetRequiredService<FolderService>();

    public MailAccessService Access => Services.GetRequiredService<MailAccessService>();

    public MailboxQuotaService Quota => Services.GetRequiredService<MailboxQuotaService>();

    public ValueTask DisposeAsync() => _scope.DisposeAsync();
}
