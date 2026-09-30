namespace ServiceDashboard.Models;

public sealed class GeneralSettings
{
    public string ProductName { get; set; } = "";
    public string EnvironmentLabel { get; set; } = "";
    public string TimeZone { get; set; } = "UTC";
    public string DateFormat { get; set; } = "yyyy-MM-dd";
    public string SupportContact { get; set; } = "";
    public int IdleTimeoutMinutes { get; set; } = 30;
    public int AbsoluteTimeoutMinutes { get; set; } = 480;
    public BannerSettings Banner { get; set; } = new();
}

public sealed class BannerSettings
{
    public bool Enabled { get; set; }
    /// <summary>Information, Warning or Maintenance.</summary>
    public string Type { get; set; } = "Information";
    public string Text { get; set; } = "";
    /// <summary>Wall-clock time in the configured time zone, "yyyy-MM-ddTHH:mm".</summary>
    public string? StartLocal { get; set; }
    public string? EndLocal { get; set; }
}

public sealed class ModulesSettings
{
    /// <summary>Module id -> enabled. Modules not listed are enabled.</summary>
    public Dictionary<string, bool> Enabled { get; set; } = new();
}

public sealed class ActionPolicy
{
    public bool JustificationRequired { get; set; }
    public int JustificationMinLength { get; set; } = 10;
    public bool TicketRequired { get; set; }
    public string? TicketPattern { get; set; }
    public bool TypedConfirmationRequired { get; set; }
}

public sealed class ActionPoliciesSettings
{
    public Dictionary<string, ActionPolicy> Actions { get; set; } = ActionKeys.All.ToDictionary(k => k, DefaultFor);
    public bool MustChangePasswordDefault { get; set; } = true;
    public int GeneratedPasswordLength { get; set; } = 16;

    private static ActionPolicy DefaultFor(string key) => new()
    {
        JustificationRequired = true,
        JustificationMinLength = 10,
        TicketRequired = false,
        TypedConfirmationRequired = key is ActionKeys.DisableUser or ActionKeys.DisableComputer or ActionKeys.ResetPassword,
    };
}

public static class ActionKeys
{
    public const string ResetPassword = "resetPassword";
    public const string Unlock = "unlock";
    public const string EnableUser = "enableUser";
    public const string DisableUser = "disableUser";
    public const string MoveUser = "moveUser";
    public const string AddToGroups = "addToGroups";
    public const string RemoveFromGroups = "removeFromGroups";
    public const string EnableComputer = "enableComputer";
    public const string DisableComputer = "disableComputer";
    public const string MoveComputer = "moveComputer";

    public static readonly string[] All =
    [
        ResetPassword, Unlock, EnableUser, DisableUser, MoveUser, AddToGroups, RemoveFromGroups,
        EnableComputer, DisableComputer, MoveComputer,
    ];
}

public sealed class ThemeColors
{
    public string Primary { get; set; } = "#1f5fbf";
    public string TopBarBackground { get; set; } = "#ffffff";
    public string NavBackground { get; set; } = "#1b2433";
    public string NavText { get; set; } = "#e6ebf3";
    public string NavSelected { get; set; } = "#2f4570";
    public string PageBackground { get; set; } = "#f3f5f9";
    public string CardBackground { get; set; } = "#ffffff";
    public string SectionHeader { get; set; } = "#1b2433";
    public string Success { get; set; } = "#146c2e";
    public string Warning { get; set; } = "#8a5a00";
    public string Error { get; set; } = "#b3261e";

    public static ThemeColors DefaultLight() => new();

    public static ThemeColors DefaultDark() => new()
    {
        Primary = "#7aa7f5",
        TopBarBackground = "#161c28",
        NavBackground = "#0f141d",
        NavText = "#d5dbe6",
        NavSelected = "#26385a",
        PageBackground = "#0b0f16",
        CardBackground = "#171e2b",
        SectionHeader = "#e6ebf3",
        Success = "#5fd08a",
        Warning = "#e5b25d",
        Error = "#ff8a80",
    };
}

public sealed class BrandingSettings
{
    public ThemeColors Light { get; set; } = ThemeColors.DefaultLight();
    public ThemeColors Dark { get; set; } = ThemeColors.DefaultDark();
}
