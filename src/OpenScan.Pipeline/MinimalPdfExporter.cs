using System.Globalization;
using System.Text;

namespace OpenScan.Pipeline;

/// <summary>
/// Assembleur PDF minimaliste et performant sans dépendance externe.
/// Intègre directement les flux d'images JPEG via le filtre /DCTDecode standard de PDF 1.4+.
/// </summary>
public sealed class MinimalPdfExporter : IPdfExporter
{
    public async Task ExportPdfAsync(
        IReadOnlyList<PdfPageInput> pages,
        Stream outputStream,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(outputStream);

        if (pages.Count == 0)
        {
            throw new InvalidOperationException("Impossible de générer un PDF avec 0 page.");
        }

        var countingStream = new CountingStream(outputStream);
        var totalObjects = 2 + (pages.Count * 3);
        var offsets = new long[totalObjects + 1];

        // 1. En-tête PDF 1.4 avec commentaire binaire (garantit le mode de transfert binaire)
        await WriteAsciiAsync(countingStream, "%PDF-1.4\r\n%\xE2\xE3\xCF\xD3\r\n", ct);

        // 2. Objet 1: Catalog
        offsets[1] = countingStream.BytesWritten;
        await WriteAsciiAsync(countingStream, "1 0 obj\r\n<< /Type /Catalog /Pages 2 0 R >>\r\nendobj\r\n", ct);

        // 3. Objet 2: Pages
        offsets[2] = countingStream.BytesWritten;
        var kidsBuilder = new StringBuilder();
        for (var i = 0; i < pages.Count; i++)
        {
            var pageObjId = 3 + (i * 3);
            kidsBuilder.Append($"{pageObjId} 0 R ");
        }
        await WriteAsciiAsync(countingStream,
            $"2 0 obj\r\n<< /Type /Pages /Kids [ {kidsBuilder} ] /Count {pages.Count} >>\r\nendobj\r\n", ct);

        // 4. Objets par page (Page, Content, Image XObject)
        for (var i = 0; i < pages.Count; i++)
        {
            var page = pages[i];
            var pageObjId = 3 + (i * 3);
            var contentObjId = pageObjId + 1;
            var imageObjId = pageObjId + 2;

            var dpi = page.Dpi > 0 ? page.Dpi : 300;
            var wPt = (page.Width * 72.0) / dpi;
            var hPt = (page.Height * 72.0) / dpi;

            var colorSpace = (page.PixelFormat?.Equals("gray", StringComparison.OrdinalIgnoreCase) == true ||
                              page.PixelFormat?.Equals("grayscale", StringComparison.OrdinalIgnoreCase) == true)
                ? "/DeviceGray"
                : "/DeviceRGB";

            // Objet Page
            offsets[pageObjId] = countingStream.BytesWritten;
            await WriteAsciiAsync(countingStream,
                $"{pageObjId} 0 obj\r\n<<\r\n  /Type /Page\r\n  /Parent 2 0 R\r\n  /MediaBox [ 0 0 {wPt.ToString("F2", CultureInfo.InvariantCulture)} {hPt.ToString("F2", CultureInfo.InvariantCulture)} ]\r\n  /Resources << /XObject << /Im1 {imageObjId} 0 R >> >>\r\n  /Contents {contentObjId} 0 R\r\n>>\r\nendobj\r\n", ct);

            // Objet Content Stream (applique la matrice d'échelle et affiche l'image)
            var contentCmd = $"q\r\n{wPt.ToString("F2", CultureInfo.InvariantCulture)} 0 0 {hPt.ToString("F2", CultureInfo.InvariantCulture)} 0 0 cm\r\n/Im1 Do\r\nQ\r\n";
            var contentBytes = Encoding.ASCII.GetBytes(contentCmd);

            offsets[contentObjId] = countingStream.BytesWritten;
            await WriteAsciiAsync(countingStream,
                $"{contentObjId} 0 obj\r\n<< /Length {contentBytes.Length} >>\r\nstream\r\n", ct);
            await countingStream.WriteAsync(contentBytes, ct);
            await WriteAsciiAsync(countingStream, "\r\nendstream\r\nendobj\r\n", ct);

            // Objet Image XObject (/Filter /DCTDecode pour flux JPEG natif)
            offsets[imageObjId] = countingStream.BytesWritten;
            long jpegLength;
            Stream jpegStream = page.JpegStream;
            if (jpegStream.CanSeek)
            {
                jpegLength = jpegStream.Length - jpegStream.Position;
            }
            else
            {
                var ms = new MemoryStream();
                await jpegStream.CopyToAsync(ms, ct);
                ms.Position = 0;
                jpegLength = ms.Length;
                jpegStream = ms;
            }

            await WriteAsciiAsync(countingStream,
                $"{imageObjId} 0 obj\r\n<<\r\n  /Type /XObject\r\n  /Subtype /Image\r\n  /Width {page.Width}\r\n  /Height {page.Height}\r\n  /ColorSpace {colorSpace}\r\n  /BitsPerComponent 8\r\n  /Filter /DCTDecode\r\n  /Length {jpegLength}\r\n>>\r\nstream\r\n", ct);

            await jpegStream.CopyToAsync(countingStream, ct);

            await WriteAsciiAsync(countingStream, "\r\nendstream\r\nendobj\r\n", ct);
        }

        // 5. Table XRef (chaque entrée fait exactement 20 octets)
        var startXref = countingStream.BytesWritten;
        await WriteAsciiAsync(countingStream, $"xref\r\n0 {totalObjects + 1}\r\n", ct);
        await WriteAsciiAsync(countingStream, "0000000000 65535 f \r\n", ct);

        for (var id = 1; id <= totalObjects; id++)
        {
            await WriteAsciiAsync(countingStream, $"{offsets[id]:D10} 00000 n \r\n", ct);
        }

        // 6. Trailer & startxref & %%EOF
        await WriteAsciiAsync(countingStream,
            $"trailer\r\n<<\r\n  /Size {totalObjects + 1}\r\n  /Root 1 0 R\r\n>>\r\nstartxref\r\n{startXref}\r\n%%EOF\r\n", ct);

        await countingStream.FlushAsync(ct);
    }

    private static async Task WriteAsciiAsync(CountingStream stream, string text, CancellationToken ct)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        await stream.WriteAsync(bytes, ct);
    }

    private sealed class CountingStream : Stream
    {
        private readonly Stream _inner;
        public long BytesWritten { get; private set; }

        public CountingStream(Stream inner) => _inner = inner;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => BytesWritten;
        public override long Position
        {
            get => BytesWritten;
            set => throw new NotSupportedException();
        }

        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => _inner.FlushAsync(ct);

        public override void Write(byte[] buffer, int offset, int count)
        {
            _inner.Write(buffer, offset, count);
            BytesWritten += count;
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            await _inner.WriteAsync(buffer.AsMemory(offset, count), ct);
            BytesWritten += count;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            await _inner.WriteAsync(buffer, ct);
            BytesWritten += buffer.Length;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
