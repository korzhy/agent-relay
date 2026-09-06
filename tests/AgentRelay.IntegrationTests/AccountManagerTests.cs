using System.Text;
using AgentRelay.Core;
using AgentRelay.Windows;

namespace AgentRelay.IntegrationTests;

public sealed class AccountManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "AgentRelayAccounts_" + Guid.NewGuid().ToString("N"));
    private readonly FakeCredentialStore _store = new();
    private readonly FakeAgyClient _agy;
    private readonly AppPaths _paths;
    private readonly AccountManager _manager;

    public AccountManagerTests()
    {
        _paths = new AppPaths(Path.Combine(_root, "home"), Path.Combine(_root, "local"));
        Directory.CreateDirectory(_paths.HomeDirectory);
        Directory.CreateDirectory(_paths.LocalAppDataDirectory);
        _agy = new FakeAgyClient(_store);
        _manager = new AccountManager(
            _paths, new AtomicFileStore(), _store, _agy,
            new FixedClock(DateTimeOffset.Parse("2026-09-06T08:00:00Z")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            try { Directory.Delete(_root, true); } catch { }
        }
    }

    [Fact]
    public async Task Add_SavesSecretOnlyInVaultAndRejectsDuplicate()
    {
        _agy.NextCredential = Credential("same");
        var account = await _manager.AddAsync("Primary", "agy.exe", true);
        var json = await File.ReadAllTextAsync(_paths.AccountsFile);
        Assert.DoesNotContain("access-same", json);
        Assert.DoesNotContain("refresh-same", json);
        Assert.Equal(account.Id, (await _manager.ListAsync()).ActiveAccountId);
        Assert.Contains(_store.Targets, target => target.EndsWith(":access", StringComparison.Ordinal));
        Assert.Contains(_store.Targets, target => target.EndsWith(":refresh", StringComparison.Ordinal));

        _agy.NextCredential = Credential("same");
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _manager.AddAsync("Duplicate", "agy.exe", false));
        Assert.Single((await _manager.ListAsync()).Accounts);
    }

    [Fact]
    public async Task CancelledOAuth_RestoresPreviousCredential()
    {
        var previous = Credential("previous");
        _store.Write(AccountManager.AgyCredentialTarget, previous);
        _agy.CancelAuthorization = true;

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _manager.AddAsync("Cancelled", "agy.exe", true));

        Assert.Equal(previous, _store.Read(AccountManager.AgyCredentialTarget));
        Assert.False(File.Exists(_paths.AccountRecoveryFile));
        Assert.Empty((await _manager.ListAsync()).Accounts);
    }

    [Fact]
    public async Task AddFreeAccount_RecordsItButNeverActivatesIt()
    {
        _agy.NextCredential = Credential("free");
        _agy.Eligibility = AccountEligibility.IneligibleFree;
        _agy.Tier = "FREE";

        var account = await _manager.AddAsync("Free", "agy.exe", true);

        Assert.Equal(AccountEligibility.IneligibleFree, account.Eligibility);
        Assert.Null((await _manager.ListAsync()).ActiveAccountId);
        Assert.Null(_store.Read(AccountManager.AgyCredentialTarget));
    }

    [Fact]
    public async Task DowngradeToFree_MakesAccountIneligibleAndRollsBackActivation()
    {
        _agy.NextCredential = Credential("pro");
        var account = await _manager.AddAsync("Pro", "agy.exe", true);
        _agy.Eligibility = AccountEligibility.IneligibleFree;
        _agy.Tier = "FREE";

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _manager.ActivateAsync(account.Id, "agy.exe"));

        Assert.Null(_store.Read(AccountManager.AgyCredentialTarget));
        var registry = await _manager.ListAsync();
        Assert.Null(registry.ActiveAccountId);
        Assert.Equal(AccountEligibility.IneligibleFree, Assert.Single(registry.Accounts).Eligibility);
    }

    [Fact]
    public async Task RecoveryJournal_RestoresCredentialAfterInterruptedOAuth()
    {
        var recovery = Credential("recovery");
        _store.Write(AccountManager.RecoveryCredentialTarget, recovery);
        await new AtomicFileStore().WriteJsonAsync(
            _paths.AccountRecoveryFile,
            new AccountRecoveryJournal(1, "op", "oauth", true, DateTimeOffset.UtcNow));
        _store.Delete(AccountManager.AgyCredentialTarget);

        await _manager.RecoverAsync();

        Assert.Equal(recovery, _store.Read(AccountManager.AgyCredentialTarget));
        Assert.False(File.Exists(_paths.AccountRecoveryFile));
    }

    [Fact]
    public async Task FailedSwitch_RestoresPreviouslyActiveAccount()
    {
        _agy.NextCredential = Credential("first");
        var first = await _manager.AddAsync("First", "agy.exe", true);
        _agy.NextCredential = Credential("second");
        var second = await _manager.AddAsync("Second", "agy.exe", false);
        var prior = _store.Read(AccountManager.AgyCredentialTarget);
        _agy.ThrowOnInspect = true;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _manager.ActivateAsync(second.Id, "agy.exe"));

        Assert.Equal(prior, _store.Read(AccountManager.AgyCredentialTarget));
        Assert.Equal(first.Id, (await _manager.ListAsync()).ActiveAccountId);
    }

    [Fact]
    public async Task Preflight_NoAccountsFailsWithoutCreatingAnything()
    {
        var result = await _manager.PreflightAsync("agy.exe", null);
        Assert.Equal("accountRequired", result.Status);
        Assert.Null(result.Account);
        Assert.Empty(_store.Targets);
    }

    [Fact]
    public async Task GlobalLease_SerializesEnrollment()
    {
        using var lease = GlobalAgyLease.TryAcquire();
        Assert.NotNull(lease);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _manager.AddAsync("Busy", "agy.exe", true));
        Assert.Contains("runnerBusy", error.Message);
    }

    [Fact]
    public void WindowsCredentialStore_RoundTripsAndDeletesIsolatedEntry()
    {
        if (!OperatingSystem.IsWindows()) return;
        var target = "AgentRelay:Test:" + Guid.NewGuid().ToString("N");
        var store = new WindowsCredentialStore();
        try
        {
            var secret = Encoding.UTF8.GetBytes("isolated-test-secret");
            store.Write(target, secret);
            Assert.Equal(secret, store.Read(target));
            store.Delete(target);
            Assert.Null(store.Read(target));
        }
        finally
        {
            store.Delete(target);
        }
    }

    private static byte[] Credential(string suffix) => Encoding.UTF8.GetBytes(
        $$"""{"token":{"access_token":"access-{{suffix}}","refresh_token":"refresh-{{suffix}}"},"email":"{{suffix}}@example.com"}""");

    private sealed class FakeCredentialStore : ICredentialStore
    {
        private readonly Dictionary<string, byte[]> _values = new(StringComparer.Ordinal);
        public IReadOnlyCollection<string> Targets => _values.Keys;
        public byte[]? Read(string target) => _values.TryGetValue(target, out var value) ? value.ToArray() : null;
        public void Write(string target, byte[] credential) => _values[target] = credential.ToArray();
        public void Delete(string target) => _values.Remove(target);
    }

    private sealed class FakeAgyClient(FakeCredentialStore store) : IAgyAccountClient
    {
        public byte[] NextCredential { get; set; } = Credential("next");
        public bool CancelAuthorization { get; set; }
        public AccountEligibility Eligibility { get; set; } = AccountEligibility.Eligible;
        public string Tier { get; set; } = "PRO";
        public bool ThrowOnInspect { get; set; }

        public Task AuthorizeAsync(string agyPath, CancellationToken cancellationToken = default)
        {
            if (CancelAuthorization) throw new OperationCanceledException("cancelled");
            store.Write(AccountManager.AgyCredentialTarget, NextCredential);
            return Task.CompletedTask;
        }

        public Task<AgyAccountInspection> InspectAsync(
            string agyPath, byte[] credential, CancellationToken cancellationToken = default)
        {
            if (ThrowOnInspect) throw new InvalidOperationException("inspection failed");
            using var document = System.Text.Json.JsonDocument.Parse(credential);
            var email = document.RootElement.GetProperty("email").GetString();
            return Task.FromResult(new AgyAccountInspection(
                new GeminiQuotaSnapshot(90, 80, DateTimeOffset.Parse("2026-09-06T08:00:00Z")),
                email, Tier, Eligibility,
                Eligibility == AccountEligibility.Eligible ? null : "not eligible"));
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
