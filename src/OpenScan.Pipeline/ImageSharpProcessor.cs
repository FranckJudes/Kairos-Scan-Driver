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

    public ImageSharpProcessor(int jpegQuality = 90, Action<string>? logAction = null)
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
            // eSCL renvoie déjà un JPEG produit par le scanner lui-même (contrairement à
            // SANE/WIA/TWAIN qui livrent du PNM/BMP brut sans perte) — le décoder puis le
            // réencoder ici recompresserait une image déjà compressée une fois par l'appareil,
            // dégradant visiblement la qualité pour rien. Si la source est déjà du JPEG (détecté
            // par ses octets magiques FF D8 FF), on la recopie telle quelle sans repasser par le
            // JpegEncoder.
            if (page.Data.CanSeek && await IsJpegAsync(page.Data, ct))
            {
                return await PassthroughJpegAsync(page, stopwatch, ct);
            }

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

    private static async Task<bool> IsJpegAsync(Stream data, CancellationToken ct)
    {
        var startPosition = data.Position;
        var header = new byte[3];
        var read = await data.ReadAsync(header.AsMemory(0, 3), ct);
        data.Position = startPosition;
        return read == 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF;
    }

    private async Task<ProcessedImageResult> PassthroughJpegAsync(AcquiredPage page, Stopwatch stopwatch, CancellationToken ct)
    {
        var startPosition = page.Data.Position;
        var info = await Image.IdentifyAsync(page.Data, ct);
        page.Data.Position = startPosition;

        var outputStream = new MemoryStream();
        await page.Data.CopyToAsync(outputStream, ct);
        outputStream.Position = 0;

        stopwatch.Stop();
        _logAction?.Invoke(
            $"[ImageSharpProcessor] Page {page.PageNumber} déjà en JPEG (source eSCL) — recopiée sans recompression en {stopwatch.ElapsedMilliseconds}ms ({info.Width}x{info.Height})");

        return new ProcessedImageResult(
            outputStream,
            info.Width,
            info.Height,
            page.PixelFormat ?? "color",
            stopwatch.Elapsed);
    }
}
