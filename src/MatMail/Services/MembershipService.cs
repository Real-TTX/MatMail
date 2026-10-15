using MatMail.Data;
using Microsoft.EntityFrameworkCore;

namespace MatMail.Services;

/// <summary>A member of a tenant whose home is another tenant, as the administrators of this tenant may see them: nothing but who they are.</summary>
public sealed record TenantMember(long UserId, string LoginName, string DisplayName, DateTime JoinedDate);

/// <summary>An invitation for the person who is signed in.</summary>
public sealed record InvitationOffer(long Id, long TenantId, string TenantName, string InvitedBy, IReadOnlyList<string> Roles, DateTime ExpiresDate);

/// <summary>A tenant a person can work in: their home tenant or one they are a member of.</summary>
public sealed record TenantChoice(long Id, string Name, bool IsHome);

/// <summary>
/// People who work in several tenants (docs/concept-tenant-memberships.md). A person has one home tenant (<see cref="User.TenantId"/>) and
/// any number of memberships (<see cref="TenantMembership"/>), each of which exists because the person accepted an invitation of an
/// administrator of that tenant. The global tenant filter and the guard of the database context stay as they are; what crosses the
/// boundary does so here and nowhere else: the tenants and invitations of the signed-in person, accept / decline / leave (own scope,
/// a fixed set of rows, one transaction) and the projection of a member for the administrators of a tenant.
/// </summary>
public sealed class MembershipService(IServiceScopeFactory scopes, CurrentUser current, MatMailDbContext db, SessionCache cache, ActivityLogger log)
{
    public const int MaxOpenInvitations = 50;
    public const int MaxInvitationsPerHour = 10;
    public static readonly TimeSpan InvitationLifetime = TimeSpan.FromDays(14);

    private const string NotAllowed = "You are not allowed to do this.";
    private const string NoSuchInvitation = "The invitation does not exist or is no longer valid.";

    /// <summary>The identifier as it is stored (trimmed, lower case), or null when it cannot be a login name or an address.</summary>
    public static string? NormalizeIdentifier(string? raw)
    {
        string value = (raw ?? string.Empty).Trim().ToLowerInvariant();
        return value.Length == 0 || value.Length > 320 || value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) ? null : value;
    }

    // -----------------------------------------------------------------------------------------------------------------
    // The administrators of a tenant
    // -----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Asks somebody to join the tenant the administrator works in. Whether the person exists is not told: the invitation is stored by the
    /// identifier and shows up for whoever it matches (see <see cref="IdentifiersOfAsync"/>).
    /// </summary>
    public async Task<(TenantInvitation? Invitation, string? Error)> InviteAsync(string? identifier, IEnumerable<long> roleIds)
    {
        if (!Administers(out long tenantId))
        {
            return (null, NotAllowed);
        }

        string? normalized = NormalizeIdentifier(identifier);
        if (normalized is null)
        {
            return (null, "Enter the login name or the address of the person.");
        }

        if (await BelongsToAsync(normalized, tenantId))
        {
            return (null, "That person already belongs to this tenant.");
        }

        (long[]? roles, string? roleError) = await InvitableRolesAsync(roleIds);
        if (roles is null)
        {
            return (null, roleError);
        }

        DateTime now = DateTime.UtcNow;
        TenantInvitation? invitation = await db.TenantInvitations.FirstOrDefaultAsync(i => i.Identifier == normalized && i.State == InvitationState.Open);
        if (invitation is null)
        {
            if (await db.TenantInvitations.CountAsync(i => i.State == InvitationState.Open && i.ExpiresDate > now) >= MaxOpenInvitations)
            {
                return (null, "There are too many open invitations. Revoke some or wait until they expire.");
            }

            if (await db.TenantInvitations.CountAsync(i => i.CreateUserId == current.UserId && i.CreateDate > now.AddHours(-1)) >= MaxInvitationsPerHour)
            {
                return (null, "You have sent a lot of invitations just now. Try again in a while.");
            }

            invitation = new TenantInvitation { TenantId = tenantId, Identifier = normalized };
            db.TenantInvitations.Add(invitation);
        }

        invitation.RoleIds = roles;
        invitation.ExpiresDate = now + InvitationLifetime;
        await db.SaveChangesAsync();

        await log.InfoAsync(ActivityCategory.Admin, $"'{normalized}' was invited to join the tenant.", tenantId: tenantId, userId: current.UserId);
        return (invitation, null);
    }

    /// <summary>Takes an open invitation back.</summary>
    public async Task<string?> RevokeAsync(long invitationId)
    {
        if (!Administers(out long tenantId))
        {
            return NotAllowed;
        }

        TenantInvitation? invitation = await db.TenantInvitations.FirstOrDefaultAsync(i => i.Id == invitationId && i.State == InvitationState.Open);
        if (invitation is null)
        {
            return NoSuchInvitation;
        }

        invitation.State = InvitationState.Revoked;
        invitation.ClosedDate = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await log.InfoAsync(ActivityCategory.Admin, $"The invitation of '{invitation.Identifier}' was revoked.", tenantId: tenantId, userId: current.UserId);
        return null;
    }

    /// <summary>The invitations of the tenant the administrator works in, newest first.</summary>
    public async Task<List<TenantInvitation>> ListInvitationsAsync()
        => Administers(out _) ? await db.TenantInvitations.AsNoTracking().OrderByDescending(i => i.CreateDate).ToListAsync() : new List<TenantInvitation>();

    /// <summary>The people of other tenants who are members of this one. Only who they are, nothing of their home tenant.</summary>
    public async Task<List<TenantMember>> ListMembersAsync()
    {
        if (!Administers(out _))
        {
            return new List<TenantMember>();
        }

        // Two queries on purpose: IgnoreQueryFilters() applies to the whole query, so a join with the users would also switch off the
        // tenant filter of the memberships and show the members of every tenant.
        var memberships = await db.TenantMemberships.AsNoTracking().Select(m => new { m.UserId, m.CreateDate }).ToListAsync();
        long[] userIds = memberships.Select(m => m.UserId).ToArray();
        Dictionary<long, (string LoginName, string DisplayName)> people = await db.Users.IgnoreQueryFilters().AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => (u.LoginName, u.DisplayName));
        return memberships
            .Where(m => people.ContainsKey(m.UserId))
            .Select(m => new TenantMember(m.UserId, people[m.UserId].LoginName, people[m.UserId].DisplayName, m.CreateDate))
            .OrderBy(m => m.DisplayName, StringComparer.CurrentCultureIgnoreCase).ThenBy(m => m.LoginName, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The roles of a member in this tenant: the roles of this tenant, under the rule that nobody hands out what they do not hold.</summary>
    public async Task<string?> SetMemberRolesAsync(long userId, IEnumerable<long> roleIds)
    {
        if (!Administers(out long tenantId))
        {
            return NotAllowed;
        }

        if (!await db.TenantMemberships.AnyAsync(m => m.UserId == userId))
        {
            return "That person is not a member of this tenant.";
        }

        List<Role> tenantRoles = await db.Roles.AsNoTracking().ToListAsync();
        bool Manageable(Role role) => current.IsSystemAdmin || role.Permissions.All(current.Can);
        long[] wanted = roleIds.ToArray();
        HashSet<long> allowed = tenantRoles.Where(r => wanted.Contains(r.Id) && Manageable(r)).Select(r => r.Id).ToHashSet();
        HashSet<long> untouchable = tenantRoles.Where(r => !Manageable(r)).Select(r => r.Id).ToHashSet();
        HashSet<long> ofTenant = tenantRoles.Select(r => r.Id).ToHashSet();

        List<UserRole> held = await db.UserRoles.Where(ur => ur.UserId == userId && ofTenant.Contains(ur.RoleId)).ToListAsync();
        db.UserRoles.RemoveRange(held.Where(ur => !allowed.Contains(ur.RoleId) && !untouchable.Contains(ur.RoleId)));
        HashSet<long> have = held.Select(ur => ur.RoleId).ToHashSet();
        foreach (long roleId in allowed.Where(id => !have.Contains(id)))
        {
            db.UserRoles.Add(new UserRole { UserId = userId, RoleId = roleId });
        }

        await db.SaveChangesAsync();
        cache.InvalidateUser(userId);
        await log.InfoAsync(ActivityCategory.Admin, "The roles of a member of the tenant were changed.", tenantId: tenantId, userId: current.UserId);
        return null;
    }

    /// <summary>An administrator takes a member out of the tenant, with everything the membership carried; their personal mailbox here stays without an owner or is deleted.</summary>
    public async Task<string?> RemoveAsync(long userId, bool deleteMailbox)
    {
        if (!Administers(out long tenantId))
        {
            return NotAllowed;
        }

        return await EndMembershipAsync(userId, tenantId, deleteMailbox, "was removed from the tenant");
    }

    // -----------------------------------------------------------------------------------------------------------------
    // The person
    // -----------------------------------------------------------------------------------------------------------------

    /// <summary>The tenants the signed-in person can work in (home first, then by name); tenants that are switched off are left out.</summary>
    public async Task<List<TenantChoice>> ListTenantsAsync()
    {
        if (current.UserId is not long userId)
        {
            return new List<TenantChoice>();
        }

        long? home = await db.Users.IgnoreQueryFilters().AsNoTracking().Where(u => u.Id == userId).Select(u => (long?)u.TenantId).FirstOrDefaultAsync();
        if (home is null)
        {
            return new List<TenantChoice>();
        }

        long[] ids = (await db.TenantMemberships.IgnoreQueryFilters().AsNoTracking().Where(m => m.UserId == userId).Select(m => m.TenantId).ToListAsync())
            .Append(home.Value).ToArray();
        List<Tenant> tenants = await db.Tenants.AsNoTracking().Where(t => ids.Contains(t.Id) && t.IsActive).ToListAsync();
        return tenants.OrderBy(t => t.Id == home.Value ? 0 : 1).ThenBy(t => t.Name).Select(t => new TenantChoice(t.Id, t.Name, t.Id == home.Value)).ToList();
    }

    /// <summary>The open invitations that are meant for the signed-in person.</summary>
    public async Task<List<InvitationOffer>> ListOffersAsync()
    {
        User? user = await FindCurrentUserAsync(db);
        if (user is null)
        {
            return new List<InvitationOffer>();
        }

        DateTime now = DateTime.UtcNow;
        string[] identifiers = await IdentifiersOfAsync(db, user);
        long[] memberOf = await db.TenantMemberships.IgnoreQueryFilters().AsNoTracking().Where(m => m.UserId == user.Id).Select(m => m.TenantId).ToArrayAsync();
        List<TenantInvitation> invitations = await db.TenantInvitations.IgnoreQueryFilters().AsNoTracking()
            .Where(i => i.State == InvitationState.Open && i.ExpiresDate > now && identifiers.Contains(i.Identifier) && i.TenantId != user.TenantId && !memberOf.Contains(i.TenantId))
            .OrderByDescending(i => i.CreateDate)
            .ToListAsync();

        long[] tenantIds = invitations.Select(i => i.TenantId).Distinct().ToArray();
        Dictionary<long, string> tenants = await db.Tenants.AsNoTracking().Where(t => tenantIds.Contains(t.Id) && t.IsActive).ToDictionaryAsync(t => t.Id, t => t.Name);
        long[] inviterIds = invitations.Select(i => i.CreateUserId).OfType<long>().Distinct().ToArray();
        Dictionary<long, string> inviters = await db.Users.IgnoreQueryFilters().AsNoTracking().Where(u => inviterIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName);
        long[] roleIds = invitations.SelectMany(i => i.RoleIds).Distinct().ToArray();
        Dictionary<long, string> roleNames = await db.Roles.IgnoreQueryFilters().AsNoTracking().Where(r => roleIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Name);

        return invitations
            .Where(i => tenants.ContainsKey(i.TenantId))
            .Select(i => new InvitationOffer(
                i.Id, i.TenantId, tenants[i.TenantId], i.CreateUserId is long by && inviters.TryGetValue(by, out string? name) ? name : string.Empty,
                i.RoleIds.Where(roleNames.ContainsKey).Select(id => roleNames[id]).ToList(), i.ExpiresDate))
            .ToList();
    }

    /// <summary>
    /// The person accepts an invitation: in one transaction and an own, unrestricted scope (the guard of the database context refuses
    /// to write a row of a tenant the person does not work in) the membership and the roles are made and the invitation is closed.
    /// Everything that is wrong with the invitation gets the same answer, so it tells nothing about other people's invitations.
    /// </summary>
    public async Task<string?> AcceptAsync(long invitationId)
    {
        if (current.UserId is not long userId)
        {
            return NotAllowed;
        }

        using IServiceScope scope = scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<CurrentUser>().RunAs(userId, null, true, Permissions.All);
        var inner = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();

        User? user = await inner.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id == userId);
        TenantInvitation? invitation = await FindOfferAsync(inner, user, invitationId);
        if (user is null || invitation is null)
        {
            return NoSuchInvitation;
        }

        long tenantId = invitation.TenantId;
        long[] granted = invitation.RoleIds;
        long[] existing = await inner.Roles.IgnoreQueryFilters().Where(r => r.TenantId == tenantId && granted.Contains(r.Id)).Select(r => r.Id).ToArrayAsync();
        if (existing.Length == 0)
        {
            existing = await inner.Roles.IgnoreQueryFilters()
                .Where(r => r.TenantId == tenantId && r.IsBuiltIn && r.Name == TenantService.UserRoleName).Select(r => r.Id).ToArrayAsync();
        }

        await using var transaction = await inner.Database.BeginTransactionAsync();
        try
        {
            inner.TenantMemberships.Add(new TenantMembership { TenantId = invitation.TenantId, UserId = userId });
            inner.UserRoles.AddRange(existing.Select(roleId => new UserRole { UserId = userId, RoleId = roleId }));
            invitation.State = InvitationState.Accepted;
            invitation.ClosedDate = DateTime.UtcNow;
            invitation.UserId = userId;
            await inner.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        catch (DbUpdateException)
        {
            return "You already belong to this tenant.";
        }

        cache.InvalidateUser(userId);
        await log.InfoAsync(ActivityCategory.Auth, $"'{user.LoginName}' joined the tenant.", tenantId: invitation.TenantId, userId: userId);
        return null;
    }

    /// <summary>The person turns an invitation down.</summary>
    public async Task<string?> DeclineAsync(long invitationId)
    {
        if (current.UserId is not long userId)
        {
            return NotAllowed;
        }

        using IServiceScope scope = scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<CurrentUser>().RunAs(userId, null, true, Permissions.All);
        var inner = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();

        User? user = await inner.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id == userId);
        TenantInvitation? invitation = await FindOfferAsync(inner, user, invitationId);
        if (user is null || invitation is null)
        {
            return NoSuchInvitation;
        }

        invitation.State = InvitationState.Declined;
        invitation.ClosedDate = DateTime.UtcNow;
        invitation.UserId = userId;
        await inner.SaveChangesAsync();
        await log.InfoAsync(ActivityCategory.Auth, $"'{user.LoginName}' declined the invitation to join the tenant.", tenantId: invitation.TenantId, userId: userId);
        return null;
    }

    /// <summary>The person leaves a tenant they are a member of (not their home tenant). What the membership carried goes with it; a personal mailbox there stays without an owner.</summary>
    public async Task<string?> LeaveAsync(long tenantId)
        => current.UserId is long userId ? await EndMembershipAsync(userId, tenantId, deleteMailbox: false, "left the tenant") : NotAllowed;

    // -----------------------------------------------------------------------------------------------------------------
    // Inside
    // -----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Ends a membership and takes with it everything it carried: the roles of that tenant, the delegations on its mailboxes and the rows
    /// that belong to the person in that tenant, so that an invitation later starts from nothing. A personal mailbox of the person in
    /// that tenant belongs to the tenant: it stays without an owner, or is deleted when an administrator says so.
    /// </summary>
    private async Task<string?> EndMembershipAsync(long userId, long tenantId, bool deleteMailbox, string what)
    {
        using IServiceScope scope = scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<CurrentUser>().RunAs(current.UserId, null, true, Permissions.All);
        var inner = scope.ServiceProvider.GetRequiredService<MatMailDbContext>();

        string? login = await inner.Users.IgnoreQueryFilters().Where(u => u.Id == userId).Select(u => u.LoginName).FirstOrDefaultAsync();
        if (login is null || !await inner.TenantMemberships.IgnoreQueryFilters().AnyAsync(m => m.TenantId == tenantId && m.UserId == userId))
        {
            return "That person is not a member of this tenant.";
        }

        await using var transaction = await inner.Database.BeginTransactionAsync();
        long[] roleIds = await inner.Roles.IgnoreQueryFilters().Where(r => r.TenantId == tenantId).Select(r => r.Id).ToArrayAsync();
        await inner.UserRoles.Where(ur => ur.UserId == userId && roleIds.Contains(ur.RoleId)).ExecuteDeleteAsync();
        await inner.MailboxPermissions.IgnoreQueryFilters().Where(p => p.TenantId == tenantId && p.UserId == userId).ExecuteDeleteAsync();
        await inner.Signatures.IgnoreQueryFilters().Where(s => s.TenantId == tenantId && s.UserId == userId).ExecuteDeleteAsync();
        await inner.MailTemplates.IgnoreQueryFilters().Where(t => t.TenantId == tenantId && t.UserId == userId).ExecuteDeleteAsync();

        IQueryable<Mailbox> owned = inner.Mailboxes.IgnoreQueryFilters().Where(m => m.TenantId == tenantId && m.OwnerUserId == userId && m.Type == MailboxType.Personal);
        if (deleteMailbox)
        {
            await owned.ExecuteDeleteAsync();
        }
        else
        {
            await owned.ExecuteUpdateAsync(s => s.SetProperty(m => m.OwnerUserId, (long?)null));
        }

        await inner.TenantMemberships.IgnoreQueryFilters().Where(m => m.TenantId == tenantId && m.UserId == userId).ExecuteDeleteAsync();
        await transaction.CommitAsync();

        cache.InvalidateUser(userId);
        await log.InfoAsync(ActivityCategory.Auth, $"'{login}' {what}.", tenantId: tenantId, userId: current.UserId);
        return null;
    }

    /// <summary>
    /// The logins and addresses an invitation can be addressed to for this person: their login name and the addresses of the personal
    /// mailboxes they own in their home tenant. Never the contact address of the profile: the person types that themselves, so
    /// anybody could read other people's invitations by entering their address there.
    /// </summary>
    private static async Task<string[]> IdentifiersOfAsync(MatMailDbContext context, User user)
    {
        List<string> addresses = await context.MailboxAliases.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.Mailbox!.OwnerUserId == user.Id && a.Mailbox.Type == MailboxType.Personal && a.Mailbox.TenantId == user.TenantId)
            .Select(a => a.Address)
            .ToListAsync();
        return addresses.Append(user.LoginName).Select(a => a.ToLowerInvariant()).Distinct().ToArray();
    }

    /// <summary>The invitation, if it is open, not expired, meant for this person and for a tenant that is on and that they do not belong to yet.</summary>
    private static async Task<TenantInvitation?> FindOfferAsync(MatMailDbContext context, User? user, long invitationId)
    {
        if (user is null || !user.IsActive || user.DirectoryDisabledDate is not null)
        {
            return null;
        }

        TenantInvitation? invitation = await context.TenantInvitations.IgnoreQueryFilters().FirstOrDefaultAsync(i => i.Id == invitationId);
        if (invitation is null || invitation.State != InvitationState.Open || invitation.ExpiresDate <= DateTime.UtcNow || invitation.TenantId == user.TenantId)
        {
            return null;
        }

        string[] identifiers = await IdentifiersOfAsync(context, user);
        bool tenantIsOn = await context.Tenants.AnyAsync(t => t.Id == invitation.TenantId && t.IsActive);
        bool member = await context.TenantMemberships.IgnoreQueryFilters().AnyAsync(m => m.TenantId == invitation.TenantId && m.UserId == user.Id);
        return identifiers.Contains(invitation.Identifier) && tenantIsOn && !member ? invitation : null;
    }

    private Task<User?> FindCurrentUserAsync(MatMailDbContext context)
        => current.UserId is long userId
            ? context.Users.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId && u.IsActive && u.DirectoryDisabledDate == null)
            : Task.FromResult<User?>(null);

    /// <summary>Whether somebody with this login or address already belongs to the tenant (as a home user or as a member).</summary>
    private async Task<bool> BelongsToAsync(string identifier, long tenantId)
    {
        long? userId = await db.Users.IgnoreQueryFilters().AsNoTracking().Where(u => u.LoginName == identifier).Select(u => (long?)u.Id).FirstOrDefaultAsync()
            ?? await db.MailboxAliases.IgnoreQueryFilters().AsNoTracking()
                .Where(a => a.Address == identifier && a.Mailbox!.Type == MailboxType.Personal && a.Mailbox.OwnerUserId != null && a.Mailbox.TenantId == a.Mailbox.OwnerUser!.TenantId)
                .Select(a => a.Mailbox!.OwnerUserId).FirstOrDefaultAsync();
        return userId is long id
            && (await db.Users.IgnoreQueryFilters().AnyAsync(u => u.Id == id && u.TenantId == tenantId)
                || await db.TenantMemberships.IgnoreQueryFilters().AnyAsync(m => m.UserId == id && m.TenantId == tenantId));
    }

    /// <summary>The roles the invitation grants: roles of this tenant that the inviter may hand out; none chosen means the built-in User role.</summary>
    private async Task<(long[]? Roles, string? Error)> InvitableRolesAsync(IEnumerable<long> requested)
    {
        List<Role> roles = await db.Roles.AsNoTracking().ToListAsync();
        bool Manageable(Role role) => current.IsSystemAdmin || role.Permissions.All(current.Can);
        long[] wanted = requested.Distinct().ToArray();
        if (wanted.Any(id => roles.FirstOrDefault(r => r.Id == id) is not { } role || !Manageable(role)))
        {
            return (null, "You cannot hand out a role with permissions you do not hold yourself.");
        }

        if (wanted.Length == 0 && roles.FirstOrDefault(r => r.IsBuiltIn && r.Name == TenantService.UserRoleName) is { } basic && Manageable(basic))
        {
            wanted = new[] { basic.Id };
        }

        return (wanted, null);
    }

    /// <summary>Whether the signed-in person may manage the people of the tenant they work in.</summary>
    private bool Administers(out long tenantId)
    {
        tenantId = current.TenantId ?? 0;
        return tenantId > 0 && current.Can(Permissions.UsersManage);
    }
}
