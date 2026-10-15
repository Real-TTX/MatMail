# Members of several tenants

Concept for backlog item 13. Decided 2026-10-15, **designed, not built**.

> *Ein User ist einfach in mehreren Mandanten und hat ein Umschalter!*

A person keeps **one login** (one password, one second factor, one profile) and can work in several tenants. The web client shows **one tenant at a time**; the tenant switcher at the foot of the menu (the one system administrators have had since `fc8af75`) lists the tenants a person belongs to. Mail programs (IMAP/SMTP) keep to the home tenant.

## Where the code stands today

- A user belongs to exactly one tenant, `User.TenantId`. Every query runs inside the tenant of the session (global query filter on `ITenantEntity`, driven by `CurrentUser.TenantId`), and `SaveChanges` refuses to write a row of another tenant (`MatMailDbContext.ApplyAuditAndTenantRules`).
- The session already carries an *active* tenant (`UserSession.TenantId`, claim `matmail:tenant`) next to the *home* tenant (`matmail:hometenant`). `SignInService.LoadSnapshotAsync` only honours it for system administrators (`user.IsSystemAdmin ? session.TenantId : user.TenantId`), `SwitchTenantAsync` and the `SwitchTenant` page only let them switch.
- Roles belong to a tenant (`Role.TenantId`); `UserRole` ties a user to a role. `UserService.SetRolesAsync` only offers roles of the user's own tenant and never lets somebody hand out a permission they do not hold themselves.
- **Two places read the roles of a user across all tenants**: `SignInService.LoadSnapshotAsync` and `MailAccessService.LoadUserAsync` (`UserRoles.IgnoreQueryFilters().Where(ur => ur.UserId == …)`). That is harmless today, because a role of another tenant cannot be assigned. It stops being harmless the moment one user holds roles of two tenants, so the filter on the role's tenant is the first change (stage 1).

## The model

Two new tables (one migration, `AddTenantMemberships`; nothing else changes shape).

| Table | Meaning | Columns beyond the audit columns |
|---|---|---|
| `TenantMembership` (`ITenantEntity`) | The person belongs to this tenant **in addition** to their home tenant. A row *is* an active membership; there is no state. | `TenantId` (the tenant that is joined), `UserId` (cascade); unique on (`TenantId`, `UserId`) |
| `TenantInvitation` (`ITenantEntity`) | An administrator of a tenant asks somebody to join. | `TenantId` (the inviting tenant), `Identifier` (lower case, as typed: a login name or an address), `RoleIds` (`long[]`, roles of that tenant to grant), `ExpiresDate`, `State` (`Open`, `Accepted`, `Declined`, `Revoked`), `ClosedDate`, `UserId` (who accepted; empty before). The inviter is `CreateUserId`. |

- **The home tenant stays `User.TenantId` and gets no row.** Everybody already has exactly the membership they need, so no installation needs a data migration, and "home" keeps its meaning: the tenant that created the person, where their login name, directory link, password and profile live.
- **Roles of a membership are ordinary `UserRole` rows** of roles of that tenant. No new role table.
- A **system administrator** needs no membership (they may enter every tenant, as today). An invitation to one is harmless and changes nothing.

Rules that always hold (each has a test):

1. A user holds roles only of tenants they belong to (home or member).
2. **The permissions of a session are the roles of the active tenant**, nothing else (system administrators: everything, as today). Admin in A and plain user in B means plain user in B.
3. **Mail programs are home only**: the roles, the mailboxes and the sending identities of IMAP and SMTP are those of the home tenant. App passwords belong to the user and open the home tenant only.
4. **There is no membership without the person's acceptance.** No code path writes a `TenantMembership` other than "accept".
5. **Ending a membership takes everything with it that the membership carried:** the roles of that tenant, the delegations (`MailboxPermission`) on that tenant's mailboxes, the per-user rows of that tenant (personal signatures and templates, …). A re-invitation starts from nothing; old access must not come back silently. The person's **personal mailboxes in that tenant** stay (they belong to the tenant, the mail is the tenant's) as mailboxes without an owner; whoever removes the member may delete them instead, as when deleting a user today.
6. **The active tenant of a session is checked every time the session is loaded** and is never trusted: a membership that is gone, a tenant that is switched off or deleted sends the session back to the home tenant (it is not signed out).

## The flows

### Invite (administrator of the tenant, permission `users.manage`)

*Admin → Users → "Invite a member"* (a list action below the list): a login name or address, and the roles to grant.

- Only roles of the inviting tenant, and only roles whose permissions the inviter holds themselves (the rule of `SetRolesAsync`). The built-in *User* role is preselected.
- **The answer is always the same:** "If that person exists, they will see the invitation under My account → Tenants." The form never says whether the login exists, so it cannot be used to find out who is on the server. (The one exception: a person who is already in this tenant, which the administrator can see anyway.)
- The invitation is stored by **identifier**, not by user id, so it exists whether or not the person does. Limits: 50 open invitations per tenant, 10 new ones per administrator and hour; inviting again replaces the open invitation (roles, expiry). Validity 14 days.
- If the identifier is an address of a mailbox on this server, the person gets a short **notice in that mailbox** (delivered inside the gateway, like the notice about a failed backup in `BackupService`): fixed text, the name of the tenant escaped, a link to `/Account/Tenants` – *no* accept link; accepting needs a signed-in session. Without a local address there is no mail; the person sees the invitation when they next open the menu.

### Which invitation is for whom

An invitation is for the user whose **login name** equals the identifier, or who **owns a mailbox in their home tenant that has that address**. **Never `User.Email`**: the person types that into their profile themselves, so anybody could read other people's invitations by entering their address there. Mailbox addresses are set by administrators and are unique across the server.

### Accept, decline (the invited person)

*My account → Tenants* (a new page and a new entry of the account menu, which carries the number of open invitations as a badge): the tenant's name, who invited, which roles, until when. *Accept* or *Decline*.

*Accept* is one POST (antiforgery) and does everything in **one transaction in its own unrestricted scope**, the pattern of `TenantService.CreateAsync` (the guard of the DbContext stays as it is): the invitation is still open, not expired, the tenant is active, the person is active and the identity still matches, they are not in the tenant yet. Then: insert the membership, one `UserRole` per role that still exists (none left: the built-in *User* role), close the invitation, invalidate the session cache of the person, log it in the tenant's activity log. *Decline* only closes the invitation (the inviter sees "declined" in the list of invitations). An invitation works once.

### Switch

- `SwitchTenantAsync` allows: a system administrator (any tenant), or anybody into **their home tenant or a tenant of an active membership of an active tenant**. The `IsSystemAdmin` check in `SwitchTenant.cshtml.cs` goes; the service is the only place that decides.
- `LoadSnapshotAsync` resolves the active tenant (rule 6), loads **only the roles of that tenant**, and works out the second-factor requirement (below). It also carries the list of the person's tenants (id, name) in the snapshot, so the layout needs no extra query on every page.
- After the switch the user lands on `/Mail`; the requested `returnUrl` (`/Admin`) is honoured only if the person may still use it in the new tenant.
- The switch is logged (Info, category Auth) **in the activity log of the tenant that is entered**, so its administrators can see when a member came in.
- The look follows the tenant on its own (its logo and brand name in the head, its accent colour for those who made no choice of their own). The name of the tenant stays visible with the user at the foot of the menu, because those who chose an accent never see the colour of the tenant.

### Leave, remove, delete

- **A member leaves** (*My account → Tenants → Leave*, confirmation dialog) or **an administrator removes the member** (*Admin → Users*, confirmation dialog with the choice for their personal mailbox in this tenant). Rule 5 applies; the sessions of the person are invalidated and fall back to the home tenant. The home tenant cannot be left.
- **The user is deleted:** memberships and roles go by cascade, personal mailboxes become owner-less (`Mailbox.OwnerUserId` is `SetNull` already), delegations go by cascade (`MailboxPermission.UserId`).
- **The tenant is deleted:** its memberships and invitations go by cascade like every `ITenantEntity`; the members lose the tenant, nothing else.
- **A tenant is switched off:** nobody can enter it, sessions fall back to home.

### Second factor

The policy of a tenant (`Tenant.TwoFactorMode`) and the flag of a role (`RequiresTwoFactor`) are evaluated **per tenant**: a person is bound to the second factor when the rules of their **home** tenant bind them (everywhere, it is their account), or when the rules of the **active** tenant bind them (while they work there, with the roles they hold there). A member who is not enrolled can still enter a tenant that demands it, and is held on `/Account/Security` there (`TwoFactorSetupMiddleware`, unchanged). Whether IMAP and SMTP accept the account password follows the home tenant, because mail programs are home only.

## What the tenant's administrators see and may do

- **The list of users** (*Admin → Users*) also shows the members, tagged *Member*, with a filter. A member is read with a deliberate `IgnoreQueryFilters` that **projects only** id, login name and display name, joined to the membership rows of the tenant.
- A member has a **reduced page** (`Admin/Users/Member`): name, login, their roles *in this tenant* (editable under the rules of `SetRolesAsync`), whether a second factor is set up, *Remove from tenant*. **No** profile, password, login name, deactivation, deletion or second-factor reset: that belongs to the home tenant.
- **Nothing reveals the other tenants.** The administrators of B never see the name of the home tenant or the other memberships of a member; the administrators of the home tenant cannot see or change memberships either. The person sees all of it on their own *Tenants* page.
- The pickers for a **mailbox owner** and for **delegation** (`Admin/Mailboxes`) include the members, tagged. `MailboxService` refuses an owner who is neither a home user nor a member. A member has **no personal mailbox in the tenant until an administrator creates one** with the member as owner; shared mailboxes delegated to the member are the usual case (a consultant).
- Where an invitation was sent from: *Admin → Users → Invitations* (a page linked from the list actions: open, accepted, declined, revoked, expired; *Revoke*).

## Around the web client

- **Mail programs:** home tenant only (decided). Test: a member with a personal and a shared mailbox in B sees none of them over IMAP and cannot send as an address of B over SMTP.
- **Notifications:** mail that arrives in a mailbox of B reaches the member's devices whichever tenant is active (`PushPlanner` plans by mailbox owner and delegation, which is right). The payload carries the tenant, and the title names it for people with more than one. A tap opens `/Mail?tenant=<id>#…`; the page never switches on a GET. If the tenant differs from the active one it shows a card: *"This message is in <tenant>. Switch"* (a POST form to `SwitchTenant`) or *Stay*.
- **Offline outbox** (`mail-outbox.js`): kept per **user and tenant** (today per user), and only the items of the active tenant are sent; a message written offline in A must not be tried in B.
- **Service worker cache:** the cached `/Mail` page and `/api/mail/bootstrap` are network first and replaced by the first load after a switch; nothing else to do, but a test of the offline path after a switch is in the list.
- **`/t/<slug>`** (the tenant's own sign-in address) starts the session in that tenant when the person is a member of it (today the address only brands the page).
- Unread counters of the other tenants in the switcher: **not in this version** (they need reads across the boundary).

## The tenant boundary: where it is crossed

The global filter and the guard of `SaveChanges` **do not change.** Everything for a member runs inside the active tenant, exactly as for a home user. There are five places that cross on purpose, all in `MembershipService` or `SignInService`, each small and each tested for what it must refuse:

1. the tenants of the current person (`TenantMembership` rows by `UserId`, filter ignored, constrained to the current user);
2. the invitations meant for the current person (the matching rule above);
3. accept, decline, leave: unrestricted scope, a fixed set of rows, one transaction;
4. the switch and the resolution of the active tenant (rule 6);
5. the projection of a member for the administrators of the tenant (above).

New code that wants to cross the boundary has to be one of these or says why not. **The most likely mistakes are in the permissions** (rule 2 and 3) and in **leftovers after leaving** (rule 5); the tests start there.

## Tests

`TenantMembershipTests` (PostgreSQL) and `TenantMembershipWebTests`; the cases that try to cross without permission first.

- Nobody enters a tenant without an active membership: only invited, declined, revoked, expired, removed, a foreign tenant id, a tenant that is switched off.
- The permissions of a session are those of the active tenant: administrator in A and user in B (and the other way round); a crafted post cannot assign a role of a tenant the person does not belong to; an administrator cannot hand out a permission they do not hold.
- The session falls back to home when the membership ends, the tenant is switched off or deleted; it is not signed out.
- An invitation does not tell whether the person exists; it is for the login or an owned mailbox address and **not** for the contact address in the profile; somebody else can neither see, accept nor decline it; it works once and not after expiry, revocation or when the tenant is off.
- Leaving or being removed takes roles, delegations and per-user rows of that tenant with it. **A test over every entity that has a `UserId` and a `TenantId`** fails when one is neither handled nor on an explicit list, so a new one cannot be forgotten.
- Administrators of B cannot read or change profile, password, login name, activity or second factor of a member, and cannot see the other tenants.
- Second factor: the home policy binds everywhere, the policy of B binds in B only.
- IMAP and SMTP: home only.
- Deleting the user or the tenant leaves nothing behind.
- The checks of `ToolkitTests` (icons, no inline styles, every menu item leads to a page) and the translations (`node tools/i18n.mjs check`) apply to the new pages.

`BackupRestoreTests` covers the migration (it restores a backup of every earlier schema version); the two tables are part of every backup without a change, and no `DataMigrations` step is needed (nothing about the files changes).

## What is touched

| Place | Change |
|---|---|
| `Data/Entities`, migration `AddTenantMemberships` | `TenantMembership`, `TenantInvitation` |
| `SignInService.LoadSnapshotAsync` | resolve the active tenant; roles of the active tenant only; second-factor rule; list of tenants in the snapshot |
| `SignInService.SwitchTenantAsync`, `Pages/Account/SwitchTenant` | who may switch; where to land; log |
| `MailAccessService.LoadUserAsync` | roles of the home tenant only |
| `AppClaims`, `SessionSnapshot`, `CurrentUser` | the list of tenants |
| `Services/MembershipService` (new) | invite, list, accept, decline, revoke, leave, remove, members of a tenant |
| `UserService.SetRolesAsync`, `MailboxService` | roles of the tenant that is administered, also for members; owner must be home user or member |
| `Pages/Admin/Users/*` (list, invite, invitations, member page), `Pages/Admin/Mailboxes/*` (pickers) | see above |
| `Pages/Account/Tenants` (new), `ShellMenus`, `NavModel` badge | the person's side |
| `Pages/Shared/_Layout.cshtml`, `TenantService.ListSwitchableAsync` | the tenants of a member, from the snapshot |
| `Push/PushPlanner`, `wwwroot/sw.js`, `mail-core.js` / `mail-ui.js`, `mail-outbox.js` | tenant in the payload, the card, the outbox key |
| `MaintenanceService` | closed and expired invitations are deleted after 90 days |
| `Resources/SharedResource.de.resx`, `CLAUDE.md`, `README.md`, `BACKLOG.md` | texts, documentation |

## Stages

Every stage ends with the tests green and is a commit of its own.

1. **The boundary, nothing visible.** The role filter in both places, the active-tenant resolution, the two tables, `MembershipService`, the tests of the rules above. The program behaves exactly as before.
2. **The pages.** *My account → Tenants*, invite and invitations, member page, the switcher for members, the pickers.
3. **Around it.** Notice mail, badge, notifications with the card, outbox key, `/t/<slug>`.
4. **Documentation.** `CLAUDE.md` (a paragraph under *Things that are easy to get wrong* and `MembershipService` in the list of services), the README, pictures of the two new pages.

## Not in this version

Mail programs for other tenants than the home tenant; unread counters of other tenants in the switcher; creating the personal mailbox together with the invitation; pausing a membership; a person asking a tenant for access; offering invitations when a directory of the tenant knows a person that already has a user elsewhere (today such a login simply belongs to the existing user); an optional second approval by an administrator of the home tenant.

## Assumptions to confirm

Everything above that the owner has not said in so many words. Each is cheap to change before it is built.

1. Invitation by login name or address, valid 14 days, the answer never says whether the person exists.
2. The administrators of B never see the name of the home tenant or the other memberships of a member; the administrators of the home tenant cannot see or change memberships.
3. Both the member and the administrators of B can end a membership; there is no pause.
4. A member has no mailbox in B until an administrator creates one.
5. The second factor: the home policy binds everywhere, the policy of B only while the member works in B.
6. Every switch is logged in the activity log of the tenant that is entered.
7. System administrators are untouched: no membership needed, the switcher lists every tenant.
