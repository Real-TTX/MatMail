using System.Security.Cryptography;
using MatMail.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace MatMail.Services;

/// <summary>A person MatIdentity has signed in (its claims <c>sub</c>, <c>email</c>, <c>name</c>, <c>preferred_username</c> and <c>role</c>).</summary>
public sealed record ExternalPerson(string Subject, string? Email, string? Name, string? PreferredUserName, string? Role);

/// <summary>
/// The local user for a person of MatIdentity, or why there is none (an English text for the log), or – <see cref="LinkTo"/> – the existing
/// account that matched by its address and is linked only after its own sign-in.
/// </summary>
public sealed record ExternalSignInResult(User? User, string? Error)
{
    public bool Succeeded => User is not null;

    /// <summary>The one unlinked account whose login name, contact address or mailbox address is the person's e-mail address: not signed in, not linked.</summary>
    public User? LinkTo { get; init; }
}

/// <summary>
/// The roles of MatMail in the Mat platform (the claim <c>role</c> of MatIdentity), the most powerful first. They are about the installation:
/// a system administrator, the administrator of their own tenant, or somebody who uses mail.
/// </summary>
public static class PlatformRoles
{
    /// <summary>A system administrator (<see cref="User.IsSystemAdmin"/>): all tenants and the server settings.</summary>
    public const string Admin = "admin";

    /// <summary>The built-in role "Administrator" of the person's own tenant.</summary>
    public const string TenantAdmin = "tenant-admin";

    /// <summary>The built-in role "User" of the person's own tenant: mail, no administration.</summary>
    public const string User = "user";

    public static readonly IReadOnlyList<string> All = [Admin, TenantAdmin, User];
}

/// <summary>
/// The web sign-in through MatIdentity (the Mat platform, on when <c>MATIDENTITY_AUTHORITY</c> is set): which local user a person of MatIdentity
/// is. Found by <see cref="User.ExternalSubject"/>; else, when one user's login name, contact address or personal mailbox address is the
/// person's e-mail address, that account is <b>not</b> linked by the address (anybody may have typed it into their own profile): the answer is
/// <see cref="ExternalSignInResult.LinkTo"/>, and the account is linked by <see cref="LinkAsync"/> only after its own sign-in here (password, and
/// the code when it has a second factor; MatPlatform's <c>MatIdentityPendingLink</c>); else the person is made new in the first tenant, without a
/// password anybody knows and without a mailbox (an administrator gives one). The claim <c>role</c> (<see cref="PlatformRoles"/>) is followed at
/// every sign-in and at the link when it is there.
/// <para>
/// Only the web sign-in is touched: the password hash, the app passwords and the directory of a user stay as they are, so IMAP and SMTP sign in
/// exactly as before. Whether the user may sign in at all is <see cref="SignInService.CanSignInAsync"/>, as for every sign-in.
/// </para>
/// </summary>
public sealed class ExternalSignInService(MatMailDbContext db, SignInService signIn, PasswordHasher<User> hasher, SessionCache cache, ActivityLogger log)
{
    public const int MaxSubjectLength = 200;

    public async Task<ExternalSignInResult> SignInAsync(ExternalPerson person, string? remoteIp = null, CancellationToken cancel = default)
    {
        string subject = (person.Subject ?? string.Empty).Trim();
        if (subject.Length == 0 || subject.Length > MaxSubjectLength)
        {
            return new ExternalSignInResult(null, "MatIdentity sent no usable subject.");
        }

        string? email = MailAddresses.IsValid(person.Email) && !MailAddresses.IsCatchAll(MailAddresses.Normalize(person.Email))
            ? MailAddresses.Normalize(person.Email)
            : null;

        User? user = await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.ExternalSubject == subject, cancel);
        if (user is null)
        {
            ExternalSignInResult matched = await FindByAddressAsync(email, cancel);
            if (matched.Error is not null)
            {
                await log.WarnAsync(ActivityCategory.Auth, $"Sign-in through MatIdentity refused: {matched.Error}", remoteIp: remoteIp);
                return matched;
            }

            if (matched.User is { } match)
            {
                if (!await signIn.CanSignInAsync(match))
                {
                    await log.WarnAsync(ActivityCategory.Auth, $"Sign-in through MatIdentity refused: '{match.LoginName}' is disabled.", tenantId: match.TenantId, userId: match.Id, remoteIp: remoteIp);
                    return new ExternalSignInResult(null, "The user is disabled.");
                }

                // The address proves nothing: the account is linked once somebody has signed in to it here (LinkAsync).
                await log.InfoAsync(ActivityCategory.Auth, $"A person of MatIdentity has the address {email} of '{match.LoginName}': it is linked after its own sign-in.", tenantId: match.TenantId, userId: match.Id, remoteIp: remoteIp);
                return new ExternalSignInResult(null, null) { LinkTo = match };
            }

            user = await CreateAsync(subject, email, person, remoteIp, cancel);
            if (user is null)
            {
                const string noTenant = "there is no tenant to create the user in.";
                await log.WarnAsync(ActivityCategory.Auth, $"Sign-in through MatIdentity refused: {noTenant}", remoteIp: remoteIp);
                return new ExternalSignInResult(null, noTenant);
            }
        }

        if (!await signIn.CanSignInAsync(user))
        {
            await log.WarnAsync(ActivityCategory.Auth, $"Sign-in through MatIdentity refused: '{user.LoginName}' is disabled.", tenantId: user.TenantId, userId: user.Id, remoteIp: remoteIp);
            return new ExternalSignInResult(null, "The user is disabled.");
        }

        await ApplyRoleAsync(user, person.Role, cancel);

        await db.SaveChangesAsync(cancel);
        cache.InvalidateUser(user.Id);
        return new ExternalSignInResult(user, null);
    }

    /// <summary>
    /// Links the account <paramref name="userId"/> with the person of MatIdentity after somebody has signed in to it here (password, and the code
    /// when it has a second factor) while the person's link was pending. Refused when the account is gone or linked already, or the subject belongs
    /// to another account. The claim <c>role</c> is followed like at a sign-in through MatIdentity.
    /// </summary>
    public async Task<ExternalSignInResult> LinkAsync(long userId, ExternalPerson person, string? remoteIp = null, CancellationToken cancel = default)
    {
        string subject = (person.Subject ?? string.Empty).Trim();
        if (subject.Length == 0 || subject.Length > MaxSubjectLength)
        {
            return new ExternalSignInResult(null, "MatIdentity sent no usable subject.");
        }

        User? user = await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id == userId, cancel);
        if (user is null)
        {
            return new ExternalSignInResult(null, "The user does not exist.");
        }

        if (user.ExternalSubject is not null || await db.Users.IgnoreQueryFilters().AnyAsync(u => u.ExternalSubject == subject, cancel))
        {
            await log.WarnAsync(ActivityCategory.Auth, $"'{user.LoginName}' was not linked with MatIdentity: it or the person is linked already.", tenantId: user.TenantId, userId: user.Id, remoteIp: remoteIp);
            return new ExternalSignInResult(null, "The user or the person is linked already.");
        }

        user.ExternalSubject = subject;
        await ApplyRoleAsync(user, person.Role, cancel);
        await db.SaveChangesAsync(cancel);
        cache.InvalidateUser(user.Id);
        await log.InfoAsync(ActivityCategory.Auth, $"'{user.LoginName}' was linked with MatIdentity after its own sign-in.", tenantId: user.TenantId, userId: user.Id, remoteIp: remoteIp);
        return new ExternalSignInResult(user, null);
    }

    /// <summary>
    /// The first sign-in of a person: the one user whose login name, contact address or personal mailbox address is their e-mail address (in
    /// <see cref="ExternalSignInResult.User"/>; nothing is linked here). Nobody: no user (a new one is made). Several, or one that belongs to
    /// another person of MatIdentity: refused, an administrator decides.
    /// </summary>
    private async Task<ExternalSignInResult> FindByAddressAsync(string? email, CancellationToken cancel)
    {
        if (email is null)
        {
            return new ExternalSignInResult(null, null);
        }

        List<long> ids = await db.Users.IgnoreQueryFilters()
            .Where(u => u.LoginName == email || (u.Email != null && u.Email.ToLower() == email))
            .Select(u => u.Id)
            .ToListAsync(cancel);
        ids.AddRange(await db.MailboxAliases.IgnoreQueryFilters()
            .Where(a => a.Address == email && a.Mailbox!.Type == MailboxType.Personal && a.Mailbox.OwnerUserId != null)
            .Select(a => a.Mailbox!.OwnerUserId!.Value)
            .ToListAsync(cancel));
        ids = ids.Distinct().ToList();

        if (ids.Count == 0)
        {
            return new ExternalSignInResult(null, null);
        }

        if (ids.Count > 1)
        {
            return new ExternalSignInResult(null, $"the address '{email}' belongs to more than one user.");
        }

        User user = await db.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == ids[0], cancel);
        if (user.ExternalSubject is not null)
        {
            return new ExternalSignInResult(null, $"the user '{user.LoginName}' belongs to another person of MatIdentity.");
        }

        return new ExternalSignInResult(user, null);
    }

    /// <summary>A new user in the first tenant with the built-in role "User" (the claim may change it right after); null without a tenant.</summary>
    private async Task<User?> CreateAsync(string subject, string? email, ExternalPerson person, string? remoteIp, CancellationToken cancel)
    {
        Tenant? tenant = await db.Tenants.Where(t => t.IsActive).OrderBy(t => t.Id).FirstOrDefaultAsync(cancel);
        if (tenant is null)
        {
            return null;
        }

        string loginName = await FreeLoginNameAsync(email, person.PreferredUserName, subject, cancel);
        string displayName = FirstText(person.Name, person.PreferredUserName, email, loginName)!;
        var user = new User
        {
            TenantId = tenant.Id,
            LoginName = loginName,
            DisplayName = displayName.Length <= 200 ? displayName : displayName[..200],
            Email = email,
            ExternalSubject = subject,
        };

        // Nobody knows this password: the person signs in through MatIdentity. An administrator can give one (IMAP, SMTP, the local form).
        user.PasswordHash = hasher.HashPassword(user, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        db.Users.Add(user);
        await db.SaveChangesAsync(cancel);

        long? userRole = await BuiltInRoleIdAsync(tenant.Id, TenantService.UserRoleName, cancel);
        if (userRole is long roleId)
        {
            db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = roleId });
            await db.SaveChangesAsync(cancel);
        }

        await log.InfoAsync(ActivityCategory.Auth, $"'{loginName}' was created at the first sign-in through MatIdentity.", tenantId: tenant.Id, userId: user.Id, remoteIp: remoteIp);
        return user;
    }

    /// <summary>
    /// Follows the claim <c>role</c>: <see cref="PlatformRoles.Admin"/> makes a system administrator, the two others take that away and set the
    /// built-in role of the user's own tenant (the other roles of the user stay). No claim, or one MatMail does not know: nothing changes. The
    /// last active system administrator is never demoted by a claim.
    /// </summary>
    private async Task ApplyRoleAsync(User user, string? role, CancellationToken cancel)
    {
        switch (role)
        {
            case PlatformRoles.Admin:
                user.IsSystemAdmin = true;
                return;
            case PlatformRoles.TenantAdmin:
            case PlatformRoles.User:
                break;
            default:
                return;
        }

        if (user.IsSystemAdmin && await db.Users.IgnoreQueryFilters().AnyAsync(u => u.IsSystemAdmin && u.IsActive && u.Id != user.Id, cancel))
        {
            user.IsSystemAdmin = false;
        }

        long? administrator = await BuiltInRoleIdAsync(user.TenantId, TenantService.AdministratorRoleName, cancel);
        long? plain = await BuiltInRoleIdAsync(user.TenantId, TenantService.UserRoleName, cancel);
        List<UserRole> held = await db.UserRoles.Where(r => r.UserId == user.Id).ToListAsync(cancel);

        void Hold(long? roleId)
        {
            if (roleId is long id && held.All(r => r.RoleId != id))
            {
                db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = id });
            }
        }

        Hold(plain);
        if (role == PlatformRoles.TenantAdmin)
        {
            Hold(administrator);
        }
        else
        {
            db.UserRoles.RemoveRange(held.Where(r => r.RoleId == administrator));
        }
    }

    private Task<long?> BuiltInRoleIdAsync(long tenantId, string name, CancellationToken cancel)
        => db.Roles.IgnoreQueryFilters()
            .Where(r => r.TenantId == tenantId && r.IsBuiltIn && r.Name == name)
            .Select(r => (long?)r.Id)
            .FirstOrDefaultAsync(cancel);

    /// <summary>The e-mail address, else the preferred user name, else a name made from the subject; with a number when it is taken.</summary>
    private async Task<string> FreeLoginNameAsync(string? email, string? preferredUserName, string subject, CancellationToken cancel)
    {
        string preferred = SignInService.NormalizeLoginName(preferredUserName);
        string fromSubject = "matidentity-" + new string(subject.ToLowerInvariant().Where(char.IsAsciiLetterOrDigit).Take(24).ToArray());
        string baseName = FirstText(email, preferred.Length is > 0 and <= 300 ? preferred : null, fromSubject)!;

        for (int n = 1; ; n++)
        {
            string candidate = n == 1 ? baseName : $"{baseName}-{n}";
            if (!await db.Users.IgnoreQueryFilters().AnyAsync(u => u.LoginName == candidate, cancel))
            {
                return candidate;
            }
        }
    }

    private static string? FirstText(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();
}
