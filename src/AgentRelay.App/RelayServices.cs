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
        CodexIntegrationService codex,
        AntigravityQuotaService quota,
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
        Codex = codex;
        Quota = quota;
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
    public CodexIntegrationService Codex { get; }
    public AntigravityQuotaService Quota { get; }
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
        string? currentVersion = null)
    {
        var files = new AtomicFileStore();
        clock ??= new SystemClock();
        var runtime = new RuntimeStore(paths, files);
        var activity = new SolActivityStore(paths, files, clock);
        var delivery = new ReviewPromptDeliveryService(
            paths, files, clipboard, clock);
        return new RelayServices(
            paths,
            files,
            new ProjectRegistry(files, paths.ProjectsFile, clock),
            new PolicyService(files, clock),
            new ProtocolService(files, clock),
            runtime,
            activity,
            delivery,
            new RuntimeRecoveryService(runtime, clock),
            new DoctorService(paths, clock),
            new CodexIntegrationService(
                paths,
                files,
                skillSource,
                clock),
            AntigravityQuotaService.FromEnvironment(clock),
            new UpdateService(
                paths,
                files,
                updateHttp ?? CreateUpdateHttpClient(),
                currentVersion ?? AppVersion.Current,
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
