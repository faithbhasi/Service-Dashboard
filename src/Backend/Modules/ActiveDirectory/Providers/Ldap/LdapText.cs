using System.Text;
using System.Text.RegularExpressions;

namespace ServiceDashboard.Modules.ActiveDirectory.Providers.Ldap;

/// <summary>Escaping and validation for text that ends up in LDAP filters and distinguished names.</summary>
public static partial class LdapText
{
    /// <summary>RFC 4515 filter value escaping: \ * ( ) and NUL. Every user-supplied search term goes through this.</summary>
    public static string EscapeFilterValue(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var sb = new StringBuilder(value.Length + 8);
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\\': sb.Append("\\5c"); break;
                case '*': sb.Append("\\2a"); break;
                case '(': sb.Append("\\28"); break;
                case ')': sb.Append("\\29"); break;
                case '\0': sb.Append("\\00"); break;
                default: sb.Append(ch); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// The binary SID of a domain object: the domain SID with one more sub-authority (the RID) appended.
    /// Done by hand because System.Security.Principal.SecurityIdentifier only works on Windows.
    /// </summary>
    public static byte[] AppendRid(byte[] domainSid, int rid)
    {
        if (domainSid.Length < 8 || domainSid[1] >= 15) throw new ArgumentException("Not a domain SID.", nameof(domainSid));
        var result = new byte[domainSid.Length + 4];
        Array.Copy(domainSid, result, domainSid.Length);
        result[1]++; // one more sub-authority
        BitConverter.GetBytes(rid).CopyTo(result, domainSid.Length); // little-endian on every supported platform
        return result;
    }

    /// <summary>Binary GUID as an LDAP filter value: \xx per byte.</summary>
    public static string EscapeGuid(Guid id) => EscapeBytes(id.ToByteArray());

    public static string EscapeBytes(byte[] bytes) => string.Concat(bytes.Select(b => "\\" + b.ToString("x2")));

    /// <summary>RFC 4514 DN attribute value escaping.</summary>
    public static string EscapeDnValue(string value)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c is ',' or '+' or '"' or '\\' or '<' or '>' or ';' or '=' || (i == 0 && (c == '#' || c == ' ')) || (i == value.Length - 1 && c == ' '))
                sb.Append('\\');
            if (c == '\0') { sb.Append("\\00"); continue; }
            sb.Append(c);
        }
        return sb.ToString();
    }

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9-]{0,63}$")]
    private static partial Regex AttributeName();

    /// <summary>Only plain attribute names may be used from settings, so a setting can never inject filter syntax.</summary>
    public static bool IsSafeAttributeName(string? name) => name != null && AttributeName().IsMatch(name);

    public static bool IsValidDn(string? dn) => DnText.IsValidDn(dn);
}

/// <summary>Builds LDAP filters. Every piece of user input is escaped with <see cref="LdapText.EscapeFilterValue"/>.</summary>
public static class LdapFilters
{
    public const string UserBase = "(&(objectCategory=person)(objectClass=user))";
    public const string ComputerBase = "(objectCategory=computer)";
    public const string GroupBase = "(objectCategory=group)";
    public const string InChain = "1.2.840.113556.1.4.1941";
    public const string BitAnd = "1.2.840.113556.1.4.803";

    public static string Users(string? text, UserFilter filter, string employeeIdAttribute, DateTime nowUtc)
    {
        var parts = new List<string> { UserBase };
        var t = LdapText.EscapeFilterValue(text?.Trim());
        if (t.Length > 0)
        {
            var emp = LdapText.IsSafeAttributeName(employeeIdAttribute) ? employeeIdAttribute : "employeeID";
            parts.Add($"(|(sAMAccountName=*{t}*)(userPrincipalName=*{t}*)(displayName=*{t}*)(givenName=*{t}*)(sn=*{t}*)(mail=*{t}*)({emp}=*{t}*))");
        }
        switch (filter)
        {
            case UserFilter.Disabled: parts.Add($"(userAccountControl:{BitAnd}:=2)"); break;
            case UserFilter.Enabled: parts.Add($"(!(userAccountControl:{BitAnd}:=2))"); break;
            case UserFilter.Locked: parts.Add("(lockoutTime>=1)"); break; // confirmed afterwards with msDS-User-Account-Control-Computed
            case UserFilter.AccountExpired: parts.Add($"(&(accountExpires>=1)(accountExpires<={nowUtc.ToFileTimeUtc()}))"); break;
        }
        return "(&" + string.Concat(parts) + ")";
    }

    public static string Computers(string? text, ComputerFilter filter)
    {
        var parts = new List<string> { ComputerBase };
        var t = LdapText.EscapeFilterValue(text?.Trim());
        if (t.Length > 0) parts.Add($"(|(cn=*{t}*)(dNSHostName=*{t}*))");
        if (filter == ComputerFilter.Disabled) parts.Add($"(userAccountControl:{BitAnd}:=2)");
        if (filter == ComputerFilter.Enabled) parts.Add($"(!(userAccountControl:{BitAnd}:=2))");
        return "(&" + string.Concat(parts) + ")";
    }

    public static string Groups(string? text)
    {
        var t = LdapText.EscapeFilterValue(text?.Trim());
        return t.Length == 0 ? GroupBase : $"(&{GroupBase}(|(cn=*{t}*)(description=*{t}*)))";
    }

    /// <summary>Direct members of a group that match the text and kind, evaluated by the directory server.</summary>
    public static string Members(string groupDn, string? text, MemberKind? kind)
    {
        var kindFilter = kind switch
        {
            MemberKind.User => UserBase,
            MemberKind.Computer => ComputerBase,
            MemberKind.Group => GroupBase,
            _ => $"(|{UserBase}{ComputerBase}{GroupBase})",
        };
        var t = LdapText.EscapeFilterValue(text?.Trim());
        var textFilter = t.Length == 0 ? "" : $"(|(cn=*{t}*)(sAMAccountName=*{t}*)(mail=*{t}*)(displayName=*{t}*))";
        return $"(&(memberOf={LdapText.EscapeFilterValue(groupDn)}){kindFilter}{textFilter})";
    }

    public static string ByGuid(Guid id) => $"(objectGUID={LdapText.EscapeGuid(id)})";
    public static string NestedGroupsOf(string dn) => $"(&{GroupBase}(member:{InChain}:={LdapText.EscapeFilterValue(dn)}))";
}
