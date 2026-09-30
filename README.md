# IT Administration Dashboard

ASP.NET Core + React (Vite) + SQLite. Version 1 manages one Active Directory domain behind Okta sign-in.
Full documentation is written in step 8 (see `docs/`).

## Run locally (fake AD, development sign-in, no Active Directory needed)

```bash
# terminal 1 - API on http://localhost:5080
cd src/Backend && dotnet run

# terminal 2 - React dev server on http://localhost:5173 (proxies /api to the API)
cd src/Frontend && npm install && npm run dev
```

Open http://localhost:5173 and pick a seeded development user.
