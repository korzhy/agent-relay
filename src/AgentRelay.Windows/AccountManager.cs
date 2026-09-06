using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentRelay.Core;

namespace AgentRelay.Windows;

public sealed class AccountManager
{
    public const string AgyCredentialTarget = "gemini:antigravity";
    public const string RecoveryCredentialTarget = "AgentRelay:Agy:Recovery";
    private const string AccountTargetPrefix = "AgentRelay:AgyAccount:";
    private readonly AppPaths _paths;
    private readonly AtomicFileStore _files;
    private readonly ICredentialStore _credentials;
    private readonly IAgyAccountClient _agy;
    private readonly IClock _clock;
    private readonly SemaphoreSlim _stateLock = new(1, 1);

    public AccountManager(
        AppPaths paths,
        AtomicFileStore files,
        ICredentialStore credentials,
        IAgyAccountClient agy,
        IClock? clock = null)
    {
        _paths = paths;
        _files = files;
        _credentials = credentials;
        _agy = agy;
        _clock = clock ?? new SystemClock();
    }

    public async Task<ManagedAccountRegistry> ListAsync(CancellationToken cancellationToken = default)
    {
        var registry = await _files.ReadJsonAsync<ManagedAccountRegistry>(
            _paths.AccountsFile, cancellationToken).ConfigureAwait(false) ?? ManagedAccountRegistry.Empty;
        registry.Validate();
        return registry;
    }

    public async Task<AccountSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _files.ReadJsonAsync<AccountSettings>(
            _paths.AccountSettingsFile, cancellationToken).ConfigureAwait(false) ?? AccountSettings.Default;
        settings.Validate();
        return settings;
    }

    public async Task<AccountSettings> SetThresholdAsync(
        int threshold,
        CancellationToken cancellationToken = default)
    {
        var settings = new AccountSettings(AccountSettings.CurrentSchemaVersion, threshold);
        settings.Validate();
        await _files.WriteJsonAsync(
            _paths.AccountSettingsFile, settings, File.Exists(_paths.AccountSettingsFile), cancellationToken)
            .ConfigureAwait(false);
        return settings;
    }

    public async Task<ManagedAccount> AddAsync(
        string label,
        string agyPath,
        bool activate,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        using var lease = GlobalAgyLease.TryAcquire()
                          ?? throw new InvalidOperationException("runnerBusy: another agy operation is active.");
        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RecoverCoreAsync(cancellationToken).ConfigureAwait(false);
            var previous = _credentials.Read(AgyCredentialTarget);
            if (previous is not null) _credentials.Write(RecoveryCredentialTarget, previous);
            else _credentials.Delete(RecoveryCredentialTarget);
            var journal = new AccountRecoveryJournal(
                AccountRecoveryJournal.CurrentSchemaVersion,
                Guid.NewGuid().ToString("N"),
                "oauth",
                previous is not null,
                _clock.UtcNow);
            await _files.WriteJsonAsync(_paths.AccountRecoveryFile, journal, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            try
            {
                ClearActiveCredential();
                await _agy.AuthorizeAsync(agyPath, cancellationToken).ConfigureAwait(false);
                var enrolledCredential = _credentials.Read(AgyCredentialTarget)
                                         ?? throw new InvalidDataException(
                                             "agy OAuth completed without a Windows credential.");
                var registry = await ListAsync(cancellationToken).ConfigureAwait(false);
                var inspection = await _agy.InspectAsync(
                    agyPath, enrolledCredential, cancellationToken).ConfigureAwait(false);
                enrolledCredential = _credentials.Read(AgyCredentialTarget) ?? enrolledCredential;
                var fingerprint = Fingerprint(enrolledCredential);
                if (registry.Accounts.Any(account =>
                        (!string.IsNullOrWhiteSpace(inspection.Email) &&
                         string.Equals(account.Email, inspection.Email, StringComparison.OrdinalIgnoreCase)) ||
                        Fingerprint(ReadAccountCredential(account.Id)) == fingerprint))
                {
                    throw new InvalidOperationException("This agy account is already enrolled.");
                }
                var id = Guid.NewGuid().ToString("N");
                WriteAccountCredential(id, enrolledCredential);
                try
                {
                    var activateEligible = activate &&
                                           inspection.Eligibility == AccountEligibility.Eligible;
                    var account = new ManagedAccount(
                        id,
                        label.Trim(),
                        inspection.Email,
                        inspection.Tier,
                        inspection.Eligibility,
                        inspection.Quota,
                        _clock.UtcNow,
                        _clock.UtcNow,
                        activateEligible ? _clock.UtcNow : null,
                        inspection.Diagnostic);
                    var updated = registry with
                    {
                        ActiveAccountId = activateEligible ? id : registry.ActiveAccountId,
                        Accounts = registry.Accounts.Append(account).ToArray()
                    };
                    await SaveAsync(updated, cancellationToken).ConfigureAwait(false);
                    if (activateEligible)
                    {
                        ActivateCredential(enrolledCredential);
                    }
                    else
                    {
                        Restore(previous);
                    }
                    return account;
                }
                catch
                {
                    try { await SaveAsync(registry, CancellationToken.None).ConfigureAwait(false); }
                    catch { /* Preserve the original failure; recovery journal remains authoritative. */ }
                    DeleteAccountCredential(id);
                    throw;
                }
            }
            catch
            {
                Restore(previous);
                throw;
            }
            finally
            {
                DeleteRecoveryArtifacts();
            }
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public async Task<ManagedAccount> ActivateAsync(
        string id,
        string agyPath,
        CancellationToken cancellationToken = default)
    {
        using var lease = GlobalAgyLease.TryAcquire()
                          ?? throw new InvalidOperationException("runnerBusy: another agy operation is active.");
        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RecoverCoreAsync(cancellationToken).ConfigureAwait(false);
            var registry = await ListAsync(cancellationToken).ConfigureAwait(false);
            var account = Find(registry, id);
            var activatingCurrentAccount = registry.ActiveAccountId == account.Id;
            var prior = _credentials.Read(AgyCredentialTarget);
            var credential = ReadAccountCredential(account.Id);
            try
            {
                ActivateCredential(credential);
                var refreshed = await InspectAccountAsync(account, credential, agyPath, cancellationToken)
                    .ConfigureAwait(false);
                if (refreshed.Eligibility != AccountEligibility.Eligible)
                {
                    var activeId = registry.ActiveAccountId == refreshed.Id
                        ? null
                        : registry.ActiveAccountId;
                    await SaveAccountAsync(registry, refreshed, activeId, cancellationToken)
                        .ConfigureAwait(false);
                    if (activeId is null) ClearActiveCredential();
                    throw new InvalidOperationException(
                        $"Account is not eligible: {refreshed.Eligibility}. {refreshed.Diagnostic}");
                }
                refreshed = refreshed with { LastUsedAt = _clock.UtcNow };
                await SaveAccountAsync(registry, refreshed, refreshed.Id, cancellationToken).ConfigureAwait(false);
                return refreshed;
            }
            catch
            {
                if (activatingCurrentAccount &&
                    (await ListAsync(cancellationToken).ConfigureAwait(false)).ActiveAccountId is null)
                {
                    ClearActiveCredential();
                }
                else
                {
                    Restore(prior);
                }
                throw;
            }
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public async Task<ManagedAccountRegistry> RefreshAsync(
        string agyPath,
        string? id,
        bool all,
        CancellationToken cancellationToken = default)
    {
        using var lease = GlobalAgyLease.TryAcquire()
                          ?? throw new InvalidOperationException("runnerBusy: another agy operation is active.");
        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RecoverCoreAsync(cancellationToken).ConfigureAwait(false);
            var registry = await ListAsync(cancellationToken).ConfigureAwait(false);
            var targets = all ? registry.Accounts : [Find(registry, id ?? registry.ActiveAccountId
                ?? throw new InvalidOperationException("No active account."))];
            foreach (var account in targets)
            {
                registry = await RefreshOneAsync(registry, account, agyPath, cancellationToken)
                    .ConfigureAwait(false);
            }
            if (registry.ActiveAccountId is not null &&
                Find(registry, registry.ActiveAccountId).Eligibility != AccountEligibility.Eligible)
            {
                registry = registry with { ActiveAccountId = null };
                await SaveAsync(registry, cancellationToken).ConfigureAwait(false);
            }
            await RestoreExpectedActiveAsync(registry, cancellationToken).ConfigureAwait(false);
            return registry;
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public async Task RemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        using var lease = GlobalAgyLease.TryAcquire()
                          ?? throw new InvalidOperationException("runnerBusy: another agy operation is active.");
        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var registry = await ListAsync(cancellationToken).ConfigureAwait(false);
            _ = Find(registry, id);
            var removingActive = registry.ActiveAccountId == id;
            var updated = registry with
            {
                ActiveAccountId = removingActive ? null : registry.ActiveAccountId,
                Accounts = registry.Accounts.Where(account => account.Id != id).ToArray()
            };
            await SaveAsync(updated, cancellationToken).ConfigureAwait(false);
            DeleteAccountCredential(id);
            if (removingActive) ClearActiveCredential();
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public async Task<AccountPreflightResult> PreflightAsync(
        string agyPath,
        ISet<string>? excludedIds,
        CancellationToken cancellationToken = default)
    {
        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RecoverCoreAsync(cancellationToken).ConfigureAwait(false);
            var registry = await ListAsync(cancellationToken).ConfigureAwait(false);
            if (registry.Accounts.Count == 0)
            {
                return new AccountPreflightResult(
                    "accountRequired", null, "Enroll a PRO or ULTRA account before dispatch.");
            }
            var settings = await GetSettingsAsync(cancellationToken).ConfigureAwait(false);

            if (registry.ActiveAccountId is not null)
            {
                var active = Find(registry, registry.ActiveAccountId);
                var expected = ReadAccountCredential(active.Id);
                var current = _credentials.Read(AgyCredentialTarget);
                if (!CryptographicOperations.FixedTimeEquals(
                        SHA256.HashData(current ?? []), SHA256.HashData(expected)))
                {
                    ActivateCredential(expected);
                }
                registry = await RefreshOneAsync(registry, active, agyPath, cancellationToken)
                    .ConfigureAwait(false);
                active = Find(registry, active.Id);
                if ((excludedIds is null || !excludedIds.Contains(active.Id)) &&
                    AccountSelection.IsAboveOrAtThreshold(active, settings.RotationThreshold))
                {
                    active = active with { LastUsedAt = _clock.UtcNow };
                    await SaveAccountAsync(registry, active, active.Id, cancellationToken).ConfigureAwait(false);
                    return new AccountPreflightResult("ready", active, "Active managed account is ready.");
                }
            }

            foreach (var candidate in registry.Accounts.Where(account =>
                         account.Id != registry.ActiveAccountId &&
                         (excludedIds is null || !excludedIds.Contains(account.Id))))
            {
                registry = await RefreshOneAsync(registry, candidate, agyPath, cancellationToken)
                    .ConfigureAwait(false);
            }
            var selected = AccountSelection.Select(registry.Accounts, settings.RotationThreshold, excludedIds);
            if (selected is null)
            {
                if (registry.ActiveAccountId is not null &&
                    Find(registry, registry.ActiveAccountId).Eligibility != AccountEligibility.Eligible)
                {
                    registry = registry with { ActiveAccountId = null };
                    await SaveAsync(registry, cancellationToken).ConfigureAwait(false);
                    ClearActiveCredential();
                }
                else
                {
                    await RestoreExpectedActiveAsync(registry, cancellationToken).ConfigureAwait(false);
                }
                return new AccountPreflightResult(
                    "noEligibleAccount", null,
                    $"No eligible account has Gemini quota at or above {settings.RotationThreshold}%.");
            }
            ActivateCredential(ReadAccountCredential(selected.Id));
            selected = selected with { LastUsedAt = _clock.UtcNow };
            await SaveAccountAsync(registry, selected, selected.Id, cancellationToken).ConfigureAwait(false);
            return new AccountPreflightResult("ready", selected, "Managed account selected deterministically.");
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or InvalidOperationException or
                System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            return new AccountPreflightResult("noEligibleAccount", null, exception.Message);
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public async Task RecoverAsync(CancellationToken cancellationToken = default)
    {
        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await RecoverCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { _stateLock.Release(); }
    }

    private async Task RecoverCoreAsync(CancellationToken cancellationToken)
    {
        var journal = await _files.ReadJsonAsync<AccountRecoveryJournal>(
            _paths.AccountRecoveryFile, cancellationToken).ConfigureAwait(false);
        if (journal is null) return;
        var recovery = _credentials.Read(RecoveryCredentialTarget);
        if (journal.HadPreviousCredential && recovery is null)
        {
            throw new InvalidDataException("Enrollment recovery credential is missing; dispatch is blocked.");
        }
        Restore(recovery);
        DeleteRecoveryArtifacts();
    }

    private async Task<ManagedAccountRegistry> RefreshOneAsync(
        ManagedAccountRegistry registry,
        ManagedAccount account,
        string agyPath,
        CancellationToken cancellationToken)
    {
        var credential = ReadAccountCredential(account.Id);
        ActivateCredential(credential);
        ManagedAccount refreshed;
        try
        {
            refreshed = await InspectAccountAsync(account, credential, agyPath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or InvalidOperationException or
                System.ComponentModel.Win32Exception)
        {
            refreshed = account with
            {
                Eligibility = AccountEligibility.CredentialInvalid,
                LastCheckedAt = _clock.UtcNow,
                Diagnostic = exception.Message
            };
        }
        return await SaveAccountAsync(registry, refreshed, registry.ActiveAccountId, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<ManagedAccount> InspectAccountAsync(
        ManagedAccount account,
        byte[] credential,
        string agyPath,
        CancellationToken cancellationToken)
    {
        var inspection = await _agy.InspectAsync(agyPath, credential, cancellationToken)
            .ConfigureAwait(false);
        var refreshedCredential = _credentials.Read(AgyCredentialTarget);
        if (refreshedCredential is not null)
        {
            WriteAccountCredential(account.Id, refreshedCredential);
        }
        return account with
        {
            Email = inspection.Email ?? account.Email,
            Tier = inspection.Tier,
            Eligibility = inspection.Eligibility,
            Quota = inspection.Quota,
            LastCheckedAt = _clock.UtcNow,
            Diagnostic = inspection.Diagnostic
        };
    }

    private async Task<ManagedAccountRegistry> SaveAccountAsync(
        ManagedAccountRegistry registry,
        ManagedAccount account,
        string? activeId,
        CancellationToken cancellationToken)
    {
        var updated = registry with
        {
            ActiveAccountId = activeId,
            Accounts = registry.Accounts.Select(item => item.Id == account.Id ? account : item).ToArray()
        };
        await SaveAsync(updated, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    private async Task SaveAsync(ManagedAccountRegistry registry, CancellationToken cancellationToken)
    {
        registry.Validate();
        await _files.WriteJsonAsync(
            _paths.AccountsFile, registry, File.Exists(_paths.AccountsFile), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task RestoreExpectedActiveAsync(
        ManagedAccountRegistry registry,
        CancellationToken cancellationToken)
    {
        if (registry.ActiveAccountId is null) ClearActiveCredential();
        else ActivateCredential(ReadAccountCredential(registry.ActiveAccountId));
        await Task.CompletedTask;
    }

    private byte[] ReadAccountCredential(string id)
    {
        var template = _credentials.Read(AccountTarget(id, "payload"))
                       ?? throw new InvalidDataException($"Credential is missing for managed account {id}.");
        var access = _credentials.Read(AccountTarget(id, "access"));
        var refresh = _credentials.Read(AccountTarget(id, "refresh"));
        var node = JsonNode.Parse(template)
                   ?? throw new InvalidDataException($"Credential template is invalid for account {id}.");
        ReplaceMarker(node, "__AGENT_RELAY_ACCESS__", access);
        ReplaceMarker(node, "__AGENT_RELAY_REFRESH__", refresh);
        return Encoding.UTF8.GetBytes(node.ToJsonString());
    }

    private void WriteAccountCredential(string id, byte[] credential)
    {
        var node = JsonNode.Parse(credential)
                   ?? throw new InvalidDataException("agy credential payload is invalid JSON.");
        var access = ExtractAndReplace(node, ["access_token", "accessToken"], "__AGENT_RELAY_ACCESS__");
        var refresh = ExtractAndReplace(node, ["refresh_token", "refreshToken"], "__AGENT_RELAY_REFRESH__");
        if (access is null || refresh is null)
        {
            throw new InvalidDataException("agy credential payload has no access/refresh token pair.");
        }
        _credentials.Write(AccountTarget(id, "access"), Encoding.UTF8.GetBytes(access));
        _credentials.Write(AccountTarget(id, "refresh"), Encoding.UTF8.GetBytes(refresh));
        _credentials.Write(AccountTarget(id, "payload"), Encoding.UTF8.GetBytes(node.ToJsonString()));
    }

    private void DeleteAccountCredential(string id)
    {
        _credentials.Delete(AccountTarget(id, "access"));
        _credentials.Delete(AccountTarget(id, "refresh"));
        _credentials.Delete(AccountTarget(id, "payload"));
    }

    private void ActivateCredential(byte[] credential)
    {
        _credentials.Write(AgyCredentialTarget, credential);
        DeleteAgyCredentialCache();
    }

    private void Restore(byte[]? credential)
    {
        if (credential is null) ClearActiveCredential();
        else ActivateCredential(credential);
    }

    private void ClearActiveCredential()
    {
        _credentials.Delete(AgyCredentialTarget);
        DeleteAgyCredentialCache();
    }

    private void DeleteAgyCredentialCache()
    {
        var path = Path.Combine(_paths.HomeDirectory, ".gemini", "oauth_creds.json");
        if (File.Exists(path)) File.Delete(path);
    }

    private void DeleteRecoveryArtifacts()
    {
        _credentials.Delete(RecoveryCredentialTarget);
        if (File.Exists(_paths.AccountRecoveryFile)) File.Delete(_paths.AccountRecoveryFile);
    }

    private static ManagedAccount Find(ManagedAccountRegistry registry, string id)
        => registry.Accounts.FirstOrDefault(account => account.Id == id)
           ?? throw new KeyNotFoundException($"Managed account was not found: {id}");

    private static string AccountTarget(string id, string part) => $"{AccountTargetPrefix}{id}:{part}";

    private static string? ExtractAndReplace(JsonNode node, string[] names, string marker)
    {
        if (node is JsonObject obj)
        {
            foreach (var property in obj.ToArray())
            {
                if (names.Contains(property.Key, StringComparer.OrdinalIgnoreCase) &&
                    property.Value is JsonValue value && value.TryGetValue<string>(out var secret))
                {
                    obj[property.Key] = marker;
                    return secret;
                }
                if (property.Value is not null)
                {
                    var nested = ExtractAndReplace(property.Value, names, marker);
                    if (nested is not null) return nested;
                }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array)
            {
                if (child is null) continue;
                var nested = ExtractAndReplace(child, names, marker);
                if (nested is not null) return nested;
            }
        }
        return null;
    }

    private static void ReplaceMarker(JsonNode node, string marker, byte[]? secret)
    {
        if (node is JsonObject obj)
        {
            foreach (var property in obj.ToArray())
            {
                if (property.Value is JsonValue value &&
                    value.TryGetValue<string>(out var text) && text == marker)
                {
                    if (secret is null)
                    {
                        throw new InvalidDataException("A credential secret entry is missing.");
                    }
                    obj[property.Key] = Encoding.UTF8.GetString(secret);
                }
                else if (property.Value is not null)
                {
                    ReplaceMarker(property.Value, marker, secret);
                }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array)
            {
                if (child is not null) ReplaceMarker(child, marker, secret);
            }
        }
    }

    private static string? Fingerprint(byte[]? credential)
        => credential is null ? null : Convert.ToHexString(SHA256.HashData(credential));
}
