using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Mvc;
using OpenScan.Agent;
using OpenScan.Backends.Mock;
using OpenScan.Backends.Sane;
using OpenScan.Backends.Wia;
using OpenScan.Backends.Twain;
using OpenScan.Backends.Escl;
using OpenScan.Contracts;
using OpenScan.Core;
using OpenScan.Core.Abstractions;
using OpenScan.Persistence;
using OpenScan.Core.Hosting;
using OpenScan.Pipeline;

var builder = WebApplication.CreateBuilder(args);

// Hébergement en tant que service (Windows SCM ou systemd Linux)
builder.Host.UseWindowsService(options => options.ServiceName = "OpenScanAgent");
builder.Host.UseSystemd();

// Résolution de l'emplacement des données
var dataDirectory = DataDirectoryResolver.Resolve(
    builder.Configuration["DataDirectory"] ?? builder.Configuration["Data:Directory"]);
Directory.CreateDirectory(dataDirectory);

// Verrou mono-instance : empêche les collisions de ports et la corruption de base
var lockTarget = OperatingSystem.IsWindows()
    ? SingleInstanceGuard.DefaultWindowsMutexName
    : Path.Combine(dataDirectory, SingleInstanceGuard.DefaultLinuxLockFileName);

if (!SingleInstanceGuard.TryAcquire(lockTarget, out var instanceLock))
{
    Console.Error.WriteLine($"[OpenScanAgent] Impossible de démarrer : une autre instance est déjà en cours d'exécution (verrou: {lockTarget}).");
    return 1;
}

using (instanceLock)
{
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();

    var dbPath = Path.Combine(dataDirectory, "openscan.db");
    var pagesDirectory = Path.Combine(dataDirectory, "pages");

builder.Services.AddSingleton(new SqliteConnectionFactory($"Data Source={dbPath}"));
builder.Services.AddSingleton<IJobRepository, SqliteJobRepository>();
builder.Services.AddSingleton<IPageRepository, SqlitePageRepository>();
builder.Services.AddSingleton<IProfileRepository, SqliteProfileRepository>();
builder.Services.AddSingleton<IPageStorage>(new FileSystemPageStorage(pagesDirectory));
builder.Services.AddSingleton<IEventBus, InMemoryEventBus>();
builder.Services.AddSingleton<IScannerBackend, MockScannerBackend>();
builder.Services.AddSingleton<IScannerBackend, SaneScannerBackend>(); 
builder.Services.AddSingleton<IScannerBackend, WiaScannerBackend>(); 
builder.Services.AddHttpClient();
builder.Services.Configure<EsclOptions>(builder.Configuration.GetSection(EsclOptions.SectionName));
builder.Services.AddSingleton<ITwainLogger>(sp =>
    new AspNetTwainLogger(sp.GetRequiredService<ILogger<TwainScannerBackend>>()));
builder.Services.AddSingleton<IScannerBackend, TwainScannerBackend>();
builder.Services.AddSingleton<IScannerBackend, EsclScannerBackend>();
builder.Services.AddSingleton<IImageProcessor, ImageSharpProcessor>();
builder.Services.AddSingleton<IPdfExporter, MinimalPdfExporter>();
builder.Services.AddSingleton<DeviceManager>();
builder.Services.AddSingleton<JobManager>();

var app = builder.Build();

SqliteSchema.EnsureCreated(app.Services.GetRequiredService<SqliteConnectionFactory>());
await app.Services.GetRequiredService<DeviceManager>().DiscoverAllAsync();
await app.Services.GetRequiredService<JobManager>().ReconcileOrphanedJobsAsync();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseStaticFiles();
app.UseHttpsRedirection();
app.UseWebSockets();

var api = app.MapGroup("/api/v1");

api.MapGet("/health", () => Results.Ok(new { status = "ok", time = DateTimeOffset.UtcNow }));

api.MapGet("/scanners", async (DeviceManager devices, CancellationToken ct) =>
    Results.Ok(await devices.DiscoverAllAsync(ct)));

api.MapGet("/scanners/{id}", (string id, DeviceManager devices) =>
{
    try { return Results.Ok(devices.GetScanner(id)); }
    catch (ScanEngineException ex) { return ex.ToProblem(); }
});

api.MapGet("/scanners/{id}/capabilities", async (string id, DeviceManager devices, CancellationToken ct) =>
{
    try
    {
        var scanner = devices.GetScanner(id);
        var backend = devices.GetBackendFor(scanner);
        return Results.Ok(await backend.GetCapabilitiesAsync(id, ct));
    }
    catch (ScanEngineException ex) { return ex.ToProblem(); }
});

api.MapGet("/profiles", async (IProfileRepository repo, CancellationToken ct) =>
    Results.Ok(await repo.ListAsync(ct)));

api.MapPost("/profiles", async (ScanProfile profile, IProfileRepository repo, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(profile.Id))
    {
        profile.Id = Guid.NewGuid().ToString("n");
    }

    await repo.SaveAsync(profile, ct);
    return Results.Created($"/api/v1/profiles/{profile.Id}", profile);
});

api.MapGet("/profiles/{id}", async (string id, IProfileRepository repo, CancellationToken ct) =>
{
    var profile = await repo.GetAsync(id, ct);
    return profile is null ? Results.NotFound() : Results.Ok(profile);
});

api.MapPut("/profiles/{id}", async (string id, ScanProfile profile, IProfileRepository repo, CancellationToken ct) =>
{
    profile.Id = id;
    await repo.SaveAsync(profile, ct);
    return Results.Ok(profile);
});

api.MapDelete("/profiles/{id}", async (string id, IProfileRepository repo, CancellationToken ct) =>
{
    await repo.DeleteAsync(id, ct);
    return Results.NoContent();
});

api.MapPost("/scans", async (CreateScanRequest request, JobManager jobs, IProfileRepository profiles, CancellationToken ct) =>
{
    var profile = request.Profile ?? (request.ProfileId is not null ? await profiles.GetAsync(request.ProfileId, ct) : null);
    if (profile is null)
    {
        return Results.BadRequest(new { error = "'profile' ou 'profileId' est requis." });
    }

    try
    {
        var job = await jobs.StartScanAsync(request.ScannerId, profile, ct);
        return Results.Created($"/api/v1/scans/{job.Id}", job);
    }
    catch (ScanEngineException ex) { return ex.ToProblem(); }
});

api.MapGet("/scans/{id}", async (string id, IJobRepository repo, CancellationToken ct) =>
{
    var job = await repo.GetAsync(id, ct);
    return job is null ? Results.NotFound() : Results.Ok(job);
});

api.MapGet("/scans/{id}/pages", async (string id, IPageRepository repo, CancellationToken ct) =>
    Results.Ok(await repo.ListByJobAsync(id, ct)));

api.MapGet("/scans/{id}/pages/{pageId}", async (string id, string pageId, IPageRepository repo, CancellationToken ct) =>
{
    var page = await repo.GetAsync(pageId, ct);
    return page is null || page.JobId != id ? Results.NotFound() : Results.Ok(page);
});

api.MapGet("/scans/{id}/pages/{pageId}/image", (string id, string pageId, IPageRepository repo, IPageStorage storage, CancellationToken ct) =>
    ScanEndpoints.GetPageImageAsync(id, pageId, repo, storage, ct));

api.MapGet("/scans/{id}/export", (
    string id,
    [FromQuery] string? format,
    IJobRepository jobRepo,
    IPageRepository pageRepo,
    IPageStorage storage,
    IPdfExporter pdfExporter,
    CancellationToken ct) =>
    ScanEndpoints.ExportScanAsync(id, format, jobRepo, pageRepo, storage, pdfExporter, ct));

api.MapPost("/scans/{id}/cancel", async (string id, JobManager jobs, CancellationToken ct) =>
{
    try
    {
        await jobs.CancelJobAsync(id, ct);
        return Results.Accepted();
    }
    catch (ScanEngineException ex) { return ex.ToProblem(); }
});

api.MapPost("/scans/{id}/resume", (string id, [FromQuery] int? fromPage, JobManager jobs, CancellationToken ct) =>
    ScanEndpoints.ResumeScanAsync(id, fromPage, jobs, ct));

api.MapPost("/scans/{id}/retry", (string id, JobManager jobs, CancellationToken ct) =>
    ScanEndpoints.RetryScanAsync(id, jobs, ct));

app.Map("/ws", async (HttpContext context, IEventBus eventBus) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    var channel = Channel.CreateUnbounded<AgentEvent>();
    using var subscription = eventBus.Subscribe(evt =>
    {
        channel.Writer.TryWrite(evt);
        return Task.CompletedTask;
    });

    try
    {
        await foreach (var evt in channel.Reader.ReadAllAsync(context.RequestAborted))
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(evt, WebSocketJsonOptionsHolder.Options);
            await socket.SendAsync(json, WebSocketMessageType.Text, endOfMessage: true, context.RequestAborted);
        }
    }
    catch (OperationCanceledException)
    {
        // Client déconnecté ou requête annulée : fermeture normale ci-dessous.
    }
    finally
    {
        if (socket.State == WebSocketState.Open)
        {
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", CancellationToken.None);
        }
    }
});

    app.Run();
    return 0;
}

sealed record CreateScanRequest(string ScannerId, string? ProfileId, ScanProfile? Profile);

static class WebSocketJsonOptionsHolder
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}

sealed class AspNetTwainLogger(ILogger<TwainScannerBackend> logger) : ITwainLogger
{
    public void LogInfo(string message) => logger.LogInformation("{Message}", message);
    public void LogWarning(string message) => logger.LogWarning("{Message}", message);
    public void LogError(string message, Exception? ex = null) => logger.LogError(ex, "{Message}", message);
}
