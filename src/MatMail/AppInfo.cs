using System.Reflection;

namespace MatMail;

/// <summary>Build/runtime facts shown in the footer and on the About page.</summary>
public static class AppInfo
{
    public const string Name = "MatMail";

    /// <summary>release: 0.1.42-20261007 · nightly: nightly-42-20261007 · local: local-20261007.</summary>
    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "local";

    public static string DataDir { get; set; } = "/data";
    public static DateTime StartedAt { get; } = DateTime.UtcNow;
}
