using System.Security.Claims;
using MatMail.Data;
using MatMail.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

namespace MatMail.Pages.Account;

/// <summary>
/// The security page of the account: the authenticator app (set-up, recovery codes, turning it off) and the app passwords for mail
/// programs. Whatever is secret and shown only once (recovery codes, a new app password) is written into the response of the request
/// that created it and nowhere else: no redirect, no TempData, no cache.
/// </summary>
public class SecurityModel(
    TwoFactorService twoFactor,
    BrandingService branding,
    CurrentUser currentUser,
    IStringLocalizer<SharedResource> l) : PageModel
{
    /// <summary>The password that is asked for before a set-up starts.</summary>
    [BindProperty]
    public PasswordInput Start { get; set; } = new();

    /// <summary>The first code of the authenticator app, to finish the set-up.</summary>
    [BindProperty]
    public CodeInput Enrol { get; set; } = new();

    /// <summary>Password and code for turning two-factor authentication off.</summary>
    [BindProperty]
    public ReauthInput Disable { get; set; } = new();

    /// <summary>Password and code for new recovery codes.</summary>
    [BindProperty]
    public ReauthInput Renew { get; set; } = new();

    [BindProperty]
    public AppPasswordInput NewAppPassword { get; set; } = new();

    public class PasswordInput
    {
        public string Password { get; set; } = string.Empty;
    }

    public class CodeInput
    {
        public string Code { get; set; } = string.Empty;
    }

    public class ReauthInput
    {
        public string Password { get; set; } = string.Empty;
        public string Code { get; set; } = string.Empty;
    }

    public class AppPasswordInput
    {
        public string Name { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
    }

    public TwoFactorOverview Overview { get; private set; } = new(TwoFactorStatus.Off, null, 0, null);
    public IReadOnlyList<AppPassword> AppPasswords { get; private set; } = Array.Empty<AppPassword>();

    /// <summary>While a set-up is running: the secret to type, the address the QR code carries, and the QR code itself.</summary>
    public string? SetupSecret => Overview.PendingSecret is null ? null : string.Join(' ', Overview.PendingSecret.Chunk(4).Select(c => new string(c)));
    public string? SetupUri { get; private set; }
    public string? SetupQr { get; private set; }

    /// <summary>Shown once, right after they were created.</summary>
    public IReadOnlyList<string>? NewRecoveryCodes { get; private set; }
    public string? CreatedAppPassword { get; private set; }
    public string? CreatedAppPasswordName { get; private set; }

    public TwoFactorStatus Status => Overview.Status;

    /// <summary>App passwords make no sense before the second factor exists if the rules demand it: they would be a way around it.</summary>
    public bool CanCreateAppPasswords => !Status.SetupRequired;

    public async Task OnGetAsync()
    {
        await LoadAsync();
        if (Overview.PendingSecret is not null)
        {
            // The page shows the secret of a set-up that is running: the browser must not keep a copy.
            Response.Headers.CacheControl = "no-store";
        }
    }

    public async Task<IActionResult> OnPostStartAsync()
    {
        (string? error, _) = await twoFactor.StartEnrolmentAsync(UserId, Start.Password, HttpContext.ClientAddress());
        if (error is not null)
        {
            await LoadAsync();
            AddError("Start.Password", error);
            return Page();
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostCancelAsync()
    {
        await twoFactor.CancelEnrolmentAsync(UserId);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostConfirmAsync()
    {
        (string? error, IReadOnlyList<string> codes) = await twoFactor.ConfirmEnrolmentAsync(UserId, Enrol.Code, CurrentSession(), HttpContext.ClientAddress());
        if (error is not null)
        {
            await LoadAsync();
            Enrol.Code = string.Empty;
            AddError("Enrol.Code", error);
            return Page();
        }

        await LoadAsync();
        NewRecoveryCodes = codes;
        this.NotifyNow(l["Two-factor authentication is on. Your other sessions were signed out."].Value);
        return NoStore();
    }

    public async Task<IActionResult> OnPostRenewAsync()
    {
        (string? error, IReadOnlyList<string> codes) = await twoFactor.RegenerateRecoveryCodesAsync(UserId, Renew.Password, Renew.Code, HttpContext.ClientAddress());
        await LoadAsync();
        if (error is not null)
        {
            AddReauthError(Renew, "Renew", error);
            return Page();
        }

        NewRecoveryCodes = codes;
        this.NotifyNow(l["The new recovery codes are valid; the old ones are not."].Value);
        return NoStore();
    }

    public async Task<IActionResult> OnPostDisableAsync()
    {
        string? error = await twoFactor.DisableAsync(UserId, Disable.Password, Disable.Code, HttpContext.ClientAddress());
        if (error is not null)
        {
            await LoadAsync();
            AddReauthError(Disable, "Disable", error);
            return Page();
        }

        this.Notify(l["Two-factor authentication is off."].Value);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostCreateAppPasswordAsync()
    {
        (string? error, string? password) = await twoFactor.CreateAppPasswordAsync(UserId, NewAppPassword.Name, NewAppPassword.Password, HttpContext.ClientAddress());
        await LoadAsync();
        if (error is not null)
        {
            AddError(IsPasswordError(error) ? "NewAppPassword.Password" : "NewAppPassword.Name", error);
            return Page();
        }

        CreatedAppPassword = password;
        CreatedAppPasswordName = NewAppPassword.Name.Trim();
        NewAppPassword = new AppPasswordInput();
        ModelState.Clear();
        return NoStore();
    }

    public async Task<IActionResult> OnPostRevokeAppPasswordAsync(Guid token)
    {
        string? error = await twoFactor.RevokeAppPasswordAsync(UserId, token);
        this.Notify(error is null ? l["The app password was revoked."].Value : l[error].Value, error is null ? NoticeKind.Ok : NoticeKind.Danger);
        return RedirectToPage();
    }

    private long UserId => currentUser.UserId ?? 0;

    private static bool IsPasswordError(string error) => error == SignInService.WrongPasswordMessage || error == SignInService.LockedMessage;

    /// <summary>The error goes under the field it is about (password or code); a code that was refused is not kept in the field.</summary>
    private void AddReauthError(ReauthInput input, string form, string error)
    {
        input.Code = string.Empty;
        AddError(form + (IsPasswordError(error) ? ".Password" : ".Code"), error);
    }

    private void AddError(string field, string error)
    {
        ModelState.Remove(field);
        ModelState.AddModelError(field, l[error]);
    }

    private Guid? CurrentSession() => Guid.TryParse(User.FindFirstValue(AppClaims.SessionToken), out Guid token) ? token : null;

    /// <summary>The page shows a secret: the browser must not keep a copy.</summary>
    private IActionResult NoStore()
    {
        Response.Headers.CacheControl = "no-store";
        return Page();
    }

    private async Task LoadAsync()
    {
        Overview = await twoFactor.GetOverviewAsync(UserId);
        AppPasswords = await twoFactor.ListAppPasswordsAsync(UserId);
        if (Overview.PendingSecret is not null)
        {
            // The account name and the issuer are what the authenticator app shows next to the code.
            Brand brand = await branding.GetAsync(currentUser.HomeTenantId);
            string account = currentUser.Username ?? string.Empty;
            SetupUri = Totp.BuildUri(brand.Name ?? "MatMail", account, Overview.PendingSecret);
            SetupQr = QrCodes.ToSvg(SetupUri);
        }
    }
}
