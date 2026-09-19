using OpenScan.Contracts;

namespace OpenScan.Core.Abstractions;

public interface IJobRepository
{
    Task SaveAsync(ScanJob job, CancellationToken ct = default);
    Task<ScanJob?> GetAsync(string jobId, CancellationToken ct = default);
    Task<IReadOnlyList<ScanJob>> ListAsync(CancellationToken ct = default);
}

public interface IPageRepository
{
    Task SaveAsync(ScanPage page, CancellationToken ct = default);
    Task<ScanPage?> GetAsync(string pageId, CancellationToken ct = default);
    Task<IReadOnlyList<ScanPage>> ListByJobAsync(string jobId, CancellationToken ct = default);
}

public interface IProfileRepository
{
    Task SaveAsync(ScanProfile profile, CancellationToken ct = default);
    Task<ScanProfile?> GetAsync(string profileId, CancellationToken ct = default);
    Task<IReadOnlyList<ScanProfile>> ListAsync(CancellationToken ct = default);
    Task DeleteAsync(string profileId, CancellationToken ct = default);
}

/// <summary>Stockage des fichiers image, contrôlé par l'Agent (section 18) — jamais dans SQLite.</summary>
public interface IPageStorage
{
    Task<(string Reference, string Checksum)> SaveAsync(string jobId, string pageId, Stream data, CancellationToken ct = default);
    Task<Stream> OpenReadAsync(string reference, CancellationToken ct = default);
}
