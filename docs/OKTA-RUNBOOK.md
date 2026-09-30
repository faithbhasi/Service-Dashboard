# Runbook: Okta application setup, verification and operations

A step-by-step procedure for whoever administers Okta and the IT Administration Dashboard. Companion to
[OKTA-SETUP.md](OKTA-SETUP.md) (concepts and values) and [DEPLOYMENT.md](DEPLOYMENT.md) (IIS). Every value in `<ANGLE_BRACKETS>` is
yours to fill in; nothing here is a real tenant.

**Roles**

| Who | Does |
| --- | --- |
| Okta administrator | Sections 2 to 4 (groups, app, claim, assignments) |
| Application administrator | Sections 5 to 7 (secrets, config, first sign-in) |
| Both | Section 8 (sign-off) |

**How sign-in works, in one paragraph.** The app redirects the browser to Okta (Authorization Code flow with PKCE). The person
enters their username and password, or uses SSO and MFA, on **Okta's** page. Okta returns an ID token that includes the person's
identity and group names. The app keeps only an encrypted, HttpOnly session cookie. It maps the Okta group names to application
roles (Users and Groups > Group Mappings). Nothing about passwords is ever handled by the application.

---

## 1. Before you start: fill in this sheet

| Item | Placeholder | Your value |
| --- | --- | --- |
| Okta org URL | `https://<OKTA_DOMAIN>` | |
| Authorization server | `default` (or a custom one) | |
| Issuer URL | `https://<OKTA_DOMAIN>/oauth2/default` | |
| App host name (production) | `<APP_HOST>` | |
| Admin group in Okta | `ITDash-Admins` | |
| Other groups (optional) | `ITDash-Helpdesk`, `ITDash-Auditors` | |
| Client ID | `<OKTA_CLIENT_ID>` | (from step 3.5) |
| Client secret | `<OKTA_CLIENT_SECRET>` | (from step 3.5, store in a secret manager) |

Redirect URIs by environment (register **only** the ones you use, exactly as written, no trailing slash):

| Environment | Sign-in redirect URI | Sign-out redirect URI |
| --- | --- | --- |
| Local development (Vite on 5173) | `http://localhost:5173/signin-oidc` | `http://localhost:5173/signout-callback-oidc` |
| Local, built app on 5080 | `http://localhost:5080/signin-oidc` | `http://localhost:5080/signout-callback-oidc` |
| Test / Production | `https://<APP_HOST>/signin-oidc` | `https://<APP_HOST>/signout-callback-oidc` |

Use a **separate Okta app integration per environment** so a test secret can never sign in to production.

---

## 2. Create the groups

Okta Admin Console > **Directory > Groups > Add group**

1. Create `ITDash-Admins`. Add the first administrators (at least two people, so the app is never left without an admin).
2. Optionally create `ITDash-Helpdesk`, `ITDash-Auditors` (or any names you like: they are mapped to roles later in the app).
3. Keep group names free of commas and avoid leading/trailing spaces. Mapping in the app is case-insensitive.

**Check:** Directory > Groups > *group* > People shows the right members.

---

## 3. Create the app integration

Okta Admin Console > **Applications > Applications > Create App Integration**

1. Sign-in method: **OIDC - OpenID Connect**. Application type: **Web Application**. Next.
2. **App integration name:** `<PRODUCT_NAME> (<ENVIRONMENT>)`.
3. **Grant type:** tick **Authorization Code** only. Leave Refresh Token and Implicit off.
4. **Sign-in redirect URIs:** the URI(s) from the table in section 1. **Sign-out redirect URIs:** likewise.
5. **Controlled access:** *Limit access to selected groups* and choose the groups from section 2 (or *Skip group assignment for now*
   and do it in section 4). Save.
6. On the **General** tab copy the **Client ID**, and under **Client Credentials** copy the **Client secret**.
   Store the secret in your secret manager now: it is not shown again in full.
7. Leave **Client authentication** as *Client secret* and **PKCE** as required or optional (the app always sends PKCE).

**Check:** General tab shows *Grant type: Authorization Code* and the redirect URIs you entered.

---

## 4. Assign people and send the groups in the token

### 4.1 Assign the app

**Applications > *your app* > Assignments > Assign > Assign to Groups**: assign `ITDash-Admins` and any other groups from section 2.
Only assigned people can sign in.

### 4.2 Make sure the authorization server allows the app

**Security > API > Authorization Servers > *default* (or your server)**

1. **Access Policies:** the **Default Policy** applies to *all clients* on the default server. On a custom server, create a policy
   that includes this app, with a rule that allows the **Authorization Code** grant and the scopes `openid profile email groups`.
   (Missing policy or rule is the most common cause of `access_denied` and "policy evaluation failed".)
2. Note the **Issuer URI** at the top of the page: this is `Okta:Issuer`.

### 4.3 Add the `groups` scope

**Scopes > Add Scope**: name `groups`, *Set as a default scope* on, *Include in public metadata* on. Save.
(Skip if a `groups` scope already exists.)

### 4.4 Add the `groups` claim

**Claims > Add Claim**

| Field | Value |
| --- | --- |
| Name | `groups` (this is `Okta:GroupsClaim`) |
| Include in token type | **ID Token**, **Always** |
| Value type | **Groups** |
| Filter | *Starts with* `ITDash` (or *Matches regex* `.*` while testing) |
| Include in | *The following scopes* > `groups` |

Save.

### 4.5 Prove it before touching the app

**Security > API > Authorization Servers > *server* > Token Preview**

* Client: your app. Grant type: Authorization Code. User: a member of `ITDash-Admins`. Scopes: `openid profile email groups`.
* Press **Preview Token**. In the **ID token** the payload must contain `"groups": ["ITDash-Admins", ...]` and an `email`.
  If `groups` is missing, fix the claim (filter, scope, token type) **before** continuing.

---

## 5. Configure the application

Never put the client secret in a file that is committed. Use user secrets locally and an environment variable on the server.

### 5.1 Local machine

```powershell
cd C:\Users\<YOU>\Service-Dashboard\src\Backend
dotnet user-secrets set "Okta:DevelopmentSignIn" "false"
dotnet user-secrets set "Okta:Issuer" "https://<OKTA_DOMAIN>/oauth2/default"
dotnet user-secrets set "Okta:ClientId" "<OKTA_CLIENT_ID>"
dotnet user-secrets set "Okta:ClientSecret" '<OKTA_CLIENT_SECRET>'
dotnet user-secrets set "Okta:GroupsClaim" "groups"
dotnet user-secrets set "App:BootstrapAdminOktaGroup" "ITDash-Admins"
dotnet user-secrets list
```

Restart the backend. Use single quotes around the secret in PowerShell.

### 5.2 IIS server (production)

1. `appsettings.Production.json` (see `appsettings.Production.example.json`) holds the non-secret values:
   ```json
   "Okta": { "Issuer": "https://<OKTA_DOMAIN>/oauth2/default", "ClientId": "<OKTA_CLIENT_ID>", "GroupsClaim": "groups", "DevelopmentSignIn": false },
   "App":  { "BootstrapAdminOktaGroup": "ITDash-Admins" }
   ```
2. The secret goes on the application pool as an environment variable:
   ```powershell
   & $env:windir\system32\inetsrv\appcmd.exe set config -section:system.applicationHost/applicationPools `
       /+"[name='ITDash'].environmentVariables.[name='Okta__ClientSecret',value='<OKTA_CLIENT_SECRET>']" /commit:apphost
   Restart-WebAppPool ITDash
   ```
3. The app **refuses to start** in Production if the issuer, client ID or secret is empty or still a `<PLACEHOLDER>`, or if
   `DevelopmentSignIn` is true. Read the message in Event Viewer or `logs\` if the site returns HTTP 500.30.

`App:BootstrapAdminOktaGroup` maps that Okta group to the **Admins** role on startup, **only if nothing is mapped to Admins yet**.
It is the way the first administrator gets in.

---

## 6. First sign-in test

1. Open the app in a **private/incognito window** (an old cookie can confuse the test):
   local `http://localhost:5173`, production `https://<APP_HOST>`.
2. The login page shows **Sign in with Okta**. Click it.
3. Okta's page appears. Sign in as a member of `ITDash-Admins` (username and password, then MFA if your Okta policy requires it).
4. You return to the app signed in, top-right shows your name and the role **Admins**.
5. Open **Activity and Logs > Logons**: a `Signed in` row for you must be there.

**Expected:** the whole round trip takes a few seconds and ends on the Home page.

---

## 7. Give everyone else access

New people who sign in and have no role see **No access assigned**, with the support contact from Settings > General.

1. As an Admin open **Users and Groups > Group Mappings**.
2. Map each Okta group to a role: for example `ITDash-Helpdesk` -> a Helpdesk role, `ITDash-Auditors` -> **Auditors and Security**.
   (Create custom roles in the **Roles** tab first.)
3. Or assign a role directly to one person in **App Users**.

Role changes take effect on the person's **next request**; group membership changes in Okta take effect at their **next sign-in**
(the app reads the groups from the ID token at sign-in).

---

## 8. Sign-off checklist

Tick each item and record the date and who verified it.

- [ ] Token Preview shows the `groups` claim and `email` (4.5)
- [ ] An admin signs in with Okta and lands on Home as **Admins** (6)
- [ ] `Signed in` appears in Activity and Logs > Logons (6)
- [ ] A person with no mapped group sees **No access assigned** (7)
- [ ] A person in a mapped group gets exactly the permissions of the mapped role (7)
- [ ] A person **not** assigned to the Okta app cannot sign in (Okta blocks them)
- [ ] A user disabled in **Users and Groups > App Users** is denied and a `Sign-in denied` row appears in Logons
- [ ] **Sign out** ends the session and returns to the login page; the browser back button does not show data
- [ ] Idle timeout works (Settings > General: idle and absolute session limits)
- [ ] `Okta:DevelopmentSignIn` is `false` and the login page shows no list of test users
- [ ] Client secret is stored in a secret manager, and is not in git, `appsettings*.json` or a ticket
- [ ] At least two people are in the admin group

---

## 9. Day-to-day operations

### Add or remove a person
* **Add:** add them to the right Okta group (and make sure the app is assigned to that group). They get access at next sign-in.
* **Remove:** remove them from the Okta group **and** unassign or deactivate them in Okta. To cut off their session immediately,
  also disable them in **Users and Groups > App Users** (their next request is rejected).

### Add a new role for an Okta group
Create the role (Users and Groups > Roles), then map the group (Group Mappings). No Okta change is needed unless the group is new
(section 2, and make sure it matches the claim's filter, for example starts with `ITDash`).

### Rotate the client secret (do at least yearly, and whenever it may have leaked)
1. Okta: **Applications > *app* > General > Client Credentials > Edit / Add secret** creates a second secret. Copy it.
2. Update `Okta__ClientSecret` (IIS: the appcmd command in 5.2; local: `dotnet user-secrets set`). Restart the app pool.
3. Test a sign-in (section 6).
4. Okta: **deactivate, then delete** the old secret.

### Change the host name or add an environment
Add the new redirect URIs in Okta (section 1 table), deploy, test. Remove the old URIs afterwards.

### Change the group filter or claim
After any claim change run **Token Preview** again (4.5) and have one user sign out and in.

### What to look at when something is wrong
**Activity and Logons** (sign-ins, denials, failures with safe reasons) then the application log in `logs\` (search by the
correlation ID shown to the user), then Okta **Reports > System Log** (search the user's name, look for `user.authentication`
and `app.oauth2` events).

---

## 10. Troubleshooting

| Symptom | Likely cause | Fix |
| --- | --- | --- |
| Okta error page: **redirect_uri mismatch** / *The 'redirect_uri' parameter must be a Login redirect URI in the client app settings* | The URI the app sent is not registered | Register exactly `.../signin-oidc` for the URL you are using (5.1 local uses port **5173**); no trailing slash; http vs https matters |
| **access_denied** / *User is not assigned to the client application* | Person is not in an assigned group | Section 4.1 |
| **policy evaluation failed** / *no matching policy* | Authorization server has no policy/rule for this app or grant | Section 4.2 |
| Back at the login page with "Sign-in could not be completed" | Wrong client secret, issuer or clock skew | Check Logons > failure reason, `dotnet user-secrets list`, server time |
| Loops between the app and Okta | Cookie not saved: mixed `localhost` / `127.0.0.1`, blocked cookies, or HTTPS terminated in front of the app without a forwarded scheme | Use one host name; clear cookies; on IIS use an HTTPS binding directly |
| Signed in but **No access assigned** | `groups` missing from the ID token, filter too narrow, or group not mapped | Token Preview (4.5); check the mapping name (case-insensitive); user must sign out and in |
| Signed in as the wrong role after a group change | Groups are read at sign-in | Sign out and in |
| Backend will not start: *Okta:ClientSecret is not set* | Secret missing on the app pool (Production) | Section 5.2 |
| **invalid_client** | Wrong or rotated client secret | Update the secret, restart the pool |
| **invalid_scope** | `groups` scope missing on the authorization server | Section 4.3 |
| Correlation failed | Sign-in started on one host name and returned on another, or the browser blocked cookies | Always use the same address; clear cookies |
| Cannot sign out of Okta (stays signed in to Okta) | Sign-out redirect URI not registered | Add `.../signout-callback-oidc` (section 3) |

---

## 11. Rollback

* **Sign-in is broken after a change:** revert the change (secret, claim, URIs), restart the app pool, retest with section 6.
* **Nobody can sign in at all (emergency):** on the server, temporarily map a known Okta admin group again by setting
  `App:BootstrapAdminOktaGroup` (it only applies while no Admins mapping exists), or fix the mapping in the database backup
  (see the restore steps in DEPLOYMENT.md). Do **not** enable development sign-in in Production: the app refuses to start with it.
* **Suspected secret leak:** rotate the secret (section 9) immediately, then review Logons and Okta System Log for the period.
