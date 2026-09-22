using System.Diagnostics;
using OpenScan.Backends.Mock;
using OpenScan.Contracts;
using OpenScan.Pipeline;
using SixLabors.ImageSharp;

namespace OpenScan.Tests;

public class ImageSharpProcessorTests
{
    [Fact]
    public async Task ProcessPageAsync_WithValidBmp_ProducesValidJpegAndMeasuresTime()
    {
        var processor = new ImageSharpProcessor(jpegQuality: 85);

        // Crée une page acquise avec un BMP généré par Mock
        var mockBackend = new MockScannerBackend();
        await mockBackend.StartAsync("mock-scanner-1", "job-test");

        AcquiredPage? acquired = null;
        await foreach (var page in mockBackend.AcquireAsync("mock-scanner-1", "job-test"))
        {
            acquired = page;
            break;
        }

        Assert.NotNull(acquired);

        var sw = Stopwatch.StartNew();
        var result = await processor.ProcessPageAsync(acquired);
        sw.Stop();

        Assert.NotNull(result);
        Assert.NotNull(result.JpegStream);
        Assert.True(result.JpegStream.Length > 0);
        Assert.Equal(2480, result.Width);
        Assert.Equal(3508, result.Height);

        // Vérifier les octets magiques JPEG (0xFF 0xD8)
        var buffer = new byte[2];
        result.JpegStream.Position = 0;
        await result.JpegStream.ReadExactlyAsync(buffer);
        Assert.Equal(0xFF, buffer[0]);
        Assert.Equal(0xD8, buffer[1]);

        // Vérifier que ImageSharp peut re-décoder le JPEG produit
        result.JpegStream.Position = 0;
        using var loaded = await Image.LoadAsync(result.JpegStream);
        Assert.Equal(2480, loaded.Width);
        Assert.Equal(3508, loaded.Height);

        // Vérifier que la conversion est rapide
        Assert.True(result.Elapsed.TotalMilliseconds > 0);
    }

    [Fact]
    public async Task ProcessPageAsync_WithPnm_ProducesValidJpeg()
    {
        var processor = new ImageSharpProcessor(jpegQuality: 85);

        // PNM P6 (RGB binaire) minimaliste 2x2
        var pnmHeader = "P6\n2 2\n255\n"u8.ToArray();
        var pnmPixels = new byte[]
        {
            255, 0, 0,    0, 255, 0,
            0, 0, 255,    255, 255, 0
        };
        var pnmBytes = pnmHeader.Concat(pnmPixels).ToArray();

        var page = new AcquiredPage
        {
            PageNumber = 1,
            Width = 2,
            Height = 2,
            Dpi = 300,
            PixelFormat = "color",
            Source = "flatbed",
            Data = new MemoryStream(pnmBytes)
        };

        var result = await processor.ProcessPageAsync(page);

        Assert.NotNull(result);
        Assert.Equal(2, result.Width);
        Assert.Equal(2, result.Height);

        var buffer = new byte[2];
        result.JpegStream.Position = 0;
        await result.JpegStream.ReadExactlyAsync(buffer);
        Assert.Equal(0xFF, buffer[0]);
        Assert.Equal(0xD8, buffer[1]);
    }

    [Fact]
    public async Task ProcessPageAsync_WithCorruptData_ThrowsScanEngineException()
    {
        var processor = new ImageSharpProcessor();
        var corruptData = new MemoryStream([1, 2, 3, 4, 5]);

        var page = new AcquiredPage
        {
            PageNumber = 1,
            Width = 100,
            Height = 100,
            Dpi = 300,
            PixelFormat = "color",
            Source = "adf",
            Data = corruptData
        };

        var ex = await Assert.ThrowsAsync<ScanEngineException>(() => processor.ProcessPageAsync(page));
        Assert.Equal(ErrorCode.DriverError, ex.Code);
    }
}
