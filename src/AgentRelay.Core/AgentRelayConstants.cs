namespace AgentRelay.Core;

public static class AgentRelayConstants
{
    public const int ProtocolVersion = 1;
    public const int PolicySchemaVersion = 1;
    public const string Provider = "Antigravity";
    public const string ModelSelector = "latest-observed-gemini-high";
    public const string LegacyFlashModelSelector = "latest-gemini-flash-high";
    public const string FallbackModel = "gemini-3.6-flash-high";
    public const string TransportDirectory = ".agent-relay";
    public const string ManagedBlockStart = "<!-- BEGIN AGENT RELAY MANAGED BLOCK -->";
    public const string ManagedBlockEnd = "<!-- END AGENT RELAY MANAGED BLOCK -->";
}
