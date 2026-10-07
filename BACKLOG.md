# Backlog

Wishes of the project owner, in the order they are worked on. The original wording (German) is kept in italics.
Status: ✅ done · 🔧 in progress · ⏳ planned · ❓ open question · 💤 later

## Now

| # | Item | Status |
|---|---|---|
| 1 | **Appearance per user** – *Aussehen und Darstellung einstellbar pro User* | 🔧 theme, accent and language exist; density, font size and list options follow |
| 2 | **Branding for tenants** – *Branding für Tenants!* | ⏳ name, logo, accent colour per tenant; shown in the app, on the sign-in page of the tenant and in sent mail templates |
| 3 | **EN/DE, English is the default** – *i18n – EN/DE (EN Default)* | ✅ English unless the user or the browser says German; every text exists in both languages (`node tools/i18n.mjs check`) |
| 4 | **TOTP, optional or enforced** – *TOTP Aktivierbar, auch forcierbar als Berechtigung oder Rule* | ⏳ authenticator app + recovery codes; enforceable per tenant / role. IMAP and SMTP cannot ask for a second factor, so they use *app passwords* once TOTP is on |
| 5 | **Salutation, title, … on the user** – *Anrede oder Titel etc., damit man Signatur-Variablen besser verwenden kann* | ⏳ new profile fields and `{{Placeholders}}` |
| 6 | **Images in signatures, rich-text editor** – *Signatur sollte auch Bilder unterstützen (Upload / Richtext Editor)* | ⏳ WYSIWYG editor like the mail editor, uploaded images are embedded into sent mail |
| 7 | **Signatures offered in the client and added to the stream** – *Signaturen im Mail Client bereitstellen, aber auch in den Mail Stream anhängen* | ⏳ a signature can be offered, appended by the server, or both |
| 8 | **Templates by rule** – *Template per Regel anpassen, z. B. eine per SMTP gesendete Nachricht mit HTML-Body versehen* | ⏳ rules that wrap plain-text mail of devices/SMTP clients in an HTML template |
| 9 | **IMAP/SMTP data one click away** – *Button / in den Settings die Daten für IMAP / SMTP* | ✅ account area "Mail programs" and an entry in the user menu |
| 11 | **A really good viewer** – *Der Viewer muss richtig gut sein … egal ob Telekom, WEB.DE oder Outlook* | ⏳ robust rendering of foreign HTML (Word/Outlook, T-Online, WEB.DE, GMX, newsletters), wide layouts, dark mode, quoted text |
| 13 | **Tenant switcher in the web client** – *Tenant Switcher auch im Web Client … falls man Zugriff auf Mails in einem anderen Tenant hat?* | ❓ system administrators have it; a user with access in several tenants needs the "member of several tenants" model first |

## Later

| # | Item | Status |
|---|---|---|
| 10 | **.EML / .MSG files** – *reinziehen (anzeigen), rausziehen bzw. Copy/Paste nach draußen* | 💤 drag a file into the client to view it, drag a message out |
| 12 | **AD / LDAP** – *Anbindung mit Mapper etc., UI zur Auswahl wer sich anmelden darf* | 💤 directory sign-in with attribute mapping and a selection of who may sign in |

## Notes on the design

- **App passwords.** As soon as a user (or the whole tenant) must use a second factor, IMAP/SMTP/provider-style clients cannot do the web login. They get *app passwords*: random, shown once, named per device, revocable, usable only for IMAP and SMTP. Without this the second factor could be bypassed through IMAP.
- **Signature kinds.** One signature has the placement "offered in the client", "added by the server" or both. Server-added signatures only apply to messages that do not carry the marker of an already applied one (so the web client's own insertion is not doubled).
- **Templates by rule** need a place in the pipeline *before* the footers: `rule (who sends / from which network / which sender domain) → template (HTML with {{Body}}) → footers`.
- **Branding** is data of the tenant (display name, logo, accent colour, optional support address); the sign-in page of a tenant is reached through its name in the URL (`/t/<slug>`), the shared sign-in page stays neutral.
