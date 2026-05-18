using System.Text.Json;

namespace Vendomat.Controller.Domain.Models;

public static class CloudTunnelMessageTypes
{
    public const string Request = "request";
    public const string Response = "response";
    public const string Sync = "sync";
    public const string Ping = "ping";
    public const string Pong = "pong";
}

public static class CloudTunnelActions
{
    public const string SyncState = "sync-state";
    public const string GetStatus = "get-status";
    public const string GetDashboard = "get-dashboard";
    public const string GetSettings = "get-settings";
    public const string SaveSettings = "save-settings";
    public const string RunSanitation = "run-sanitation";
    public const string AddCredit = "add-credit";
}

public sealed class CloudTunnelEnvelope
{
    public string MessageType { get; set; } = string.Empty;
    public string RequestId { get; set; } = string.Empty;
    public Guid MachineId { get; set; }
    public string Action { get; set; } = string.Empty;
    public bool Success { get; set; } = true;
    public string ErrorMessage { get; set; } = string.Empty;
    public JsonElement Payload { get; set; } = JsonSerializer.SerializeToElement(new { });
}

public sealed class CloudTunnelAcceptedResponse
{
    public string Status { get; set; } = "accepted";
}

public sealed class CloudTunnelRemoteCreditResponse
{
    public MachineStatusSnapshot? Snapshot { get; set; }
    public bool IsQueued { get; set; }
    public Guid? CommandId { get; set; }
}
