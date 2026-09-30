# Adding a module (Microsoft 365, Mimecast, Citrix, Okta...)

A module is **a folder plus one registration method**. There is no plugin framework. The core application does not change,
beyond registering the module, adding its permissions and adding its navigation entry.

The Active Directory module (`src/Backend/Modules/ActiveDirectory`) is the worked example. This guide uses **M365**
(`m365`) as the placeholder.

## 1. Pick the module id

Short, lowercase and fixed once released: `m365`, `mimecast`, `citrix`, `okta`. It becomes the route prefix
(`/api/modules/m365`), the key of the enabled/disabled setting and the audit `Module` value.

## 2. Create the folder

```
src/Backend/Modules/M365/
  Controllers/          controllers (routes under api/modules/m365)
  Services/             the module's logic
  Providers/            the client for the external system (behind an interface)
    Fake/               an in-memory version for development and tests
  M365Module.cs         the one registration method
```

Keep the same rule as AD: **controllers never touch the external SDK or HTTP client directly**. They call services; services
call one interface (for example `IM365Provider`) that has a real and a Fake implementation, chosen by one setting.
The architecture tests in `tests/Backend.Tests/SecurityTests.cs` show how to extend the "no provider types outside the
provider folders" check to a new module.

## 3. The registration method

```csharp
public static class M365Module
{
    public const string Id = "m365";

    public static IServiceCollection AddM365Module(this IServiceCollection services, IConfiguration config)
    {
        services.AddSingleton(new ModuleDescriptor(Id, "Microsoft 365", "Licences and mailboxes."));
        // provider chosen by one setting, like ActiveDirectory:Provider
        services.AddSingleton<IM365Provider, GraphM365Provider>();
        services.AddScoped<M365Service>();

        // optional: results in global search and cards on Home
        services.AddScoped<IModuleSearchProvider, M365SearchProvider>();
        services.AddScoped<IDashboardCardProvider, M365DashboardCards>();
        return services;
    }
}
```

Call it in `Program.cs` next to `AddActiveDirectoryModule`:

```csharp
builder.Services.AddM365Module(builder.Configuration);
```

The module now appears in **Settings > Modules** with a toggle (remove it from `ModuleCatalog.ComingSoon` in
`Services/ModuleCatalog.cs`, which is what makes it "Coming Soon" today).

## 4. Routes

All of a module's endpoints, including its settings and connection test, live under `/api/modules/m365`:

```csharp
[ApiController, ModuleGate(M365Module.Id)]          // a disabled module answers every route with the same "module disabled" error
[Route("api/modules/m365/licences")]
public sealed class M365LicencesController(M365Service svc) : ControllerBase
{
    [HttpGet, Authorize(Policy = Permissions.M365LicencesRead)]     // every endpoint names a permission
    public async Task<IActionResult> List(...) => Ok(await svc.ListAsync(...));
}
```

Core routes (`/api/auth`, `/api/search`, `/api/dashboard`, `/api/logs`, `/api/admin`, `/api/settings`) do **not** change and nothing
module-specific goes under them.

`SecurityTests.Every_API_endpoint_names_a_permission_policy_or_is_explicitly_whitelisted` and
`Every_protected_endpoint_rejects_anonymous_callers_and_users_without_the_permission` automatically cover the new
controllers: a new endpoint without a policy fails the build.

## 5. Permissions

1. Add the constants and descriptions in `src/Backend/Models/Permissions.cs` (`Permissions.M365LicencesRead`, ...) and add them to
   `Permissions.All`. Each permission is automatically an ASP.NET Core authorization policy.
2. The **Admins** role receives every permission at the next start. Grant them to other roles in **Users and Groups > Roles**.
3. Mirror the ids in `src/Frontend/Services/permissions.ts`.
4. If a page needs "any of several permissions", add a combined policy in `PermissionPolicies`.

## 6. Every change goes through the audited pipeline

If the module changes anything in the external system, model it on `AdChangeService`: check permission, validate input against
the Action Policies, **re-read** the target, apply allowlists, run a **dry run**, make the change, write the **audit** record
(`AuditEntry` with `Module = "m365"`), and return a result with the correlation ID. Add the new action keys to `ActionKeys`
so they appear in **Settings > Action Policies**.

## 7. Front end

* Navigation: add an entry in `src/Frontend/Layouts/Nav.tsx` (permission-gated; show the module tag from the shell's module list).
* Pages: add `src/Frontend/Pages/m365/...` and routes in `App.tsx`, wrapped in `<PageGuard page="m365.licences" requires={[...]}>`.
  Add the page key and its permissions to `PageAccess.Required` in `Controllers/AuthController.cs` so page views and denials
  reach the Application Access log.
* Settings page (optional): add a controller at `/api/modules/m365/settings` (read: `settings.read`, save: `settings.manage`,
  audited with before and after values) and add a section to `Pages/SettingsPage.tsx`, exactly like **AD Integration**.
  Use `useSettingsForm` for Save/Cancel, validation and the unsaved-changes warning.
* Search (optional): implement `IModuleSearchProvider`. Return only the categories the user may read, cap results, and escape
  any user input for the target system. Results are grouped by module and category automatically.
* Dashboard (optional): implement `IDashboardCardProvider`; cache expensive counts like `AdDashboardCards` does.

## 8. Checklist

- [ ] Module folder, `AddM365Module()` and the call in `Program.cs`
- [ ] Permissions added to `Permissions.cs` and `permissions.ts`
- [ ] Every controller action has `[Authorize(Policy = ...)]`, controllers have `[ModuleGate]`
- [ ] Fake provider for local development, one setting to switch
- [ ] Changes go through a shared pipeline and are audited; dry run supported
- [ ] Removed from `ModuleCatalog.ComingSoon`; navigation entry; optional Settings page and search results
- [ ] Tests: endpoints reject users without permission, allowlists are enforced on the backend, nothing secret in logs or audit
