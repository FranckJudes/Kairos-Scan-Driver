using System.Text;
using OpenScan.Pipeline;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace OpenScan.Tests;

public class MinimalPdfExporterTests
{
    private static MemoryStream CreateDummyJpeg(int width, int height)
    {
        using var image = new Image<Rgb24>(width, height);
        var ms = new MemoryStream();
        image.SaveAsJpeg(ms);
        ms.Position = 0;
        return ms;
    }

    [Fact]
    public async Task ExportPdfAsync_WithSinglePage_ProducesCompliantPdf()
    {
        var exporter = new MinimalPdfExporter();
        using var jpeg = CreateDummyJpeg(200, 300);

        var pages = new List<PdfPageInput>
        {
            new(jpeg, 200, 300, Dpi: 300, PixelFormat: "color")
        };

        using var pdfOutput = new MemoryStream();
        await exporter.ExportPdfAsync(pages, pdfOutput);

        var pdfBytes = pdfOutput.ToArray();
        var pdfText = Encoding.ASCII.GetString(pdfBytes);

        Assert.True(pdfBytes.Length > 0);
        Assert.StartsWith("%PDF-1.4", pdfText);
        Assert.Contains("/Type /Catalog", pdfText);
        Assert.Contains("/Type /Pages", pdfText);
        Assert.Contains("/Count 1", pdfText);
        Assert.Contains("/Type /Page", pdfText);
        Assert.Contains("/Filter /DCTDecode", pdfText);
        Assert.Contains("xref", pdfText);
        Assert.Contains("trailer", pdfText);
        Assert.Contains("%%EOF", pdfText);
    }

    [Fact]
    public async Task ExportPdfAsync_WithMultiplePages_ProducesMultiPagePdf()
    {
        var exporter = new MinimalPdfExporter();
        using var jpeg1 = CreateDummyJpeg(100, 100);
        using var jpeg2 = CreateDummyJpeg(200, 200);
        using var jpeg3 = CreateDummyJpeg(300, 300);

        var pages = new List<PdfPageInput>
        {
            new(jpeg1, 100, 100, Dpi: 150),
            new(jpeg2, 200, 200, Dpi: 200),
            new(jpeg3, 300, 300, Dpi: 300)
        };

        using var pdfOutput = new MemoryStream();
        await exporter.ExportPdfAsync(pages, pdfOutput);

        var pdfBytes = pdfOutput.ToArray();
        var pdfText = Encoding.ASCII.GetString(pdfBytes);

        Assert.Contains("/Count 3", pdfText);
        Assert.Contains("/Kids [ 3 0 R 6 0 R 9 0 R  ]", pdfText);
        Assert.Contains("3 0 obj", pdfText);
        Assert.Contains("6 0 obj", pdfText);
        Assert.Contains("9 0 obj", pdfText);
    }

    [Fact]
    public async Task ExportPdfAsync_WithZeroPages_ThrowsInvalidOperationException()
    {
        var exporter = new MinimalPdfExporter();
        using var pdfOutput = new MemoryStream();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            exporter.ExportPdfAsync([], pdfOutput));
    }
}
