using ServiceDashboard.Configuration;
using ServiceDashboard.Modules.ActiveDirectory.Providers;
using ServiceDashboard.Modules.ActiveDirectory.Providers.Fake;
using ServiceDashboard.Modules.ActiveDirectory.Providers.Ldap;
using ServiceDashboard.Modules.ActiveDirectory.Services;
using ServiceDashboard.Services;

namespace ServiceDashboard.Modules.ActiveDirectory;

/// <summary>
/// A module is a folder plus this one registration method. This is the only place that knows which provider
/// implementation is in use; everything else talks to IDirectoryProvider.
/// </summary>
public static class ActiveDirectoryModule
{
    public const string Id = "ad";

    public static IServiceCollection AddActiveDirectoryModule(this IServiceCollection services, IConfiguration config)
    {
        services.AddSingleton(new ModuleDescriptor(Id, "Active Directory", "Users, computers and groups in one Active Directory domain."));

        var provider = config[$"{ActiveDirectoryOptions.Section}:Provider"];
        if (string.Equals(provider, "Fake", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<FakeDirectoryProvider>();
            services.AddSingleton<IDirectoryProvider>(sp => sp.GetRequiredService<FakeDirectoryProvider>());
        }
        else
        {
            services.AddSingleton<IDirectoryProvider, LdapDirectoryProvider>();
        }

        services.AddScoped<AdSettingsService>();
        services.AddScoped<AdDirectoryService>();
        services.AddScoped<IModuleSearchProvider, AdSearchProvider>();
        services.AddScoped<IDashboardCardProvider, AdDashboardCards>();
        return services;
    }
}
