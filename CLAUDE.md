# CLAUDE.md

Guidance for working in this repository. Keep it in sync with the code: update it whenever architecture, persistence, interfaces or major data flow change.

## What MatMail is

A small self-hosted mail **gateway with a built-in web mail client** (think "tiny Exchange"). Mail accounts of external providers (Strato, Netcup, …) are connected once; users then work with the gateway's own credentials in the web client, in Outlook/Thunderbird (IMAP + SMTP of the gateway) or send through it as a smart host. A hoster can give customers their own **tenant**; the provider's real credentials never leave the server, and a provider move only changes one record.

Stack: ASP.NET Core on **.NET 10**, C#, **Razor Pages** (no Blazor/SPA framework) + TagHelper controls, EF Core + **PostgreSQL**, MailKit/MimeKit, Docker-first. Web port **9933**.

## Build / run / test

- **Build:** `dotnet build MatMail.slnx` (note: `.slnx`). SDK 10 (see `global.json`).
- **Run / test the app: always rebuild the container and redeploy the stack** (project rule): `./scripts/redeploy.ps1` (`-Fresh` also wipes the volumes → the setup page appears again). Dev stack: `docker-compose.dev.yml` → http://localhost:9933, plain HTTP; mail on host ports 2525/2587/2465/2143/2993; PostgreSQL on 15432.
- **Release stack:** `docker-compose.release.yml` (image `ghcr.io/real-ttx/matmail`), HTTPS on 9933, standard mail ports.
- **Unit/integration tests:** `dotnet test MatMail.slnx`. Tests that need PostgreSQL read `MATMAIL_TEST_DB` (e.g. `Host=localhost;Port=15432;Database=matmail_test;Username=postgres;Password=matmail`, with the dev stack up) and are skipped without it.
- **Migrations:** `dotnet ef migrations add <Name> --project src/MatMail/MatMail.csproj --output-dir Migrations` (local tool, `dotnet tool restore`). They are applied automatically at start-up.
- **Translations:** UI text is English in the source (`L["…"]`), German in `src/MatMail/Resources/SharedResource.de.resx` (English text = key). `node tools/i18n.mjs check` lists missing keys, `add patch.json` merges `{ "English": "Deutsch" }`. Keep it complete before committing UI changes.
- **Screenshots** for the README: the `/screenshots` command (Playwright + Edge headless); the embedded browser pane does not render frames here.

## Rules from the project brief (do not drift)

- **Git:** branches `main` (release) and `dev` (development); work on `dev`. Commit under the user's own name. **Never add Claude/Anthropic as author or co-author** (no `Co-Authored-By`, no "Generated with" line). Do not push unless asked.
- **Versioning** (`Directory.Build.props`, channels): release `<major>.<minor>.<build>-<date>` (main), nightly `nightly-<build>-<date>` (dev), local `local-<date>`. The CI (`.github/workflows/docker.yml`) builds, tests and pushes the image to GHCR.
- **UI:** CRUD = list page + separate edit/create page. Every list has the **toolbar above** (search, filters, sort), list actions **below, left aligned** (destructive ones with distance), **pagination directly under the table** and nothing else. Form buttons in one row, positive → negative: `Save`, `Back`, spacer, `Delete`. Icons where they help. **One code base per control** (`src/MatMail/Controls`): table, pagination, toolbar, form, field, tabs, dialog, picker, buttons; several of the same control may share a page. **Mobile first-class:** dialogs and custom dropdowns become full-screen sheets on phones. The mail client looks like a modern client (Gmail). Dependent form fields are shown only when needed (`show-when-field`).
- **Code:** little nesting, several related business functions may live in one class, readable names (Microsoft C# guidelines). No repository layers.
- **Database:** PostgreSQL for logic and mail, JSON (`/data/config/app.json`) for configuration. Tables are **PascalCase** (singular, = entity name), primary key always **`Id` BIGINT**, every row has `CreateDate`, `CreateUserId`, `UpdateDate`, `UpdateUserId` (filled by `MatMailDbContext.SaveChanges`). Tables whose key acts as a security token carry a `Token` (e.g. `UserSession`: `Id` + `Token` uuid).
- **Sessions** are rows in `UserSession` (cookie carries only the token) → they survive container restarts; data-protection keys live in `/data/keys`.

## Architecture in short

Single web project `src/MatMail` (+ `tests/MatMail.Tests`).

- `Data/` – entities (`Entities/*.cs`), enums (stored as text), `MatMailDbContext` (audit columns, tenant guard, **global tenant query filter** driven by `CurrentUser.TenantId`; use `IgnoreQueryFilters()` deliberately), migrations.
- `Services/` – business logic: `SignInService` (credentials, sessions, claims), `SessionCookieEvents` (validates the cookie against the DB on every request, 15 s cache), `CurrentUser` (web principal or an explicit actor for background/IMAP/SMTP: `RunAs…`), `TenantService`, `UserService`, `MailboxService`, `Permissions` (role catalogue), `CertificateProvider` (PFX/PEM/self-signed, hot reload), `ActivityLogger`.
- `Configuration/AppConfig.cs` – JSON config + `MATMAIL__Section__Key` environment overrides.
- `Controls/` – TagHelpers (`mm-…`) and the SVG icon sprite (`Icons.cs`).
- `Pages/` – Razor Pages: `Account/` (login, setup, profile…), `Admin/` (dashboard, tenants, users, roles, …), `Mail/` (web client), `Shared/` layouts.
- `wwwroot/` – `css/app.css` (tokens, shell), `components.css` (controls), `mail.css`; `js/app.js` (control behaviour), `picker.js`.

Multi-tenancy: every tenant-owned entity implements `ITenantEntity`; system administrators (`User.IsSystemAdmin`) can switch tenant (stored on the `UserSession`). Permissions are role-based (`Role.Permissions`, catalogue in `Permissions.cs`); access to *another person's mailbox* is delegated separately (`MailboxPermission`), so admins do not automatically read mail.
