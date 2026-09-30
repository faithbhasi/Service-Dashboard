using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ServiceDashboard.Configuration;
using ServiceDashboard.Models;

namespace ServiceDashboard.Data;

public sealed class DatabaseSeeder(AppDbContext db, IOptions<AppOptions> app, IOptions<OktaOptions> okta, IHostEnvironment env, ILogger<DatabaseSeeder> log)
{
    public static readonly (string Name, string Display, string RoleName)[] DevUsers =
    [
        ("dev.admin", "Dev Admin", DefaultRoles.Admins),
        ("dev.auditor", "Dev Auditor", DefaultRoles.Auditors),
        ("dev.helpdesk", "Dev Helpdesk", "Helpdesk (sample)"),
        ("dev.user", "Dev User", DefaultRoles.Users),
        ("dev.noaccess", "Dev No Access", ""),
        ("dev.disabled", "Dev Disabled", DefaultRoles.Users),
    ];

    public async Task SeedAsync()
    {
        await db.Database.MigrateAsync();
        await EnsureDefaultRolesAsync();
        await BootstrapAdminGroupAsync();
        if (okta.Value.DevelopmentSignIn && env.IsDevelopment()) await SeedDevUsersAsync();
    }

    private async Task EnsureDefaultRolesAsync()
    {
        var roles = await db.Roles.Include(r => r.Permissions).ToListAsync();

        Role Get(Guid id, string name, string description)
        {
            var r = roles.FirstOrDefault(x => x.Id == id);
            if (r == null)
            {
                r = new Role { Id = id, Name = name, Description = description, IsSystem = true };
                db.Roles.Add(r);
                roles.Add(r);
                SetPermissions(r, id == DefaultRoles.AdminsId ? Permissions.AllIds
                    : id == DefaultRoles.AuditorsId ? DefaultRoles.AuditorPermissions : DefaultRoles.UserPermissions);
            }
            return r;
        }

        var admins = Get(DefaultRoles.AdminsId, DefaultRoles.Admins, "Everything, including access management and settings.");
        Get(DefaultRoles.AuditorsId, DefaultRoles.Auditors, "Read access to AD and full access to Activity and Logs. No changes.");
        Get(DefaultRoles.UsersId, DefaultRoles.Users, "Only the modules and actions assigned to them.");

        // Admins always holds every permission, including ones added by later versions.
        SetPermissions(admins, Permissions.AllIds);
        await db.SaveChangesAsync();
    }

    public static void SetPermissions(Role role, IEnumerable<string> permissions)
    {
        var wanted = permissions.ToHashSet();
        role.Permissions.RemoveAll(p => !wanted.Contains(p.Permission));
        foreach (var p in wanted.Where(w => role.Permissions.All(x => x.Permission != w)))
            role.Permissions.Add(new RolePermission { RoleId = role.Id, Permission = p });
    }

    /// <summary>
    /// The very first admin has to come from somewhere: if an Okta group is configured and nothing maps to Admins yet,
    /// map it. Everything after that is managed in the Users and Groups area.
    /// </summary>
    private async Task BootstrapAdminGroupAsync()
    {
        var group = app.Value.BootstrapAdminOktaGroup;
        if (string.IsNullOrWhiteSpace(group) || StartupValidator.IsPlaceholder(group)) return;
        if (await db.GroupMappings.AnyAsync(m => m.RoleId == DefaultRoles.AdminsId)) return;
        db.GroupMappings.Add(new GroupMapping { OktaGroup = group.Trim(), RoleId = DefaultRoles.AdminsId });
        await db.SaveChangesAsync();
        log.LogWarning("Mapped Okta group {Group} to the Admins role (bootstrap).", group);
    }

    private async Task SeedDevUsersAsync()
    {
        var helpdesk = await db.Roles.Include(r => r.Permissions).FirstOrDefaultAsync(r => r.Name == "Helpdesk (sample)");
        if (helpdesk == null)
        {
            helpdesk = new Role { Name = "Helpdesk (sample)", Description = "Sample custom role for local testing." };
            db.Roles.Add(helpdesk);
            SetPermissions(helpdesk,
            [
                Permissions.DashboardRead, Permissions.AdUsersRead, Permissions.AdUsersUnlock, Permissions.AdUsersResetPassword,
                Permissions.AdUsersGroupsAdd, Permissions.AdComputersRead, Permissions.AdGroupsRead, Permissions.LogsReadOwn,
            ]);
            await db.SaveChangesAsync();
        }

        var roleIds = await db.Roles.ToDictionaryAsync(r => r.Name, r => r.Id);
        foreach (var (name, display, roleName) in DevUsers)
        {
            var subject = "dev|" + name;
            if (await db.Users.AnyAsync(u => u.Subject == subject)) continue;
            var user = new AppUser
            {
                Subject = subject, DisplayName = display, Email = name + "@example.invalid",
                IsEnabled = name != "dev.disabled",
            };
            if (roleName != "") user.UserRoles.Add(new UserRole { RoleId = roleIds[roleName] });
            db.Users.Add(user);
        }
        await db.SaveChangesAsync();
    }
}
