using System.Collections.Concurrent;
using MatMail.Data;
using Microsoft.EntityFrameworkCore;

namespace MatMail.Services;

/// <summary>
/// The back-channel logout of MatIdentity (MatPlatform 1.4.0, <c>POST /signout-backchannel</c>): MatIdentity says that a person signed out there,
/// or that an administrator ended their sessions, and MatMail ends its own sessions of that person. A session is a row of <c>UserSession</c>, so
/// ending it is deleting the row and dropping it from <see cref="SessionCache"/>: the next request of its cookie finds no session and is signed out
/// (<see cref="SessionCookieEvents"/>). What is still on its way to a session – the ticket of the code page, a pending link – is held by
/// <see cref="EndedIdentitySessions"/>.
/// </summary>
public sealed class IdentitySessionService(MatMailDbContext db, SessionCache cache, EndedIdentitySessions ended, ActivityLogger log)
{
    public const int MaxSessionIdLength = 200;

    /// <summary>
    /// <paramref name="sessionId"/> set: the sessions that started with that session of MatIdentity (and, when <paramref name="subject"/> is set too,
    /// belong to the user linked to it). Only <paramref name="subject"/>: every session of the user linked to it, whatever way it was signed in.
    /// A subject or session that MatMail does not know ends nothing and is no error. Returns how many sessions ended.
    /// </summary>
    public async Task<int> EndSessionsAsync(string? subject, string? sessionId, CancellationToken cancel = default)
    {
        subject = string.IsNullOrWhiteSpace(subject) ? null : subject.Trim();
        sessionId = string.IsNullOrWhiteSpace(sessionId) || sessionId.Length > MaxSessionIdLength ? null : sessionId;
        if (subject is null && sessionId is null)
        {
            return 0;
        }

        long? userId = null;
        if (subject is not null)
        {
            userId = await db.Users.IgnoreQueryFilters().Where(u => u.ExternalSubject == subject).Select(u => (long?)u.Id).FirstOrDefaultAsync(cancel);
            if (userId is null)
            {
                return 0;   // nobody here is linked to this person
            }
        }

        IQueryable<UserSession> sessions = db.UserSessions;
        if (sessionId is not null)
        {
            ended.EndSession(sessionId);
            sessions = sessions.Where(s => s.IdentitySessionId == sessionId);
            if (userId is long owner)
            {
                sessions = sessions.Where(s => s.UserId == owner);
            }
        }
        else
        {
            ended.EndUser(userId!.Value);
            sessions = sessions.Where(s => s.UserId == userId);
        }

        var gone = await sessions.Select(s => new { s.Token, s.UserId }).ToListAsync(cancel);
        if (gone.Count > 0)
        {
            List<Guid> tokens = gone.Select(s => s.Token).ToList();
            await db.UserSessions.Where(s => tokens.Contains(s.Token)).ExecuteDeleteAsync(cancel);
            foreach (Guid token in tokens)
            {
                cache.Invalidate(token);
            }
        }

        foreach (long user in gone.Select(s => s.UserId).Append(userId ?? 0).Where(id => id != 0).Distinct())
        {
            var who = await db.Users.IgnoreQueryFilters().Where(u => u.Id == user).Select(u => new { u.LoginName, u.TenantId }).FirstAsync(cancel);
            int count = gone.Count(s => s.UserId == user);
            string what = sessionId is null ? "signed out everywhere" : "signed out";
            await log.InfoAsync(ActivityCategory.Auth, $"MatIdentity: '{who.LoginName}' {what}; {count} session(s) here ended.", tenantId: who.TenantId, userId: user);
        }

        return gone.Count;
    }

    /// <summary>
    /// Whether a sign-in that is still on its way (the ticket of the code page, a pending link) came from a session of MatIdentity that has ended
    /// since: its <paramref name="sessionId"/> was ended, or every session of <paramref name="userId"/> was ended after <paramref name="sinceUtc"/>.
    /// </summary>
    public bool HasEnded(string? sessionId, long userId, DateTime? sinceUtc) => ended.HasEnded(sessionId, userId, sinceUtc);
}

/// <summary>
/// The sessions of MatIdentity that ended in the last minutes, in memory (a singleton): what has no <c>UserSession</c> row yet – the ticket of the
/// code page (five minutes), a pending link (ten) – is checked against it before its session is made. Older entries are dropped; a restart forgets
/// them, which only matters for a sign-in that was half done at that moment.
/// </summary>
public sealed class EndedIdentitySessions(TimeProvider time)
{
    public static readonly TimeSpan Remembered = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<string, DateTime> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<long, DateTime> _users = new();

    public void EndSession(string sessionId)
    {
        Prune();
        _sessions[sessionId] = Now;
    }

    public void EndUser(long userId)
    {
        Prune();
        _users[userId] = Now;
    }

    public bool HasEnded(string? sessionId, long userId, DateTime? sinceUtc)
    {
        if (sessionId is not null && _sessions.TryGetValue(sessionId, out DateTime at) && Now - at < Remembered)
        {
            return true;
        }

        return _users.TryGetValue(userId, out DateTime all) && Now - all < Remembered && (sinceUtc is null || all >= sinceUtc);
    }

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    private void Prune()
    {
        DateTime limit = Now - Remembered;
        foreach (var entry in _sessions.Where(e => e.Value < limit))
        {
            _sessions.TryRemove(entry.Key, out _);
        }

        foreach (var entry in _users.Where(e => e.Value < limit))
        {
            _users.TryRemove(entry.Key, out _);
        }
    }
}
