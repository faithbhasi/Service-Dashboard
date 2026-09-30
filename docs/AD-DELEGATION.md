# Active Directory: the service account and delegation

The application never uses Domain Admin or any Tier 0 right. It runs as a **group Managed Service Account (gMSA)** that
has *only* the rights below, and *only* on the OUs you choose. There is no AD password to store: the IIS application pool
runs as the gMSA and the LDAP connection binds as that identity.

> **Read this first.** The rights below are what the application's actions need, worked out from the AD schema and the
> LDAP calls the code makes. The code has not been run against a real domain controller in this repository's test
> environment. Create a **test OU** first, delegate there, and use each action's **Validate only** button (the dry run
> reads `allowedAttributesEffective` / `allowedChildClassesEffective`, so it tells you which right is missing without
> changing anything) before delegating in production.

Placeholders: `<AD_DOMAIN_FQDN>`, `<AD_NETBIOS>`, `<IIS_SERVER>` (the computer account name), `<AD_BASE_DN>`,
`<USERS_OU_DN>`, `<COMPUTERS_OU_DN>`, `<GROUPS_OU_DN>`.

## 1. Create the gMSA

Run once on a domain controller (or a machine with RSAT), as a domain admin. The KDS root key must already exist.

```powershell
# Only if the domain has never used gMSAs:
#   Add-KdsRootKey -EffectiveImmediately      (in a lab; production needs the normal 10 hour wait)

New-ADServiceAccount -Name svc-itdash `
    -DNSHostName svc-itdash.<AD_DOMAIN_FQDN> `
    -PrincipalsAllowedToRetrieveManagedPassword "<IIS_SERVER>$" `
    -KerberosEncryptionType AES256
```

On the IIS server:

```powershell
Install-WindowsFeature RSAT-AD-PowerShell        # only needed for Test-ADServiceAccount
Install-ADServiceAccount svc-itdash
Test-ADServiceAccount svc-itdash                 # must return True
```

Then set the IIS application pool identity to `<AD_NETBIOS>\svc-itdash$` (see [DEPLOYMENT.md](DEPLOYMENT.md)).

## 2. What each action needs

| Action | LDAP operation | Right to delegate on the manageable OU (descendant objects) |
| --- | --- | --- |
| Search / read users, computers, groups | search, read | Nothing extra: the default *Authenticated Users* read access is enough. The computed attributes (`msDS-User-Account-Control-Computed`, `msDS-UserPasswordExpiryTimeComputed`, `msDS-ResultantPSO`) are readable by default |
| Reset password | `unicodePwd` replace (LDAPS) | **Reset Password** control access right on **user** objects |
| Force change at next sign-in | `pwdLastSet` = 0 | Write property **pwdLastSet** on user objects |
| Unlock | `lockoutTime` = 0 | Write property **lockoutTime** on user objects |
| Enable / disable user | `userAccountControl` | Write property **userAccountControl** on user objects |
| Enable / disable computer | `userAccountControl` | Write property **userAccountControl** on computer objects |
| Move user / computer | ModifyDN | On the source and target OUs: **Create** and **Delete** child objects of class user / computer |
| Add / remove group member | `member` add / delete | Write property **member** on **group** objects in the OUs that hold the manageable groups |

Nothing else is delegated: no create/delete of users, no group edits, no `Full control`, no rights on the domain root.

## 3. Delegate with `dsacls`

Run as a domain admin. Repeat per OU. `/I:S` means "this object and all descendants"; the class name after the
property limits the right to that object class.

```powershell
$sa = '<AD_NETBIOS>\svc-itdash$'

# --- users OU (<USERS_OU_DN>) ---
dsacls "<USERS_OU_DN>" /I:S /G "${sa}:CA;Reset Password;user"
dsacls "<USERS_OU_DN>" /I:S /G "${sa}:WP;pwdLastSet;user"
dsacls "<USERS_OU_DN>" /I:S /G "${sa}:WP;lockoutTime;user"
dsacls "<USERS_OU_DN>" /I:S /G "${sa}:WP;userAccountControl;user"
dsacls "<USERS_OU_DN>" /I:T /G "${sa}:CC;user"      # create child (needed to receive a moved user)
dsacls "<USERS_OU_DN>" /I:T /G "${sa}:DC;user"      # delete child  (needed to move a user out)

# --- computers OU (<COMPUTERS_OU_DN>) ---
dsacls "<COMPUTERS_OU_DN>" /I:S /G "${sa}:WP;userAccountControl;computer"
dsacls "<COMPUTERS_OU_DN>" /I:T /G "${sa}:CC;computer"
dsacls "<COMPUTERS_OU_DN>" /I:T /G "${sa}:DC;computer"

# --- groups OU (<GROUPS_OU_DN>): only where the manageable groups live ---
dsacls "<GROUPS_OU_DN>" /I:S /G "${sa}:WP;member;group"
```

You can achieve the same with the *Delegation of Control Wizard* (custom task, "Only the following objects in the
folder"), choosing the properties above. Moving between two OUs that are both delegated needs the create-child /
delete-child pair on both.

## 4. What the gMSA must NOT have

* No membership of Domain Admins, Enterprise Admins, Administrators, Account/Backup/Server/Print Operators or any group with
  `adminCount=1`.
* No rights on `OU=Domain Controllers`, any Tier 0 or administrative OU, or the service-account OU.
* AD's AdminSDHolder / SDProp process strips inherited delegated rights from protected accounts, which is exactly what you
  want: those accounts stay out of reach even if they end up under a delegated OU. The application also refuses them in
  software (see below).

## 5. Layers of protection (all enforced on the backend, not just the UI)

1. **AD delegation** (this document) - the hard boundary.
2. **Manageable OU allowlist** and **manageable groups allowlist** (Settings > AD Integration). Empty means nothing is manageable.
3. **Always blocked in the application**: Domain Controllers, OUs whose name marks them as Tier 0 / admin / service account,
   the configured *protected OUs*, protected groups (the built-in list, `adminCount=1` and the configured list) and user
   accounts with `adminCount=1`.

Set the allowlists to the OUs you delegated above and no more.

## 6. Directory servers and TLS

* The application connects with **LDAPS on port 636**. The domain controller needs a certificate whose subject or SAN
  matches `ActiveDirectory:Server`, issued by a CA the IIS server trusts. Certificate validation stays **on**: the
  application refuses to start in Production with `ActiveDirectory:VerifyCertificate` false or `UseLdaps` false.
* Passwords can only be set over an encrypted connection; that is one more reason LDAPS is required.
* Open TCP 636 from the IIS server to the domain controller. Use a DC in the site nearest to the server; set
  `ActiveDirectory:Server` to that DC's FQDN (or a DC locator name).

## 7. Check it works

1. Sign in as an Admin, open **Settings > AD Integration** and press **Test connection**: bind, search base, secure
   connection and a sample search should all pass.
2. Open a user in a delegated test OU and press **Validate only** on Account Actions (Password reset, Unlock, Enable/Disable), Groups (Add) and
   Move OU. Each check names the right that is missing, if any.
3. Try one real change on a test account and look at **Activity and Logs > Admin Actions**.
