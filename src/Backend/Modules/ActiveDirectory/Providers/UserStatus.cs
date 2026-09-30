namespace ServiceDashboard.Modules.ActiveDirectory.Providers;

/// <summary>
/// Turns raw AD attribute values into the account and password status shown in the UI.
/// Shared by every provider so the Fake provider behaves exactly like real AD.
/// </summary>
public static class UserStatus
{
    public const int AccountDisable = 0x2;
    public const int Lockout = 0x10;
    public const int PasswdNotReqd = 0x20;
    public const int PasswdCantChange = 0x40;
    public const int EncryptedTextPwdAllowed = 0x80;
    public const int NormalAccount = 0x200;
    public const int DontExpirePassword = 0x10000;
    public const int SmartcardRequired = 0x40000;
    public const int TrustedForDelegation = 0x80000;
    public const int NotDelegated = 0x100000;
    public const int UseDesKeyOnly = 0x200000;
    public const int DontRequirePreauth = 0x400000;
    public const int PasswordExpired = 0x800000;
    public const int TrustedToAuthForDelegation = 0x1000000;

    /// <summary>Bits of msDS-User-Account-Control-Computed that reflect the live state.</summary>
    public const int ComputedLockout = 0x10;
    public const int ComputedPasswordExpired = 0x800000;

    public const long NeverFileTime = long.MaxValue;

    private static readonly (int Bit, string Name, string Meaning)[] Flags =
    [
        (AccountDisable, "ACCOUNTDISABLE", "The account is disabled."),
        (Lockout, "LOCKOUT", "The account is locked out."),
        (PasswdNotReqd, "PASSWD_NOTREQD", "No password is required for this account."),
        (PasswdCantChange, "PASSWD_CANT_CHANGE", "The user cannot change their own password."),
        (EncryptedTextPwdAllowed, "ENCRYPTED_TEXT_PWD_ALLOWED", "The password may be stored with reversible encryption."),
        (NormalAccount, "NORMAL_ACCOUNT", "A normal user account."),
        (DontExpirePassword, "DONT_EXPIRE_PASSWORD", "The password never expires."),
        (SmartcardRequired, "SMARTCARD_REQUIRED", "A smart card is required to sign in."),
        (TrustedForDelegation, "TRUSTED_FOR_DELEGATION", "Trusted for Kerberos delegation."),
        (NotDelegated, "NOT_DELEGATED", "The account cannot be delegated."),
        (UseDesKeyOnly, "USE_DES_KEY_ONLY", "Only DES encryption types are used for this account."),
        (DontRequirePreauth, "DONT_REQ_PREAUTH", "Kerberos pre-authentication is not required."),
        (PasswordExpired, "PASSWORD_EXPIRED", "The password has expired."),
        (TrustedToAuthForDelegation, "TRUSTED_TO_AUTH_FOR_DELEGATION", "Trusted to authenticate for delegation."),
    ];

    public static IReadOnlyList<UacFlagInfo> DescribeFlags(int uac) =>
        Flags.Where(f => (uac & f.Bit) != 0).Select(f => new UacFlagInfo { Name = f.Name, Meaning = f.Meaning }).ToList();

    public static bool IsEnabled(int uac) => (uac & AccountDisable) == 0;

    /// <summary>
    /// Locked only if the live computed bit says so. An old lockoutTime alone does not mean the account is
    /// still locked, because the lockout duration may have passed.
    /// </summary>
    public static bool IsLockedOut(int computedUac) => (computedUac & ComputedLockout) != 0;

    public static (string Kind, DateTime? Date) AccountExpiry(long accountExpiresFileTime, DateTime nowUtc)
    {
        if (accountExpiresFileTime is 0 or NeverFileTime) return ("Never", null);
        var date = FromFileTime(accountExpiresFileTime);
        if (date == null) return ("Never", null);
        return (date <= nowUtc ? "Expired" : "Expires", date);
    }

    /// <param name="pwdExpiryComputed">msDS-UserPasswordExpiryTimeComputed (already reflects fine-grained policies), 0 or null if unknown.</param>
    public static (string Status, DateTime? ExpiresUtc) Password(int uac, int computedUac, long pwdLastSet, long? pwdExpiryComputed, DateTime nowUtc)
    {
        if ((uac & DontExpirePassword) != 0) return ("NeverExpires", null);
        if (pwdLastSet == 0) return ("MustChange", null);
        if ((computedUac & ComputedPasswordExpired) != 0 || (uac & PasswordExpired) != 0) return ("Expired", FromFileTime(pwdExpiryComputed ?? 0));
        if (pwdExpiryComputed is null or 0) return ("Unknown", null);
        if (pwdExpiryComputed == NeverFileTime) return ("NeverExpires", null);
        var date = FromFileTime(pwdExpiryComputed.Value);
        if (date == null) return ("Unknown", null);
        return (date <= nowUtc ? "Expired" : "Expires", date);
    }

    public static DateTime? FromFileTime(long ft)
    {
        if (ft <= 0 || ft >= NeverFileTime) return null;
        try { return DateTime.FromFileTimeUtc(ft); } catch (ArgumentOutOfRangeException) { return null; }
    }

    public static long ToFileTime(DateTime utc) => utc.ToFileTimeUtc();

    /// <summary>The OU (or container) that holds an object: everything after the first RDN of its DN.</summary>
    public static string ParentDn(string dn)
    {
        var i = DnText.FirstUnescapedComma(dn);
        return i < 0 ? "" : dn[(i + 1)..];
    }
}

/// <summary>DN helpers shared by the providers and the allowlist checks.</summary>
public static partial class DnText
{
    public static int FirstUnescapedComma(string dn)
    {
        for (var i = 0; i < dn.Length; i++)
        {
            if (dn[i] == '\\') { i++; continue; }
            if (dn[i] == ',') return i;
        }
        return -1;
    }

    /// <summary>Splits a DN into its RDNs, honouring backslash escapes.</summary>
    public static List<string> SplitRdns(string dn)
    {
        var parts = new List<string>();
        var start = 0;
        for (var i = 0; i < dn.Length; i++)
        {
            if (dn[i] == '\\') { i++; continue; }
            if (dn[i] == ',') { parts.Add(dn[start..i].Trim()); start = i + 1; }
        }
        parts.Add(dn[start..].Trim());
        return parts;
    }

    /// <summary>Case-insensitive, whitespace-insensitive comparison of two DNs.</summary>
    public static bool Equal(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    public static string Normalize(string dn) => string.Join(",", SplitRdns(dn).Select(r =>
    {
        var eq = r.IndexOf('=');
        return eq < 0 ? r : r[..eq].Trim() + "=" + r[(eq + 1)..].Trim();
    }));

    /// <summary>True if <paramref name="dn"/> is the same as, or below, <paramref name="ancestor"/>.</summary>
    public static bool IsUnderOrEqual(string dn, string ancestor)
    {
        var a = SplitRdns(Normalize(dn));
        var b = SplitRdns(Normalize(ancestor));
        if (b.Count == 0 || b.Count > a.Count) return false;
        for (var i = 1; i <= b.Count; i++)
            if (!string.Equals(a[^i], b[^i], StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^([A-Za-z][A-Za-z0-9-]*|\d+(\.\d+)*)$")]
    private static partial System.Text.RegularExpressions.Regex AttributeType();


    /// <summary>
    /// RFC 4514 shape check: comma-separated RDNs, each "type=value" (multi-valued RDNs joined with an unescaped '+'),
    /// with backslash escapes as either a special character or two hex digits, and no unescaped specials.
    /// </summary>
    public static bool IsValidDn(string? dn)
    {
        if (string.IsNullOrWhiteSpace(dn) || dn.Length > 2048) return false;
        foreach (var rdn in DnText.SplitRdns(dn))
        {
            if (rdn.Length == 0) return false;
            foreach (var pair in SplitUnescaped(rdn, '+'))
            {
                var eq = pair.IndexOf('=');
                if (eq <= 0 || !AttributeType().IsMatch(pair[..eq].Trim())) return false;
                if (!IsValidValue(pair[(eq + 1)..])) return false;
            }
        }
        return true;
    }

    private static bool IsValidValue(string v)
    {
        if (v.Length == 0) return true; // empty values are legal in RFC 4514
        for (var i = 0; i < v.Length; i++)
        {
            var c = v[i];
            if (c == '\0' || c == '\n' || c == '\r') return false;
            if (c is '"' or '<' or '>' or ';') return false;
            if (c == '\\')
            {
                if (i + 1 >= v.Length) return false;
                var n = v[i + 1];
                if (Uri.IsHexDigit(n) && i + 2 < v.Length && Uri.IsHexDigit(v[i + 2])) { i += 2; continue; }
                if (n is ',' or '+' or '"' or '\\' or '<' or '>' or ';' or '=' or ' ' or '#') { i++; continue; }
                return false;
            }
        }
        return true;
    }

    private static IEnumerable<string> SplitUnescaped(string s, char sep)
    {
        var start = 0;
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\') { i++; continue; }
            if (s[i] == sep) { yield return s[start..i]; start = i + 1; }
        }
        yield return s[start..];
    }
}
