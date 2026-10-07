# MatMail

A small self-hosted **mail gateway with a built-in web mail client** – a bit like a tiny Exchange.

Connect the mail accounts you already have (Strato, Netcup, …) once. Users then work with the gateway's own credentials: in the web client, in Outlook/Thunderbird (IMAP + SMTP of the gateway), or through it as an SMTP smart host. The provider's real credentials never leave the server, and a provider move only changes one record.

> Work in progress – see `CLAUDE.md` for the architecture and the project rules.

## Branches

| Branch | Purpose | Image tag |
|---|---|---|
| `main` | release | `<major>.<minor>.<build>-<date>`, `latest` |
| `dev` | development | `nightly-<build>-<date>`, `nightly` |
| local build | `scripts/redeploy.ps1` | `local-<date>` |
