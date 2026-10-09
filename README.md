<div align="center">

<img src="src/MatMail/wwwroot/icons/logo.svg" width="96" alt="MatMail" />

# MatMail

**A mail gateway with its own web client – for the mailboxes you already have.**

Connected provider accounts, web client, IMAP and SMTP for Outlook and Thunderbird, smart host,
signatures, templates, tenants and two-factor sign-in.
Docker and PostgreSQL, no cloud, no third-party services.

</div>

![The web client with folders, message list and search](docs/images/mail-inbox.png)

---

## What this is about

Mail at a small company or an association usually lives at a hoster – Strato, Netcup, IONOS – one
mailbox per person, passwords handed around, no say over signatures or legal footers, and a move to
another provider means touching every mail program. MatMail sits in between: the provider accounts
are connected once, and from then on your people sign in to MatMail with credentials of its own – in
the web client, in Outlook or Thunderbird through the IMAP and SMTP servers of the gateway, or as an
SMTP smart host (printers, scanners, scripts). The real provider credentials never leave the
server, and a move to another provider changes one record.

A hoster can give every customer an own **tenant** with their own domains, users, signatures and
branding.

## At a glance

**Mail in, mail out**
- **Connected accounts** (IMAP, POP3, SMTP) with a role: everyday *mail*, a *backup* that only
  copies, a *migration* that moves a whole account from an old provider, or *send only*
- Per account: keep the original, delete it after the download, or **live access** – nothing is
  stored, lists and bodies come from the provider when needed. Flags and deletions are
  synchronised both ways
- **Routing by address**: a recipient address belongs to a mailbox, **catch-all** accounts hand
  everything of a domain to one mailbox, and what fits nowhere waits in **Unassigned**
- **Own IMAP and SMTP servers** for Outlook, Thunderbird and phones: STARTTLS and implicit TLS,
  push (IDLE), sign-in per address or login name, throttled per client and login
- **Smart host** for printers, scanners and scripts: networks you trust relay without signing in,
  optionally limited to sender domains
- **Outgoing queue** with retries and bounce messages; sending goes through the provider account of
  the sender address, or directly (MX lookup) when none is configured
- **Mail transfer log** (*Administration → Mail transfers*): what came in – from other servers or from
  connected accounts – what went out and what moved between mailboxes, with the way it took (SMTP,
  mail program, smart host, web client, account, rule) and how it ended; an outgoing message has one
  line that follows it through the queue (queued, deferred, delivered, failed). Kept for 30 days by
  default; subjects can be left out
- **Synchronise now** for everybody, in the account menu: fetches every connected account that feeds
  your mailboxes and tells you what came in

**The web client**
- Folders (also those of the provider) with **subfolders** – below the inbox or any other folder,
  created from the folder menu and moved by menu or drag & drop – stars, drafts with auto-save,
  attachments, address suggestions, **live updates** when mail arrives
- **Rules per mailbox** (*Account → Mail rules*): when a message arrives and fits – sender, recipient,
  subject, text, a header field, attachment, size – it is moved into a folder, marked as read,
  starred, labelled, sent to the trash, deleted or **forwarded** as a copy. They run before the
  message is stored, so it arrives where it belongs. Rules of shared mailboxes are for those who
  have full control of the mailbox
- **Storage per mailbox** shown below the folders, in the mailbox list of the administration (sortable)
  and per folder on the page of the mailbox
- **Search like in Gmail**: `from:anna has:attachment newer_than:7d`, several operators at once,
  `OR`, `-` to leave out, brackets and quoted phrases; the operators work in German too (`von:`,
  `hat:anhang`, `ist:ungelesen`). An **advanced search** panel builds the text for you. When a search
  (or a folder) has more hits than a page, **select all** offers to take *all* of them – and then
  mark them as read, move, archive or delete them in one go
- **Shared mailboxes** and mailboxes **delegated** by others
- **A reader that copes with what the world sends**: foreign HTML is sanitised, shown in a sandboxed
  frame and scaled to the width, quoted history is folded, remote pictures stay blocked until you
  allow them
- Compose with rich text, pictures (paste or drop), a signature chooser and keyboard shortcuts
- **An app on your phone or computer** (PWA): put it on the home screen or install it; it opens in a
  window of its own with the colour of your theme in the status bar, cannot be zoomed by accident and
  keeps working **without a connection** – mail you write offline (also with attachments) waits in an
  outbox on the device and goes out as soon as you are back online
- **Notifications** for new mail on every device you turn them on for (Web Push, no third-party
  service of ours: the push service of your browser carries a message only your device can read); you
  choose your own mailbox only or also the shared and delegated ones. Needs https (a certificate your
  phone trusts), and on an iPhone the app on the home screen
- **Print** a message as a clean page of its own (subject, people, date and attachments above the
  text), also from a phone; there the share sheet is offered as well (the message as `.eml`)
- **Light, dark and phone**: a first-class mobile layout, accent colour, text size, density and time
  zone per user

**Signatures, footers and templates**
- **Signatures** with a rich text editor (formatting, links, lists, colours, pictures, HTML source)
  and placeholders such as `{{FullName}}`, `{{JobTitle}}`, `{{Phone}}` – a line whose placeholders
  are all empty is left out; scope: the whole tenant, one mailbox or one user
- Offered in the web client and – if you want – **appended by the server** to messages of mail
  programs and smart hosts that carry none
- **Footers** (legal notice) on every outgoing message, not removable by the sender
- **Templates by rule**: the plain text of a printer becomes an HTML mail in the look of the
  company; chosen by sender, by where the message comes from and by smart-host rule. Signed and
  encrypted messages are never touched

**Tenants, people and rights**
- **Tenants** with their own domains, mailboxes, users, roles, signatures and **branding** (name,
  logo, accent colour, an own sign-in page at `/t/<name>`)
- **Roles** with fine-grained permissions; access to somebody else's mailbox is **delegated**
  separately, so administrators do not read mail by default
- **Two-factor authentication** with an authenticator app (TOTP) and recovery codes: optional, or
  mandatory for administrators, for a tenant or for the members of a role. Mail programs sign in
  with **app passwords**
- Sessions live in the database and survive restarts, failed sign-ins are throttled, an **activity
  log** covers sign-ins, SMTP, IMAP, synchronisation and the queue
- English and German, English by default

## Screenshots

Taken from a demo instance with made-up data (English UI; German is built in).

### A reader that copes with what the world sends

| A newsletter | A wide table |
|---|---|
| ![A newsletter of WEB.DE](docs/images/mail-reader.png) | ![A report with a wide table, scaled to fit](docs/images/mail-table.png) |

Foreign HTML – newsletters of T-Online, WEB.DE and Telekom, mails of Outlook – is sanitised and
shown in a sandboxed frame, scaled to the width of the window. Wide tables get an *Original size*
button, quoted history is folded, and remote pictures stay blocked until you allow them.

### Compose with the right signature

![The compose window with a signature](docs/images/mail-compose.png)

Rich text, pictures by paste or drop, address suggestions, drafts that save themselves. The
signature chooser at the bottom offers the signatures of the sender's scope – the default is
already in place.

### Plain text in, HTML out

| What the printer sent | The template behind it |
|---|---|
| ![A message of a printer, put into the template of the company](docs/images/mail-device.png) | ![The template editor](docs/images/admin-template.png) |

A printer sends plain text through the smart host. MatMail puts it into the HTML template of the
company, adds the signature and the legal footer, and keeps the plain text next to it. A template is
chosen by sender, by where the message comes from (web client, mail program, a given smart-host
rule) and by whether it has an HTML part at all. Signed and encrypted messages are never touched.

### Signatures with pictures and placeholders

![The signature editor with a preview](docs/images/admin-signature.png)

A rich text editor with pictures and placeholders; the preview shows the signature of the signed-in
user. A signature is offered in the web client and, if you want, appended by the server to messages
of mail programs and smart hosts that carry none.

### Providers and printers

| Connected accounts | SMTP relay |
|---|---|
| ![Connected accounts with their role and state](docs/images/admin-accounts.png) | ![Networks that may relay without signing in](docs/images/admin-relay.png) |

Connected accounts bring the mail of Strato, Netcup and the like in – with a role, what happens to
the mail on the provider and the state of the last synchronisation. Networks listed under *SMTP
relay* may send without signing in: printers, scanners, internal servers.

### Tenants with their own look

| Branding | The sign-in page of a tenant |
|---|---|
| ![Name, logo and accent colour of a tenant](docs/images/admin-branding.png) | ![The sign-in page at /t/eine-firma](docs/images/login-tenant.png) |

Name, logo and accent colour per tenant, shown in the app, on the tenant's own sign-in page
(`/t/<name>`) and as `{{Website}}` in signatures.

### Two-factor authentication

| Set up | Recovery codes | Second step |
|---|---|---|
| ![Setting up the authenticator app with a QR code](docs/images/account-security-setup.png) | ![Ten recovery codes, shown once](docs/images/account-security-codes.png) | ![The second step of the sign-in](docs/images/account-two-factor.png) |

An authenticator app with a QR code and ten one-time recovery codes. Everybody can switch it on, and
it can be made mandatory for the administrators, for all users of a tenant or for the members of a
role. Someone who is bound to it but has not set it up yet is held on the security page.

### Mail programs and phones

| The data for Outlook and Thunderbird | App passwords |
|---|---|
| ![Server, ports and user name for mail programs](docs/images/account-mail-programs.png) | ![App passwords of a user](docs/images/account-security-apps.png) |

Every user finds server, ports and user name one click away. Mail programs cannot ask for a code, so
while two-factor authentication is on they sign in with an **app password** – one per device, shown
once, revocable, valid for IMAP and SMTP only.

### Administration in one place

| Dashboard | Mailboxes | Activity log |
|---|---|---|
| ![Dashboard](docs/images/admin-dashboard.png) | ![Personal and shared mailboxes](docs/images/admin-mailboxes.png) | ![Activity log](docs/images/admin-logs.png) |

What runs, what is queued and what happened; personal and shared mailboxes with their delegates;
sign-ins, mail traffic, synchronisation and delivery events. Only the sections you hold a
permission for are shown.

### Light, dark and narrow screens

| Appearance | Dark | Phone |
|---|---|---|
| ![Appearance settings](docs/images/account-appearance.png) | ![The dark theme](docs/images/mail-dark.png) | ![The mail list on a phone](docs/images/mail-mobile.png) |

Light, dark or system, seven accent colours, text size, density and time zone – per user. On a phone
the web client is a first-class app: dialogs and menus become full-screen sheets.

## Quick start

Ready-made images are published to the GitHub Container Registry:

| Tag | Built from | Use it for |
|---|---|---|
| `ghcr.io/real-ttx/matmail:latest` | `main` | releases |
| `ghcr.io/real-ttx/matmail:nightly` | `dev` | the newest features |

### 1. Just run it

Copy this into `docker-compose.yml` and start it. The only password in it belongs to the database;
the app brings the same value as its default, so it needs no entry of its own (to choose another
one, see the notes below *2. First steps in the app*):

```yaml
services:
  matmail:
    image: ghcr.io/real-ttx/matmail:latest
    restart: unless-stopped
    depends_on:
      - db
    ports:
      - "9933:9933"   # web interface (HTTP: put a reverse proxy with TLS in front)
      - "25:25"       # SMTP
      - "587:587"     # SMTP submission
      - "465:465"     # SMTPS
      - "143:143"     # IMAP
      - "993:993"     # IMAPS
    volumes:
      - matmail-data:/data

  db:
    image: postgres:17
    restart: unless-stopped
    environment:
      POSTGRES_PASSWORD: matmail
    volumes:
      - matmail-db:/var/lib/postgresql/data

volumes:
  matmail-data:
  matmail-db:
```

```bash
docker compose up -d
```

Open **http://localhost:9933**. The web interface speaks plain HTTP on purpose: MatMail is made to
run behind a reverse proxy (Caddy, nginx, Traefik …) that brings the HTTPS certificate – see
*Behind a reverse proxy* below. The first visit asks for the name of the first tenant and the
administrator account. The `matmail-data` volume keeps the configuration, the keys and the
certificates, `matmail-db` the mail – an update is just `docker compose pull && docker compose up
-d`. The same file lives in the repository as `docker-compose.yml`.

Set the real host name under **Administration → Server settings** (*Public host name*); after a
restart of the container the self-signed certificate of the mail servers carries it. Everything
else is optional.

At this point MatMail has no mail yet – which brings us to the interesting part.

### 2. First steps in the app

1. **Domains** – add the domains you receive mail for (*Mail → Domains*).
2. **Users and mailboxes** – create the people; a personal mailbox is made for each (*People →
   Users*). Shared mailboxes (info@, support@) are delegated to users with their own rights.
3. **Connected accounts** – connect the provider accounts (*Mail → Connected accounts*): incoming
   server (IMAP or POP3), outgoing server, and what happens to the mail on the provider. Mail is
   routed by recipient address to the mailboxes.
4. **Mail programs** – every user finds the data for Outlook, Thunderbird and phones under
   *My account → Mail programs* (server, ports, user name).
5. **Smart host** – let printers and scripts send without signing in (*Delivery → SMTP relay*).
6. **Signatures, footers, templates** – the look of everything that leaves the building.

Worth knowing:

- **The provider can stay the source of truth.** Per account you choose between keeping the original
  on the provider, deleting it after the download and *live access*, where nothing is stored.
- **Mail that fits nowhere is not lost.** It waits in **Unassigned** until an administrator assigns
  it to a mailbox.
- **Mail is encrypted by default.** SMTP and IMAP offer STARTTLS and implicit TLS right away: without
  a certificate the app creates a self-signed one on the first start. Put your own into the data
  volume (`certs/fullchain.pem` + `certs/privkey.pem`, or `certs/server.pfx`) and it is picked up
  right away; **Server settings → TLS certificate** shows what is in use.
- **Behind a reverse proxy.** The web interface listens on plain HTTP (port 9933) and expects a
  proxy in front that terminates TLS. Add `MATMAIL__Server__TrustProxyHeaders: "true"` on the
  `matmail` service so that the real client address (sign-in throttling, smart-host rules) and
  `https` (secure cookies) come through `X-Forwarded-*`. Only do that when the port is reachable
  through the proxy alone. A proxy that sends the same headers can be as small as
  `reverse_proxy matmail:9933` in Caddy.
- **Without a proxy.** To let MatMail serve HTTPS itself (the certificate above, self-signed until
  you bring your own) set `MATMAIL__Server__WebHttps: "true"` and open `https://<your-server>:9933`.
  Notifications and the installed app need HTTPS in the browser, wherever it comes from.
- **The database is not exposed.** PostgreSQL has no published port, so its password stays inside
  the compose network. To choose your own, set `POSTGRES_PASSWORD` and `MATMAIL__Database__Password`
  to the same value.
- **Rootless Podman and very old Docker versions** do not let the non-root app user open ports below
  1024. Add `net.ipv4.ip_unprivileged_port_start: 0` under `sysctls:` to the `matmail` service.

### 3. From source

```bash
./scripts/redeploy.ps1        # rebuilds the image and recreates the dev stack (http://localhost:9933)
./scripts/redeploy.ps1 -Fresh # …after wiping the volumes: the setup page appears again
dotnet test MatMail.slnx      # see "Development"
```

### Settings that matter

None of these is needed to start. Settings live in `/data/config/app.json` (edited under *Server
settings*) and can be overridden by environment variables `MATMAIL__Section__Key` on the `matmail`
service:

| Variable | Default | Meaning |
|---|---|---|
| `MATMAIL__Server__Hostname` | `localhost` | name of the server (certificate, greeting) |
| `MATMAIL__Server__WebHttps` | `false` | HTTPS for the web interface itself (off: plain HTTP behind a reverse proxy) |
| `MATMAIL__Server__TrustProxyHeaders` | `false` | trust `X-Forwarded-*` of a reverse proxy |
| `MATMAIL__Database__Host` / `Port` / `Database` / `Username` / `Password` | `db` / `5432` / `matmail` / `postgres` / `matmail` | PostgreSQL |
| `MATMAIL__Smtp__Port` / `SubmissionPort` / `ImplicitTlsPort` | `25` / `587` / `465` | SMTP ports |
| `MATMAIL__Imap__Port` / `ImplicitTlsPort` / `MaxConnections` | `143` / `993` / `500` | IMAP ports, overall connection limit |
| `MATMAIL__Queue__AllowDirectDelivery` | `true` | deliver directly (MX) when no provider account fits |
| `MATMAIL__Display__TimeZone` / `Culture` | `Europe/Berlin` / `en-US` | defaults for dates and language |
| `MATMAIL_ADMIN_USER`, `MATMAIL_ADMIN_PASSWORD` | – | create the first tenant and administrator unattended |
| `MATMAIL_DATA` | `/data` | data directory |

### After the first sign-in

*Administration*, at the bottom of the sidebar right above your account, opens the admin area –
dashboard, mailboxes, domains, connected accounts, signatures, templates, unassigned mail, users,
roles, SMTP relay, queue, activity log, branding, security, server settings and tenants; everybody
sees what their role allows. For production, look at **Administration → Security** first: it
decides whether two-factor authentication is optional, mandatory for the administrators or
mandatory for everybody in the tenant. Whoever administers several tenants switches between them
in the account menu.

### Updates, backups and a lost second factor

- **Updates:** `docker compose pull && docker compose up -d`. The database is migrated on start.
- **Backups:** the database volume (the mail) and the data volume (`/data`, see below). Keep
  `/data/keys`: it protects the stored provider passwords and the secrets of the authenticator apps.
- **A lost second factor:** an administrator resets it on the user's page (*People → Users*). If the
  only administrator lost the phone and the recovery codes:
  `DELETE FROM "UserTotp" WHERE "UserId" = (SELECT "Id" FROM "User" WHERE "LoginName" = '…');`

### The `/data` volume

```
/data
├─ config/         app.json – what Server settings edits
├─ keys/           DataProtection keys (sessions, stored passwords, authenticator secrets)
├─ certs/          the TLS certificate: self-signed on the first start, or your own
└─ tmp/            attachments of messages that are being written
```

The mail itself lives in the PostgreSQL volume.

## Status

| Area | Content | Status |
|---|---|---|
| Mail core | Connected accounts, routing, catch-all, Unassigned, own IMAP and SMTP servers, smart host, outgoing queue | ✅ |
| Web client | Folders, search (Gmail operators, select all hits), drafts, shared mailboxes, reader for foreign HTML, compose, print, phone layout | ✅ |
| App | Installable (PWA), offline outbox, notifications for new mail (Web Push) | ✅ |
| Signatures, footers, templates | Rich text editor, pictures, placeholders, added by the server, templates by rule | ✅ |
| Tenants and rights | Tenants, roles, delegation, branding per tenant | ✅ |
| Two-factor authentication | Authenticator app, recovery codes, app passwords, enforced per tenant or role | ✅ |
| Look and language | Theme, accent, text size, density, time zone per user; English and German | ✅ |
| Reading pane, conversation view | More list options for the web client | planned |
| Tenant switcher for ordinary users | Needs "member of several tenants" first | open question |
| `.eml` / `.msg` files | Drag a file in to view it, drag a message out | later |
| AD / LDAP | Directory sign-in with attribute mapping and a selection of who may sign in | later |

The wishes in the order they are worked on, and the reasoning behind them, live in
[BACKLOG.md](BACKLOG.md).

## Development

Building and testing:

```bash
dotnet build MatMail.slnx
dotnet test MatMail.slnx
```

Most tests need a PostgreSQL server (`MATMAIL_TEST_DB`, e.g. the database of the dev stack) and are
skipped without it; the synchronisation tests also need a GreenMail test server
(`MATMAIL_TEST_IMAP`, see `tests/MatMail.Tests/Support/TestProvider.cs`). The CI starts both.

UI text is English in the source; German lives in `src/MatMail/Resources/SharedResource.de.resx`
(`node tools/i18n.mjs check` lists what is missing). [`CLAUDE.md`](CLAUDE.md) describes the
architecture and the project rules.

## How it is built

- **.NET 10**, ASP.NET Core **Razor Pages** with TagHelper controls – no SPA framework; the web
  client is plain JavaScript, no build step
- **PostgreSQL** (Npgsql / EF Core 10) for the logic and the mail, JSON for the configuration
- **MailKit / MimeKit** towards the providers; the **IMAP and SMTP servers** of the gateway are
  written for MatMail
- Foreign HTML goes through **HtmlSanitizer** and a sandboxed frame; MX lookups use **DnsClient**;
  the QR code of the authenticator app is drawn on the server with **QRCoder**
- Provider passwords and the secrets of the authenticator apps are encrypted with ASP.NET
  **Data Protection**
- Runs entirely in **Docker**

## Branches & versioning

| Branch | Purpose | Version | Image tag |
|---|---|---|---|
| `main` | Release | `<major>.<minor>.<build>-<yyyyMMdd>` | `latest` |
| `dev` | Development | `nightly-<build>-<yyyyMMdd>` | `nightly` |
| local | built by `scripts/redeploy.ps1` | `local-<yyyyMMdd>` | – |

`MatMailMajor` and `MatMailMinor` live in [`Directory.Build.props`](Directory.Build.props), the build
number comes from the GitHub action (`.github/workflows/docker.yml`), which builds, tests and pushes
the image to the GitHub Container Registry.
