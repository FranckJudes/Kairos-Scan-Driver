using Dapper;
using OpenScan.Contracts;
using OpenScan.Core.Abstractions;

namespace OpenScan.Persistence;

public sealed class SqliteProfileRepository(SqliteConnectionFactory factory) : IProfileRepository
{
    public async Task SaveAsync(ScanProfile profile, CancellationToken ct = default)
    {
        using var connection = factory.Create();
        await connection.ExecuteAsync("""
            INSERT INTO Profiles
                (Id, Name, Source, Duplex, Resolution, PixelFormat, BitDepth, PageSize, Orientation,
                 Brightness, Contrast, Feeder, BlankPageDetection, Deskew, Crop, Compression, OutputFormat)
            VALUES
                (@Id, @Name, @Source, @Duplex, @Resolution, @PixelFormat, @BitDepth, @PageSize, @Orientation,
                 @Brightness, @Contrast, @Feeder, @BlankPageDetection, @Deskew, @Crop, @Compression, @OutputFormat)
            ON CONFLICT(Id) DO UPDATE SET
                Name = excluded.Name,
                Source = excluded.Source,
                Duplex = excluded.Duplex,
                Resolution = excluded.Resolution,
                PixelFormat = excluded.PixelFormat,
                BitDepth = excluded.BitDepth,
                PageSize = excluded.PageSize,
                Orientation = excluded.Orientation,
                Brightness = excluded.Brightness,
                Contrast = excluded.Contrast,
                Feeder = excluded.Feeder,
                BlankPageDetection = excluded.BlankPageDetection,
                Deskew = excluded.Deskew,
                Crop = excluded.Crop,
                Compression = excluded.Compression,
                OutputFormat = excluded.OutputFormat
            """, profile);
    }

    public async Task<ScanProfile?> GetAsync(string profileId, CancellationToken ct = default)
    {
        using var connection = factory.Create();
        return await connection.QuerySingleOrDefaultAsync<ScanProfile>(
            "SELECT * FROM Profiles WHERE Id = @profileId", new { profileId });
    }

    public async Task<IReadOnlyList<ScanProfile>> ListAsync(CancellationToken ct = default)
    {
        using var connection = factory.Create();
        var rows = await connection.QueryAsync<ScanProfile>("SELECT * FROM Profiles ORDER BY Name");
        return rows.ToList();
    }

    public async Task DeleteAsync(string profileId, CancellationToken ct = default)
    {
        using var connection = factory.Create();
        await connection.ExecuteAsync("DELETE FROM Profiles WHERE Id = @profileId", new { profileId });
    }
}
