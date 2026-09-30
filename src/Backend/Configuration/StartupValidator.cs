using System.Text.RegularExpressions;

namespace ServiceDashboard.Configuration;

public static partial class StartupValidator
{
    [GeneratedRegex("^<[A-Za-z0-9_]+>$")]
    private static partial Regex PlaceholderRegex();

    public static bool IsPlaceholder(string? s) => s != null && PlaceholderRegex().IsMatch(s.Trim());

    /// <summary>Returns everything that must be fixed before the application may start.</summary>
    public static List<string> Validate(IConfiguration config, bool isDevelopment, bool isProduction)
    {
        var errors = new List<string>();
        var okta = config.GetSection(OktaOptions.Section).Get<OktaOptions>() ?? new();
        var ad = config.GetSection(ActiveDirectoryOptions.Section).Get<ActiveDirectoryOptions>() ?? new();
        var app = config.GetSection(AppOptions.Section).Get<AppOptions>() ?? new();

        if (okta.DevelopmentSignIn && !isDevelopment)
            errors.Add("Okta:DevelopmentSignIn is enabled but the environment is not Development. Development sign-in is for local testing only.");

        if (!isDevelopment && (!string.IsNullOrEmpty(ad.BindUsername) || !string.IsNullOrEmpty(ad.BindPassword)))
            errors.Add("ActiveDirectory:BindUsername/BindPassword are for local testing only. Outside Development the application binds as its own identity (the gMSA) and no AD password may be configured.");

        if (ad.Provider is not ("Ldap" or "Fake"))
            errors.Add($"ActiveDirectory:Provider must be 'Ldap' or 'Fake' (was '{ad.Provider}').");

        if (isProduction)
        {
            if (ad.Provider == "Fake") errors.Add("ActiveDirectory:Provider is 'Fake' in Production. Use 'Ldap'.");
            if (!ad.VerifyCertificate) errors.Add("ActiveDirectory:VerifyCertificate is false in Production. LDAP certificate checks must stay on.");
            if (ad.Provider == "Ldap" && !ad.UseLdaps) errors.Add("ActiveDirectory:UseLdaps is false in Production. LDAP must use LDAPS.");
            if (ad.Provider == "Ldap")
                Require(errors, "ActiveDirectory:Domain", ad.Domain, "ActiveDirectory:Server", ad.Server, "ActiveDirectory:BaseDn", ad.BaseDn);
            if (!okta.DevelopmentSignIn)
                Require(errors, "Okta:Issuer", okta.Issuer, "Okta:ClientId", okta.ClientId, "Okta:ClientSecret", okta.ClientSecret);
            Require(errors, "App:DataDirectory", app.DataDirectory, "App:AssetDirectory", app.AssetDirectory,
                "Serilog:LogDirectory", config["Serilog:LogDirectory"]);
        }
        return errors;
    }

    private static void Require(List<string> errors, params string?[] nameValuePairs)
    {
        for (var i = 0; i < nameValuePairs.Length; i += 2)
        {
            var name = nameValuePairs[i]!;
            var value = nameValuePairs[i + 1];
            if (string.IsNullOrWhiteSpace(value) || IsPlaceholder(value))
                errors.Add($"{name} is not set (still empty or a <PLACEHOLDER>).");
        }
    }
}
