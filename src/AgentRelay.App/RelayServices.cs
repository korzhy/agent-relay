using System.IO;
using System.Net.Http;
using AgentRelay.Core;
using AgentRelay.Windows;

namespace AgentRelay.App;

public sealed class RelayServices
{
    private RelayServices(
        AppPaths paths,
        AtomicFileStore files,
        ProjectRegistry projects,
        PolicyService policy,
        ProtocolService protocol,
        RuntimeStore runtime,
        SolActivityStore activity,
        ReviewPromptDeliveryService delivery,
        RuntimeRecoveryService recovery,
        DoctorService doctor,
        AgyModelSelectionService models,
        CodexIntegrationService codex,
        AntigravityQuotaService quota,
        AccountManager accounts,
        UpdateService updates)
    {
        Paths = paths;
        Files = files;
        Projects = projects;
        Policy = policy;
        Protocol = protocol;
        Runtime = runtime;
        Activity = activity;
        Delivery = delivery;
        Recovery = recovery;
        Doctor = doctor;
        Models = models;
        Codex = codex;
        Quota = quota;
        Accounts = accounts;
        Updates = updates;
    }

    public AppPaths Paths { get; }
    public AtomicFileStore Files { get; }
    public ProjectRegistry Projects { get; }
    public PolicyService Policy { get; }
    public ProtocolService Protocol { get; }
    public RuntimeStore Runtime { get; }
    public SolActivityStore Activity { get; }
    public ReviewPromptDeliveryService Delivery { get; }
    public RuntimeRecoveryService Recovery { get; }
    public DoctorService Doctor { get; }
    public AgyModelSelectionService Models { get; }
    public CodexIntegrationService Codex { get; }
    public AntigravityQuotaService Quota { get; }
    public AccountManager Accounts { get; }
    public UpdateService Updates { get; }

    public static RelayServices Create()
    {
        var paths = AppPaths.FromEnvironment();
        return Create(
            paths,
            Path.Combine(AppContext.BaseDirectory, "Assets", "external-agent-delegation"),
            new ClipboardTextWriter(),
            new SystemClock());
    }

    public static RelayServices Create(
        AppPaths paths,
        string skillSource,
        IClipboardWriter clipboard,
        IClock? clock = null,
        HttpClient? updateHttp = null,
        IUpdateInstallerLauncher? updateLauncher = null,
        string? currentVersion = null,
        ICredentialStore? credentialStore = null,
        IAgyAccountClient? agyAccountClient = null)
    {
        var files = new AtomicFileStore();
        clock ??= new SystemClock();
        var protocol = new ProtocolService(files, clock);
        var runtime = new RuntimeStore(paths, files);
        var activity = new SolActivityStore(paths, files, clock);
        var delivery = new ReviewPromptDeliveryService(
            paths, files, clipboard, clock);
        var models = new AgyModelSelectionService(paths, files, clock);
        var resolvedCurrentVersion = currentVersion ?? AppVersion.Current;
        var resolvedCredentialStore = credentialStore ?? new WindowsCredentialStore();
        var accounts = new AccountManager(
            paths,
            files,
            resolvedCredentialStore,
            agyAccountClient ?? new AgyAccountClient(clock: clock, credentials: resolvedCredentialStore),
            clock);
        return new RelayServices(
            paths,
            files,
            new ProjectRegistry(files, paths.ProjectsFile, clock),
            new PolicyService(files, clock),
            protocol,
            runtime,
            activity,
            delivery,
            new RuntimeRecoveryService(runtime, clock, protocol),
            new DoctorService(
                paths,
                clock,
                files,
                resolvedCurrentVersion,
                Environment.ProcessPath),
            models,
            new CodexIntegrationService(
                paths,
                files,
                skillSource,
                clock),
            AntigravityQuotaService.FromEnvironment(clock),
            accounts,
            new UpdateService(
                paths,
                files,
                updateHttp ?? CreateUpdateHttpClient(),
                resolvedCurrentVersion,
                updateLauncher,
                clock));
    }

    public AgyRunner CreateRunner(RunnerOptions? options = null)
        => new(Protocol, Runtime, new SystemClock(), options, Activity, Delivery);

    private static HttpClient CreateUpdateHttpClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5
        });
        client.Timeout = TimeSpan.FromMinutes(5);
        return client;
    }
}
