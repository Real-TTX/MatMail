<div align="center">

<img src="src/MatMail/wwwroot/icons/logo.svg" width="96" alt="MatMail" />

# MatMail

**A mail gateway with its own web client – for the mailboxes you already have.**

Connected provider accounts, web client, IMAP and SMTP for Outlook and Thunderbird, smart host,
signatures, templates, tenants, two-factor sign-in and complete backups to a NAS.
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
  created from the folder menu and moved by menu or drag & drop (onto another folder, or onto the
  mailbox for the top level); **messages are dragged from the list onto a folder**, all the ticked
  ones at once – stars, drafts with auto-save, attachments, address suggestions, **live updates**
  when mail arrives
- **Rules per mailbox** (*Account → Mail rules*): when a message arrives and fits – sender, recipient,
  subject, text, a header field, attachment, size – it is moved into a folder, marked as read,
  starred, labelled, sent to the trash, deleted or **forwarded** as a copy. They run before the
  message is stored, so it arrives where it belongs. Rules of shared mailboxes are for those who
  have full control of the mailbox
- **Mailbox info and storage limit**: every mailbox has a menu (the three dots at its name, or the
  right mouse button) with *Info* – whose it is, its addresses, your access and what it takes up, as
  a bar against its limit and by folder. An administrator can give a mailbox a **storage limit**; a
  full mailbox takes no new mail until something is deleted. The use is also shown below the folders,
  in the mailbox list of the administration (sortable) and per folder on the page of the mailbox
- **Search like in Gmail**: `from:anna has:attachment newer_than:7d`, several operators at once,
  `OR`, `-` to leave out, brackets and quoted phrases; the operators work in German too (`von:`,
  `hat:anhang`, `ist:ungelesen`). An **advanced search** panel builds the text for you. When a search
  (or a folder) has more hits than a page, **select all** offers to take *all* of them – and then
  mark them as read, move, archive or delete them in one go
- **Shared mailboxes** and mailboxes **delegated** by others
- **A reader that copes with what the world sends**: foreign HTML is sanitised, shown in a sandboxed
  frame and scaled to the width, quoted history is folded, remote pictures stay blocked until you
  allow them
- **A reading pane and conversations**, if you like them (*My account → Appearance*): the message opens
  to the right of the list or below it, with a bar between them that you can drag – on wide screens;
  and the messages of a thread become one row, with the people who wrote and how many there are.
  Opened, the conversation is a stack: the newest and the unread messages open, the older ones a
  line each
- **Message files**: drop an `.eml` or an Outlook `.msg` on the client (or use the button over the
  list) to read it – pictures and attachments included, nothing of it lands in a mailbox – and keep it
  in a folder with one click; drag a message out of the list to get it as an `.eml` (Chrome, Edge)
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
  programs and smart hosts that carry none: the server recognises the signature of Thunderbird,
  Gmail, Outlook and others (and the usual `-- ` separator) and does not add a second one; a quoted
  original or the "Sent from my iPhone" of a phone does not count as a signature
- **Footers** (legal notice) on every outgoing message, not removable by the sender
- **Templates by rule**: the plain text of a printer becomes an HTML mail in the look of the
  company; chosen by sender, by where the message comes from and by smart-host rule. Signed and
  encrypted messages are never touched

**Tenants, people and rights**
- **Tenants** with their own domains, mailboxes, users, roles, signatures and **branding** (name,
  logo, accent colour, an own sign-in page at `/t/<name>`)
- **Roles** with fine-grained permissions; access to somebody else's mailbox is **delegated**
  separately, so administrators do not read mail by default
- **Active Directory and other LDAP servers**: people sign in with the password they already have –
  in the web client, in mail programs and for SMTP. The password is checked at the directory and
  never stored. A mapper fills name, address, title and phone from the directory, a filter and a
  group say who may sign in, and whoever leaves the company or drops out of the group is blocked at
  the next comparison and loses their open sessions
- **Two-factor authentication** with an authenticator app (TOTP) and recovery codes: optional, or
  mandatory for administrators, for a tenant or for the members of a role. Mail programs sign in
  with **app passwords**
- Sessions live in the database and survive restarts, failed sign-ins are throttled, an **activity
  log** covers sign-ins, SMTP, IMAP, synchronisation and the queue
- English and German, English by default

**Backups and restore**
- **A complete backup of everything**: every table of the database and every file of the data volume
  (configuration, keys, certificates), taken from one consistent snapshot while MatMail keeps running.
  Nothing is listed by hand, so what future versions add is in it too
- **Schedules**: every few hours, daily, weekly or monthly; the newest *N* are kept, plus one per day,
  week and month if you want; a failed run is tried again and the administrators get a message in
  their mailbox
- **Targets**: a folder of the server (a mounted disk or share) or a **network share (NAS) over SMB**.
  The SMB client is built in – nothing has to be mounted, and the password is stored encrypted
- **Encrypted** with a passphrase if you like (AES-256); every part carries a checksum that is checked
  when it is written and again when it is read
- **Restore** from the web interface, from a file you upload, on the **setup page of a new
  installation**, with an environment variable when a new server comes up, or from the command line.
  Backups of **earlier versions** restore too (the database is migrated up to the current version);
  one from a newer version is refused. The current state is saved first, and a restore that fails
  changes nothing

## Screenshots

Taken from a demo instance with made-up data (English UI; German is built in).

### A reader that copes with what the world sends

| A newsletter | A wide table |
|---|---|
| ![A newsletter of WEB.DE](docs/images/mail-reader.png) | ![A report with a wide table, scaled to fit](docs/images/mail-table.png) |

Foreign HTML – newsletters of T-Online, WEB.DE and Telekom, mails of Outlook – is sanitised and
shown in a sandboxed frame, scaled to the width of the window. Wide tables get an *Original size*
button, quoted history is folded, and remote pictures stay blocked until you allow them.

### Search like in Gmail

| The advanced search | Select all that match |
|---|---|
| ![The advanced search panel under the search box](docs/images/mail-search.png) | ![All 93 hits of a search selected for one action](docs/images/mail-select-all.png) |

`from:netcup has:attachment newer_than:7d` – typed into the search box or built with the panel
beside it. The operators work in English and German (`von:`, `hat:anhang`, `ist:ungelesen`) and
combine with `OR`, `-` and brackets. When a search (or a folder) has more hits than fit on a page,
*select all* offers to take **all of them** – then mark them as read, move, archive or delete them in
one go.

### Message files

![A message from an Outlook file, opened for reading](docs/images/mail-file.png)

An `.eml` or an Outlook `.msg` – dropped anywhere on the web client, or chosen with the button over
the list – opens in the reader with its pictures and attachments, marked as a file: it is in no
mailbox. *Save to a folder* puts it in one (it keeps the date it was written and arrives read);
*Download* gives it back as an `.eml`. The other way round, a row can be dragged out of the list onto
the desktop or into a folder of the file manager, where it becomes an `.eml` (browsers based on Chromium).
Outlook files are turned into ordinary messages on the server (sender, recipients, text, pictures,
attachments, the ids that tie a reply to its conversation); the file is kept for a day.

### Reading pane and conversations

| A reading pane with a conversation | The settings |
|---|---|
| ![The list on the left, the conversation Projektplan Q4 on the right](docs/images/mail-reading-pane.png) | ![The appearance settings: reading pane and conversations](docs/images/account-appearance-mail.png) |

Both are a choice per user. The reading pane shows the message beside the list or below it (the bar
between them can be dragged, and the size is remembered); on a small screen a message always opens
in place of the list. With conversations the list has one row per thread – who wrote, how many
messages – and opening it stacks all of them: the newest and the unread ones open, the older ones
closed to a line each. Archiving, deleting or moving a row does that to the whole conversation. A
search still lists single messages.

### Mailbox info and storage limits

| The info of a mailbox | The limit in the administration |
|---|---|
| ![The info of a mailbox: addresses, access, the storage as a bar and by folder](docs/images/mail-mailbox-info.png) | ![The page of a mailbox: the use against the limit, the limit, the folders](docs/images/admin-mailbox-limit.png) |

The menu of a mailbox (the three dots or the right mouse button) opens its *Info*: whose it is, its
addresses, what you may do with it and what it takes up – as a bar against the limit (amber from 75 %,
orange from 90 %, red when it is reached) and by folder, the largest first. An administrator sets the
**limit** on the page of the mailbox, in gigabytes; empty means none.

A mailbox that has reached its limit takes no new mail until something is deleted (messages in the
trash count until it is emptied). The SMTP server answers *452*, a temporary error, so the sending
server tries again later; a connected account leaves the mail at the provider and goes on when there is
room again, so nothing is lost. Sending to it – from the web client, a mail program or by a rule – is
refused (the web client names the mailboxes that are full), and so is moving or copying messages into
it, over IMAP with `OVERQUOTA`. What always works, so that the owner can make room: deleting, moving
within the mailbox and what the owner writes themselves (drafts, the copy in *Sent*). A message for
several recipients goes to all of them or to none, so that a second try never delivers twice.

### Compose with the right signature

![The compose window with a signature](docs/images/mail-compose.png)

Rich text, pictures by paste or drop, address suggestions, drafts that save themselves. The
signature chooser at the bottom offers the signatures of the sender's scope – the default is
already in place.

### An app, also without a connection

| The app page | Writing offline |
|---|---|
| ![Install the app, turn on notifications, the devices that get them](docs/images/account-app.png) | ![Writing a message without a connection: it waits on the device](docs/images/mail-offline.png) |

MatMail installs like an app (PWA) and opens in a window of its own. Without a connection it still
opens; what you write – with attachments – waits in an outbox on the device and goes out as soon as
you are back online. New mail can be announced on every device you turn notifications on for (Web
Push; it needs https, and on an iPhone the app on the home screen).

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
of mail programs and smart hosts that carry none – one that Thunderbird, Gmail or Outlook has put there
is recognised, so no message gets two.

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

### Sign in with the company directory

| The connection | Who is a person, and the mapper | Who may sign in |
|---|---|---|
| ![A directory: server, encryption, the account that searches](docs/images/admin-directory.png) | ![Base, filter, login attribute and the attributes that fill the user](docs/images/admin-directory-people.png) | ![A group decides who may sign in; what a first-time user gets](docs/images/admin-directory-access.png) |

| Import people | In the list of users |
|---|---|
| ![People of the directory, some of them users already](docs/images/admin-directory-import.png) | ![Users that a directory signs in carry a badge](docs/images/admin-users-directory.png) |

Active Directory, OpenLDAP, FreeIPA and the like – plain LDAP, STARTTLS or LDAPS. The connection
page tests what is in the form (the server, the account, how many people the filter finds) before
anything is saved. People can be imported with a mailbox and roles, or are made when they sign in
for the first time; somebody who is a local user already can be switched over. The password is
always the directory's: it is checked by signing in as the person, so changing it there changes it
here, and nothing about it is stored. Two-factor authentication stays MatMail's (the directory's
password, then the code); once it is on, mail programs sign in with app passwords like everybody
else's.

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

### Backups to a NAS

| The history | A schedule | A NAS as target |
|---|---|---|
| ![Backups with their history](docs/images/admin-backups.png) | ![A schedule: when, how many are kept, encrypted](docs/images/admin-backup-schedule.png) | ![A network share as target, with a connection test](docs/images/admin-backup-target.png) |

| Restore from the NAS | Before anything is replaced |
|---|---|
| ![The backups on the share, newest first](docs/images/admin-backup-restore.png) | ![What is restored, and the options of the restore](docs/images/admin-backup-confirm.png) |

A schedule says when a backup is made, where it goes and how long it is kept. A target is a folder of
the server or a share on the NAS, which MatMail reaches itself (SMB 2 or 3). Backups are written in
scratch space, checked, sent to the target under a temporary name and renamed when complete. A restore
shows what a backup holds before it replaces anything, saves the current state first, and when the
backup is damaged or does not fit it stops and leaves everything as it was.

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
7. **A directory** (optional) – if your people have accounts in Active Directory or another LDAP
   server, connect it (*People → Directories*, see *Directories in detail* below) and they sign in
   with the password they have.
8. **Backups** – a target and a schedule (*System → Backups*), so that the data is saved regularly
   (see *Backup and restore in detail* below).

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
| `MATMAIL__Display__TimeZone` / `Culture` | `Europe/Berlin` / `en-US` | defaults for dates and language (the time zone of the backup schedules) |
| `MATMAIL__Directories__SyncMinutes` | `60` | how often the users of a directory are compared with it (somebody who left or was disabled loses open sessions then); `0` = never, sign-ins still ask the directory |
| `MATMAIL__Backup__Enabled` | `true` | scheduled backups on or off (the schedules stay) |
| `MATMAIL__Backup__TempDirectory` | `/data/tmp` | where a backup is written before it goes to its target (needs room for the backup, twice when it is encrypted) |
| `MATMAIL_RESTORE_FROM`, `MATMAIL_RESTORE_PASSPHRASE` | – | restore this backup file when the installation is still empty (see below) |
| `MATMAIL_ADMIN_USER`, `MATMAIL_ADMIN_PASSWORD` | – | create the first tenant and administrator unattended |
| `MATMAIL_DATA` | `/data` | data directory |

### After the first sign-in

*Administration*, at the bottom of the sidebar right above your account, opens the admin area –
dashboard, mailboxes, domains, connected accounts, signatures, templates, unassigned mail, users,
roles, directories, SMTP relay, queue, activity log, branding, security, server settings and tenants; everybody
sees what their role allows. For production, look at **Administration → Security** first: it
decides whether two-factor authentication is optional, mandatory for the administrators or
mandatory for everybody in the tenant. Whoever administers several tenants switches between them
in the account menu.

### Updates, backups and a lost second factor

- **Updates:** `docker compose pull && docker compose up -d`. The database is migrated on start.
- **Backups:** *System → Backups* (see below). A backup holds the database and the data volume as a
  whole, including `/data/keys`, which protects the stored provider passwords and the secrets of the
  authenticator apps: treat backup files like the secrets they contain, and encrypt them when they leave
  the server.
- **A lost second factor:** an administrator resets it on the user's page (*People → Users*). If the
  only administrator lost the phone and the recovery codes:
  `DELETE FROM "UserTotp" WHERE "UserId" = (SELECT "Id" FROM "User" WHERE "LoginName" = '…');`

### Directories in detail

**Connecting.** *People → Directories → New directory.* Start from the usual values for Active
Directory, OpenLDAP or FreeIPA, then fill in the server, the encryption (STARTTLS on 389 or LDAPS
on 636; plain LDAP only inside a network nobody can listen to), the account that searches (a
read-only service account is enough) and the base. *Test connection* says how many people the
filter finds before anything is saved. For Active Directory that is, for example: server
`dc1.example.com`, LDAPS on 636, account `svc-matmail@example.com`, base `dc=example,dc=com`, filter
`(&(objectCategory=person)(objectClass=user))`, login attribute `sAMAccountName` (or
`userPrincipalName`), and as group `cn=Mail,ou=Groups,dc=example,dc=com` with *nested groups* on
when people sit in groups inside it. A server that presents its own certificate needs the
checkbox for it (or its certificate in the trust store of the container).

**Signing in.** A login that is no user here yet is looked up in the directories that let people
create themselves; when the password is right – checked by signing in at the directory as that
person – the user is made, with the roles and the mailbox the directory names. For a user of a
directory the password is always checked at the directory and never stored here; IMAP and SMTP
remember a success for two minutes, because mail programs sign in again for every folder. A login
name that exists on this server already always belongs to that user, so a local user is never taken
over by accident (*Import people* can switch one over on purpose).

**Who may sign in.** The filter, and the group when there is one. The comparison – every hour, and
with *Compare now* – blocks whoever is gone from the directory, disabled in it (Active Directory:
`userAccountControl`) or outside the group: they cannot sign in any more and their sessions end at
once; the next comparison lets them in again when the directory does. A directory that cannot be
reached changes nothing.

**Good to know.** MatMail only reads the directory; it never changes a password or an entry. After
five wrong passwords for somebody who has no user here yet it stops asking the directory about that
login for a quarter of an hour (the same five as the lockout of its own users), so that nobody can
lock your people out of their company accounts through the web client: keep the lockout threshold
of the directory above five. Deleting a directory leaves the users it made, but nobody can sign in
as them until an administrator gives them a password. Name, address, title and phone are taken
from the directory at every sign-in; a field the directory has no attribute for stays as it is.

### Backup and restore in detail

**What is in a backup.** One zip file: every table of the database as a PostgreSQL binary export (read in
a single consistent snapshot), every file of the data volume except `tmp/`, `backups/` and `restore/`,
and a manifest with the size and SHA-256 of every part, the version of the database and the version of
the program that made it. Nothing is listed by hand: a new table or a new file is part of the next
backup without anybody remembering to add it. What is *not* in it: mail that only exists at a provider
(accounts with *live access* store nothing here), and anything outside PostgreSQL and `/data` – the one
rule of the project is that there is nothing else, apart from places you point the configuration to
yourself (a certificate folder on another volume, say).

**Versions.** The schema of the database (EF Core migrations), the layout of the files (numbered steps
in the program) and the format of the backup each have a version. A backup of an older version is
restored by building the schema it had, loading the data, and then migrating it forward exactly like an
update would; a program never touches data of a newer version. The restore is **one database
transaction**: a damaged or incompatible backup, a failing step or a full disk changes nothing.

**Schedules and targets.** *System → Backups* has the overview (what runs, the history, warnings), the
schedules (hourly, daily, weekly or monthly in the time zone of the server; keep the newest *N*, one per
day, per week, per month), the targets and the restore. A target is a folder (outside `/data`, or inside
`/data/backups`) or a folder on an SMB share: server, share, folder, user and password; *Test
connection* writes and removes a small file. The SMB client speaks SMB 2.0.2 to 3.0.2 on port 445 and signs
and encrypts when the server or the share requires it (a server that only offers SMB 1 or only SMB 3.1.1
cannot be used). Old backups are removed by a schedule only from its own files – several MatMail
installations may share one folder on the NAS.

**Restoring.** A running MatMail does not replace its own data: the web interface checks the backup (and
reads all of it, if you ask), leaves a request and stops the program; Docker starts it again
(`restart: unless-stopped`, as in the compose file above), the start-up restores while a progress page
answers on the same port, and MatMail comes back. Before anything is replaced the current state is saved
in `backups/` (the last three are kept). Outgoing mail that was queued when the backup was made is put
on *failed* with a note (it may have been sent since), IMAP clients load their folders again, and how
the server is deployed – the database connection and the `Server` settings (ports, HTTPS) – is kept,
so a restore can never make it unreachable.

- **A new installation** shows *Or restore a backup* on its setup page: upload the file (and give the
  passphrase), and the users, mailboxes, settings and keys come back.
- **A new server, unattended:** mount the backup and set `MATMAIL_RESTORE_FROM` (and
  `MATMAIL_RESTORE_PASSPHRASE` for an encrypted one); the file is restored when the installation is still
  empty and ignored afterwards:

  ```yaml
  matmail:
    environment:
      MATMAIL_RESTORE_FROM: /restore/matmail-backup.zip
    volumes:
      - ./matmail-backup.zip:/restore/matmail-backup.zip:ro
  ```

- **From the command line**, for a stopped installation or a cron job:
  `docker compose exec matmail dotnet MatMail.dll --backup /data/backups/` makes a backup of a running one;
  `docker compose run --rm matmail dotnet MatMail.dll --restore /data/backups/<file>` restores one
  (`--passphrase-env NAME` reads the passphrase from an environment variable, `--force` ignores other
  connections to the database, `--no-safety-backup` and `--keep-queue` leave out the safety copy and the
  hold on queued mail).

### The `/data` volume

```
/data
├─ config/         app.json – what Server settings edits
├─ keys/           DataProtection keys (sessions, stored passwords, authenticator secrets)
├─ certs/          the TLS certificate: self-signed on the first start, or your own
├─ backups/        the default folder for backups, and the copies made before a restore
├─ restore/        a request for a restore, uploaded backups, how the last restore ended
└─ tmp/            scratch space: attachments being written, backups being made
```

The mail itself lives in the PostgreSQL volume. A backup contains both.

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
| Backups | Complete backups of database and files, schedules with retention, folder and SMB (NAS) targets, encryption, restore (also of earlier versions) | ✅ |
| Reading pane, conversation view | The reader beside or below the list, threads as one row (per user) | ✅ |
| Mailbox info, storage limits | A menu and an info dialog per mailbox; a limit that a full mailbox obeys (SMTP, provider accounts, IMAP, web client); drag & drop of messages onto folders | ✅ |
| Tenant switcher for ordinary users | Needs "member of several tenants" first | open question |
| `.eml` / `.msg` files | Drop a file in to read it and keep it, drag a message out | ✅ |
| AD / LDAP | Directories per tenant: sign-in with the password of Active Directory or another LDAP server, a mapper, a group that says who may sign in, import, a comparison that blocks whoever left | ✅ |

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
(`MATMAIL_TEST_IMAP`, see `tests/MatMail.Tests/Support/TestProvider.cs`), the tests of the SMB targets a Samba
server (`MATMAIL_TEST_SMB`, see `tests/MatMail.Tests/Support/TestSmb.cs`), the tests of the directories an OpenLDAP
(`MATMAIL_TEST_LDAP`, see `tests/MatMail.Tests/Support/TestLdap.cs`). The CI starts all four. Against a real Active Directory
(a Samba domain controller, `MATMAIL_TEST_AD`, see `tests/MatMail.Tests/Support/TestAd.cs`) the same tests can be run locally.

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
  the QR code of the authenticator app is drawn on the server with **QRCoder**; backups reach a NAS
  through **SMBLibrary**, no mount needed; directories are asked with **Novell.Directory.Ldap**
  (pure .NET, no native LDAP library in the image)
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
