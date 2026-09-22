namespace OpenScan.Pipeline;

/// <summary>
/// Données d'une page à inclure dans un document PDF.
/// </summary>
public sealed record PdfPageInput(
    Stream JpegStream,
    int Width,
    int Height,
    int Dpi = 300,
    string? PixelFormat = "color");

/// <summary>
/// Exporte un ensemble de pages JPEG en un document PDF multi-pages.
/// </summary>
public interface IPdfExporter
{
    /// <summary>
    /// Assemble les flux JPEG en un flux PDF sans ré-encodage (filtre DCTDecode).
    /// </summary>
    Task ExportPdfAsync(
        IReadOnlyList<PdfPageInput> pages,
        Stream outputStream,
        CancellationToken ct = default);
}
