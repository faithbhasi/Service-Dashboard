using System.Security.Cryptography;
using System.Text;
using ServiceDashboard.Modules.ActiveDirectory.Providers;

namespace ServiceDashboard.Modules.ActiveDirectory.Providers.Fake;

internal sealed class FakeUser
{
    public Guid Id { get; init; }
    public string Sam { get; init; } = "";
    public string? Given { get; init; }
    public string? Surname { get; init; }
    public string? Display { get; set; }
    public string? Email { get; init; }
    public string? Employee { get; init; }
    public string? Title { get; init; }
    public string? Department { get; init; }
    public string? Office { get; init; }
    public string? Phone { get; init; }
    public string? Mobile { get; init; }
    public string? Description { get; init; }
    public Guid? Manager { get; init; }
    public string Ou { get; set; } = "";
    public int Uac { get; set; } = UserStatus.NormalAccount;
    public bool LockedOut { get; set; }
    public long AccountExpires { get; init; }
    public long PwdLastSet { get; set; }
    public string? Pso { get; init; }
    public int PsoMaxAgeDays { get; init; }
    public DateTime? LastLogon { get; init; }
    public DateTime Created { get; init; }
    public DateTime Changed { get; set; }
    public bool AdminCount { get; init; }
    public string Dn => $"CN={Display ?? Sam},{Ou}";
}

internal sealed class FakeComputer
{
    public Guid Id { get; init; }
    public string Name { get; init; } = "";
    public string? Os { get; init; }
    public string? OsVersion { get; init; }
    public bool Disabled { get; set; }
    public string Ou { get; set; } = "";
    public DateTime? LastLogon { get; init; }
    public DateTime PwdLastSet { get; init; }
    public Guid? ManagedBy { get; init; }
    public string? Description { get; init; }
    public DateTime Created { get; init; }
    public DateTime Changed { get; set; }
    public string Dn => $"CN={Name},{Ou}";
}

internal sealed class FakeGroup
{
    public Guid Id { get; init; }
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public string Scope { get; init; } = "Global";
    public string Type { get; init; } = "Security";
    public string Ou { get; init; } = "";
    public Guid? ManagedBy { get; init; }
    public bool AdminCount { get; init; }
    public int Token { get; init; }
    public string Dn => $"CN={Name},{Ou}";
}

/// <summary>Deterministic seed data: the same GUIDs on every start so links and tests stay stable.</summary>
internal sealed class FakeDirectoryData
{
    public const string Base = "DC=fake,DC=local";
    public const string Corp = "OU=Corp," + Base;
    public const string Staff = "OU=Staff," + Corp;
    public const string Contractors = "OU=Contractors," + Corp;
    public const string Workstations = "OU=Workstations," + Corp;
    public const string Laptops = "OU=Laptops," + Corp;
    public const string Servers = "OU=Servers," + Corp;
    public const string GroupsOu = "OU=Groups," + Corp;
    public const string ServiceAccounts = "OU=Service Accounts," + Corp;
    public const string Tier0 = "OU=Tier 0," + Corp;
    public const string DomainControllers = "OU=Domain Controllers," + Base;
    public const string Builtin = "CN=Builtin," + Base;
    public const string UsersContainer = "CN=Users," + Base;

    public static readonly string[] OuList =
    [
        Corp, Staff, "OU=Sales," + Staff, "OU=Engineering," + Staff, "OU=Finance," + Staff, Contractors,
        Workstations, Laptops, Servers, GroupsOu, ServiceAccounts, Tier0, DomainControllers,
    ];

    public List<FakeUser> Users { get; } = [];
    public List<FakeComputer> Computers { get; } = [];
    public List<FakeGroup> Groups { get; } = [];
    /// <summary>Group id -> ids of direct members (users, computers or groups).</summary>
    public Dictionary<Guid, HashSet<Guid>> Members { get; } = [];
    /// <summary>Targets the simulated service account has no rights on (caught by dry-run).</summary>
    public HashSet<Guid> DeniedTargets { get; } = [];

    public static Guid Id(string key) => new(MD5.HashData(Encoding.UTF8.GetBytes("service-dashboard-fake:" + key)));

    public FakeGroup Group(string name) => Groups.First(g => g.Name == name);
    public FakeUser User(string sam) => Users.First(u => u.Sam == sam);

    public void AddMember(string group, Guid member)
    {
        var g = Group(group).Id;
        if (!Members.TryGetValue(g, out var set)) Members[g] = set = [];
        set.Add(member);
    }

    private static readonly string[] First = ["Alex", "Blake", "Casey", "Dana", "Eli", "Fran", "Gio", "Hana", "Ivan", "Jules", "Kira", "Leo", "Maya", "Noor", "Omar", "Pia", "Quinn", "Ravi", "Sara", "Tomas"];
    private static readonly string[] Last = ["Adams", "Baker", "Chen", "Diaz", "Evans", "Fox", "Green", "Hughes", "Iyer", "Jones", "Khan", "Lopez", "Miller", "Nguyen", "Owens", "Patel", "Quinn", "Reed", "Singh", "Turner"];
    private static readonly string[] Departments = ["Sales", "Engineering", "Finance"];
    private static readonly string[] Offices = ["London", "Manchester", "Leeds", "Remote"];

    public static FakeDirectoryData Seed()
    {
        var d = new FakeDirectoryData();
        var now = DateTime.UtcNow;
        long Ft(DateTime t) => t.ToFileTimeUtc();

        FakeUser U(string sam, string given, string sur, string ou, Action<Dictionary<string, object?>>? tweak = null)
        {
            var o = new Dictionary<string, object?>();
            tweak?.Invoke(o);
            T? G<T>(string k) => o.TryGetValue(k, out var v) ? (T?)v : default;
            var u = new FakeUser
            {
                Id = Id("user:" + sam), Sam = sam, Given = given, Surname = sur, Display = $"{given} {sur}",
                Email = o.ContainsKey("noemail") ? null : $"{sam}@fake.example",
                Employee = G<string>("emp"), Title = G<string>("title"), Department = G<string>("dept"), Office = G<string>("office"),
                Phone = G<string>("phone"), Mobile = G<string>("mobile"), Description = G<string>("desc"),
                Manager = G<Guid?>("mgr"), Ou = ou,
                Uac = UserStatus.NormalAccount | G<int>("uac"),
                LockedOut = G<bool>("locked"),
                AccountExpires = o.TryGetValue("expires", out var ex) ? (long)ex! : 0,
                PwdLastSet = o.TryGetValue("pwdset", out var ps) ? (long)ps! : Ft(now.AddDays(-20)),
                Pso = G<string>("pso"), PsoMaxAgeDays = G<int>("psoAge"),
                LastLogon = o.TryGetValue("lastlogon", out var ll) ? (DateTime?)ll : now.AddDays(-2),
                Created = now.AddDays(-400), Changed = now.AddDays(-5), AdminCount = G<bool>("admincount"),
            };
            d.Users.Add(u);
            return u;
        }

        // ---- named users covering every state ----
        var carol = U("carol.white", "Carol", "White", Staff, o => { o["title"] = "Director of Engineering"; o["dept"] = "Engineering"; o["office"] = "London"; o["phone"] = "+44 20 5550 0101"; o["mobile"] = "+44 7700 900101"; o["emp"] = "E1001"; });
        var bob = U("bob.jones", "Bob", "Jones", "OU=Engineering," + Staff, o => { o["title"] = "Engineering Manager"; o["dept"] = "Engineering"; o["office"] = "London"; o["phone"] = "+44 20 5550 0102"; o["mobile"] = "+44 7700 900102"; o["emp"] = "E1002"; o["mgr"] = carol.Id; });
        U("alice.smith", "Alice", "Smith", "OU=Engineering," + Staff, o => { o["title"] = "Software Engineer"; o["dept"] = "Engineering"; o["office"] = "London"; o["phone"] = "+44 20 5550 0103"; o["mobile"] = "+44 7700 900103"; o["emp"] = "E1003"; o["mgr"] = bob.Id; o["desc"] = "Platform team"; });
        U("dave.locked", "Dave", "Locked", "OU=Sales," + Staff, o => { o["title"] = "Account Executive"; o["dept"] = "Sales"; o["office"] = "Manchester"; o["emp"] = "E1004"; o["mgr"] = carol.Id; o["locked"] = true; });
        U("erin.disabled", "Erin", "Disabled", "OU=Sales," + Staff, o => { o["title"] = "Sales Analyst"; o["dept"] = "Sales"; o["office"] = "Manchester"; o["emp"] = "E1005"; o["uac"] = UserStatus.AccountDisable; o["lastlogon"] = now.AddDays(-90); });
        U("frank.expired", "Frank", "Expired", Contractors, o => { o["title"] = "Contractor"; o["dept"] = "Engineering"; o["office"] = "Remote"; o["emp"] = "C2001"; o["expires"] = Ft(now.AddDays(-10)); });
        U("gina.expiring", "Gina", "Expiring", Contractors, o => { o["title"] = "Contractor"; o["dept"] = "Finance"; o["office"] = "Leeds"; o["emp"] = "C2002"; o["expires"] = Ft(now.AddDays(30)); });
        U("grace.pwdexpired", "Grace", "Pwdexpired", "OU=Finance," + Staff, o => { o["title"] = "Accountant"; o["dept"] = "Finance"; o["office"] = "Leeds"; o["emp"] = "E1006"; o["pwdset"] = Ft(now.AddDays(-200)); });
        U("henry.neverexpires", "Henry", "Neverexpires", "OU=Finance," + Staff, o => { o["title"] = "Finance Systems Owner"; o["dept"] = "Finance"; o["office"] = "Leeds"; o["emp"] = "E1007"; o["uac"] = UserStatus.DontExpirePassword; o["pwdset"] = Ft(now.AddDays(-700)); });
        U("irene.mustchange", "Irene", "Mustchange", "OU=Sales," + Staff, o => { o["title"] = "New Starter"; o["dept"] = "Sales"; o["office"] = "London"; o["emp"] = "E1008"; o["pwdset"] = 0L; o["lastlogon"] = null; });
        U("jack.pso", "Jack", "Pso", "OU=Engineering," + Staff, o => { o["title"] = "Site Reliability Engineer"; o["dept"] = "Engineering"; o["office"] = "Remote"; o["emp"] = "E1009"; o["mgr"] = bob.Id; o["pso"] = "Privileged-Users-PSO"; o["psoAge"] = 30; o["pwdset"] = Ft(now.AddDays(-12)); });
        U("kim.sparse", "Kim", "Sparse", Contractors, o => { o["noemail"] = true; });
        U("svc.noperm", "Svc", "Noperm", Contractors, o => { o["title"] = "Simulated: service account lacks rights"; o["dept"] = "Engineering"; o["emp"] = "C2003"; });
        U("adm.tier0", "Adm", "Tier0", Tier0, o => { o["title"] = "Tier 0 administrator"; o["dept"] = "Engineering"; o["emp"] = "A0001"; o["admincount"] = true; });
        U("svc.backup", "Svc", "Backup", ServiceAccounts, o => { o["title"] = "Backup service account"; o["uac"] = UserStatus.DontExpirePassword; });
        d.DeniedTargets.Add(Id("user:svc.noperm"));

        // ---- filler users (paging, big group, dashboard counts) ----
        for (var i = 1; i <= 600; i++)
        {
            var given = First[i % First.Length];
            var sur = Last[(i / First.Length + i) % Last.Length];
            var sam = $"{given.ToLower()}.{sur.ToLower()}{i:000}";
            var dept = Departments[i % 3];
            var ou = dept switch { "Sales" => "OU=Sales," + Staff, "Engineering" => "OU=Engineering," + Staff, _ => "OU=Finance," + Staff };
            U(sam, given, sur, ou, o =>
            {
                o["title"] = dept == "Engineering" ? "Engineer" : dept == "Sales" ? "Sales Associate" : "Finance Analyst";
                o["dept"] = dept; o["office"] = Offices[i % 4]; o["emp"] = $"E{3000 + i}";
                o["mgr"] = i % 2 == 0 ? bob.Id : carol.Id;
                if (i % 37 == 0) o["locked"] = true;
                if (i % 23 == 0) o["uac"] = UserStatus.AccountDisable;
                if (i % 41 == 0) o["expires"] = Ft(now.AddDays(-3));
                if (i % 53 == 0) o["pwdset"] = Ft(now.AddDays(-150));
            });
        }

        // ---- computers ----
        FakeComputer C(string name, string ou, string os, string ver, bool disabled = false, Guid? managedBy = null, string? desc = null, DateTime? lastLogon = null) =>
            Add(d.Computers, new FakeComputer
            {
                Id = Id("computer:" + name), Name = name, Ou = ou, Os = os, OsVersion = ver, Disabled = disabled, ManagedBy = managedBy,
                Description = desc, LastLogon = lastLogon ?? now.AddDays(-1), PwdLastSet = now.AddDays(-14), Created = now.AddDays(-300), Changed = now.AddDays(-3),
            });
        C("DC01", DomainControllers, "Windows Server 2022 Datacenter", "10.0 (20348)");
        C("SRV-APP01", Servers, "Windows Server 2022 Standard", "10.0 (20348)", managedBy: bob.Id, desc: "CRM application server");
        C("SRV-FILE01", Servers, "Windows Server 2019 Standard", "10.0 (17763)", managedBy: Id("group:GG-Helpdesk"));
        C("WS-NOPERM", Workstations, "Windows 11 Pro", "10.0 (22631)", desc: "Simulated: service account lacks rights");
        d.DeniedTargets.Add(Id("computer:WS-NOPERM"));
        C("LT-OLD01", Laptops, "Windows 10 Enterprise", "10.0 (19045)", disabled: true, lastLogon: now.AddDays(-200));
        C("LT-OLD02", Laptops, "Windows 10 Pro", "10.0 (19044)", disabled: true, lastLogon: now.AddDays(-320));
        for (var i = 1; i <= 60; i++)
        {
            var laptop = i % 3 == 0;
            var owner = d.Users[(i * 7) % 40 + 3];
            C($"{(laptop ? "LT" : "WS")}-{i:000}", laptop ? Laptops : Workstations,
                i % 5 == 0 ? "Windows 10 Enterprise" : "Windows 11 Enterprise", i % 5 == 0 ? "10.0 (19045)" : "10.0 (22631)",
                disabled: i % 19 == 0, managedBy: i % 4 == 0 ? owner.Id : null, desc: $"Last user: {owner.Sam}");
        }

        // ---- groups ----
        FakeGroup G(string name, string ou, string scope = "Global", string type = "Security", bool admin = false, string? desc = null, Guid? managedBy = null, int token = 0) =>
            Add(d.Groups, new FakeGroup
            {
                Id = Id("group:" + name), Name = name, Ou = ou, Scope = scope, Type = type, AdminCount = admin, Description = desc,
                ManagedBy = managedBy, Token = token == 0 ? 1100 + d.Groups.Count : token,
            });
        G("Domain Users", UsersContainer, desc: "All domain users", token: 513);
        G("Domain Computers", UsersContainer, desc: "All workstations and servers", token: 515);
        G("Domain Admins", UsersContainer, admin: true, desc: "Designated administrators of the domain", token: 512);
        G("Enterprise Admins", UsersContainer, "Universal", admin: true, desc: "Designated administrators of the enterprise");
        G("Schema Admins", UsersContainer, "Universal", admin: true, desc: "Designated administrators of the schema");
        G("Administrators", Builtin, "DomainLocal", admin: true, desc: "Administrators have complete and unrestricted access");
        G("Account Operators", Builtin, "DomainLocal", admin: true, desc: "Members can administer domain user and group accounts");
        G("Backup Operators", Builtin, "DomainLocal", admin: true, desc: "Backup Operators can override security restrictions");
        G("Server Operators", Builtin, "DomainLocal", admin: true, desc: "Members can administer domain servers");
        G("Print Operators", Builtin, "DomainLocal", admin: true, desc: "Members can administer domain printers");
        G("Custom-Protected-Admins", GroupsOu, admin: true, desc: "Custom group with adminCount=1 (protected by SDProp)");
        G("GG-Sales", GroupsOu, desc: "Sales department", managedBy: carol.Id);
        G("GG-Engineering", GroupsOu, desc: "Engineering department", managedBy: bob.Id);
        G("GG-Finance", GroupsOu, desc: "Finance department");
        G("GG-All-Staff", GroupsOu, desc: "Everyone on the staff payroll (nested: departments)");
        G("GG-Intranet", GroupsOu, desc: "Intranet access (nested: All Staff)");
        G("GG-VPN-Users", GroupsOu, desc: "Allowed to use the VPN");
        G("GG-Helpdesk", GroupsOu, desc: "Helpdesk operators");
        G("GG-Finance-Share-RW", GroupsOu, "DomainLocal", desc: "Read/write on the finance share");
        G("GG-Finance-Share-RO", GroupsOu, "DomainLocal", desc: "Read-only on the finance share");
        G("APP-CRM-Users", GroupsOu, "Universal", desc: "Users of the CRM application");
        G("DL-Announcements", GroupsOu, "Universal", "Distribution", desc: "Company announcements mailing list");
        G("GG-All-Company", GroupsOu, desc: "Very large group used to test member search and paging");

        // ---- memberships ----
        foreach (var u in d.Users.Where(u => u.Ou != Tier0 && u.Ou != ServiceAccounts)) d.AddMember("GG-All-Company", u.Id);
        foreach (var c in d.Computers.Take(40)) d.AddMember("GG-All-Company", c.Id);
        foreach (var u in d.Users) d.AddMember(u.Ou == Tier0 ? "Domain Admins" : "Domain Users", u.Id);
        foreach (var c in d.Computers) d.AddMember("Domain Computers", c.Id);
        foreach (var u in d.Users.Where(u => u.Department == "Engineering")) d.AddMember("GG-Engineering", u.Id);
        foreach (var u in d.Users.Where(u => u.Department == "Sales")) d.AddMember("GG-Sales", u.Id);
        foreach (var u in d.Users.Where(u => u.Department == "Finance" && u.Ou.StartsWith("OU=Finance"))) d.AddMember("GG-Finance", u.Id);
        d.AddMember("GG-All-Staff", d.Group("GG-Sales").Id);
        d.AddMember("GG-All-Staff", d.Group("GG-Engineering").Id);
        d.AddMember("GG-All-Staff", d.Group("GG-Finance").Id);
        d.AddMember("GG-Intranet", d.Group("GG-All-Staff").Id);
        d.AddMember("GG-Finance-Share-RW", d.Group("GG-Finance").Id);
        foreach (var sam in new[] { "alice.smith", "bob.jones", "jack.pso" }) d.AddMember("GG-VPN-Users", d.User(sam).Id);
        d.AddMember("APP-CRM-Users", d.User("dave.locked").Id);
        d.AddMember("GG-Helpdesk", d.User("carol.white").Id);
        d.AddMember("Domain Admins", d.User("carol.white").Id); // an ordinary user who is also in a protected group
        d.AddMember("Administrators", d.Group("Domain Admins").Id);
        d.AddMember("Custom-Protected-Admins", d.User("jack.pso").Id);
        return d;
    }

    private static T Add<T>(List<T> list, T item) { list.Add(item); return item; }
}
