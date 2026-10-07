using MatMail.Configuration;
using MatMail.Data;
using MatMail.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

namespace MatMail.Pages.Admin.Settings;

public class IndexModel(AppConfig effective, ActivityLogger log, CurrentUser currentUser, IStringLocalizer<SharedResource> l) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    public int WebPort => effective.Server.WebPort;

    public IReadOnlyList<SelectListItem> CultureItems => new[] { new SelectListItem("Deutsch", "de-DE"), new SelectListItem("English", "en-US") };
    public IReadOnlyList<SelectListItem> ThemeItems => new[]
    {
        new SelectListItem(l["System"].Value, "system"), new SelectListItem(l["Light"].Value, "light"), new SelectListItem(l["Dark"].Value, "dark"),
    };
    public IReadOnlyList<SelectListItem> AccentItems => ThemeService.Accents.Select(a => new SelectListItem(a, a)).ToList();

    public class InputModel
    {
        public string Hostname { get; set; } = "localhost";
        public bool WebHttps { get; set; }
        public bool TrustProxyHeaders { get; set; }
        public int MaxUploadMb { get; set; }

        public bool SmtpEnabled { get; set; }
        public int SmtpPort { get; set; }
        public int SmtpSubmissionPort { get; set; }
        public int SmtpImplicitTlsPort { get; set; }
        public int SmtpMaxMessageSizeMb { get; set; }
        public int SmtpMaxRecipients { get; set; }
        public bool SmtpRequireTlsForAuth { get; set; }

        public bool ImapEnabled { get; set; }
        public int ImapPort { get; set; }
        public int ImapImplicitTlsPort { get; set; }
        public bool ImapRequireTls { get; set; }

        public bool QueueEnabled { get; set; }
        public string QueueRetryMinutes { get; set; } = string.Empty;
        public int QueueMaxAgeHours { get; set; }
        public bool QueueAllowDirectDelivery { get; set; }
        public bool SyncEnabled { get; set; }
        public int SyncMaxParallel { get; set; }

        public int ActivityLogDays { get; set; }
        public int TrashDays { get; set; }
        public int JunkDays { get; set; }
        public int SentQueueDays { get; set; }

        public string TimeZone { get; set; } = "Europe/Berlin";
        public string Culture { get; set; } = "en-US";
        public string ThemeMode { get; set; } = "system";
        public string ThemeAccent { get; set; } = "blue";
    }

    public void OnGet() => Input = ToInput(AppConfigLoader.LoadFile(AppInfo.DataDir));

    public async Task<IActionResult> OnPostAsync()
    {
        Validate();
        if (!ModelState.IsValid)
        {
            return Page();
        }

        AppConfig config = AppConfigLoader.LoadFile(AppInfo.DataDir);
        Apply(config);
        try
        {
            AppConfigLoader.Save(AppInfo.DataDir, config);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ModelState.AddModelError(string.Empty, l["The configuration file could not be written: {0}", ex.Message]);
            return Page();
        }

        await log.InfoAsync(ActivityCategory.Admin, "Server settings were changed.", userId: currentUser.UserId);
        this.Notify(l["The settings were saved. Ports and server switches apply after a restart of the container."].Value);
        return RedirectToPage();
    }

    private void Validate()
    {
        if (string.IsNullOrWhiteSpace(Input.Hostname))
        {
            ModelState.AddModelError("Input.Hostname", l["The host name is required."]);
        }

        foreach ((string field, int port) in new[]
                 {
                     ("Input.SmtpPort", Input.SmtpPort), ("Input.SmtpSubmissionPort", Input.SmtpSubmissionPort), ("Input.SmtpImplicitTlsPort", Input.SmtpImplicitTlsPort),
                     ("Input.ImapPort", Input.ImapPort), ("Input.ImapImplicitTlsPort", Input.ImapImplicitTlsPort),
                 })
        {
            if (port is < 0 or > 65535)
            {
                ModelState.AddModelError(field, l["The port must be between 0 and 65535."]);
            }
        }

        if (Input.MaxUploadMb < 1 || Input.SmtpMaxMessageSizeMb < 1)
        {
            ModelState.AddModelError("Input.MaxUploadMb", l["Sizes must be at least 1 MB."]);
        }

        if (!ParseRetry(Input.QueueRetryMinutes, out _))
        {
            ModelState.AddModelError("Input.QueueRetryMinutes", l["Enter whole minutes, separated by commas."]);
        }

        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(Input.TimeZone);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            ModelState.AddModelError("Input.TimeZone", l["This time zone is unknown."]);
        }
    }

    private static bool ParseRetry(string text, out int[] minutes)
    {
        var parsed = new List<int>();
        foreach (string part in text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(part, out int value) || value < 1)
            {
                minutes = Array.Empty<int>();
                return false;
            }

            parsed.Add(value);
        }

        minutes = parsed.Count == 0 ? new[] { 5, 15, 60, 240, 720 } : parsed.ToArray();
        return true;
    }

    private InputModel ToInput(AppConfig c) => new()
    {
        Hostname = c.Server.Hostname, WebHttps = c.Server.WebHttps, TrustProxyHeaders = c.Server.TrustProxyHeaders, MaxUploadMb = c.Server.MaxUploadMb,
        SmtpEnabled = c.Smtp.Enabled, SmtpPort = c.Smtp.Port, SmtpSubmissionPort = c.Smtp.SubmissionPort, SmtpImplicitTlsPort = c.Smtp.ImplicitTlsPort,
        SmtpMaxMessageSizeMb = c.Smtp.MaxMessageSizeMb, SmtpMaxRecipients = c.Smtp.MaxRecipients, SmtpRequireTlsForAuth = c.Smtp.RequireTlsForAuth,
        ImapEnabled = c.Imap.Enabled, ImapPort = c.Imap.Port, ImapImplicitTlsPort = c.Imap.ImplicitTlsPort, ImapRequireTls = c.Imap.RequireTls,
        QueueEnabled = c.Queue.Enabled, QueueRetryMinutes = string.Join(", ", c.Queue.RetryMinutes), QueueMaxAgeHours = c.Queue.MaxAgeHours, QueueAllowDirectDelivery = c.Queue.AllowDirectDelivery,
        SyncEnabled = c.Sync.Enabled, SyncMaxParallel = c.Sync.MaxParallel,
        ActivityLogDays = c.Retention.ActivityLogDays, TrashDays = c.Retention.TrashDays, JunkDays = c.Retention.JunkDays, SentQueueDays = c.Retention.SentQueueDays,
        TimeZone = c.Display.TimeZone, Culture = c.Display.Culture, ThemeMode = c.Display.ThemeMode, ThemeAccent = c.Display.ThemeAccent,
    };

    private void Apply(AppConfig c)
    {
        c.Server.Hostname = Input.Hostname.Trim();
        c.Server.WebHttps = Input.WebHttps;
        c.Server.TrustProxyHeaders = Input.TrustProxyHeaders;
        c.Server.MaxUploadMb = Input.MaxUploadMb;
        c.Smtp.Enabled = Input.SmtpEnabled;
        c.Smtp.Port = Input.SmtpPort;
        c.Smtp.SubmissionPort = Input.SmtpSubmissionPort;
        c.Smtp.ImplicitTlsPort = Input.SmtpImplicitTlsPort;
        c.Smtp.MaxMessageSizeMb = Input.SmtpMaxMessageSizeMb;
        c.Smtp.MaxRecipients = Math.Max(1, Input.SmtpMaxRecipients);
        c.Smtp.RequireTlsForAuth = Input.SmtpRequireTlsForAuth;
        c.Imap.Enabled = Input.ImapEnabled;
        c.Imap.Port = Input.ImapPort;
        c.Imap.ImplicitTlsPort = Input.ImapImplicitTlsPort;
        c.Imap.RequireTls = Input.ImapRequireTls;
        c.Queue.Enabled = Input.QueueEnabled;
        ParseRetry(Input.QueueRetryMinutes, out int[] retry);
        c.Queue.RetryMinutes = retry;
        c.Queue.MaxAgeHours = Math.Max(1, Input.QueueMaxAgeHours);
        c.Queue.AllowDirectDelivery = Input.QueueAllowDirectDelivery;
        c.Sync.Enabled = Input.SyncEnabled;
        c.Sync.MaxParallel = Math.Clamp(Input.SyncMaxParallel, 1, 32);
        c.Retention.ActivityLogDays = Math.Max(1, Input.ActivityLogDays);
        c.Retention.TrashDays = Math.Max(0, Input.TrashDays);
        c.Retention.JunkDays = Math.Max(0, Input.JunkDays);
        c.Retention.SentQueueDays = Math.Max(1, Input.SentQueueDays);
        c.Display.TimeZone = Input.TimeZone.Trim();
        c.Display.Culture = Input.Culture;
        c.Display.ThemeMode = ThemeService.Modes.Contains(Input.ThemeMode) ? Input.ThemeMode : "system";
        c.Display.ThemeAccent = ThemeService.Accents.Contains(Input.ThemeAccent) ? Input.ThemeAccent : "blue";
    }
}
