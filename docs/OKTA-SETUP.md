# Okta setup

The application signs users in with **OpenID Connect, Authorization Code flow with PKCE**, using the standard ASP.NET Core
OpenID Connect handler and a cookie session. The server keeps the session; the browser only holds an HttpOnly, Secure,
SameSite cookie. No tokens are stored in browser storage.

Placeholders below look like `<THIS>`: replace them with your own values. Nothing here is a real tenant.

## 1. Create the app integration

In the Okta admin console: **Applications > Applications > Create App Integration**

* Sign-in method: **OIDC - OpenID Connect**
* Application type: **Web Application**

Settings:

| Setting | Value |
| --- | --- |
| App integration name | `<PRODUCT_NAME>` |
| Grant type | **Authorization Code** (leave Implicit off; the app sends a PKCE challenge) |
| Sign-in redirect URIs | `https://<APP_HOST>/signin-oidc` |
| Sign-out redirect URIs | `https://<APP_HOST>/signout-callback-oidc` |
| Controlled access | Assign the groups that may use the app (the app also refuses people with no role) |

For local testing against a real Okta org you can add `http://localhost:5080/signin-oidc` and
`http://localhost:5080/signout-callback-oidc`, and turn off `Okta:DevelopmentSignIn`. The normal local workflow does not
need Okta at all (development sign-in).

Copy the **Client ID** and **Client secret** from the app's General tab.

## 2. Send the user's groups in the ID token

Access is granted by mapping **Okta group names to application roles** (Users and Groups > Group Mappings), so the ID
token must carry the groups.

In **Security > API > Authorization Servers** open the server you will use (for example `default`):

1. **Scopes > Add Scope**: name `groups` (if it does not exist), "Include in public metadata" on. The app requests
   the scopes `openid profile email groups`.
2. **Claims > Add Claim**:
   * Name: `groups` (this is `Okta:GroupsClaim`)
   * Include in token type: **ID Token**, *Always*
   * Value type: **Groups**
   * Filter: *Matches regex* `.*` - or, better, *Starts with* your prefix (for example `IT-`) so only relevant groups are sent
   * Include in: The following scopes > `groups`

If you use the org authorization server instead, configure the groups claim on the app's **Sign On** tab
(Groups claim type *Filter*, name `groups`).

## 3. Configure the application

In `appsettings.Production.json` (see `src/Backend/appsettings.Production.example.json`):

```json
"Okta": {
  "Issuer": "https://<OKTA_DOMAIN>/oauth2/default",
  "ClientId": "<OKTA_CLIENT_ID>",
  "GroupsClaim": "groups",
  "DevelopmentSignIn": false
}
```

The client secret is **not** put in a file. Set it as an environment variable on the IIS application pool
(see [DEPLOYMENT.md](DEPLOYMENT.md)): `Okta__ClientSecret`. Locally use user secrets:

```bash
cd src/Backend
dotnet user-secrets set "Okta:ClientSecret" "<SECRET>"
```

`Issuer` is the authorization server's issuer URL (no trailing `/.well-known/...`).

## 4. Give someone access the first time

A new user who signs in gets an app user record and **no access** until a role is assigned or one of their Okta groups is
mapped to a role. To create the first administrator, set the Okta group that your administrators belong to:

```json
"App": { "BootstrapAdminOktaGroup": "<OKTA_ADMIN_GROUP>" }
```

On startup, if nothing is mapped to the **Admins** role yet, that group is mapped to it. After that, manage everything in
**Users and Groups** and remove the setting if you like. Roles are re-evaluated on every request from the groups seen at the
user's last sign-in, so a change in **Group Mappings** applies at the user's next sign-in (group membership changes in Okta
apply at their next sign-in too).

## What happens at sign-in and sign-out

* Sign-in success, failure, denial (user disabled in the app) and sign-out are all written to the audit log (Activity and
  Logs > Logons). That page shows sign-ins to *this application only*, not the Okta System Log.
* The encrypted session cookie holds only the user's identity and the ID token (needed to sign out of Okta). The access and
  refresh tokens are discarded.
* Idle timeout and absolute session lifetime are set in Settings > General (defaults 30 minutes and 8 hours).
* **Sign out** clears the local session and then signs out of Okta.

## Troubleshooting

| Symptom | Likely cause |
| --- | --- |
| Okta shows `redirect_uri` mismatch | The sign-in redirect URI must be exactly `https://<APP_HOST>/signin-oidc` |
| Signed in but "No access assigned" | No role, and no Okta group mapped to a role. Check the ID token really contains the `groups` claim (use the Okta token preview) and that the group name matches the mapping (case-insensitive) |
| Sign-in fails and returns to the login page with an error | See the **Logons** tab and the application log for the (safe) reason; usually a wrong client secret or issuer |
| The app will not start: "Okta:ClientSecret is not set" | The environment variable is missing on the app pool, or still a `<PLACEHOLDER>` |
