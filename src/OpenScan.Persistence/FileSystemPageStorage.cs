using System.Security.Cryptography;
using OpenScan.Core.Abstractions;

namespace OpenScan.Persistence;

/// <summary>
/// Stockage des fichiers image sur disque, séparé de SQLite (section 18).
/// Chaque page est écrite en flux (pas de chargement intégral en mémoire) et hachée au passage.
/// </summary>
public sealed class FileSystemPageStorage : IPageStorage
{
    private readonly string _rootDirectory;

    public FileSystemPageStorage(string rootDirectory)
    {
        _rootDirectory = rootDirectory;
        Directory.CreateDirectory(_rootDirectory);
    }

    public Task<(string Reference, string Checksum)> SaveAsync(string jobId, string pageId, Stream data, CancellationToken ct = default) =>
        SaveAsync(jobId, pageId, data, extension: ".jpg", ct);

    public async Task<(string Reference, string Checksum)> SaveAsync(string jobId, string pageId, Stream data, string extension, CancellationToken ct = default)
    {
        var jobDirectory = Path.Combine(_rootDirectory, jobId);
        Directory.CreateDirectory(jobDirectory);
        var ext = string.IsNullOrWhiteSpace(extension) ? ".jpg" : (extension.StartsWith('.') ? extension : "." + extension);
        var path = Path.Combine(jobDirectory, $"{pageId}{ext}");

        using var sha256 = SHA256.Create();
        await using (var fileStream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        await using (var hashingStream = new CryptoStream(fileStream, sha256, CryptoStreamMode.Write, leaveOpen: false))
        {
            await data.CopyToAsync(hashingStream, ct);
        }

        var checksum = Convert.ToHexString(sha256.Hash!);
        return (path, checksum);
    }

    public Task<Stream> OpenReadAsync(string reference, CancellationToken ct = default)
    {
        Stream stream = new FileStream(reference, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        return Task.FromResult(stream);
    }
}
