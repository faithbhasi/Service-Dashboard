using System.Runtime.InteropServices;
using System.Security;
using System.Text.RegularExpressions;
using ServiceDashboard.Models;
using ServiceDashboard.Modules.ActiveDirectory.Providers;
using ServiceDashboard.Services;

namespace ServiceDashboard.Modules.ActiveDirectory.Services;

/// <summary>What every AD change request carries. Justification and ticket are checked against the Action Policies.</summary>
public class ChangeInput
{
    public string? Justification { get; set; }
    public string? TicketNumber { get; set; }
    /// <summary>The account or computer name typed by the user, when the action policy asks for typed confirmation.</summary>
    public string? TypedConfirmation { get; set; }
    /// <summary>Run the dry run only ("Validate only"): find the target, check rights, return the intended change, write nothing.</summary>
    public bool ValidateOnly { get; set; }
}

public sealed class ResetPasswordRequest : ChangeInput
{
    public string? NewPassword { get; set; }
    public bool MustChangeAtNextSignIn { get; set; }
    public bool UnlockAccount { get; set; }
    // A record-style ToString would print the password if this object were ever logged.
    public override string ToString() => "ResetPasswordRequest";
}

public sealed class MoveRequest : ChangeInput { public string? TargetOu { get; set; } }
public sealed class GroupsRequest : ChangeInput { public Guid[]? GroupIds { get; set; } }
/// <summary>Adds or removes several users from one group (the Groups page), as opposed to one user from several groups.</summary>
public sealed class GroupMembersRequest : ChangeInput { public Guid[]? UserIds { get; set; } }

public sealed record ChangeResult(
    string Status, string Message, string CorrelationId, string Action, string Target, string? ErrorCode,
    IReadOnlyList<DirectoryChange> Changes, IReadOnlyList<DryRunCheck> Checks, bool DryRun, Guid? GroupId = null, string? GroupName = null)
{
    public const string Success = "Success", NoChange = "NoChange", Failed = "Failed", Denied = "Denied", Validated = "Validated";
    public bool IsOk => Status is Success or NoChange or Validated;
}

/// <summary>The object being changed, freshly read from the directory for this request.</summary>
public sealed record ChangeTarget(Guid Id, DirectoryObjectKind Kind, string Label, string Ou, DirectoryUser? User, DirectoryComputer? Computer, AdSettings Settings);

public sealed class ChangeSpec
{
    public required string Action { get; init; }
    public required string PolicyKey { get; init; }
    public required string Permission { get; init; }
    public required Guid TargetId { get; init; }
    public required DirectoryObjectKind Kind { get; init; }
    public required ChangeInput Input { get; init; }
    /// <summary>Allowlist and protected-object rules, applied to the fresh data. Returns the reason for a denial, or null.</summary>
    public required Func<ChangeTarget, Task<string?>> Check { get; init; }
    /// <summary>The intended change (current value to new value) worked out from the fresh state. Null = nothing to change.</summary>
    public required Func<ChangeTarget, IReadOnlyList<DirectoryChange>?> Describe { get; init; }
    public string? NoChangeMessage { get; init; }
    public required Func<bool, Task<DirectoryResult>> Apply { get; init; }
    public Guid? GroupId { get; init; }
    public string? GroupName { get; init; }
}

/// <summary>
/// The one place where every AD change happens (specification section 12):
/// permission, input checks, fresh re-read, allowlists, dry run, the change itself, audit, and a result with a correlation ID.
/// </summary>
public sealed class AdChangeService(
    IDirectoryProvider provider, AdSettingsService adSettings, SettingsService settings, ICurrentUser currentUser,
    IAuditService audit, IHttpContextAccessor http)
{
    private const string Module = ActiveDirectoryModule.Id;
    private string Correlation => http.HttpContext?.TraceIdentifier ?? "";

    // ================================================================== the shared pipeline

    public async Task<ChangeResult> RunAsync(ChangeSpec spec, CancellationToken ct)
    {
        var user = await currentUser.GetAsync() ?? throw new ApiException(401, "Not signed in", "Sign in to continue.", "unauthenticated");
        var input = spec.Input;
        var label = spec.Kind.ToString().ToLowerInvariant();
        var targetText = spec.TargetId.ToString();

        // 1. Permission (the endpoint policy already checked; this is the defence in depth for every caller of the service).
        if (!user.Has(spec.Permission))
            return await Denied(spec, targetText, "You do not have permission to do this.", input);

        // 2. Validate the input against the Action Policies.
        var policies = await settings.GetActionPoliciesAsync();
        var policy = policies.Actions.TryGetValue(spec.PolicyKey, out var p) ? p : new ActionPolicy();
        ValidateInput(policy, input);

        // 3. Re-read the object's current state from AD.
        var s = await adSettings.GetAsync();
        ChangeTarget? target;
        try { target = await ReadTargetAsync(spec, s, ct); }
        catch (ModuleUnavailableException) { await Record(spec, targetText, AuditResult.Failure, "The directory could not be reached", null, input); throw; }
        if (target == null)
            return await Finish(spec, targetText, ChangeResult.Failed, DirectoryErrors.NotFound, "The object no longer exists in the directory.", [], [], false, AuditResult.Failure, input, null);
        targetText = target.Label;

        if (policy.TypedConfirmationRequired && !input.ValidateOnly)
        {
            var expected = target.User?.SamAccountName ?? target.Computer?.Name ?? "";
            if (!string.Equals(input.TypedConfirmation?.Trim(), expected, StringComparison.OrdinalIgnoreCase))
                throw new ApiException(400, "Confirmation does not match", $"Type {expected} exactly to confirm.", "validation");
        }

        // 4. Allowlists and protected objects, against the fresh data.
        var denial = await spec.Check(target);
        if (denial != null) return await Denied(spec, targetText, denial, input);

        var changes = spec.Describe(target);
        if (changes == null)
            return await Finish(spec, targetText, ChangeResult.NoChange, null, spec.NoChangeMessage ?? "Already in the requested state. No change was made.",
                [], [], false, AuditResult.Success, input, "No change needed");

        // 5. Dry run through the provider. If it fails, stop.
        DirectoryResult dry;
        try { dry = await spec.Apply(true); }
        catch (ModuleUnavailableException) { await Record(spec, targetText, AuditResult.Failure, "The directory could not be reached", changes, input); throw; }

        if (!dry.Success)
            return await Finish(spec, targetText, ChangeResult.Failed, dry.ErrorCode, "Validation failed: " + dry.Message, changes, dry.Checks, true, AuditResult.Failure, input, dry.Message);

        // Every dry run is recorded, whether it was requested ("Validate only", the confirmation preview) or automatic.
        await Record(spec, targetText, AuditResult.Validated, null, changes, input);
        if (input.ValidateOnly)
            return Result(spec, targetText, ChangeResult.Validated, null, "Validated. No change was made.", changes, dry.Checks, true);

        // 6. The change itself.
        DirectoryResult done;
        try { done = await spec.Apply(false); }
        catch (ModuleUnavailableException) { await Record(spec, targetText, AuditResult.Failure, "The directory could not be reached", changes, input); throw; }

        // 7 + 8. Audit the outcome and return it with the correlation ID.
        return done.Success
            ? await Finish(spec, targetText, ChangeResult.Success, null, "The change was made.", changes, dry.Checks, false, AuditResult.Success, input, null)
            : await Finish(spec, targetText, ChangeResult.Failed, done.ErrorCode, done.Message ?? "The directory refused the change.", changes, dry.Checks, false, AuditResult.Failure, input, done.Message);
    }

    private async Task<ChangeTarget?> ReadTargetAsync(ChangeSpec spec, AdSettings s, CancellationToken ct)
    {
        var o = s.ReadOptions;
        if (spec.Kind == DirectoryObjectKind.User)
        {
            var u = await provider.GetUserAsync(spec.TargetId, o, ct);
            return u == null ? null : new ChangeTarget(u.Id, spec.Kind, $"{u.SamAccountName} ({u.DisplayName ?? u.SamAccountName})", u.Ou, u, null, s);
        }
        var c = await provider.GetComputerAsync(spec.TargetId, o, ct);
        return c == null ? null : new ChangeTarget(c.Id, spec.Kind, c.Name, c.Ou, null, c, s);
    }

    private static void ValidateInput(ActionPolicy policy, ChangeInput input)
    {
        var justification = input.Justification?.Trim() ?? "";
        if (policy.JustificationRequired && justification.Length < Math.Max(1, policy.JustificationMinLength))
            throw new ApiException(400, "Justification required", $"Enter a justification of at least {Math.Max(1, policy.JustificationMinLength)} characters.", "validation");
        if (justification.Length > 2000) throw new ApiException(400, "Justification too long", "The justification can be up to 2000 characters.", "validation");

        var ticket = input.TicketNumber?.Trim() ?? "";
        if (policy.TicketRequired && ticket.Length == 0)
            throw new ApiException(400, "Ticket number required", "Enter the ticket number for this change.", "validation");
        if (ticket.Length > 100) throw new ApiException(400, "Ticket number too long", "The ticket number can be up to 100 characters.", "validation");
        if (ticket.Length > 0 && !string.IsNullOrWhiteSpace(policy.TicketPattern))
        {
            bool ok;
            try { ok = Regex.IsMatch(ticket, policy.TicketPattern, RegexOptions.None, TimeSpan.FromMilliseconds(250)); }
            catch (RegexMatchTimeoutException) { ok = false; }
            catch (ArgumentException) { ok = true; } // a broken pattern in Settings must not lock everyone out; it is validated on save
            if (!ok) throw new ApiException(400, "Ticket number format", "The ticket number does not match the required format.", "validation");
        }
    }

    // ---------- outcome helpers ----------

    private ChangeResult Result(ChangeSpec spec, string target, string status, string? code, string message,
        IReadOnlyList<DirectoryChange> changes, IReadOnlyList<DryRunCheck> checks, bool dryRun) =>
        new(status, message, Correlation, spec.Action, target, code, changes, checks, dryRun, spec.GroupId, spec.GroupName);

    private async Task<ChangeResult> Denied(ChangeSpec spec, string target, string reason, ChangeInput input)
    {
        await Record(spec, target, AuditResult.Denied, reason, null, input);
        return Result(spec, target, ChangeResult.Denied, "denied", reason, [], [], false);
    }

    private async Task<ChangeResult> Finish(ChangeSpec spec, string target, string status, string? code, string message,
        IReadOnlyList<DirectoryChange> changes, IReadOnlyList<DryRunCheck> checks, bool dryRun, string auditResult, ChangeInput input, string? auditError)
    {
        await Record(spec, target, auditResult, auditError, changes, input);
        return Result(spec, target, status, code, message, changes, checks, dryRun);
    }

    private Task Record(ChangeSpec spec, string target, string result, string? error, IReadOnlyList<DirectoryChange>? changes, ChangeInput input) =>
        audit.WriteAsync(new AuditEntry
        {
            Action = spec.Action, Module = Module,
            Target = spec.GroupName == null ? target : $"{target} / group {spec.GroupName}",
            TargetId = spec.TargetId.ToString(),
            PreviousValue = changes is { Count: > 0 } ? string.Join("; ", changes.Select(c => $"{c.Field}: {c.From ?? "-"}")) : null,
            NewValue = changes is { Count: > 0 } ? string.Join("; ", changes.Select(c => $"{c.Field}: {c.To ?? "-"}")) : null,
            Result = result, Error = error,
            Justification = input.Justification?.Trim(), TicketNumber = input.TicketNumber?.Trim(),
        });

    // ================================================================== the actions

    private static string? ProtectedAccount(ChangeTarget t) =>
        t.User is { AdminCount: true } ? "This account is marked as protected (adminCount=1) and cannot be changed in this application." : null;

    private string? OuCheck(ChangeTarget t)
    {
        var allow = AdProtection.AllowlistFor(t.Kind, t.Settings);
        return AdProtection.OuUseReason(t.Ou, allow, t.Settings, provider.BaseDn);
    }

    private Task<string?> UserOuCheck(ChangeTarget t) => Task.FromResult(ProtectedAccount(t) ?? OuCheck(t));

    public Task<ChangeResult> ResetPasswordAsync(Guid userId, ResetPasswordRequest req, CancellationToken ct)
    {
        // Take the password out of the request object straight away; only a SecureString travels on.
        SecureString? secure = null;
        if (!req.ValidateOnly)
        {
            if (string.IsNullOrEmpty(req.NewPassword)) throw new ApiException(400, "Password required", "Enter the new password.", "validation");
            if (req.NewPassword.Length > 256) throw new ApiException(400, "Password too long", "The password is too long.", "validation");
            secure = new SecureString();
            foreach (var ch in req.NewPassword) secure.AppendChar(ch);
            secure.MakeReadOnly();
        }
        req.NewPassword = null;

        var options = new ResetPasswordOptions(req.MustChangeAtNextSignIn, req.UnlockAccount);
        return RunWithSecureAsync(secure, () => RunAsync(new ChangeSpec
        {
            Action = "ad.user.resetPassword", PolicyKey = ActionKeys.ResetPassword, Permission = Permissions.AdUsersResetPassword,
            TargetId = userId, Kind = DirectoryObjectKind.User, Input = req,
            Check = async t =>
            {
                // "Also unlock the account" is an unlock, so it needs the unlock right as well as the reset right.
                if (options.UnlockAccount && await currentUser.GetAsync() is { } who && !who.Has(Permissions.AdUsersUnlock))
                    return "Unlocking the account as part of a password reset needs the unlock permission, which you do not have.";
                return await UserOuCheck(t);
            },
            Describe = t =>
            [
                new DirectoryChange("Password", "Current password (not shown)", "New password (not shown)"),
                new DirectoryChange("Must change password at next sign-in", t.User!.PasswordStatus == "MustChange" ? "Yes" : "No", options.MustChangeAtNextSignIn ? "Yes" : "No"),
                new DirectoryChange("Account unlock", t.User.LockedOut ? "Locked" : "Not locked",
                    !t.User.LockedOut ? "Not locked" : options.UnlockAccount ? "Unlocked" : "Stays locked"),
            ],
            // A dry run never sends a password to AD: only the target and the right to reset are checked.
            Apply = dry => provider.ResetPasswordAsync(userId, secure ?? new SecureString(), options, dry, ct),
        }, ct));
    }

    private static async Task<ChangeResult> RunWithSecureAsync(SecureString? secure, Func<Task<ChangeResult>> run)
    {
        try { return await run(); }
        finally { secure?.Dispose(); }
    }

    public Task<ChangeResult> UnlockAsync(Guid userId, ChangeInput req, CancellationToken ct) => RunAsync(new ChangeSpec
    {
        Action = "ad.user.unlock", PolicyKey = ActionKeys.Unlock, Permission = Permissions.AdUsersUnlock,
        TargetId = userId, Kind = DirectoryObjectKind.User, Input = req, Check = UserOuCheck,
        // Re-checked against the fresh read: if the lockout has already passed, nothing is changed.
        Describe = t => t.User!.LockedOut ? [new DirectoryChange("Locked out", "Yes", "No")] : null,
        NoChangeMessage = "The account is no longer locked. No change was made.",
        Apply = dry => provider.UnlockAsync(userId, dry, ct),
    }, ct);

    public Task<ChangeResult> SetUserEnabledAsync(Guid userId, bool enable, ChangeInput req, CancellationToken ct) => RunAsync(new ChangeSpec
    {
        Action = enable ? "ad.user.enable" : "ad.user.disable",
        PolicyKey = enable ? ActionKeys.EnableUser : ActionKeys.DisableUser,
        Permission = enable ? Permissions.AdUsersEnable : Permissions.AdUsersDisable,
        TargetId = userId, Kind = DirectoryObjectKind.User, Input = req, Check = UserOuCheck,
        Describe = t => t.User!.Enabled == enable ? null : [new DirectoryChange("Account", t.User.Enabled ? "Enabled" : "Disabled", enable ? "Enabled" : "Disabled")],
        NoChangeMessage = enable ? "The account is already enabled. No change was made." : "The account is already disabled. No change was made.",
        Apply = dry => provider.SetEnabledAsync(userId, DirectoryObjectKind.User, enable, dry, ct),
    }, ct);

    public Task<ChangeResult> SetComputerEnabledAsync(Guid computerId, bool enable, ChangeInput req, CancellationToken ct) => RunAsync(new ChangeSpec
    {
        Action = enable ? "ad.computer.enable" : "ad.computer.disable",
        PolicyKey = enable ? ActionKeys.EnableComputer : ActionKeys.DisableComputer,
        Permission = enable ? Permissions.AdComputersEnable : Permissions.AdComputersDisable,
        TargetId = computerId, Kind = DirectoryObjectKind.Computer, Input = req, Check = t => Task.FromResult(OuCheck(t)),
        Describe = t => t.Computer!.Enabled == enable ? null : [new DirectoryChange("Computer account", t.Computer.Enabled ? "Enabled" : "Disabled", enable ? "Enabled" : "Disabled")],
        NoChangeMessage = enable ? "The computer is already enabled. No change was made." : "The computer is already disabled. No change was made.",
        Apply = dry => provider.SetEnabledAsync(computerId, DirectoryObjectKind.Computer, enable, dry, ct),
    }, ct);

    public Task<ChangeResult> MoveAsync(Guid id, DirectoryObjectKind kind, MoveRequest req, CancellationToken ct)
    {
        var target = (req.TargetOu ?? "").Trim();
        if (!DnText.IsValidDn(target) || !DnText.IsUnderOrEqual(target, provider.BaseDn))
            throw new ApiException(400, "Invalid target OU", "Choose a target OU from the tree.", "validation");
        var user = kind == DirectoryObjectKind.User;

        return RunAsync(new ChangeSpec
        {
            Action = user ? "ad.user.move" : "ad.computer.move",
            PolicyKey = user ? ActionKeys.MoveUser : ActionKeys.MoveComputer,
            Permission = user ? Permissions.AdUsersMove : Permissions.AdComputersMove,
            TargetId = id, Kind = kind, Input = req,
            Check = async t =>
            {
                if (ProtectedAccount(t) is { } prot) return prot;
                var allow = AdProtection.AllowlistFor(kind, t.Settings).ToList();
                // Both the current OU and the target OU must be allowed and not blocked.
                if (AdProtection.OuUseReason(t.Ou, allow, t.Settings, provider.BaseDn) is { } cur) return "Current location: " + cur;
                if (AdProtection.OuUseReason(target, allow, t.Settings, provider.BaseDn) is { } tgt) return "Target: " + tgt;
                if (await provider.GetOuAsync(target, ct) == null) return "The target OU does not exist.";
                return null;
            },
            Describe = t => DnText.Equal(t.Ou, target) ? null : [new DirectoryChange("OU", t.Ou, target)],
            NoChangeMessage = "The object is already in that OU. No change was made.",
            Apply = dry => provider.MoveAsync(id, kind, target, dry, ct),
        }, ct);
    }

    /// <summary>Adds or removes the user from each group in turn. Every group goes through the full pipeline and is reported separately.</summary>
    public async Task<IReadOnlyList<ChangeResult>> ChangeGroupsAsync(Guid userId, bool add, GroupsRequest req, CancellationToken ct)
    {
        var ids = (req.GroupIds ?? []).Distinct().ToList();
        if (ids.Count == 0) throw new ApiException(400, "No groups selected", "Select at least one group.", "validation");
        if (ids.Count > 50) throw new ApiException(400, "Too many groups", "Select up to 50 groups at a time.", "validation");

        var results = new List<ChangeResult>();
        foreach (var gid in ids)
        {
            var group = await provider.GetGroupAsync(gid, ct);
            var name = group?.Name ?? gid.ToString();
            results.Add(await RunAsync(new ChangeSpec
            {
                Action = add ? "ad.user.groups.add" : "ad.user.groups.remove",
                PolicyKey = add ? ActionKeys.AddToGroups : ActionKeys.RemoveFromGroups,
                Permission = add ? Permissions.AdUsersGroupsAdd : Permissions.AdUsersGroupsRemove,
                TargetId = userId, Kind = DirectoryObjectKind.User, Input = req, GroupId = gid, GroupName = name,
                Check = async t =>
                {
                    if (ProtectedAccount(t) is { } prot) return prot;
                    if (OuCheck(t) is { } ou) return ou;
                    // Re-read the group too: protection and the allowlist are checked on fresh data, never on what the browser sent.
                    var fresh = await provider.GetGroupAsync(gid, ct);
                    if (fresh == null) return "The group no longer exists.";
                    if (AdProtection.GroupBlockReason(fresh, t.Settings) is { } g) return g;
                    if (!add)
                    {
                        var m = await provider.GetMembershipsAsync(userId, DirectoryObjectKind.User, ct);
                        if (m?.Primary != null && m.Primary.Id == gid) return "The primary group cannot be removed.";
                    }
                    return null;
                },
                Describe = t => [new DirectoryChange("Group membership: " + name, add ? "Not a member" : "Member", add ? "Member" : "Not a member")],
                Apply = dry => add ? provider.AddToGroupsAsync(userId, [gid], dry, ct) : provider.RemoveFromGroupsAsync(userId, [gid], dry, ct),
            }, ct));
        }
        return results;
    }

    /// <summary>
    /// Adds or removes several users from one group. Each user goes through exactly the same pipeline as from the user's own
    /// Groups tab (permission, Action Policy, fresh re-read, allowlists, protected objects, dry run, audit) and is reported separately.
    /// </summary>
    public async Task<IReadOnlyList<ChangeResult>> ChangeGroupMembersAsync(Guid groupId, bool add, GroupMembersRequest req, CancellationToken ct)
    {
        var ids = (req.UserIds ?? []).Distinct().ToList();
        if (ids.Count == 0) throw new ApiException(400, "No users selected", "Select at least one user.", "validation");
        if (ids.Count > 50) throw new ApiException(400, "Too many users", "Select up to 50 users at a time.", "validation");

        var group = await provider.GetGroupAsync(groupId, ct) ?? throw new ApiException(404, "Not found", "Group not found in the directory.", "not_found");
        // From the Groups page the thing being changed is the group, so the typed confirmation is the group's name (checked once here).
        var policies = await settings.GetActionPoliciesAsync();
        var key = add ? ActionKeys.AddToGroups : ActionKeys.RemoveFromGroups;
        if (policies.Actions.TryGetValue(key, out var policy) && policy.TypedConfirmationRequired && !req.ValidateOnly
            && !string.Equals(req.TypedConfirmation?.Trim(), group.Name, StringComparison.OrdinalIgnoreCase))
            throw new ApiException(400, "Confirmation does not match", $"Type {group.Name} exactly to confirm.", "validation");

        var results = new List<ChangeResult>();
        foreach (var userId in ids)
        {
            var user = await provider.GetUserAsync(userId, (await adSettings.GetAsync()).ReadOptions, ct);
            var perUser = new GroupsRequest
            {
                GroupIds = [groupId], Justification = req.Justification, TicketNumber = req.TicketNumber, ValidateOnly = req.ValidateOnly,
                TypedConfirmation = user?.SamAccountName, // already confirmed against the group name above
            };
            results.AddRange(await ChangeGroupsAsync(userId, add, perUser, ct));
        }
        return results;
    }

    /// <summary>The groups a user could be added to: the manageable allowlist, minus protected groups, flagged if already a member.</summary>
    public async Task<IReadOnlyList<object>> AddableGroupsAsync(Guid userId, string? q, CancellationToken ct)
    {
        var s = await adSettings.GetAsync();
        var memberships = await provider.GetMembershipsAsync(userId, DirectoryObjectKind.User, ct)
            ?? throw new ApiException(404, "Not found", "User not found in the directory.", "not_found");
        var direct = memberships.Direct.Select(g => g.Id).ToHashSet();
        var primary = memberships.Primary?.Id;

        var groups = await Task.WhenAll(s.ManageableGroups.Take(300).Select(dn => provider.GetGroupByDnAsync(dn, ct)));
        var text = q?.Trim();
        return groups.Where(g => g != null).Select(g => g!)
            .Where(g => string.IsNullOrEmpty(text) || g.Name.Contains(text, StringComparison.OrdinalIgnoreCase) || (g.Description?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false))
            .Where(g => !AdProtection.IsGroupProtected(g, s))
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => (object)new { g.Id, g.Name, g.Description, g.Scope, g.Type, alreadyMember = direct.Contains(g.Id) || primary == g.Id })
            .ToList();
    }
}

public static class ChangeResults
{
    /// <summary>Success, NoChange and Validated are 200; a denial is 403; a failed change or failed validation is 422. The body is always the ChangeResult.</summary>
    public static Microsoft.AspNetCore.Mvc.IActionResult ToActionResult(Microsoft.AspNetCore.Mvc.ControllerBase c, ChangeResult r) =>
        r.Status switch
        {
            ChangeResult.Denied => c.StatusCode(403, r),
            ChangeResult.Failed => c.StatusCode(422, r),
            _ => c.Ok(r),
        };
}
