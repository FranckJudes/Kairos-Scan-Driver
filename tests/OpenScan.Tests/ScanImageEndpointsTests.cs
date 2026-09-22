using System.Collections.Concurrent;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using OpenScan.Agent;
using OpenScan.Contracts;
using OpenScan.Core.Abstractions;
using OpenScan.Persistence;
using OpenScan.Pipeline;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Microsoft.Extensions.DependencyInjection;

namespace OpenScan.Tests;

public class ScanImageEndpointsTests : IDisposable
{
    private readonly string _storageDir;

    public ScanImageEndpointsTests()
    {
        _storageDir = Path.Combine(Path.GetTempPath(), $"openscan-endpoint-tests-{Guid.NewGuid():n}");
        Directory.CreateDirectory(_storageDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_storageDir))
        {
            try { Directory.Delete(_storageDir, recursive: true); } catch { }
        }
    }

    private static MemoryStream CreateValidJpegStream(int width = 100, int height = 100)
    {
        using var image = new Image<Rgb24>(width, height);
        var ms = new MemoryStream();
        image.SaveAsJpeg(ms);
        ms.Position = 0;
        return ms;
    }

    private static DefaultHttpContext CreateHttpContext()
    {
        var context = new DefaultHttpContext();
        context.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        context.Response.Body = new MemoryStream();
        return context;
    }

    [Fact]
    public async Task GetImage_WithExistingPage_Returns200AndStreamsJpeg()
    {
        var jobRepo = new TestJobRepository();
        var pageRepo = new TestPageRepository();
        var storage = new FileSystemPageStorage(_storageDir);

        var jobId = "job-100";
        var pageId = "job-100-0001";

        await jobRepo.SaveAsync(new ScanJob { Id = jobId, Status = JobStatus.Completed });

        using var jpegStream = CreateValidJpegStream(200, 300);
        var (fileRef, checksum) = await storage.SaveAsync(jobId, pageId, jpegStream);

        await pageRepo.SaveAsync(new ScanPage
        {
            Id = pageId,
            JobId = jobId,
            PageNumber = 1,
            Width = 200,
            Height = 300,
            Dpi = 300,
            PixelFormat = "color",
            Status = PageStatus.Persisted,
            FileReference = fileRef,
            Checksum = checksum
        });

        var result = await ScanEndpoints.GetPageImageAsync(jobId, pageId, pageRepo, storage);

        var httpContext = CreateHttpContext();
        await result.ExecuteAsync(httpContext);

        Assert.Equal(200, httpContext.Response.StatusCode);
        Assert.Equal("image/jpeg", httpContext.Response.ContentType);

        httpContext.Response.Body.Position = 0;
        var bytes = ((MemoryStream)httpContext.Response.Body).ToArray();
        Assert.True(bytes.Length > 0);
        Assert.Equal(0xFF, bytes[0]);
        Assert.Equal(0xD8, bytes[1]);
    }

    [Fact]
    public async Task GetImage_WithNonExistentPage_ReturnsNotFound()
    {
        var pageRepo = new TestPageRepository();
        var storage = new FileSystemPageStorage(_storageDir);

        var result = await ScanEndpoints.GetPageImageAsync("job-1", "missing-page", pageRepo, storage);

        Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        var statusResult = (IStatusCodeHttpResult)result;
        Assert.Equal(404, statusResult.StatusCode);
    }

    [Fact]
    public async Task GetImage_WithJobIdMismatch_ReturnsNotFound()
    {
        var pageRepo = new TestPageRepository();
        var storage = new FileSystemPageStorage(_storageDir);

        var jobId = "job-200";
        var pageId = "job-200-0001";

        using var jpegStream = CreateValidJpegStream(100, 100);
        var (fileRef, checksum) = await storage.SaveAsync(jobId, pageId, jpegStream);

        await pageRepo.SaveAsync(new ScanPage
        {
            Id = pageId,
            JobId = jobId,
            PageNumber = 1,
            Status = PageStatus.Persisted,
            FileReference = fileRef,
            Checksum = checksum
        });

        var result = await ScanEndpoints.GetPageImageAsync("wrong-job-id", pageId, pageRepo, storage);

        Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        var statusResult = (IStatusCodeHttpResult)result;
        Assert.Equal(404, statusResult.StatusCode);
    }

    [Fact]
    public async Task ExportPdf_WithPersistedPages_Returns200AndApplicationPdf()
    {
        var jobRepo = new TestJobRepository();
        var pageRepo = new TestPageRepository();
        var storage = new FileSystemPageStorage(_storageDir);
        var pdfExporter = new MinimalPdfExporter();

        var jobId = "job-300";
        await jobRepo.SaveAsync(new ScanJob { Id = jobId, Status = JobStatus.Completed });

        for (var i = 1; i <= 2; i++)
        {
            var pageId = $"{jobId}-{i:D4}";
            using var jpegStream = CreateValidJpegStream(150, 200);
            var (fileRef, checksum) = await storage.SaveAsync(jobId, pageId, jpegStream);

            await pageRepo.SaveAsync(new ScanPage
            {
                Id = pageId,
                JobId = jobId,
                PageNumber = i,
                Width = 150,
                Height = 200,
                Dpi = 300,
                PixelFormat = "color",
                Status = PageStatus.Persisted,
                FileReference = fileRef,
                Checksum = checksum
            });
        }

        var result = await ScanEndpoints.ExportScanAsync(jobId, "pdf", jobRepo, pageRepo, storage, pdfExporter);

        var httpContext = CreateHttpContext();
        await result.ExecuteAsync(httpContext);

        Assert.Equal(200, httpContext.Response.StatusCode);
        Assert.Equal("application/pdf", httpContext.Response.ContentType);

        httpContext.Response.Body.Position = 0;
        var bytes = ((MemoryStream)httpContext.Response.Body).ToArray();
        Assert.True(bytes.Length > 0);

        var header = Encoding.ASCII.GetString(bytes, 0, 8);
        Assert.StartsWith("%PDF-1.4", header);
    }

    [Fact]
    public async Task ExportPdf_WithInvalidFormat_ReturnsBadRequest()
    {
        var jobRepo = new TestJobRepository();
        var pageRepo = new TestPageRepository();
        var storage = new FileSystemPageStorage(_storageDir);
        var pdfExporter = new MinimalPdfExporter();

        var result = await ScanEndpoints.ExportScanAsync("job-1", "docx", jobRepo, pageRepo, storage, pdfExporter);

        Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        var statusResult = (IStatusCodeHttpResult)result;
        Assert.Equal(400, statusResult.StatusCode);
    }

    [Fact]
    public async Task ExportPdf_WithJobWithoutPages_ReturnsNotFoundProblem()
    {
        var jobRepo = new TestJobRepository();
        var pageRepo = new TestPageRepository();
        var storage = new FileSystemPageStorage(_storageDir);
        var pdfExporter = new MinimalPdfExporter();

        var jobId = "job-empty";
        await jobRepo.SaveAsync(new ScanJob { Id = jobId, Status = JobStatus.Completed });

        var result = await ScanEndpoints.ExportScanAsync(jobId, "pdf", jobRepo, pageRepo, storage, pdfExporter);

        Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        var statusResult = (IStatusCodeHttpResult)result;
        Assert.Equal(StatusCodes.Status404NotFound, statusResult.StatusCode);
    }

    private sealed class TestJobRepository : IJobRepository
    {
        private readonly ConcurrentDictionary<string, ScanJob> _jobs = new();
        public Task SaveAsync(ScanJob job, CancellationToken ct = default) { _jobs[job.Id] = job; return Task.CompletedTask; }
        public Task<ScanJob?> GetAsync(string jobId, CancellationToken ct = default) => Task.FromResult(_jobs.GetValueOrDefault(jobId));
        public Task<IReadOnlyList<ScanJob>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ScanJob>>([.. _jobs.Values]);
    }

    private sealed class TestPageRepository : IPageRepository
    {
        private readonly ConcurrentDictionary<string, ScanPage> _pages = new();
        public Task SaveAsync(ScanPage page, CancellationToken ct = default) { _pages[page.Id] = page; return Task.CompletedTask; }
        public Task<ScanPage?> GetAsync(string pageId, CancellationToken ct = default) => Task.FromResult(_pages.GetValueOrDefault(pageId));
        public Task<IReadOnlyList<ScanPage>> ListByJobAsync(string jobId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ScanPage>>([.. _pages.Values.Where(p => p.JobId == jobId).OrderBy(p => p.PageNumber)]);
    }
}
