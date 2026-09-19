using OpenScan.Contracts;
using OpenScan.Persistence;

namespace OpenScan.Tests;

public class SqlitePersistenceTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"openscan-tests-{Guid.NewGuid():n}.db");
    private readonly SqliteConnectionFactory _factory;

    public SqlitePersistenceTests()
    {
        _factory = new SqliteConnectionFactory($"Data Source={_dbPath}");
        SqliteSchema.EnsureCreated(_factory);
    }

    public void Dispose()
    {
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    [Fact]
    public async Task ProfileRepository_RoundTripsAllFields()
    {
        var repo = new SqliteProfileRepository(_factory);
        var profile = new ScanProfile
        {
            Id = "profile-1",
            Name = "Courrier",
            Source = "adf",
            Duplex = true,
            Resolution = 300,
            PixelFormat = "grayscale",
            BitDepth = 8,
            PageSize = "a4",
            Orientation = "auto",
            Brightness = 10,
            Contrast = -5,
            Feeder = "auto",
            BlankPageDetection = true,
            Deskew = true,
            Crop = "auto",
            Compression = "jpeg",
            OutputFormat = "pdf"
        };

        await repo.SaveAsync(profile);
        var reloaded = await repo.GetAsync("profile-1");

        Assert.NotNull(reloaded);
        Assert.Equal(profile.Name, reloaded!.Name);
        Assert.Equal(profile.Duplex, reloaded.Duplex);
        Assert.Equal(profile.BitDepth, reloaded.BitDepth);
        Assert.Equal(profile.Brightness, reloaded.Brightness);
        Assert.Equal(profile.OutputFormat, reloaded.OutputFormat);
    }

    [Fact]
    public async Task ProfileRepository_SaveTwice_UpsertsInsteadOfDuplicating()
    {
        var repo = new SqliteProfileRepository(_factory);
        var profile = new ScanProfile { Id = "profile-1", Name = "V1", Source = "adf", Resolution = 300, PixelFormat = "color" };
        await repo.SaveAsync(profile);

        profile.Name = "V2";
        await repo.SaveAsync(profile);

        var all = await repo.ListAsync();
        Assert.Single(all);
        Assert.Equal("V2", all[0].Name);
    }

    [Fact]
    public async Task JobRepository_RoundTripsNestedConfigurationAndError()
    {
        var repo = new SqliteJobRepository(_factory);
        var job = new ScanJob
        {
            Id = "JOB-1",
            ScannerId = "mock-scanner-1",
            ProfileId = "profile-1",
            RequestedConfiguration = new ScanProfile { Id = "profile-1", Name = "V1", Resolution = 300 },
            EffectiveConfiguration = new EffectiveConfiguration
            {
                Requested = new Dictionary<string, object?> { ["resolution"] = 350 },
                Effective = new Dictionary<string, object?> { ["resolution"] = 300 },
                Adjustments = [new ConfigurationAdjustment { Parameter = "resolution", Reason = "nearest_supported" }]
            },
            Status = JobStatus.Error,
            TotalPages = 2,
            ProcessedPages = 1,
            FailedPages = 1,
            Error = new ScanError { Code = ErrorCode.PaperJam, Message = "bourrage papier", JobId = "JOB-1" }
        };

        await repo.SaveAsync(job);
        var reloaded = await repo.GetAsync("JOB-1");

        Assert.NotNull(reloaded);
        Assert.Equal(JobStatus.Error, reloaded!.Status);
        Assert.Equal("V1", reloaded.RequestedConfiguration?.Name);
        Assert.Single(reloaded.EffectiveConfiguration!.Adjustments);
        Assert.Equal(ErrorCode.PaperJam, reloaded.Error?.Code);
    }

    [Fact]
    public async Task PageRepository_ListByJob_ReturnsOnlyMatchingPagesOrderedByPageNumber()
    {
        var repo = new SqlitePageRepository(_factory);
        await repo.SaveAsync(new ScanPage { Id = "JOB-1-0002", JobId = "JOB-1", PageNumber = 2, Status = PageStatus.Persisted });
        await repo.SaveAsync(new ScanPage { Id = "JOB-1-0001", JobId = "JOB-1", PageNumber = 1, Status = PageStatus.Persisted });
        await repo.SaveAsync(new ScanPage { Id = "JOB-2-0001", JobId = "JOB-2", PageNumber = 1, Status = PageStatus.Persisted });

        var pages = await repo.ListByJobAsync("JOB-1");

        Assert.Equal(2, pages.Count);
        Assert.Equal([1, 2], pages.Select(p => p.PageNumber));
    }
}
