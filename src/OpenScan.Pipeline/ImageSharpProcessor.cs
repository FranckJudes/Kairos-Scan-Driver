using System.Diagnostics;
using OpenScan.Contracts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;

namespace OpenScan.Pipeline;

/// <summary>
/// Implémentation haute performance de IImageProcessor basée sur SixLabors.ImageSharp.
/// Décode le flux brut (BMP, PNM, etc.) et encode immédiatement en JPEG sans latence.
/// </summary>
public sealed class ImageSharpProcessor : IImageProcessor
{
    private readonly int _jpegQuality;
    private readonly Action<string>? _logAction;

    public ImageSharpProcessor(int jpegQuality = 85, Action<string>? logAction = null)
    {
        _jpegQuality = Math.Clamp(jpegQuality, 1, 100);
        _logAction = logAction;
    }

    public async Task<ProcessedImageResult> ProcessPageAsync(
        AcquiredPage page,
        ScanProfile? profile = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(page.Data);

        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var image = await Image.LoadAsync(page.Data, ct);

            var outputStream = new MemoryStream();
            var encoder = new JpegEncoder
            {
                Quality = _jpegQuality
            };

            await image.SaveAsJpegAsync(outputStream, encoder, ct);
            outputStream.Position = 0;

            stopwatch.Stop();

            _logAction?.Invoke(
                $"[ImageSharpProcessor] Page {page.PageNumber} convertie en JPEG ({_jpegQuality}%) en {stopwatch.ElapsedMilliseconds}ms ({image.Width}x{image.Height})");

            return new ProcessedImageResult(
                outputStream,
                image.Width,
                image.Height,
                page.PixelFormat ?? "color",
                stopwatch.Elapsed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logAction?.Invoke($"[ImageSharpProcessor] Erreur de décodage page {page.PageNumber}: {ex.Message}");
            throw new ScanEngineException(
                ErrorCode.DriverError,
                $"Échec du décodage de l'image de la page {page.PageNumber}: {ex.Message}",
                recoverable: false,
                inner: ex);
        }
    }
}
