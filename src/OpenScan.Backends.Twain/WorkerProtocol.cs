using System.Text.Json;
using System.Text.Json.Serialization;
using OpenScan.Contracts;

namespace OpenScan.Backends.Twain;

/// <summary>
/// Contrat IPC pur entre OpenScan.Backends.Twain (Agent) et OpenScan.Twain.Worker (Processus isolé).
/// Ligne par ligne en JSON sur stdin/stdout (section 6.5).
/// Les données binaires d'image ne transitent jamais ici : seul le chemin du fichier temporaire est transmis.
/// </summary>
public static class WorkerProtocol
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string SerializeCommand<T>(T command) where T : class =>
        JsonSerializer.Serialize(command, JsonOptions);

    public static string SerializeResponse<T>(T response) where T : class =>
        JsonSerializer.Serialize(response, JsonOptions);

    public static WorkerResponse? DeserializeResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("type", out var typeProp))
        {
            return null;
        }

        var type = typeProp.GetString();
        return type switch
        {
            "scanners" => JsonSerializer.Deserialize<ScannersResponse>(json, JsonOptions),
            "capabilities" => JsonSerializer.Deserialize<CapabilitiesResponse>(json, JsonOptions),
            "page" => JsonSerializer.Deserialize<PageAcquiredResponse>(json, JsonOptions),
            "batchComplete" => JsonSerializer.Deserialize<BatchCompleteResponse>(json, JsonOptions),
            "error" => JsonSerializer.Deserialize<ErrorResponse>(json, JsonOptions),
            "status" => JsonSerializer.Deserialize<StatusResponse>(json, JsonOptions),
            "ok" => JsonSerializer.Deserialize<OkResponse>(json, JsonOptions),
            _ => null
        };
    }

    public static WorkerCommand? DeserializeCommand(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("cmd", out var cmdProp))
        {
            return null;
        }

        var cmd = cmdProp.GetString();
        return cmd switch
        {
            "discover" => JsonSerializer.Deserialize<DiscoverCommand>(json, JsonOptions),
            "open" => JsonSerializer.Deserialize<OpenCommand>(json, JsonOptions),
            "close" => JsonSerializer.Deserialize<CloseCommand>(json, JsonOptions),
            "capabilities" => JsonSerializer.Deserialize<GetCapabilitiesCommand>(json, JsonOptions),
            "configure" => JsonSerializer.Deserialize<ConfigureCommand>(json, JsonOptions),
            "start" => JsonSerializer.Deserialize<StartCommand>(json, JsonOptions),
            "acquire" => JsonSerializer.Deserialize<AcquireCommand>(json, JsonOptions),
            "stop" => JsonSerializer.Deserialize<StopCommand>(json, JsonOptions),
            "status" => JsonSerializer.Deserialize<GetStatusCommand>(json, JsonOptions),
            "cancel" => JsonSerializer.Deserialize<CancelCommand>(json, JsonOptions),
            "exit" => JsonSerializer.Deserialize<ExitCommand>(json, JsonOptions),
            _ => null
        };
    }
}

// ---------------------- Commandes (Agent -> Worker) ----------------------

public abstract record WorkerCommand
{
    [JsonPropertyName("cmd")]
    public abstract string Cmd { get; }
}

public sealed record DiscoverCommand : WorkerCommand
{
    public override string Cmd => "discover";
}

public sealed record OpenCommand(string ScannerId) : WorkerCommand
{
    public override string Cmd => "open";
}

public sealed record CloseCommand(string ScannerId) : WorkerCommand
{
    public override string Cmd => "close";
}

public sealed record GetCapabilitiesCommand(string ScannerId) : WorkerCommand
{
    public override string Cmd => "capabilities";
}

public sealed record ConfigureCommand(
    string ScannerId,
    string JobId,
    string Source,
    bool Duplex,
    int Resolution,
    string PixelFormat,
    string PageSize) : WorkerCommand
{
    public override string Cmd => "configure";
}

public sealed record StartCommand(string ScannerId, string JobId) : WorkerCommand
{
    public override string Cmd => "start";
}

public sealed record AcquireCommand(string ScannerId, string JobId) : WorkerCommand
{
    public override string Cmd => "acquire";
}

public sealed record StopCommand(string ScannerId, string JobId) : WorkerCommand
{
    public override string Cmd => "stop";
}

public sealed record GetStatusCommand(string ScannerId) : WorkerCommand
{
    public override string Cmd => "status";
}

public sealed record CancelCommand(string ScannerId, string JobId) : WorkerCommand
{
    public override string Cmd => "cancel";
}

public sealed record ExitCommand : WorkerCommand
{
    public override string Cmd => "exit";
}

// ---------------------- Réponses (Worker -> Agent) ----------------------

public abstract record WorkerResponse
{
    [JsonPropertyName("type")]
    public abstract string Type { get; }
}

public sealed record ScannersResponse(List<Scanner> Scanners) : WorkerResponse
{
    public override string Type => "scanners";
}

public sealed record CapabilitiesResponse(string ScannerId, ScannerCapabilities Capabilities) : WorkerResponse
{
    public override string Type => "capabilities";
}

public sealed record PageAcquiredResponse(
    string JobId,
    int PageNumber,
    string FilePath,
    int Width,
    int Height,
    int Dpi,
    string PixelFormat,
    string Source) : WorkerResponse
{
    public override string Type => "page";
}

public sealed record BatchCompleteResponse(string JobId) : WorkerResponse
{
    public override string Type => "batchComplete";
}

public sealed record ErrorResponse(
    ErrorCode Code,
    string Message,
    bool Recoverable,
    string? ScannerId = null,
    string? JobId = null,
    string? TechnicalDetails = null) : WorkerResponse
{
    public override string Type => "error";
}

public sealed record StatusResponse(string ScannerId, ScannerStatus Status) : WorkerResponse
{
    public override string Type => "status";
}

public sealed record OkResponse(string Cmd) : WorkerResponse
{
    public override string Type => "ok";
}
