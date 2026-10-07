# Backlog

Wishes of the project owner, in the order they are worked on. The original wording (German) is kept in italics.
Status: ✅ done · 🔧 in progress · ⏳ planned · ❓ open question · 💤 later

## Now

| # | Item | Status |
|---|---|---|
| 1 | **Appearance per user** – *Aussehen und Darstellung einstellbar pro User* | ✅ theme, accent colour, language, text size, density, time zone and message previews (`/Account/Appearance`); more list options (reading pane, conversation view) can follow |
| 2 | **Branding for tenants** – *Branding für Tenants!* | ✅ name, logo, accent colour and website per tenant (`/Admin/Branding`); shown in the app, on the sign-in page of the tenant (`/t/<slug>`) and as `{{Website}}` in signatures |
| 3 | **EN/DE, English is the default** – *i18n – EN/DE (EN Default)* | ✅ English unless the user or the browser says German; every text exists in both languages (`node tools/i18n.mjs check`) |
| 4 | **TOTP, optional or enforced** – *TOTP Aktivierbar, auch forcierbar als Berechtigung oder Rule* | ✅ authenticator app, recovery codes and app passwords; enforceable per tenant (nobody / administrators / everyone) and per role; IMAP and SMTP take app passwords once TOTP is on or required |
| 5 | **Salutation, title, … on the user** – *Anrede oder Titel etc., damit man Signatur-Variablen besser verwenden kann* | ✅ profile fields (salutation, title, first/last name, department, mobile, fax) and `{{Placeholders}}`; a line whose placeholders are all empty is left out |
| 6 | **Images in signatures, rich-text editor** – *Signatur sollte auch Bilder unterstützen (Upload / Richtext Editor)* | ✅ WYSIWYG editor (`rte.js`), pictures are embedded and sent as inline parts |
| 7 | **Signatures offered in the client and added to the stream** – *Signaturen im Mail Client bereitstellen, aber auch in den Mail Stream anhängen* | ✅ a signature is offered in the web client and, if set, appended by the server to messages of mail programs; footers always |
| 8 | **Templates by rule** – *Template per Regel anpassen, z. B. eine per SMTP gesendete Nachricht mit HTML-Body versehen* | ✅ templates (HTML frame with `{{Body}}`, `/Admin/Templates`) chosen by sender, source (web, mail program, smart-host rule) and kind of message; plain text of devices leaves as HTML with the text kept as alternative |
| 9 | **IMAP/SMTP data one click away** – *Button / in den Settings die Daten für IMAP / SMTP* | ✅ account area "Mail programs" and an entry in the user menu |
| 11 | **A really good viewer** – *Der Viewer muss richtig gut sein … egal ob Telekom, WEB.DE oder Outlook* | ✅ sandboxed reader with fit-to-width, quote folding, dark mode and a corpus of real-world fixtures; keep adding fixtures when something looks wrong |
| 13 | **Tenant switcher in the web client** – *Tenant Switcher auch im Web Client … falls man Zugriff auf Mails in einem anderen Tenant hat?* | ❓ system administrators have it; a user with access in several tenants needs the "member of several tenants" model first |

## Ideas that came up on the way

| Item | Status |
|---|---|
| A reading pane and a conversation view for the web client (list options of the appearance settings) | ⏳ |
| Recognise a signature that is already in an HTML message of a mail program (not only the marker of the web client) before the server adds its own | ⏳ |
| "Member of several tenants" for ordinary users (consultants, shared services): the tenant switcher of the web client needs it, cross-tenant delegation has to respect the tenant guard | ❓ |

## Later

| # | Item | Status |
|---|---|---|
| 10 | **.EML / .MSG files** – *reinziehen (anzeigen), rausziehen bzw. Copy/Paste nach draußen* | 💤 drag a file into the client to view it, drag a message out |
| 12 | **AD / LDAP** – *Anbindung mit Mapper etc., UI zur Auswahl wer sich anmelden darf* | 💤 directory sign-in with attribute mapping and a selection of who may sign in |

## Notes on the design

- **App passwords** (implemented). As soon as a user (or the whole tenant) must use a second factor, IMAP/SMTP clients cannot do the web login. They get *app passwords*: random, shown once, named per device, revocable, usable only for IMAP and SMTP. Without this the second factor could be bypassed through IMAP. Not built: notification mails, "remember this device", WebAuthn.
- **Signature kinds.** One signature has the placement "offered in the client", "added by the server" or both. Server-added signatures only apply to messages that do not carry the marker of an already applied one (so the web client's own insertion is not doubled). Signed and encrypted messages are never changed.
- **Templates by rule** sit in the pipeline *before* the signature and the footers: `template (HTML with {{Body}}) → server signature → footers`. A template is chosen by who sends (tenant, mailbox, user), where the message comes from (web client, mail program, a given smart-host rule) and whether it has an HTML part at all.
- **Branding** is data of the tenant (display name, logo, accent colour, optional support address); the sign-in page of a tenant is reached through its name in the URL (`/t/<slug>`), the shared sign-in page stays neutral.
