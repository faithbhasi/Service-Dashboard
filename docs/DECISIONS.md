# Decisions and known limitations

Where the specification was open, unclear or silent, the simpler option that is still secure was chosen and is recorded
here. Section numbers refer to the build specification.

## 1. Provider choice per action (specification 2.1)

**Every Active Directory action uses LDAP over LDAPS.** PowerShell is used for none of them, so
`Modules/ActiveDirectory/Providers/PowerShell/` is intentionally empty (it holds a README with the rules for adding it).

| Action | Implementation | Why LDAP is practical and supported |
| --- | --- | --- |
| Search / read users, computers, groups, OUs | LDAP search (server-side paging with sort + VLV) | native |
| Nested groups | LDAP `LDAP_MATCHING_RULE_IN_CHAIN` (1.2.840.113556.1.4.1941) | native |
| Reset password | LDAP modify `unicodePwd` (needs LDAPS) | this is the documented way to set a password over LDAP |
| Force change at next sign-in | LDAP modify `pwdLastSet=0` | native |
| Unlock | LDAP modify `lockoutTime=0` | native |
| Enable / disable user or computer | LDAP modify `userAccountControl` | native |
| Move | LDAP `ModifyDN` | native |
| Add / remove group member | LDAP modify `member` | native |
| Dry run of all of the above | LDAP read of `allowedAttributesEffective` / `allowedChildClassesEffective` | native; no `-WhatIf` needed |
| Test connection | bind, base search, TLS settings check, sample search | native |

Nothing in Version 1 needs a cmdlet-only feature, so no RSAT module is required on the server.

## 2. Architecture and technology

* **.NET 10 (LTS)**, React 19 + TypeScript + Vite. No UI framework and no data-fetching library: plain CSS variables, `fetch`, small hooks.
* **OpenAPI**: the built-in `Microsoft.AspNetCore.OpenApi` document at `/openapi/v1.json`, Development only. There is no Swagger UI
  (no extra dependency); load the JSON in any viewer.
* **Provider selection** is one setting (`ActiveDirectory:Provider`: `Ldap` or `Fake`), read by the single `AddActiveDirectoryModule()`
  registration method, which is the only file that knows the implementations. The Fake provider also supplies starting allowlists
  (`IDirectoryProvider.SuggestedDefaults`) so a fresh local install works immediately; a real directory starts with nothing manageable.
* **Modules**: a module is a folder plus one registration method that registers a `ModuleDescriptor` and optionally an
  `IModuleSearchProvider` and an `IDashboardCardProvider`. A disabled module's routes answer HTTP 403 with code `module_disabled`.
* Frontend tests live in `tests/Frontend.Tests` but run with the Vite project (`npm test`); the Vite config aliases React and
  Testing Library to `src/Frontend/node_modules` so the two folders share one install.

## 3. Database and settings

* SQLite only, WAL mode and a 5 s busy timeout set on every connection. All `DateTime` values are stored and read as UTC.
* Runtime settings are stored as **one JSON document per section** in a `Settings` table (`general`, `modules`, `actionPolicies`,
  `branding`, `ad`), cached in memory until saved. The OU/group allowlists, protected lists and attribute names are part of the `ad`
  document rather than separate tables (fewer moving parts for one engineer, same audit trail).
* The **audit table is append-only**: a SQLite trigger aborts any `UPDATE`. Rows are only removed by the retention job (one delete
  statement, itself audited). There is no feature to edit or delete audit records.
* **Backups** use `VACUUM INTO` (safe with WAL) plus a copy of the logo folder, on an optional daily timer
  (`App:DailyBackupEnabled`, off by default). It is a plain hosted service with a `PeriodicTimer`, not a job framework.
* **First administrator**: `App:BootstrapAdminOktaGroup` maps an Okta group to Admins on startup when nothing is mapped yet.
  Without such a bootstrap nobody could ever sign in with rights.

## 4. Authentication and sessions

* Cookie authentication (HttpOnly, Secure outside Development, SameSite=Lax) plus the standard OpenID Connect handler, code flow with PKCE,
  `response_mode=query`. Only the **ID token** is kept (inside the encrypted cookie) so sign-out can end the Okta session; access and
  refresh tokens are discarded. Nothing is stored in browser storage.
* **Idle timeout and absolute lifetime** are read from Settings > General on every request (claims `sd_active` and `sd_signin` in the
  cookie), so changes apply immediately. The cookie itself has a 24 h outer bound.
* A user's roles and permissions are **resolved from the database on every request** (direct assignments plus Okta group mappings, using
  the groups saved at the last sign-in), so role edits take effect at once and a user disabled in the app loses their session immediately.
* Anti-forgery: a global filter requires an `X-XSRF-TOKEN` header (or form field) on every non-GET API request. The token comes from
  `/api/auth/config` (before sign-in) and `/api/auth/me`. Sign-out is a real form POST so the browser can follow the redirect to Okta.
* "Development sign-in" is registered only when enabled **and** the environment is Development; the startup validator refuses to start
  otherwise. It signs in as one of the seeded users with the same cookie and permission path as Okta.
* Startup checks go slightly beyond the specification: Production also refuses `Provider=Fake`, `UseLdaps=false` and any unfilled
  `<PLACEHOLDER>` in required settings.

## 5. Roles, permissions and safeguards (specification 11)

* Default roles are seeded once. **Admins** always gets every permission (re-synced at each start, including permissions added later)
  and cannot be edited or deleted. **Auditors and Security** = dashboard, AD read (users, computers, groups), group member export,
  logs read and export. **Users** = dashboard and `logs.read.own` only ("only what is assigned to them").
* "Users cannot give themselves more access" is implemented as: nobody can change their own roles or disable/enable themselves, and nobody
  can grant, or change the assignment of, a role whose permissions they do not fully hold. Editing a role can only *add* permissions the editor holds.
* **Last admin**: at least one enabled user with the Admins role, or one Okta group mapped to Admins, must remain. A mapping counts as one
  assignment whether or not the Okta group currently has members.
* A role cannot be deleted while assigned or mapped. Default roles cannot be deleted; only Admins is locked against editing.
* Access-management denials are audited too.

## 6. How AD changes work (specification 12)

* **One pipeline** (`AdChangeService`): permission, input checks from Action Policies, fresh re-read, allowlist/protected checks,
  dry run, change, audit, result with the correlation ID. Group add/remove runs the full pipeline **once per group** so each is validated,
  audited and reported separately.
* **Dry runs are always recorded** as "Validated (no change made)": the confirmation preview, an explicit "Validate only", and the
  automatic dry run inside a real change. A single change therefore leaves up to three audit rows (preview, automatic validation, result).
* "Validate only" needs the action's own permission. In the UI the button is shown to people with `settings.manage` (the spec says
  "admins"); the confirmation dialog uses the same dry run to show its preview to everyone who may make the change.
* HTTP mapping of the result body (a `ChangeResult` in every case): success, no-change and validated = 200; denied = 403; failed
  (including a failed dry run) = 422; missing justification/ticket/typed confirmation = 400; directory unreachable = 503.
* **No-change outcomes**: unlocking an unlocked account, or enabling an enabled one, reports `NoChange` and writes nothing.
* **Typed confirmation** compares against the account's `sAMAccountName` (users) or the computer name.
* **Password**: the request DTO's password is moved into a `SecureString` immediately, the DTO field is nulled, its `ToString()` is
  redacted, dry runs never carry it, the Serilog enricher redacts password/secret/token-like properties (also inside structured
  objects and `password=...` text), and tests prove it is absent from logs, audit rows and every response.
* Section 8.4 says the remove rules of "Section 8.9" apply; 8.9 is the Move OU section, so the **8.5 rules** (allowlist, protected groups,
  primary group cannot be removed) are applied to removal.
* **Users with `adminCount=1`** cannot be changed by any action, in addition to the OU rules. They are privileged accounts and AD
  delegation will not reach them anyway.
* **Blocked OUs**: the OU name is matched (case-insensitively) against `tier 0`, `admin`, `service account`, `svc*` and `privileged`,
  plus `Domain Controllers`, built-in `CN=` containers and the configured protected OUs. The match is deliberately broad: blocking too
  much is the safe failure. An OU such as "Administration" is therefore blocked; rename it or manage it in AD.
* Allowlist entries are DNs. An OU is allowed when it equals or sits below an allowlisted OU **and** is not blocked; protected always wins.
  Both the current and the target OU are checked for moves.

## 7. AD reads

* **Search** matches substrings (`*text*`) on the specified attributes. All input is escaped per RFC 4515; DNs are validated (RFC 4514
  shape) before use; attribute names taken from settings must be plain names.
* **Paging**: the LDAP provider uses the sort control (by `cn`) plus **VLV** so page N is fetched without reading earlier pages, and gets
  the total from the server. If a server refuses VLV it falls back to a bounded scan. Totals are capped at the *search result limit*
  setting (default 1000) and shown with a "+".
* **Group members** are found with `(memberOf=<group DN>)` plus the type and text filters, evaluated by the directory and paged with
  VLV. Consequences: members whose *primary* group is this group are not included in `memberOf` results (usually only Domain Users), and the
  member count comes from the same query. The Fake provider filters in memory (it *is* memory), so the "does not load the whole group"
  guarantee is a property of the LDAP provider; the test for it asserts the page-size and total contract.
* **Locked users**: `lockoutTime>=1` narrows the search, then the computed bit `msDS-User-Account-Control-Computed` (0x10) decides.
  The list filter "Locked" therefore scans up to the result limit instead of using VLV; the dashboard count pages through all matches.
* **Password expiry** uses `msDS-UserPasswordExpiryTimeComputed` (fine-grained policies included); the policy name comes from
  `msDS-ResultantPSO`. **Last logon** uses `lastLogonTimestamp` and is labelled approximate. `whenChanged` is labelled as reported
  by the domain controller queried.
* The OU tree assumes every OU may have children (finding out would cost a query per OU); an empty expansion is harmless.
* **Computers' last logged-in user** is shown only if an attribute is configured in Settings; otherwise "Not available in AD".

## 8. Search, dashboard and logs

* Search runs the three AD category searches in parallel (after warming the settings cache so the scoped database context is never used
  concurrently), 5 results each, minimum 2 characters, maximum 100.
* Dashboard counts are cached in memory for `App:DashboardCacheMinutes` (default 5) with one refresh at a time; the cache is per process
  (single server). "Actions performed today" counts Admin-category audit rows with result Success, Failure or Denied since midnight in the
  configured time zone; "Failed actions today" counts Failure and Denied. Both need `logs.read`; people with only `logs.read.own` see
  their own numbers, labelled "Your ...".
* **Log categories**: Logons = `logon.*`; Application Access = page views (`page.view`, reported by the UI, decided by the server) and
  denied API calls (`api.access`); Admin Actions = everything else, including exports, settings, role and AD changes.
* `logs.read.own` is enforced on the server: the query is forced to the caller's records whatever the URL says.
* CSV: UTF-8 with BOM (Excel), cells starting with `=`, `+`, `-`, `@`, tab or CR get a leading apostrophe, exports are capped by
  `App:ExportRowLimit` (default 50,000), refused with a "narrow the filter" message when over, and audited (successful or not).

## 9. Settings, personalization and UI

* Product name is stored once (General) and can be edited from both General and Personalization.
* Banner start/end are stored as wall-clock text in the configured time zone and evaluated with that zone on the server.
* Logos: PNG, JPEG or WebP, detected from the file's **bytes** (never its name or content type), written under fixed names
  (`logoLight.png`, ...) so nothing from an upload reaches a path; SVG is refused.
* Colours must be `#rrggbb` (so a colour can never carry CSS). Text colours (page, card, top bar, buttons) are **derived** from the
  backgrounds so custom colours stay readable, and the WCAG AA warnings are computed for every text/background pair that is configurable.
* The Content-Security-Policy has no `unsafe-inline`: scripts and styles come from the same origin; colours are applied through CSSOM
  (`style.setProperty`), which CSP allows.
* Per-viewer conveniences (visible table columns, dismissed information banner) use `localStorage`/`sessionStorage` inside try/catch
  and work without them. Drawer and list state live in the URL.

## 10. Security details

* Rate limiting uses ASP.NET Core's limiter with a fixed one-minute window **per signed-in user** (authentication runs before the limiter;
  a test caught the reverse order). Limits are `App:SearchRateLimitPerMinute` (120) and `App:WriteRateLimitPerMinute` (60).
* A test enumerates every API endpoint and fails if one has no authorization, uses an unknown policy, or answers a caller without the
  permission with anything but 403 (401 when anonymous).
* An architecture test scans the assembly's signatures and the source files and fails if anything outside the provider folders (and the
  single registration file) references LDAP, PowerShell or Fake provider types.
* Error responses are Problem Details with a safe message and a correlation ID (`X-Correlation-ID`, reused from the request only when it
  matches `[A-Za-z0-9-_]{8,64}`); stack traces never leave the server.

## Known limitations

* **The LDAP provider has been run against Samba 4.19 (an AD-compatible domain controller), not against Microsoft AD.** Against Samba it
  was verified end to end (LDAPS bind, search, sort + VLV paging, computed lockout/expiry attributes, nested and primary groups, group
  member search on 120+ members, `unicodePwd` reset, `pwdLastSet`, enable/disable, unlock after a real lockout, `ModifyDN` moves,
  membership changes, and the dry run reading `allowedAttributesEffective` / `allowedChildClassesEffective`); see
  [SAMBA-TEST-AD.md](SAMBA-TEST-AD.md). That exercise found and fixed a Windows-only API (`SecurityIdentifier`) used for the primary group.
  Microsoft AD can still differ (AdminSDHolder/SDProp, fine-grained password policy objects, some constructed attributes), so verify
  the delegated rights in `AD-DELEGATION.md` in a test OU of a Microsoft test domain, using "Validate only", before go-live.
* Nothing was tested against a real Okta org; the OIDC handler configuration follows the standard pattern and `OKTA-SETUP.md`.
* Not tested on Windows/IIS (built and tested on Linux). `dotnet publish` output was inspected and contains `web.config` (in-process hosting).
* Idle-timeout behaviour is unit tested through the cookie validator; there is no browser test that waits out a real timeout.
* Group members whose *primary* group is the group are not in `memberOf` results (see above).
* The dashboard cache and rate limits are per process, which is right for one IIS server and would need rethinking for several.
* The OU-name blocking heuristic can over-block (see above).
* There is no automated visual/end-to-end suite in the repository; the flows were exercised manually in a headless browser during development.
* Deliberately out of scope for Version 1: multiple domains, approval workflows, access reviews, SIEM integration, scheduled reports,
  hash-chained audit logs, notifications, settings history, OpenTelemetry, per-OU scoped permissions and step-up re-authentication.
