using MatMail.Data;
using Microsoft.EntityFrameworkCore;

namespace MatMail.Services;

/// <summary>What the user form edits.</summary>
public sealed class UserInput
{
    public string LoginName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? JobTitle { get; set; }
    public string? Phone { get; set; }
    public string? Salutation { get; set; }
    public string? Title { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? Department { get; set; }
    public string? Mobile { get; set; }
    public string? Fax { get; set; }

    /// <summary>Required for new users; for existing users only set when the password is to change.</summary>
    public string? Password { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsSystemAdmin { get; set; }
    public bool MustChangePassword { get; set; }
    public long[] RoleIds { get; set; } = Array.Empty<long>();

    /// <summary>New users only: create a personal mailbox and, optionally, its primary address.</summary>
    public bool CreateMailbox { get; set; } = true;
    public string? PrimaryAddress { get; set; }
}

/// <summary>Business functions around users: create, update, password, delete. Errors come back as English source strings.</summary>
public sealed class UserService
{
    /// <summary>What a person is told who tries to set the password of somebody a directory (Active Directory, LDAP) signs in.</summary>
    public const string DirectoryPasswordMessage = "The password of this user is managed in the directory.";

    private readonly MatMailDbContext _db;
    private readonly CurrentUser _current;
    private readonly SignInService _signIn;
    private readonly MailboxService _mailboxes;
    private readonly SessionCache _cache;

    public UserService(MatMailDbContext db, CurrentUser current, SignInService signIn, MailboxService mailboxes, SessionCache cache)
    {
        _db = db;
        _current = current;
        _signIn = signIn;
        _mailboxes = mailboxes;
        _cache = cache;
    }

    public async Task<(User? User, string? Error)> CreateAsync(UserInput input, long? tenantId = null)
    {
        long tenant = tenantId ?? _current.TenantId ?? throw new InvalidOperationException("No tenant to create the user in.");
        string loginName = SignInService.NormalizeLoginName(input.LoginName);

        string? error = ValidateCommon(input, loginName) ?? SignInService.ValidatePasswordStrength(input.Password);
        if (error is not null)
        {
            return (null, error);
        }

        if (await _db.Users.IgnoreQueryFilters().AnyAsync(u => u.LoginName == loginName))
        {
            return (null, "This login name is already in use.");
        }

        if (input.IsSystemAdmin && !_current.IsSystemAdmin)
        {
            return (null, "Only system administrators can create system administrators.");
        }

        var user = new User
        {
            TenantId = tenant,
            LoginName = loginName,
            DisplayName = input.DisplayName.Trim(),
            Email = Clean(input.Email),
            JobTitle = Clean(input.JobTitle),
            Phone = Clean(input.Phone),
            Salutation = Clean(input.Salutation),
            Title = Clean(input.Title),
            FirstName = Clean(input.FirstName),
            LastName = Clean(input.LastName),
            Department = Clean(input.Department),
            Mobile = Clean(input.Mobile),
            Fax = Clean(input.Fax),
            IsActive = input.IsActive,
            IsSystemAdmin = input.IsSystemAdmin,
            MustChangePassword = input.MustChangePassword,
        };
        user.PasswordHash = _signIn.HashPassword(user, input.Password!);
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        await SetRolesAsync(user, input.RoleIds);

        if (input.CreateMailbox)
        {
            Mailbox mailbox = await _mailboxes.CreateMailboxAsync(user.DisplayName, MailboxType.Personal, user.Id, tenant);
            string? address = Clean(input.PrimaryAddress) ?? (MailAddresses.IsValid(loginName) ? loginName : null);
            if (address is not null)
            {
                string? addressError = await _mailboxes.AddAddressAsync(mailbox, address, isPrimary: true, registerDomain: _current.Can(Permissions.DomainsManage));
                if (addressError is not null)
                {
                    return (user, addressError);
                }
            }
        }

        return (user, null);
    }

    /// <param name="keepSession">The session of the person who makes the change: it stays valid when they change their own password.</param>
    public async Task<string?> UpdateAsync(long id, UserInput input, Guid? keepSession = null)
    {
        User? user = await _db.Users.FirstOrDefaultAsync(u => u.Id == id);
        if (user is null)
        {
            return "The user does not exist.";
        }

        // Whoever may manage the users of a tenant must not be able to take over the installation by editing a system administrator.
        if (user.IsSystemAdmin && !_current.IsSystemAdmin)
        {
            return "Only system administrators can change a system administrator.";
        }

        string loginName = SignInService.NormalizeLoginName(input.LoginName);
        string? error = ValidateCommon(input, loginName);
        if (error is null && !string.IsNullOrEmpty(input.Password))
        {
            // The password of somebody who signs in through a directory is the directory's: a hash set here would never be asked for.
            error = user.DirectoryId is not null ? DirectoryPasswordMessage : SignInService.ValidatePasswordStrength(input.Password);
        }

        if (error is not null)
        {
            return error;
        }

        if (user.DirectoryId is not null)
        {
            input.MustChangePassword = false;
        }

        if (loginName != user.LoginName && await _db.Users.IgnoreQueryFilters().AnyAsync(u => u.LoginName == loginName && u.Id != id))
        {
            return "This login name is already in use.";
        }

        bool isSelf = _current.UserId == id;
        if (isSelf && !input.IsActive)
        {
            return "You cannot deactivate your own account.";
        }

        if (input.IsSystemAdmin != user.IsSystemAdmin)
        {
            if (!_current.IsSystemAdmin)
            {
                return "Only system administrators can change this setting.";
            }

            if (!input.IsSystemAdmin && !await _db.Users.IgnoreQueryFilters().AnyAsync(u => u.IsSystemAdmin && u.Id != id && u.IsActive))
            {
                return "There must be at least one active system administrator.";
            }
        }

        if (user.IsSystemAdmin && user.IsActive && !input.IsActive && !await _db.Users.IgnoreQueryFilters().AnyAsync(u => u.IsSystemAdmin && u.Id != id && u.IsActive))
        {
            return "There must be at least one active system administrator.";
        }

        user.LoginName = loginName;
        user.DisplayName = input.DisplayName.Trim();
        user.Email = Clean(input.Email);
        user.JobTitle = Clean(input.JobTitle);
        user.Phone = Clean(input.Phone);
        user.Salutation = Clean(input.Salutation);
        user.Title = Clean(input.Title);
        user.FirstName = Clean(input.FirstName);
        user.LastName = Clean(input.LastName);
        user.Department = Clean(input.Department);
        user.Mobile = Clean(input.Mobile);
        user.Fax = Clean(input.Fax);
        user.IsActive = input.IsActive;
        user.IsSystemAdmin = input.IsSystemAdmin;
        user.MustChangePassword = input.MustChangePassword;
        bool passwordChanged = !string.IsNullOrEmpty(input.Password);
        if (passwordChanged)
        {
            user.PasswordHash = _signIn.HashPassword(user, input.Password!);
            user.FailedLoginCount = 0;
            user.LockedUntilDate = null;
        }

        await _db.SaveChangesAsync();
        await SetRolesAsync(user, input.RoleIds);

        // A deactivated user and a new password end the sessions that were signed in before (a stolen session must not survive a reset).
        if (!input.IsActive)
        {
            await _db.UserSessions.Where(s => s.UserId == id).ExecuteDeleteAsync();
        }
        else if (passwordChanged)
        {
            await _db.UserSessions.Where(s => s.UserId == id && (keepSession == null || s.Token != keepSession)).ExecuteDeleteAsync();
        }

        _cache.InvalidateUser(id);
        return null;
    }

    /// <summary>Sets a new password for the user and ends all of their other sessions.</summary>
    public async Task<string?> ChangePasswordAsync(long userId, string newPassword, Guid? keepSession = null)
    {
        string? error = SignInService.ValidatePasswordStrength(newPassword);
        if (error is not null)
        {
            return error;
        }

        User? user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null)
        {
            return "The user does not exist.";
        }

        if (user.DirectoryId is not null)
        {
            return DirectoryPasswordMessage;
        }

        user.PasswordHash = _signIn.HashPassword(user, newPassword);
        user.MustChangePassword = false;
        await _db.SaveChangesAsync();

        await _db.UserSessions.Where(s => s.UserId == userId && (keepSession == null || s.Token != keepSession)).ExecuteDeleteAsync();
        _cache.InvalidateUser(userId);
        return null;
    }

    /// <summary>Deletes a user. The personal mailbox is deleted with them or kept as an owner-less mailbox.</summary>
    public async Task<string?> DeleteAsync(long id, bool deleteMailbox)
    {
        if (_current.UserId == id)
        {
            return "You cannot delete your own account.";
        }

        User? user = await _db.Users.FirstOrDefaultAsync(u => u.Id == id);
        if (user is null)
        {
            return "The user does not exist.";
        }

        if (user.IsSystemAdmin && !_current.IsSystemAdmin)
        {
            return "Only system administrators can change a system administrator.";
        }

        if (user.IsSystemAdmin && !await _db.Users.IgnoreQueryFilters().AnyAsync(u => u.IsSystemAdmin && u.Id != id && u.IsActive))
        {
            return "There must be at least one active system administrator.";
        }

        if (deleteMailbox)
        {
            await _db.Mailboxes.Where(m => m.OwnerUserId == id && m.Type == MailboxType.Personal).ExecuteDeleteAsync();
        }

        _db.Users.Remove(user);
        await _db.SaveChangesAsync();
        _cache.InvalidateUser(id);
        return null;
    }

    private async Task SetRolesAsync(User user, long[] roleIds)
    {
        // Only roles of the user's own tenant can be assigned, and nobody hands out (or takes away) rights they do not hold themselves:
        // a role with a permission the acting person lacks stays exactly as it is.
        List<Role> tenantRoles = await _db.Roles.IgnoreQueryFilters().AsNoTracking().Where(r => r.TenantId == user.TenantId).ToListAsync();
        bool Manageable(Role role) => _current.IsSystemAdmin || role.Permissions.All(_current.Can);
        HashSet<long> allowed = tenantRoles.Where(r => roleIds.Contains(r.Id) && Manageable(r)).Select(r => r.Id).ToHashSet();
        HashSet<long> untouchable = tenantRoles.Where(r => !Manageable(r)).Select(r => r.Id).ToHashSet();

        List<UserRole> current = await _db.UserRoles.Where(ur => ur.UserId == user.Id).ToListAsync();
        _db.UserRoles.RemoveRange(current.Where(ur => !allowed.Contains(ur.RoleId) && !untouchable.Contains(ur.RoleId)));

        HashSet<long> have = current.Select(ur => ur.RoleId).ToHashSet();
        foreach (long roleId in allowed.Where(id => !have.Contains(id)))
        {
            _db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = roleId });
        }

        await _db.SaveChangesAsync();
    }

    private static string? ValidateCommon(UserInput input, string loginName)
    {
        if (loginName.Length == 0)
        {
            return "Login name is required.";
        }

        if (loginName.Length > 320 || loginName.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            return "The login name must not contain spaces.";
        }

        if (string.IsNullOrWhiteSpace(input.DisplayName))
        {
            return "Name is required.";
        }

        if (!string.IsNullOrWhiteSpace(input.Email) && !MailAddresses.IsValid(input.Email))
        {
            return "The e-mail address is not valid.";
        }

        return null;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
