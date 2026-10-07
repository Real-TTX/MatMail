using MatMail.Data;
using MatMail.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

namespace MatMail.Pages.Admin.Branding;

public class IndexModel(BrandingService branding, CurrentUser currentUser, ActivityLogger log, IStringLocalizer<SharedResource> l) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    /// <summary>The picture chosen in the form.</summary>
    [BindProperty]
    public IFormFile? Logo { get; set; }

    public Brand Current { get; private set; } = Brand.None;

    public string SignInAddress => string.IsNullOrEmpty(Input.Slug) ? string.Empty : $"{Request.Scheme}://{Request.Host}/t/{Input.Slug}";

    public class InputModel
    {
        public string? BrandName { get; set; }
        public string? Slug { get; set; }
        public string? Website { get; set; }

        /// <summary>"#rrggbb"; only used when <see cref="UseAccent"/> is on.</summary>
        public string AccentColor { get; set; } = "#1a73e8";
        public bool UseAccent { get; set; }
        public bool RemoveLogo { get; set; }
    }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostAsync()
    {
        long? tenantId = currentUser.TenantId;
        if (tenantId is null)
        {
            return NotFound();
        }

        byte[]? logo = null;
        if (Logo is { Length: > 0 })
        {
            if (Logo.Length > BrandingService.MaxLogoBytes)
            {
                ModelState.AddModelError("Logo", l["The logo is larger than 512 KB."]);
            }
            else
            {
                await using var buffer = new MemoryStream();
                await Logo.CopyToAsync(buffer, HttpContext.RequestAborted);
                logo = buffer.ToArray();
            }
        }

        if (ModelState.IsValid)
        {
            string? error = await branding.SaveAsync(tenantId.Value, new BrandingInput
            {
                BrandName = Input.BrandName,
                Slug = Input.Slug,
                Website = Input.Website,
                AccentColor = Input.UseAccent ? Input.AccentColor : null,
                NewLogo = logo,
                RemoveLogo = Input.RemoveLogo,
            }, HttpContext.RequestAborted);
            if (error is not null)
            {
                ModelState.AddModelError(string.Empty, l[error]);
            }
        }

        if (!ModelState.IsValid)
        {
            Current = await branding.GetAsync(tenantId, HttpContext.RequestAborted);
            return Page();
        }

        await log.InfoAsync(ActivityCategory.Admin, "The branding of the tenant was changed.", userId: currentUser.UserId);
        this.Notify(l["The branding was saved."].Value);
        return RedirectToPage();
    }

    private async Task LoadAsync()
    {
        Current = await branding.GetAsync(currentUser.TenantId, HttpContext.RequestAborted);
        Input = new InputModel
        {
            BrandName = Current.Name,
            Slug = Current.Slug,
            Website = Current.Website,
            AccentColor = Current.AccentColor ?? "#1a73e8",
            UseAccent = Current.AccentColor is not null,
        };
    }
}
