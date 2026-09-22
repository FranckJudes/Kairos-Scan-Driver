using Microsoft.AspNetCore.Http;
using OpenScan.Contracts;
using OpenScan.Core;
using OpenScan.Core.Abstractions;
using OpenScan.Pipeline;

namespace OpenScan.Agent;

/// <summary>
/// Gestionnaires d'endpoints HTTP pour la restitution d'images et l'export PDF.
/// </summary>
public static class ScanEndpoints
{
    public static async Task<IResult> GetPageImageAsync(
        string id,
        string pageId,
        IPageRepository repo,
        IPageStorage storage,
        CancellationToken ct = default)
    {
        var page = await repo.GetAsync(pageId, ct);
        if (page is null || page.JobId != id || string.IsNullOrEmpty(page.FileReference))
        {
            return Results.NotFound();
        }

        if (!File.Exists(page.FileReference))
        {
            return Results.NotFound();
        }

        var contentType = Path.GetExtension(page.FileReference).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".pnm" => "image/x-portable-anymap",
            ".bmp" => "image/bmp",
            _ => "image/jpeg"
        };

        var stream = await storage.OpenReadAsync(page.FileReference, ct);
        return Results.Stream(stream, contentType: contentType, enableRangeProcessing: true);
    }

    public static async Task<IResult> ExportScanAsync(
        string id,
        string? format,
        IJobRepository jobRepo,
        IPageRepository pageRepo,
        IPageStorage storage,
        IPdfExporter pdfExporter,
        CancellationToken ct = default)
    {
        if (!string.Equals(format, "pdf", StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest(new { error = "Format d'export non supporté. Seul 'pdf' est disponible." });
        }

        var job = await jobRepo.GetAsync(id, ct);
        if (job is null)
        {
            return Results.NotFound();
        }

        var pages = await pageRepo.ListByJobAsync(id, ct);
        var persistedPages = pages
            .Where(p => p.Status == PageStatus.Persisted && !string.IsNullOrEmpty(p.FileReference) && File.Exists(p.FileReference))
            .OrderBy(p => p.PageNumber)
            .ToList();

        if (persistedPages.Count == 0)
        {
            return Results.Problem("Aucune page disponible pour l'export.", statusCode: StatusCodes.Status404NotFound);
        }

        var pdfPages = new List<PdfPageInput>();
        var streamsToDispose = new List<Stream>();

        try
        {
            foreach (var p in persistedPages)
            {
                var stream = await storage.OpenReadAsync(p.FileReference!, ct);
                streamsToDispose.Add(stream);
                pdfPages.Add(new PdfPageInput(
                    stream,
                    p.Width > 0 ? p.Width : 2480,
                    p.Height > 0 ? p.Height : 3508,
                    p.Dpi > 0 ? p.Dpi : 300,
                    p.PixelFormat ?? "color"));
            }

            var pdfStream = new MemoryStream();
            await pdfExporter.ExportPdfAsync(pdfPages, pdfStream, ct);
            pdfStream.Position = 0;

            return Results.Stream(
                pdfStream,
                contentType: "application/pdf",
                fileDownloadName: $"scan-{id}.pdf");
        }
        finally
        {
            foreach (var s in streamsToDispose)
            {
                await s.DisposeAsync();
            }
        }
    }

    public static async Task<IResult> ResumeScanAsync(
        string id,
        int? fromPage,
        JobManager jobs,
        CancellationToken ct = default)
    {
        try
        {
            var job = await jobs.ResumeJobAsync(id, fromPage, ct);
            return Results.Accepted($"/api/v1/scans/{job.Id}", job);
        }
        catch (ScanEngineException ex)
        {
            return ex.ToProblem();
        }
    }

    public static async Task<IResult> RetryScanAsync(
        string id,
        JobManager jobs,
        CancellationToken ct = default)
    {
        try
        {
            var job = await jobs.RetryJobAsync(id, ct);
            return Results.Accepted($"/api/v1/scans/{job.Id}", job);
        }
        catch (ScanEngineException ex)
        {
            return ex.ToProblem();
        }
    }
}
