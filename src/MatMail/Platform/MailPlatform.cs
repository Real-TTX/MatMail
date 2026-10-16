using System.Text.Json;
using MatMail.Configuration;
using MatMail.Controls;
using MatMail.Data;
using MatMail.MailSync;
using MatMail.Messaging;
using MatMail.Services;
using MatPlatform;

namespace MatMail.Platform;

/// <summary>
/// MatMail as an app of the Mat platform (MatDesign docs/PLATFORM.md), on the shared package MatPlatform (the folder <c>MatPlatform/</c>, a copy):
/// how Mattiverse shows it (<see cref="Describe"/>: tiles, a few settings, buttons), its values and what the buttons do. The platform looks at the
/// <b>installation</b>, never at one tenant: the numbers are those of all tenants, the settings are a few of the server settings
/// (<c>/data/config/app.json</c>, the same file and values as /Admin/Settings; they apply after a restart). Texts are German or English, as the
/// caller asks with <c>Accept-Language</c>. No database here: the values come from the services.
/// </summary>
public sealed class MailPlatform(
    InstanceOverviewService overview,
    OutboundQueue queue,
    MailSyncTrigger sync,
    CurrentUser currentUser,
    ActivityLogger log,
    AppConfig effective) : IMatPlatformState, IMatPlatformConfiguration, IMatPlatformActions, IMatPlatformHealth
{
    /// <summary>The id of MatMail in the platform (MatDesign's AppRegistry) and the default client id in MatIdentity.</summary>
    public const string AppId = "matmail";

    /// <summary>Graphite, the accent of MatMail (MatDesign's AppRegistry, <c>Ui.App.Accent</c>).</summary>
    public const string Accent = Ui.App.Accent;

    public const int MaxSizeMb = 2048;
    public const int MaxRetentionDays = 3650;

    /// <summary>The languages of the user interface (<c>Display.Culture</c>).</summary>
    public static readonly IReadOnlyList<string> Cultures = ["en-US", "de-DE"];

    /// <summary>The MatApiDefinition of MatMail (<c>GET /api</c>); <c>auth</c> and <c>endpoints</c> are filled in by MatPlatform.</summary>
    public static void Describe(MatPlatformBuilder o)
    {
        var T = o.T;

        o.App(
            AppId,
            AppInfo.Name,
            T("Mail-Gateway mit Webmail für mehrere Mandanten", "Mail gateway with web mail for several tenants"),
            AppInfo.Version,
            "/icons/logo.svg",
            Accent);

        o.Page("overview", T("Übersicht", "Overview"), "dashboard",
            MatSection.Tiles("tenants", "users", "mailboxes", "domains", "messages", "storage", "queue", "queueFailed", "unassigned", "accounts",
                "accountErrors", "lastReceived", "lastSent", "lastBackup"),
            MatSection.Actions("openAdmin", "openSettings", "openQueue", "fetchMail", "retryQueue"));
        o.Page("settings", T("Einstellungen", "Settings"), "settings",
            MatSection.Form(T("Server", "Server"), "hostname", "culture", "timeZone"),
            MatSection.Form(T("Nachrichten", "Messages"), "maxMessageSizeMb", "maxUploadMb", "allowDirectDelivery"),
            MatSection.Form(T("Aufbewahrung", "Retention"), "trashDays", "junkDays", "transferLogDays"));

        o.Tile("tenants", T("Mandanten", "Tenants"), icon: "layers");
        o.Tile("users", T("Benutzer", "Users"), icon: "users");
        o.Tile("mailboxes", T("Postfächer", "Mailboxes"), icon: "mail");
        o.Tile("domains", T("Domänen", "Domains"), icon: "globe");
        o.Tile("messages", T("Nachrichten", "Messages"), icon: "archive");
        o.Tile("storage", T("Belegter Speicher", "Storage used"), icon: "hard-drive", unit: "bytes");
        o.Tile(new MatStateItem("queue", T("Ausgang: wartend", "Outgoing: waiting")) { Icon = "upload", WarnAbove = 20, ErrorAbove = 200 });
        o.Tile(new MatStateItem("queueFailed", T("Ausgang: aufgegeben", "Outgoing: given up")) { Icon = "x-circle", WarnAbove = 0 });
        o.Tile(new MatStateItem("unassigned", T("Nicht zugeordnet", "Unassigned")) { Icon = "help-circle", WarnAbove = 0 });
        o.Tile("accounts", T("Verbundene Konten", "Connected accounts"), icon: "plug");
        o.Tile(new MatStateItem("accountErrors", T("Konten mit Fehler", "Accounts with errors")) { Icon = "alert", WarnAbove = 0 });
        o.Tile("lastReceived", T("Zuletzt empfangen", "Last received"), "time", icon: "download");
        o.Tile("lastSent", T("Zuletzt gesendet", "Last sent"), "time", icon: "upload");
        o.Tile("lastBackup", T("Letzte Sicherung", "Last backup"), "time", icon: "database");

        o.Field(new MatField("hostname", T("Hostname", "Host name"), "text")
        {
            Required = true,
            Max = 253,
            Placeholder = "mail.example.com",
            Hint = T("Öffentlicher Name des Servers: SMTP- und IMAP-Begrüßung, selbst signiertes Zertifikat.", "Public name of the server: SMTP and IMAP banners, the self-signed certificate."),
            Restart = true,
        });
        o.Field(new MatField("culture", T("Sprache", "Language"), "select")
        {
            Required = true,
            Options = [new MatOption("en-US", "English"), new MatOption("de-DE", "Deutsch")],
            Hint = T("Für alle, die keine eigene gewählt haben und deren Browser nichts anderes sagt.", "For everybody without a choice of their own whose browser says nothing else."),
            Restart = true,
        });
        o.Field(new MatField("timeZone", T("Zeitzone", "Time zone"), "text")
        {
            Required = true,
            Max = 100,
            Placeholder = "Europe/Berlin",
            Hint = T("IANA-Name, z. B. Europe/Berlin.", "IANA name, e.g. Europe/Berlin."),
            Restart = true,
        });
        o.Field(new MatField("maxMessageSizeMb", T("Größte Nachricht (SMTP, MB)", "Largest message (SMTP, MB)"), "number")
        {
            Required = true,
            Min = 1,
            Max = MaxSizeMb,
            Restart = true,
        });
        o.Field(new MatField("maxUploadMb", T("Größter Upload im Web-Client (MB)", "Largest upload in the web client (MB)"), "number")
        {
            Required = true,
            Min = 1,
            Max = MaxSizeMb,
            Restart = true,
        });
        o.Field(new MatField("allowDirectDelivery", T("Direkt zustellen (MX), wenn kein Konto passt", "Deliver directly (MX) when no account applies"), "bool")
        {
            Hint = T("Aus: ohne passendes Anbieterkonto bleibt die Nachricht im Ausgang.", "Off: without a matching provider account the message stays in the queue."),
            Restart = true,
        });
        o.Field(new MatField("trashDays", T("Papierkorb leeren nach (Tagen)", "Empty the trash after (days)"), "number")
        {
            Required = true,
            Min = 0,
            Max = MaxRetentionDays,
            Hint = T("0 = nie.", "0 = never."),
            Restart = true,
        });
        o.Field(new MatField("junkDays", T("Spam leeren nach (Tagen)", "Empty junk after (days)"), "number")
        {
            Required = true,
            Min = 0,
            Max = MaxRetentionDays,
            Hint = T("0 = nie.", "0 = never."),
            Restart = true,
        });
        o.Field(new MatField("transferLogDays", T("Transferprotokoll aufbewahren (Tage)", "Keep the transfer log (days)"), "number")
        {
            Required = true,
            Min = 0,
            Max = MaxRetentionDays,
            Hint = T("0 = kein Transferprotokoll.", "0 = no transfer log."),
            Restart = true,
        });

        o.LinkAction("openAdmin", T("Administration öffnen", "Open admin"), "external", "/Admin");
        o.LinkAction("openSettings", T("Alle Servereinstellungen", "All server settings"), "settings", "/Admin/Settings");
        o.LinkAction("openQueue", T("Ausgang öffnen", "Open the queue"), "list", "/Admin/Queue");
        o.PostAction("fetchMail", T("Jetzt abrufen", "Fetch mail now"), "refresh");
        o.PostAction("retryQueue", T("Ausgang jetzt senden", "Send the queue now"), "upload",
            confirm: T("Alle wartenden und aufgegebenen Nachrichten jetzt erneut senden?", "Try every waiting and given-up message again now?"));

        o.Role(PlatformRoles.Admin, T("Systemadministrator", "System administrator"));
        o.Role(PlatformRoles.TenantAdmin, T("Mandanten-Administrator", "Tenant administrator"));
        o.Role(PlatformRoles.User, T("Benutzer", "User"));
    }

    public async Task<IReadOnlyList<MatHealthCheck>> CheckHealthAsync(MatPlatformCall call, CancellationToken ct)
    {
        currentUser.RunAsSystem();
        InstanceHealth health = await overview.CheckHealthAsync(ct);
        if (!health.Database)
        {
            return [new MatHealthCheck("database", "down", call.T("Die Datenbank (PostgreSQL) antwortet nicht.", "The database (PostgreSQL) does not answer."))];
        }

        var checks = new List<MatHealthCheck> { new("database", "ok") };
        checks.Add(health.HasUsers
            ? new MatHealthCheck("setup", "ok")
            : new MatHealthCheck("setup", "degraded", call.T("Noch nicht eingerichtet: die Einrichtungsseite wartet auf den ersten Administrator.", "Not set up yet: the set-up page waits for the first administrator.")));
        checks.Add(health.AccountErrors == 0
            ? new MatHealthCheck("accounts", "ok")
            : new MatHealthCheck("accounts", "degraded", call.T($"{health.AccountErrors} verbundene(s) Konto/Konten mit Fehler beim Abruf.", $"{health.AccountErrors} connected account(s) failed to synchronise.")));
        checks.Add(health.BackupFailed
            ? new MatHealthCheck("backup", "degraded", call.T("Die letzte geplante Sicherung ist fehlgeschlagen.", "The last scheduled backup failed."))
            : new MatHealthCheck("backup", "ok"));
        return checks;
    }

    public async Task<IReadOnlyDictionary<string, MatStateValue>> GetStateAsync(MatPlatformCall call, CancellationToken ct)
    {
        currentUser.RunAsSystem();
        InstanceSnapshot s = await overview.GetSnapshotAsync(ct);
        return new Dictionary<string, MatStateValue>
        {
            ["tenants"] = new(s.Tenants),
            ["users"] = new(s.Users),
            ["mailboxes"] = new(s.Mailboxes),
            ["domains"] = new(s.Domains),
            ["messages"] = new(s.Messages),
            ["storage"] = new(s.StorageBytes),
            ["queue"] = new(s.QueueWaiting),
            ["queueFailed"] = new(s.QueueFailed, s.QueueFailed > 0 ? "warn" : null),
            ["unassigned"] = new(s.Unassigned),
            ["accounts"] = new(s.Accounts),
            ["accountErrors"] = new(s.AccountErrors, s.AccountErrors > 0 ? "warn" : null),
            ["lastReceived"] = new(Time(s.LastReceivedUtc)),
            ["lastSent"] = new(Time(s.LastSentUtc)),
            ["lastBackup"] = new(Time(s.LastBackupUtc)),
        };
    }

    /// <summary>The settings as they are in the file (like /Admin/Settings shows them; environment overrides are not part of it).</summary>
    public Task<IReadOnlyDictionary<string, object?>> GetConfigurationAsync(MatPlatformCall call, CancellationToken ct)
    {
        AppConfig c = AppConfigLoader.LoadFile(effective.DataDir);
        IReadOnlyDictionary<string, object?> values = new Dictionary<string, object?>
        {
            ["hostname"] = c.Server.Hostname,
            ["culture"] = Cultures.FirstOrDefault(x => string.Equals(x, c.Display.Culture, StringComparison.OrdinalIgnoreCase)) ?? Cultures[0],
            ["timeZone"] = c.Display.TimeZone,
            ["maxMessageSizeMb"] = c.Smtp.MaxMessageSizeMb,
            ["maxUploadMb"] = c.Server.MaxUploadMb,
            ["allowDirectDelivery"] = c.Queue.AllowDirectDelivery,
            ["trashDays"] = c.Retention.TrashDays,
            ["junkDays"] = c.Retention.JunkDays,
            ["transferLogDays"] = c.Retention.TransferLogDays,
        };
        return Task.FromResult(values);
    }

    /// <summary>
    /// The values were checked against the definition by MatPlatform already (types, required, min/max, options); here what it cannot know: a
    /// host name that is only blanks and a time zone this server does not know. Saved into the file like /Admin/Settings does, logged with the caller.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> ConfigureAsync(IReadOnlyDictionary<string, JsonElement> values, MatPlatformCall call, CancellationToken ct)
    {
        AppConfig c = AppConfigLoader.LoadFile(effective.DataDir);
        var errors = new Dictionary<string, string>();

        foreach ((string key, JsonElement value) in values)
        {
            switch (key)
            {
                case "hostname":
                    string hostname = value.GetString()?.Trim() ?? string.Empty;
                    if (hostname.Length == 0 || hostname.Contains(' '))
                    {
                        errors[key] = call.T("Bitte einen Hostnamen ohne Leerzeichen angeben.", "Enter a host name without blanks.");
                    }

                    c.Server.Hostname = hostname;
                    break;
                case "culture":
                    c.Display.Culture = value.GetString()!;
                    break;
                case "timeZone":
                    string zone = value.GetString()?.Trim() ?? string.Empty;
                    if (!TimeZoneInfo.TryFindSystemTimeZoneById(zone, out _))
                    {
                        errors[key] = call.T("Diese Zeitzone ist unbekannt.", "This time zone is unknown.");
                    }

                    c.Display.TimeZone = zone;
                    break;
                case "maxMessageSizeMb":
                    c.Smtp.MaxMessageSizeMb = Whole(value, key, errors, call);
                    break;
                case "maxUploadMb":
                    c.Server.MaxUploadMb = Whole(value, key, errors, call);
                    break;
                case "allowDirectDelivery":
                    c.Queue.AllowDirectDelivery = value.ValueKind == JsonValueKind.True;
                    break;
                case "trashDays":
                    c.Retention.TrashDays = Whole(value, key, errors, call);
                    break;
                case "junkDays":
                    c.Retention.JunkDays = Whole(value, key, errors, call);
                    break;
                case "transferLogDays":
                    c.Retention.TransferLogDays = Whole(value, key, errors, call);
                    break;
                default:
                    errors[key] = call.T("Diese Einstellung gibt es nicht.", "There is no such setting.");
                    break;
            }
        }

        if (errors.Count > 0)
        {
            return errors;
        }

        try
        {
            AppConfigLoader.Save(effective.DataDir, c);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Dictionary<string, string> { [string.Empty] = call.T("Die Konfigurationsdatei konnte nicht geschrieben werden: ", "The configuration file could not be written: ") + ex.Message };
        }

        await log.InfoAsync(
            ActivityCategory.Admin,
            $"Server settings were changed through the platform API ({call.Caller}): {string.Join(", ", values.Keys)}. They apply after a restart.");
        return new Dictionary<string, string>();
    }

    public async Task<MatActionResult?> RunActionAsync(string id, MatPlatformCall call, CancellationToken ct)
    {
        currentUser.RunAsSystem();
        switch (id)
        {
            case "fetchMail":
                if (!sync.IsEnabled)
                {
                    return new MatActionResult(false, call.T("Der Abruf ist in den Servereinstellungen ausgeschaltet.", "Fetching is switched off in the server settings."));
                }

                IReadOnlyList<long> accounts = await overview.ListFetchedAccountIdsAsync(ct);
                foreach (long account in accounts)
                {
                    sync.RequestSync(account);
                }

                await log.InfoAsync(ActivityCategory.Sync, $"Fetching all {accounts.Count} account(s) was requested through the platform API ({call.Caller}).");
                return new MatActionResult(true, call.T($"Abruf von {accounts.Count} Konto/Konten gestartet.", $"Started fetching {accounts.Count} account(s)."));
            case "retryQueue":
                int retried = await queue.RetryAllAsync(ct);
                await log.InfoAsync(ActivityCategory.Queue, $"{retried} outgoing message(s) were tried again through the platform API ({call.Caller}).");
                return new MatActionResult(true, call.T($"{retried} Nachricht(en) werden jetzt gesendet.", $"{retried} message(s) are being sent now."));
            default:
                return null;
        }
    }

    /// <summary>A whole number; anything else is an error under the key (and 0 for the moment: nothing is saved then).</summary>
    private static int Whole(JsonElement value, string key, Dictionary<string, string> errors, MatPlatformCall call)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number))
        {
            return number;
        }

        errors[key] = call.T("Bitte eine ganze Zahl angeben.", "Enter a whole number.");
        return 0;
    }

    private static DateTimeOffset? Time(DateTime? utc) => utc is DateTime value ? new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)) : null;
}
