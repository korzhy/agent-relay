using System.Reflection;

namespace AgentRelay.App;

public static class AppVersion
{
    public static string Current { get; } = Resolve();

    private static string Resolve()
    {
        var assembly = typeof(AppVersion).Assembly;
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        return !string.IsNullOrWhiteSpace(informational)
            ? informational
            : assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
