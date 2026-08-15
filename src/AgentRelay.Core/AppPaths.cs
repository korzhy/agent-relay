namespace AgentRelay.Core;

public sealed record AppPaths(string HomeDirectory, string LocalAppDataDirectory)
{
    public static AppPaths FromEnvironment()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(home) || string.IsNullOrWhiteSpace(local))
        {
            throw new InvalidOperationException("HOME and LOCALAPPDATA could not be resolved.");
        }

        return new AppPaths(home, local);
    }

    public string DataRoot => Path.Combine(LocalAppDataDirectory, "AgentRelay");
    public string ProjectsFile => Path.Combine(DataRoot, "projects.json");
    public string LogsDirectory => Path.Combine(DataRoot, "logs");
    public string RuntimeDirectory => Path.Combine(DataRoot, "runtime");
    public string ModelSelectionFile => Path.Combine(DataRoot, "model-selection.json");
    public string UpdatesDirectory => Path.Combine(DataRoot, "updates");
    public string UpdateSettingsFile => Path.Combine(UpdatesDirectory, "settings.json");
    public string UpdateStateFile => Path.Combine(UpdatesDirectory, "state.json");
    public string UpdatePackagesDirectory => Path.Combine(UpdatesDirectory, "packages");
    public string InstalledExecutable => Path.Combine(
        LocalAppDataDirectory, "Programs", "AgentRelay", "AgentRelay.exe");
    public string CodexDirectory => Path.Combine(HomeDirectory, ".codex");
    public string CodexAgentsFile => Path.Combine(CodexDirectory, "AGENTS.md");
    public string CodexPolicyFile => Path.Combine(CodexDirectory, "external-agent-delegation.json");
    public string CodexSkillDirectory => Path.Combine(CodexDirectory, "skills", "external-agent-delegation");
    public string IntegrationDirectory => Path.Combine(DataRoot, "codex-integration");
    public string IntegrationManifest => Path.Combine(IntegrationDirectory, "ownership.json");
}
