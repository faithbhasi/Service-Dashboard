using ServiceDashboard.Configuration;

namespace ServiceDashboard.Data;

/// <summary>Resolves the data, asset and backup folders (relative paths are relative to the content root).</summary>
public sealed class AppPaths
{
    public string DataDirectory { get; }
    public string AssetDirectory { get; }
    public string LogoDirectory => Path.Combine(AssetDirectory, "logos");
    public string BackupDirectory { get; }
    public string DatabaseFile => Path.Combine(DataDirectory, "service-dashboard.db");

    public AppPaths(AppOptions o, string contentRoot)
    {
        DataDirectory = Resolve(o.DataDirectory, contentRoot, "data");
        AssetDirectory = Resolve(o.AssetDirectory, contentRoot, Path.Combine("data", "assets"));
        BackupDirectory = string.IsNullOrWhiteSpace(o.BackupDirectory) ? "" : Resolve(o.BackupDirectory, contentRoot, "backups");
    }

    private static string Resolve(string configured, string root, string fallback)
    {
        var p = string.IsNullOrWhiteSpace(configured) ? fallback : configured;
        return Path.GetFullPath(p, root);
    }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogoDirectory);
        if (!OperatingSystem.IsWindows())
        {
            // On Windows the folder ACL is set at deployment time (docs/DEPLOYMENT.md).
            File.SetUnixFileMode(DataDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
