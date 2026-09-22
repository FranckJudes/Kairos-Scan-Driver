using OpenScan.Contracts;

namespace OpenScan.Pipeline;

/// <summary>
/// Résultat du traitement et de la compression d'une page acquise.
/// </summary>
public sealed record ProcessedImageResult(
    Stream JpegStream,
    int Width,
    int Height,
    string PixelFormat,
    TimeSpan Elapsed);

/// <summary>
/// Traite et compresse en flux chaque page scannée dès son acquisition.
/// </summary>
public interface IImageProcessor
{
    /// <summary>
    /// Décode le flux brut de la page et l'encode immédiatement en JPEG haute performance.
    /// </summary>
    Task<ProcessedImageResult> ProcessPageAsync(
        AcquiredPage page,
        ScanProfile? profile = null,
        CancellationToken ct = default);
}
