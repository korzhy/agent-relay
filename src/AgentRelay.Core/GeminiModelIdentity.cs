using System.Text.RegularExpressions;

namespace AgentRelay.Core;

public static partial class GeminiModelIdentity
{
    [GeneratedRegex(
        @"^gemini-(?<version>\d+(?:\.\d+)+)-(?<family>[a-z0-9]+(?:-[a-z0-9]+)*)-high$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ModelPattern();

    public static bool TryParse(string model, out Version version, out string family)
    {
        version = new Version();
        family = string.Empty;
        if (string.IsNullOrWhiteSpace(model))
        {
            return false;
        }

        var match = ModelPattern().Match(model);
        if (!match.Success || !Version.TryParse(match.Groups["version"].Value, out var parsed))
        {
            return false;
        }

        version = parsed;
        family = match.Groups["family"].Value;
        return true;
    }

    public static bool TryGetVersion(string model, out Version version)
        => TryParse(model, out version, out _);

    public static bool IsSupported(string model)
        => TryParse(model, out _, out _);
}
