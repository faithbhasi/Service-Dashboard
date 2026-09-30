# IT Administration Dashboard

One ASP.NET Core application that serves both the API and the React front end. Version 1 manages a single
Active Directory domain behind Okta sign-in: global search, a home dashboard, AD users / computers / groups,
role-based access control, an audit log with CSV export, settings and personalization.

* Backend: ASP.NET Core (.NET 10 LTS), EF Core + SQLite, Serilog, Okta OpenID Connect (cookie session)
* Frontend: React + TypeScript (Vite), plain CSS variables (no UI framework)
* Directory access: `IDirectoryProvider` with an LDAP(S) implementation and an in-memory **Fake** implementation
* Runs on one Windows Server 2022 machine under IIS, and locally with **one command and no Active Directory**

## Run it locally (about 5 minutes)

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download) and [Node.js 20+](https://nodejs.org/).

```bash
# terminal 1 - the API, on http://localhost:5080  (Fake AD + development sign-in are on in Development)
cd src/Backend
dotnet run

# terminal 2 - the React dev server, on http://localhost:5173 (it proxies /api to the API)
cd src/Frontend
npm install
npm run dev
```

Open <http://localhost:5173> and pick a seeded user:

| User | What they have |
| --- | --- |
| Dev Admin | Everything (the Admins role) |
| Dev Auditor | Read access to AD, full Activity and Logs incl. export |
| Dev Helpdesk | A sample custom role: read, unlock, reset password, add to groups |
| Dev User | The default **Users** role: dashboard and their own activity only |
| Dev No Access | Signed in but no role: sees "No access assigned" |
| Dev Disabled | Disabled in the app: sign-in is denied and logged |

The Fake directory has ~600 users in every state (locked, disabled, expired account, expired password, never-expiring
password, must change password, fine-grained policy), computers, nested and protected groups, a Domain Controllers OU,
a Tier 0 OU, and a group with 600+ members. It resets every time the API restarts. Try `dave.locked`, `alice.smith`,
`svc.noperm` (simulates a missing delegated right, caught by the dry run) and the `GG-All-Company` group.

Runtime data (SQLite database, logos, logs) goes to `src/Backend/data` and `src/Backend/logs` (git-ignored).
Delete `src/Backend/data` to start from a clean database.

## Tests

```bash
dotnet test                       # backend: 128 tests (real app on an in-memory server, temp SQLite, Fake AD)
cd src/Frontend && npm test       # frontend: 37 tests (vitest + Testing Library; files live in tests/Frontend.Tests)
cd src/Frontend && npm run build  # type-checks and builds the React app into src/Backend/wwwroot
```

## Build one deployable folder

```bash
dotnet publish src/Backend -c Release -o publish
```

`publish` runs `npm ci && npm run build` for you, so the output folder contains the API and the React app.
Deployment to IIS is described in [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md).

## Project layout

```
src/Backend/           ASP.NET Core app (Controllers, Services, Models, Data, Middleware, Configuration)
  Modules/ActiveDirectory/   the AD module: Controllers, Services, Providers/{Ldap,PowerShell,Fake}
src/Frontend/          React app (Pages, Components, Services, Hooks, Layouts, Themes)
tests/Backend.Tests/   xUnit
tests/Frontend.Tests/  vitest
docs/                  setup, deployment and design decisions
```

## Documentation

* [docs/CONNECT-REAL-AD-AND-OKTA.md](docs/CONNECT-REAL-AD-AND-OKTA.md) - try a real test AD and Okta from your own machine, step by step
* [docs/OKTA-RUNBOOK.md](docs/OKTA-RUNBOOK.md) - step-by-step Okta app setup, sign-off checklist, day-to-day operations and troubleshooting
* [docs/OKTA-SETUP.md](docs/OKTA-SETUP.md) - the Okta app, redirect URIs and the groups claim
* [docs/AD-DELEGATION.md](docs/AD-DELEGATION.md) - the gMSA and the exact rights to delegate on the manageable OUs
* [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md) - IIS, HTTPS, SQLite location, backup and restore, upgrades
* [docs/ADDING-A-MODULE.md](docs/ADDING-A-MODULE.md) - how to add M365, Mimecast or Citrix later
* [docs/DECISIONS.md](docs/DECISIONS.md) - every choice made where the specification was open, and known limitations

## Configuration in one minute

Infrastructure settings live in `appsettings.json` (placeholders such as `<AD_DOMAIN_FQDN>` must be replaced; the app
refuses to start in Production while any remain). Secrets never go in files: use .NET user secrets locally
(`dotnet user-secrets set "Okta:ClientSecret" "..."`) and environment variables on the IIS app pool on the server.
Settings that admins change in the UI (banner, allowlists, action policies, colours...) live in SQLite.
Set `"ActiveDirectory": { "Provider": "Fake" }` for local development and `"Ldap"` for a real domain.
