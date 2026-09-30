namespace ServiceDashboard.Configuration;

public sealed class AppOptions
{
    public const string Section = "App";
    public string ProductName { get; set; } = "IT Administration Dashboard";
    public string Environment { get; set; } = "Development";
    public string DataDirectory { get; set; } = "";
    public string AssetDirectory { get; set; } = "";
    public string BackupDirectory { get; set; } = "";
    public int BackupRetentionCount { get; set; } = 7;
    /// <summary>Optional Okta group that is mapped to the Admins role on startup when no admin mapping exists yet.</summary>
    public string BootstrapAdminOktaGroup { get; set; } = "";
    public int DashboardCacheMinutes { get; set; } = 5;
    public int ExportRowLimit { get; set; } = 50_000;
    public int AuditRetentionDays { get; set; } = 365;
    public int LogoMaxBytes { get; set; } = 512 * 1024;
    public int SearchRateLimitPerMinute { get; set; } = 120;
    public int WriteRateLimitPerMinute { get; set; } = 60;
}

public sealed class OktaOptions
{
    public const string Section = "Okta";
    public string Issuer { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string GroupsClaim { get; set; } = "groups";
    public string[] Scopes { get; set; } = ["openid", "profile", "email", "groups"];
    public bool DevelopmentSignIn { get; set; }
}

public sealed class ActiveDirectoryOptions
{
    public const string Section = "ActiveDirectory";
    /// <summary>"Ldap" or "Fake".</summary>
    public string Provider { get; set; } = "Fake";
    public string Domain { get; set; } = "";
    public string Server { get; set; } = "";
    public int Port { get; set; } = 636;
    public bool UseLdaps { get; set; } = true;
    public string BaseDn { get; set; } = "";
    /// <summary>Must stay true in Production.</summary>
    public bool VerifyCertificate { get; set; } = true;
    /// <summary>Fake provider only: make every call fail as if the domain controller were unreachable.</summary>
    public bool SimulateServerUnavailable { get; set; }
}
