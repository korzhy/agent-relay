namespace AgentRelay.Core;

public static class AgentRelayConstants
{
    public const int ProtocolVersion = 1;
    public const int PolicySchemaVersion = 1;
    public const string Provider = "Antigravity";
    public const string Model = "gemini-3.6-flash-high";
    public const string TransportDirectory = ".agent-relay";
    public const string ManagedBlockStart = "<!-- BEGIN AGENT RELAY MANAGED BLOCK -->";
    public const string ManagedBlockEnd = "<!-- END AGENT RELAY MANAGED BLOCK -->";
}
