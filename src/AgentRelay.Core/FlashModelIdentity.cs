using System.Text.RegularExpressions;

namespace AgentRelay.Core;

public static partial class FlashModelIdentity
{
    [GeneratedRegex(@"^gemini-(?<version>\d+(?:\.\d+)+)-flash-high$", RegexOptions.CultureInvariant)]
    private static partial Regex ModelPattern();

    public static bool TryGetVersion(string model, out Version version)
    {
        version = new Version();
        if (string.IsNullOrWhiteSpace(model))
        {
            return false;
        }

        var match = ModelPattern().Match(model);
        return match.Success && Version.TryParse(match.Groups["version"].Value, out version!);
    }

    public static bool IsSupported(string model) => TryGetVersion(model, out _);
}
