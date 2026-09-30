# Connecting a real (test) Active Directory and Okta on your own machine

Local development normally uses the Fake directory and a "pick a test user" sign-in. These two switches are independent, so
do it in two stages and check each one before adding the next:

* **Part A** - real AD, still signing in with the development picker.
* **Part B** - Okta sign-in as well.

Local overrides go in **user secrets** (never in files you commit). They override `appsettings.Development.json`:

```powershell
cd src\Backend
dotnet user-secrets list          # shows what is set
dotnet user-secrets clear         # removes them all (back to Fake AD + development sign-in)
```

Use **single quotes** around values in PowerShell so characters like `$` are not interpreted.

## Part A - Active Directory

### A1. What the test domain needs

1. A domain controller reachable from your machine: `Test-NetConnection <DC_FQDN> -Port 636` must say `TcpTestSucceeded : True`.
2. **LDAPS on the DC** (port 636 with a certificate). Passwords can only be set over an encrypted connection. If port 636 is
   closed, install AD Certificate Services (or import a certificate into the DC's NTDS store) and restart the DC.
3. Your machine must **trust the certificate's issuer** (import the CA certificate into *Trusted Root Certification Authorities*).
   Lab shortcut, Development only: set `ActiveDirectory:VerifyCertificate` to `false` (see A3). The app refuses that in Production.
4. A few **test OUs and objects** so nothing important is touched, for example
   `OU=ITDash-Test,DC=corp,DC=test` with child OUs `Users`, `Computers`, `Groups`, a handful of test users, a test computer
   account and some test groups.
5. A **service account** for the app to bind as. Create a normal user, for example `svc-itdash-test`, and delegate rights on the
   test OUs only (the `dsacls` commands in [AD-DELEGATION.md](AD-DELEGATION.md), section 3, with this account instead of the gMSA).
   Do not use a Domain Admin, even in a lab: the app is meant to run with least privilege and you want to see what it can and cannot do.

### A2. Find your values

| You need | Example | How to find it |
| --- | --- | --- |
| `<AD_DOMAIN_FQDN>` | `corp.test` | `(Get-ADDomain).DNSRoot` |
| `<DC_FQDN>` | `dc01.corp.test` | `(Get-ADDomainController).HostName` |
| `<AD_BASE_DN>` | `DC=corp,DC=test` | `(Get-ADDomain).DistinguishedName` |

### A3. Set the secrets

```powershell
cd C:\Users\fbhasi_pa\Service-Dashboard\src\Backend
dotnet user-secrets set "ActiveDirectory:Provider" "Ldap"
dotnet user-secrets set "ActiveDirectory:Domain" "corp.test"
dotnet user-secrets set "ActiveDirectory:Server" "dc01.corp.test"
dotnet user-secrets set "ActiveDirectory:Port" "636"
dotnet user-secrets set "ActiveDirectory:UseLdaps" "true"
dotnet user-secrets set "ActiveDirectory:BaseDn" "DC=corp,DC=test"
dotnet user-secrets set "ActiveDirectory:BindUsername" 'svc-itdash-test@corp.test'
dotnet user-secrets set "ActiveDirectory:BindPassword" '<PASSWORD>'
# only if your machine does not trust the DC certificate yet (Development only):
dotnet user-secrets set "ActiveDirectory:VerifyCertificate" "false"
```

`BindUsername` / `BindPassword` exist for **local testing only**: the application refuses to start outside Development if they are
set. On the server the IIS app pool runs as the gMSA and no AD password exists anywhere. If your machine is joined to the test
domain and you are running as an account that may read AD you can leave both out, and the app binds as you.

### A4. Run and check

1. Start the backend (`dotnet run`) and the frontend (`npm run dev`) as usual and sign in as **Dev Admin**.
2. **Settings > AD Integration**: the connection card should now say provider **Ldap**. Press **Test connection**: Bind, Search base,
   Secure connection and Sample search should all pass. Failures name the step.
3. The allowlists start **empty on a real directory** (nothing is manageable). Still in AD Integration:
   * add your test users OU under *Manageable OUs - users* (use **Browse OUs**), the computers OU under *computers*,
   * search for your test groups under *Manageable groups* and add them,
   * Save.
4. Open **Active Directory > Users**, search for a test user and open it. Try **Validate** on the Account Actions tab (Password reset, Unlock, Enable/Disable), Groups and Move OU:
   it reads `allowedAttributesEffective` from AD and names any delegated right that is missing, without changing anything.
5. Then do one real change on a test account and look at **Activity and Logs > Admin Actions**.

### A5. Typical problems

| Symptom | Cause / fix |
| --- | --- |
| Test connection: Bind fails, LDAP error 49 | Wrong username or password. Use the UPN (`user@corp.test`) |
| LDAP error 81/91 or "Directory unavailable" | DC name not resolvable, port 636 closed, or the certificate does not match `ActiveDirectory:Server` (use the DC's FQDN, not an IP) |
| Certificate / SSL error | Trust the CA (A1.3) or use `VerifyCertificate=false` in Development |
| Search returns nothing | `BaseDn` wrong, or the service account cannot read the OU |
| Changes say "The service account does not have permission" | The missing right is named in the dry-run; delegate it (AD-DELEGATION.md) |
| Reset password: "The domain rejected the password" | AD's own policy (length, complexity, history, minimum age) |

## Part B - Okta

### B1. In Okta

1. **Groups**: create a group for administrators, for example `ITDash-Admins`, and add yourself. (Optionally `ITDash-Helpdesk`.)
2. **Applications > Create App Integration > OIDC - OpenID Connect > Web Application**.
   * Grant type: *Authorization Code*.
   * **Sign-in redirect URI**: `http://localhost:5173/signin-oidc`
   * **Sign-out redirect URI**: `http://localhost:5173/signout-callback-oidc`
   * (Use port **5173**: the Vite dev server forwards those two paths to the backend, and the redirect URI must match exactly.)
   * Assign the app to the groups that should sign in.
3. Copy the **Client ID** and **Client secret**.
4. **Security > API > Authorization Servers > default** (or the one you use):
   * *Scopes*: make sure a `groups` scope exists.
   * *Claims > Add claim*: name `groups`, include in **ID Token / Always**, value type **Groups**, filter *Starts with* `ITDash` (or *Matches regex* `.*`), scope `groups`.
   * Note the **Issuer URI**, for example `https://<OKTA_DOMAIN>/oauth2/default`.

More detail, and the production values, are in [OKTA-SETUP.md](OKTA-SETUP.md).

### B2. Set the secrets

```powershell
cd C:\Users\fbhasi_pa\Service-Dashboard\src\Backend
dotnet user-secrets set "Okta:DevelopmentSignIn" "false"
dotnet user-secrets set "Okta:Issuer" "https://<OKTA_DOMAIN>/oauth2/default"
dotnet user-secrets set "Okta:ClientId" "<OKTA_CLIENT_ID>"
dotnet user-secrets set "Okta:ClientSecret" '<OKTA_CLIENT_SECRET>'
dotnet user-secrets set "Okta:GroupsClaim" "groups"
dotnet user-secrets set "App:BootstrapAdminOktaGroup" "ITDash-Admins"
```

`BootstrapAdminOktaGroup` is the first-administrator switch: on startup, if nothing is mapped to the Admins role yet, that Okta group
is mapped to it. **Restart the backend** after setting it.

### B3. Sign in

1. Restart the backend (`dotnet run`); keep `npm run dev` running.
2. Open **http://localhost:5173** (not 5080: the redirect URI you registered is on 5173).
3. The login page now shows **Sign in with Okta**. You are sent to Okta, sign in with your username and password (or SSO / MFA),
   and come back signed in. Members of `ITDash-Admins` get the Admins role.
4. Everyone else who signs in lands on **No access assigned** until you give them a role: **Users and Groups > Group Mappings**
   (map an Okta group to a role) or **App Users** (assign a role directly).

The old development users stay in the local database but can no longer sign in. To start clean, stop the backend and delete
`src\Backend\data` (this also resets the allowlists and other settings).

### B4. Typical problems

| Symptom | Cause / fix |
| --- | --- |
| Okta error: `redirect_uri` mismatch | The URI in Okta must be exactly `http://localhost:5173/signin-oidc` |
| Returns to the login page with "Sign-in could not be completed" | Check the **Logons** tab (or the console) for the reason; usually wrong client secret or issuer |
| "Correlation failed" / loops back to login | Use one host consistently (`localhost`, not `127.0.0.1`) and clear cookies for it |
| Signed in but **No access assigned** | The `groups` claim is missing from the ID token, or your group does not match `BootstrapAdminOktaGroup` (case-insensitive). Use Okta's *Token Preview* to check |
| Backend refuses to start "Okta:ClientSecret is not set" | Only in Production. Locally, check `dotnet user-secrets list` |

## Going back

`dotnet user-secrets clear` removes every override and you are back to the Fake directory with the development sign-in.
