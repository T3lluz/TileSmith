using System.Reflection;

namespace TileSmith.Services;

public static class AppInfo
{
    public const string Name = "TileSmith";
    public const string RepoUrl = "https://github.com/T3lluz/TileSmith";

    public static string Version { get; } =
        (Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
         ?? "0.0.0").Split('+')[0];

    public static string AssetPath(string file) => Path.Combine(AppContext.BaseDirectory, "Assets", file);
}
