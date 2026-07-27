using System.IO;
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
        RuntimeRecoveryService recovery,
        DoctorService doctor,
        CodexIntegrationService codex,
        AntigravityQuotaService quota)
    {
        Paths = paths;
        Files = files;
        Projects = projects;
        Policy = policy;
        Protocol = protocol;
        Runtime = runtime;
        Recovery = recovery;
        Doctor = doctor;
        Codex = codex;
        Quota = quota;
    }

    public AppPaths Paths { get; }
    public AtomicFileStore Files { get; }
    public ProjectRegistry Projects { get; }
    public PolicyService Policy { get; }
    public ProtocolService Protocol { get; }
    public RuntimeStore Runtime { get; }
    public RuntimeRecoveryService Recovery { get; }
    public DoctorService Doctor { get; }
    public CodexIntegrationService Codex { get; }
    public AntigravityQuotaService Quota { get; }

    public static RelayServices Create()
    {
        var paths = AppPaths.FromEnvironment();
        var files = new AtomicFileStore();
        var clock = new SystemClock();
        var runtime = new RuntimeStore(paths, files);
        return new RelayServices(
            paths,
            files,
            new ProjectRegistry(files, paths.ProjectsFile, clock),
            new PolicyService(files, clock),
            new ProtocolService(files, clock),
            runtime,
            new RuntimeRecoveryService(runtime, clock),
            new DoctorService(paths, clock),
            new CodexIntegrationService(
                paths,
                files,
                Path.Combine(AppContext.BaseDirectory, "Assets", "external-agent-delegation"),
                clock),
            AntigravityQuotaService.FromEnvironment(clock));
    }

    public AgyRunner CreateRunner(RunnerOptions? options = null)
        => new(Protocol, Runtime, new SystemClock(), options);
}
