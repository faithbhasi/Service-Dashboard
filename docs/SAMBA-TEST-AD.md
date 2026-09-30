# A throwaway test Active Directory with Samba

If you do not have a test domain to point the app at, **Samba** can act as an Active Directory domain controller. It speaks the
same LDAP/LDAPS, Kerberos and AD schema, so it is a good way to try the LDAP provider, the allowlists and every action safely.

The LDAP provider in this repository was exercised end to end against a **Samba 4.19 AD DC** (Ubuntu 24.04): bind over LDAPS,
search, paging (sort + VLV), computed attributes (lockout, password expiry), nested and primary groups, group member search on a
120+ member group, password reset (`unicodePwd`), must-change-at-next-sign-in, enable/disable, unlock (after a real lockout),
move OU, add/remove members, computers, and the dry run for an unprivileged account. It is **not** Microsoft AD, so still do a
pass against a real Microsoft test domain before go-live (differences: AdminSDHolder/SDProp behaviour, fine-grained password
policy objects, and a few constructed attributes).

## Where to run it

Samba's domain controller role only runs on Linux. On a Windows PC use one of:

| Option | Notes |
| --- | --- |
| **WSL 2** (Ubuntu) | `wsl --install -d Ubuntu`. Needs admin once and virtualisation enabled; may be blocked on managed PCs |
| **A Linux VM** (Hyper-V, VirtualBox, VMware) | Bridged or host-only network. Works everywhere virtualisation is allowed |
| **A spare Linux box or a cloud VM** | Simplest if your network allows it |

Everything below runs **inside Ubuntu** (22.04 or 24.04). Use a lab-only realm, here `CORP.TEST`.

## 1. Install and provision

```bash
sudo apt update
sudo DEBIAN_FRONTEND=noninteractive apt install -y samba smbclient winbind ldb-tools ldap-utils krb5-config dnsutils
sudo systemctl disable --now smbd nmbd winbind 2>/dev/null   # the AD DC role runs as the single "samba" service
sudo mv /etc/samba/smb.conf /etc/samba/smb.conf.orig

# make the name resolve locally (replace 127.0.0.1 with the machine's IP if other computers must reach it)
echo "127.0.0.1 dc1.corp.test dc1" | sudo tee -a /etc/hosts

sudo samba-tool domain provision --use-rfc2307 --realm=CORP.TEST --domain=CORP \
    --adminpass='Adm1n-Passw0rd!' --server-role=dc --dns-backend=SAMBA_INTERNAL --host-name=dc1
```

Start it. With systemd:

```bash
sudo systemctl unmask samba-ad-dc && sudo systemctl enable --now samba-ad-dc
```

or in the foreground (handy in WSL without systemd): `sudo samba -i -M single`

Messages about DNS or SPN *updates* failing are harmless in a lab. Check it is listening on 389 and 636:

```bash
ss -ltn | grep -E ':(389|636) '
```

Samba creates a self-signed LDAPS certificate for `dc1.corp.test` automatically.

## 2. Create test data

```bash
S="sudo samba-tool"
$S ou create "OU=ITDash-Test,DC=corp,DC=test"
$S ou create "OU=Users,OU=ITDash-Test,DC=corp,DC=test"
$S ou create "OU=Computers,OU=ITDash-Test,DC=corp,DC=test"
$S ou create "OU=Groups,OU=ITDash-Test,DC=corp,DC=test"

for u in alice bob carol; do
  $S user create $u 'Passw0rd-Init1!' --userou="OU=Users,OU=ITDash-Test" --given-name=${u^} --surname=Test \
     --mail-address=$u@corp.test --department=Engineering --job-title=Tester
done
$S user create svc-itdash-test 'Svc-Passw0rd-1!'          # the account the app binds as (see section 4)

$S group add GG-Test-Team --groupou="OU=Groups,OU=ITDash-Test"
$S group add GG-Test-VPN  --groupou="OU=Groups,OU=ITDash-Test"
$S group addmembers GG-Test-Team alice,bob
$S computer create WS-TEST01 --computerou="OU=Computers,OU=ITDash-Test"   # created disabled, like a real unjoined computer
$S user setexpiry carol --days=-1                                          # an expired account
```

To try lockout, `sudo samba-tool domain passwordsettings set --account-lockout-threshold=3 --account-lockout-duration=30
--reset-account-lockout-after=30`, then fail a bind for a user three times.

## 3. Point the app at it (from Windows)

1. On the **Windows** PC, map the name to the Linux machine's address. Open Notepad as administrator, edit
   `C:\Windows\System32\drivers\etc\hosts` and add a line (for WSL 2 on the same PC, `127.0.0.1` works; otherwise use the VM's IP):
   ```
   127.0.0.1   dc1.corp.test
   ```
2. Check the ports from PowerShell: `Test-NetConnection dc1.corp.test -Port 636` -> `TcpTestSucceeded : True`.
3. Set the user secrets and follow **Part A** of [CONNECT-REAL-AD-AND-OKTA.md](CONNECT-REAL-AD-AND-OKTA.md), using:

   | Setting | Value |
   | --- | --- |
   | `ActiveDirectory:Domain` | `corp.test` |
   | `ActiveDirectory:Server` | `dc1.corp.test` |
   | `ActiveDirectory:BaseDn` | `DC=corp,DC=test` |
   | `ActiveDirectory:BindUsername` | `Administrator@corp.test` (or `svc-itdash-test@corp.test`, section 4) |
   | `ActiveDirectory:BindPassword` | the password from provisioning |
   | `ActiveDirectory:VerifyCertificate` | `false` (self-signed certificate; Development only) |

   Windows accepts the "skip certificate validation" setting. (On **Linux** hosts run the app with `LDAPTLS_REQCERT=never` instead.)
4. In **Settings > AD Integration**, allow `OU=Users,OU=ITDash-Test,DC=corp,DC=test`, the Computers OU and the test groups.

## 4. Two ways to give the app rights

* **Quick lab:** bind as `Administrator`. Every action works and the dry-run rights checks always pass. Good for trying the
  screens, but it does not show you what least privilege looks like.
* **Realistic:** bind as `svc-itdash-test`, which starts with no write rights. Use **Validate only** on any action: the dry run fails
  and names the attribute the account cannot write (for example `userAccountControl` or `unicodePwd`), and the real change is
  blocked with nothing written. Grant rights on the test OUs (Samba: `samba-tool dsacl set`, or Windows RSAT tools /
  *Delegation of Control Wizard* pointed at the Samba domain) and watch the checks turn green. The list of rights per action is in
  [AD-DELEGATION.md](AD-DELEGATION.md).

## Gotchas

| Symptom | Cause |
| --- | --- |
| `LDAP error 81` on bind | Port 636 not reachable, name not resolving, or (Linux only) certificate verification, see above |
| `Strong authentication is required` | You connected on 389 without TLS; keep `UseLdaps` true |
| `samba-tool` says `No module named 'ldb'` | A non-system `python3` is first on the PATH; run `/usr/bin/python3 /usr/bin/samba-tool ...` |
| Computer shows "Disabled" | Normal: Samba creates unjoined computer accounts disabled. Enable it from the app to test |
