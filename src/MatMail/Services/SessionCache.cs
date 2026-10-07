using System.Collections.Concurrent;

namespace MatMail.Services;

/// <summary>Everything the session cookie principal is built from: loaded from the database, refreshed every few seconds.</summary>
public sealed record SessionSnapshot(
    long UserId,
    long TenantId,
    string TenantName,
    long HomeTenantId,
    string LoginName,
    string DisplayName,
    bool IsSystemAdmin,
    bool MustChangePassword,
    string[] Permissions,
    string? ThemeMode,
    string? ThemeAccent,
    string? Culture,
    DateTime ExpiresDate,
    bool TwoFactorEnabled,
    bool TwoFactorRequired);

/// <summary>
/// Short-lived cache in front of the session table, so the mail client's frequent background requests do not each cost a
/// database round trip. Changing a user, role or session calls <see cref="InvalidateUser"/> / <see cref="Invalidate"/>.
/// </summary>
public sealed class SessionCache
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(15);

    private readonly ConcurrentDictionary<Guid, (SessionSnapshot Snapshot, DateTime LoadedAt)> _entries = new();

    public bool TryGet(Guid token, out SessionSnapshot snapshot)
    {
        if (_entries.TryGetValue(token, out var entry) && DateTime.UtcNow - entry.LoadedAt < Lifetime)
        {
            snapshot = entry.Snapshot;
            return true;
        }

        snapshot = null!;
        return false;
    }

    public void Set(Guid token, SessionSnapshot snapshot) => _entries[token] = (snapshot, DateTime.UtcNow);

    public void Invalidate(Guid token) => _entries.TryRemove(token, out _);

    public void InvalidateUser(long userId)
    {
        foreach (var pair in _entries.Where(e => e.Value.Snapshot.UserId == userId).ToList())
        {
            _entries.TryRemove(pair.Key, out _);
        }
    }

    /// <summary>Forget everything (roles changed: any user may be affected).</summary>
    public void Clear() => _entries.Clear();
}
