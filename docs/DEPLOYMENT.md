# Deployment to Windows Server 2022 / IIS

One folder, one IIS site, one application pool running as the gMSA. SQLite is the only database and lives **outside** the
web root.

## 1. Server prerequisites

1. Windows Server 2022 with the **Web Server (IIS)** role.
2. The **.NET 10 Hosting Bundle** (installs the ASP.NET Core Module V2 and the runtime). Restart IIS afterwards (`iisreset`).
3. The gMSA and its delegated rights: [AD-DELEGATION.md](AD-DELEGATION.md).
4. An Okta app integration: [OKTA-SETUP.md](OKTA-SETUP.md).
5. A TLS certificate for `<APP_HOST>` (a PFX from your CA) imported into `Cert:\LocalMachine\My`.
6. RSAT ActiveDirectory PowerShell is **not** required: Version 1 uses LDAP for every action (see
   [DECISIONS.md](DECISIONS.md)). Install it only if you want `Test-ADServiceAccount`.

## 2. Build

On any machine with the .NET 10 SDK and Node 20+ (the build machine, not the server):

```powershell
dotnet publish src/Backend -c Release -o publish
```

The `publish` folder is the whole application: the API, `web.config`, and the built React app in `wwwroot`.
`appsettings.Development.json` is deliberately left out. Copy the folder to the server, for example `D:\Apps\ITDash\app`.

## 3. Folders and permissions

Keep code and data apart so an upgrade never touches data:

| Folder | Placeholder | Holds | Who may access |
| --- | --- | --- | --- |
| `D:\Apps\ITDash\app` | | the published application | gMSA: read; Administrators: full |
| `D:\Apps\ITDash\data` | `<DATA_DIRECTORY>` | `service-dashboard.db` (+ `-wal`, `-shm`) | **gMSA and Administrators only** |
| `D:\Apps\ITDash\assets` | `<ASSET_DIRECTORY>` | uploaded logos (`logos\`) | gMSA and Administrators only |
| `D:\Apps\ITDash\logs` | `<LOG_DIRECTORY>` | rolling application logs | gMSA: modify; Administrators |
| `D:\Backups\ITDash` | `<BACKUP_DIRECTORY>` | database backups | gMSA: modify; Administrators |

```powershell
$sa = 'CONTOSO\svc-itdash$'        # <AD_NETBIOS>\svc-itdash$
foreach ($d in 'data','assets','logs') {
  $p = "D:\Apps\ITDash\$d"; New-Item -ItemType Directory -Force $p | Out-Null
  icacls $p /inheritance:r /grant "Administrators:(OI)(CI)F" "SYSTEM:(OI)(CI)F" "${sa}:(OI)(CI)M"
}
icacls "D:\Apps\ITDash\app" /grant "${sa}:(OI)(CI)RX"
```

The database file is never under the web root, so IIS cannot serve it.

## 4. IIS site and application pool

```powershell
Import-Module WebAdministration
New-WebAppPool -Name ITDash
Set-ItemProperty IIS:\AppPools\ITDash managedRuntimeVersion ''            # No Managed Code
Set-ItemProperty IIS:\AppPools\ITDash processModel.identityType 3         # SpecificUser
Set-ItemProperty IIS:\AppPools\ITDash processModel.userName 'CONTOSO\svc-itdash$'
Set-ItemProperty IIS:\AppPools\ITDash processModel.password ''            # gMSA: no password
Set-ItemProperty IIS:\AppPools\ITDash processModel.loadUserProfile $true

New-Website -Name ITDash -PhysicalPath D:\Apps\ITDash\app -ApplicationPool ITDash `
    -Port 443 -Ssl -HostHeader <APP_HOST>                                  # then pick the certificate in the binding
```

* Create the HTTPS binding with your certificate (IIS Manager > Site > Bindings, or `New-WebBinding` + `netsh http add sslcert`).
* Add a plain HTTP binding on port 80 for the same host name if you want HTTP to redirect: the application redirects to
  HTTPS and sends HSTS (`Strict-Transport-Security`) outside Development.
* The site uses the in-process hosting model from the generated `web.config`.

## 5. Configuration

Infrastructure settings go in **`appsettings.Production.json`** next to the application, or in environment variables
(`Section__Key`). Start from `appsettings.Production.example.json`. Restrict the file to the gMSA and Administrators:

```powershell
icacls D:\Apps\ITDash\app\appsettings.Production.json /inheritance:r /grant "Administrators:F" "SYSTEM:F" "${sa}:R"
```

Set the secret as an environment variable on the application pool (no secrets in files or source control):

```powershell
& $env:windir\system32\inetsrv\appcmd.exe set config -section:system.applicationHost/applicationPools `
    /+"[name='ITDash'].environmentVariables.[name='Okta__ClientSecret',value='<SECRET>']" /commit:apphost
```

Required values: see the table in the example file. `ASPNETCORE_ENVIRONMENT` is `Production` by default under IIS.

### Startup checks

The application **refuses to start** (and says why in the log and in Event Viewer) if, in Production:

* `Okta:DevelopmentSignIn` is true (it is refused outside Development in any case),
* `ActiveDirectory:VerifyCertificate` is false, `UseLdaps` is false, or `Provider` is `Fake`,
* any of the Okta, AD or folder settings is empty or still a `<PLACEHOLDER>`.

Check `https://<APP_HOST>/api/health` (returns `{"status":"ok"}`) after each start.

### First start

On first start the app creates the SQLite database and applies its migrations automatically, seeds the three default roles
(Admins, Auditors and Security, Users) and, if `App:BootstrapAdminOktaGroup` is set, maps that Okta group to Admins so
the first administrator can sign in. Then open **Settings** and set the manageable OU and group allowlists
([AD-DELEGATION.md](AD-DELEGATION.md), section 5).

### Logs

Rolling files `app-YYYYMMDD.log` in `<LOG_DIRECTORY>` (30 days kept), with passwords, secrets and tokens redacted. These are
diagnostics; the audit trail is the **Activity and Logs** area (SQLite), which is append-only and keeps
`App:AuditRetentionDays` (default 365) days. Retention removals are themselves logged.

## 6. Backup and restore (SQLite)

Back up **both** the database and the logo folder (`<ASSET_DIRECTORY>`): logos are files, not database rows.

### Option A - offline backup (simplest)

1. Stop the IIS application pool: `Stop-WebAppPool ITDash`.
2. Copy the database file `service-dashboard.db`. Because WAL mode is on, also copy `service-dashboard.db-wal` and
   `service-dashboard.db-shm` if they exist.
3. Copy the `assets` folder.
4. Start the pool: `Start-WebAppPool ITDash`.

### Option B - online backup (the application keeps running)

Do **not** just copy the live `.db` file while the application is running: in WAL mode the copy can be incomplete.
Use the built-in backup, which uses SQLite's `VACUUM INTO` to write a consistent copy and copies the logo folder beside it:

```json
"App": {
  "BackupDirectory": "D:\\Backups\\ITDash",
  "BackupRetentionCount": 14,
  "DailyBackupEnabled": true
}
```

A simple timer in the application runs the backup once a day (30 seconds after start, then every 24 hours), writes
`backup-<date>-<time>\service-dashboard.db` and `backup-...\assets\...`, keeps the newest `BackupRetentionCount` folders,
and logs each backup (application log and audit log). Copy that folder off the server with your normal backup tooling.

### Restore

1. Stop the application pool.
2. **Rename** the current database file (keep it until the restore is confirmed) and delete any leftover `-wal` and
   `-shm` files.
3. Copy the backup's `service-dashboard.db` into `<DATA_DIRECTORY>` with the original file name.
4. Restore the logo folder from the **same backup date** into `<ASSET_DIRECTORY>`.
5. Start the pool and confirm that sign-in works, the settings look right and recent audit records are there.

**Test a restore at least once before go-live**, on a spare machine or a copy of the site.

## 7. Upgrades

1. Take a backup (Option A or B).
2. Publish the new version (`dotnet publish`) and copy it over the application folder. Keep `appsettings.Production.json`
   (it is not in the publish output) and never overwrite `data`, `assets` or `logs`.
   To avoid serving a half-copied site, drop an `app_offline.htm` file in the application folder first and delete it after.
3. Start the pool. New database migrations are applied automatically at startup and new permissions are added to the
   Admins role automatically; grant them to other roles in **Users and Groups > Roles**.
4. Check `/api/health` and sign in.

To roll back: stop the pool, restore the previous application folder, and restore the database backup taken in step 1
(a newer database can contain migrations that older code does not know about).

## 8. Troubleshooting

| Symptom | Where to look |
| --- | --- |
| HTTP 500.30 / the site does not start | Event Viewer > Application (the startup checks print what to fix); `logs\` |
| "Directory unavailable" (503) in the app | `ActiveDirectory:Server`/port, TLS certificate trust, firewall to TCP 636; **Settings > AD Integration > Test connection** |
| Password reset says the domain rejected it | It is AD's password policy (length, complexity, history, minimum age), which the app deliberately does not second-guess |
| Sign-in loops back to the login page | Redirect URI mismatch, wrong `Okta:Issuer`, or the clock on the server is wrong |
| A user says "Access denied" | **Activity and Logs > Application Access** shows the denied attempt and the permission is missing from their role |
