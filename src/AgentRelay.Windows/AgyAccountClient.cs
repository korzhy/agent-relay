using System.Diagnostics;
using System.Text.Json;
using AgentRelay.Core;

namespace AgentRelay.Windows;

public sealed record AgyAccountInspection(
    GeminiQuotaSnapshot Quota,
    string? Email,
    string? Tier,
    AccountEligibility Eligibility,
    string? Diagnostic);

public interface IAgyAccountClient
{
    Task AuthorizeAsync(string agyPath, CancellationToken cancellationToken = default);
    Task<AgyAccountInspection> InspectAsync(
        string agyPath,
        byte[] credential,
        CancellationToken cancellationToken = default);
}

public sealed class AgyAccountClient : IAgyAccountClient
{
    private readonly IClock _clock;
    private readonly ICredentialStore? _credentials;

    public AgyAccountClient(
        HttpClient? http = null,
        IClock? clock = null,
        ICredentialStore? credentials = null)
    {
        _clock = clock ?? new SystemClock();
        _credentials = credentials;
    }

    public async Task AuthorizeAsync(string agyPath, CancellationToken cancellationToken = default)
    {
        var start = new ProcessStartInfo(agyPath)
        {
            UseShellExecute = true,
            CreateNoWindow = false,
            WindowStyle = ProcessWindowStyle.Normal
        };
        start.ArgumentList.Add("--print");
        start.ArgumentList.Add("/usage");
        start.ArgumentList.Add("--print-timeout");
        start.ArgumentList.Add("10m");
        using var process = Process.Start(start)
                            ?? throw new InvalidOperationException("agy OAuth process did not start.");
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (_credentials?.Read(AccountManager.AgyCredentialTarget) is null)
        {
            throw new OperationCanceledException(
                $"agy OAuth produced no credential (exit code {process.ExitCode}).");
        }
    }

    public async Task<AgyAccountInspection> InspectAsync(
        string agyPath,
        byte[] credential,
        CancellationToken cancellationToken = default)
    {
        var usageJson = await RunUsageAsync(agyPath, cancellationToken).ConfigureAwait(false);
        var quota = AgyUsageParser.Parse(usageJson, _clock.UtcNow);
        credential = _credentials?.Read(AccountManager.AgyCredentialTarget) ?? credential;
        string? email = null;
        string? accessToken = null;
        try
        {
            using var credentialJson = JsonDocument.Parse(credential);
            email = FindString(credentialJson.RootElement, "email");
            email ??= ReadEmailFromIdToken(FindString(credentialJson.RootElement, "id_token") ??
                                           FindString(credentialJson.RootElement, "idToken"));
            accessToken = FindString(credentialJson.RootElement, "access_token") ??
                          FindString(credentialJson.RootElement, "accessToken");
        }
        catch (JsonException exception)
        {
            return new AgyAccountInspection(
                quota, null, null, AccountEligibility.CredentialInvalid,
                $"Credential payload is not valid JSON: {exception.Message}");
        }

        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return new AgyAccountInspection(
                quota, email, null, AccountEligibility.CredentialInvalid,
                "Credential payload contains no access token.");
        }

        return new AgyAccountInspection(
            quota,
            email,
            null,
            AccountEligibility.Eligible,
            "Tier verification is disabled; monitor the Google account type manually.");
    }

    private static async Task<string> RunUsageAsync(string agyPath, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(agyPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[]
                 { "--output-format", "json", "--print", "/usage", "--print-timeout", "45s" })
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start)
                            ?? throw new InvalidOperationException("agy /usage did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"agy /usage exited with code {process.ExitCode}: {(await stderr.ConfigureAwait(false)).Trim()}");
        }
        return await stdout.ConfigureAwait(false);
    }

    private static string? FindString(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.String)
                {
                    return property.Value.GetString();
                }
                var nested = FindString(property.Value, name);
                if (nested is not null) return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
            {
                var nested = FindString(child, name);
                if (nested is not null) return nested;
            }
        }
        return null;
    }

    private static string? ReadEmailFromIdToken(string? idToken)
    {
        if (string.IsNullOrWhiteSpace(idToken)) return null;
        try
        {
            var parts = idToken.Split('.');
            if (parts.Length < 2) return null;
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + ((4 - payload.Length % 4) % 4), '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
            return FindString(document.RootElement, "email");
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            return null;
        }
    }

}
