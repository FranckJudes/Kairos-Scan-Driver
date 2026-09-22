using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using OpenScan.Backends.Escl;
using OpenScan.Contracts;
using Xunit;

namespace OpenScan.Tests;

public class EsclScannerBackendTests
{
    private const string SampleMfpXml = """
        <scan:ScannerCapabilities xmlns:scan="http://schemas.hp.com/imaging/escl/2011/05/03" xmlns:pwg="http://www.pwg.org/schemas/2010/12/sm">
          <pwg:Version>2.0</pwg:Version>
          <pwg:MakeAndModel>HP OfficeJet Pro</pwg:MakeAndModel>
          <pwg:SerialNumber>CN123456</pwg:SerialNumber>
          <scan:Platen>
            <scan:PlatenInputCaps>
              <scan:SupportedResolutions>
                <scan:DiscreteResolutions>
                  <scan:DiscreteResolution><scan:XResolution>300</scan:XResolution></scan:DiscreteResolution>
                </scan:DiscreteResolutions>
              </scan:SupportedResolutions>
            </scan:PlatenInputCaps>
          </scan:Platen>
          <scan:Adf>
            <scan:AdfSimplexInputCaps>
              <scan:SupportedResolutions>
                <scan:DiscreteResolutions>
                  <scan:DiscreteResolution><scan:XResolution>300</scan:XResolution></scan:DiscreteResolution>
                </scan:DiscreteResolutions>
              </scan:SupportedResolutions>
            </scan:AdfSimplexInputCaps>
          </scan:Adf>
        </scan:ScannerCapabilities>
        """;

    [Fact]
    public async Task DiscoverAsync_IgnoresUnreachableHostsAndReturnsAvailableScanners()
    {
        var fakeHandler = new FakeEsclHttpMessageHandler();
        fakeHandler.Handler = req =>
        {
            if (req.RequestUri?.Host == "192.168.1.50")
            {
                var resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(SampleMfpXml, Encoding.UTF8, "text/xml")
                };
                return Task.FromResult(resp);
            }

            throw new HttpRequestException("Host unreachable");
        };

        var httpClient = new HttpClient(fakeHandler);
        var factory = new FakeHttpClientFactory(httpClient);
        var options = Options.Create(new EsclOptions
        {
            KnownDevices = ["192.168.1.50:8080", "192.168.1.99"]
        });

        var backend = new EsclScannerBackend(factory, options);
        var scanners = await backend.DiscoverAsync();

        Assert.Single(scanners);
        var scanner = scanners[0];
        Assert.Equal("escl:192.168.1.50:8080", scanner.Id);
        Assert.Equal("HP OfficeJet Pro", scanner.Name);
        Assert.Equal("escl", scanner.Backend);
        Assert.Equal(ConnectionType.Network, scanner.ConnectionType);
    }

    [Fact]
    public async Task GetCapabilitiesAsync_QueriesAndParsesCapabilities()
    {
        var fakeHandler = new FakeEsclHttpMessageHandler();
        fakeHandler.Handler = req =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SampleMfpXml, Encoding.UTF8, "text/xml")
            };
            return Task.FromResult(resp);
        };

        var httpClient = new HttpClient(fakeHandler);
        var factory = new FakeHttpClientFactory(httpClient);
        var backend = new EsclScannerBackend(factory);

        var caps = await backend.GetCapabilitiesAsync("escl:192.168.1.50:8080");

        Assert.NotNull(caps);
        Assert.True(caps.Adf);
        Assert.True(caps.Flatbed);
        Assert.Contains(300, caps.Resolutions);
    }

    [Fact]
    public async Task ScanFlow_AcquiresPagesUntil404AndTerminatesNormally()
    {
        var fakeHandler = new FakeEsclHttpMessageHandler();
        var nextDocCount = 0;

        fakeHandler.Handler = req =>
        {
            var uri = req.RequestUri?.ToString() ?? "";

            // 1. ScannerCapabilities
            if (uri.EndsWith("/eSCL/ScannerCapabilities"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(SampleMfpXml, Encoding.UTF8, "text/xml")
                });
            }

            // 2. ScanJobs (POST)
            if (req.Method == HttpMethod.Post && uri.EndsWith("/eSCL/ScanJobs"))
            {
                var resp = new HttpResponseMessage(HttpStatusCode.Created);
                resp.Headers.Location = new Uri("http://192.168.1.50:8080/eSCL/ScanJobs/job-uuid-1234");
                return Task.FromResult(resp);
            }

            // 3. NextDocument (GET)
            if (req.Method == HttpMethod.Get && uri.Contains("/NextDocument"))
            {
                nextDocCount++;
                if (nextDocCount <= 2)
                {
                    // 2 premières pages disponibles
                    var dummyJpegBytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46 };
                    var resp = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(dummyJpegBytes)
                    };
                    resp.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
                    return Task.FromResult(resp);
                }

                // 3ème appel : bac vide -> 404
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            // 4. DELETE job
            if (req.Method == HttpMethod.Delete && uri.Contains("/ScanJobs/job-uuid-1234"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        };

        var httpClient = new HttpClient(fakeHandler);
        var factory = new FakeHttpClientFactory(httpClient);
        var backend = new EsclScannerBackend(factory);

        var scannerId = "escl:192.168.1.50:8080";
        var jobId = "JOB-ESCL-001";

        var config = new EffectiveConfiguration
        {
            Effective = new Dictionary<string, object?>
            {
                ["source"] = "adf",
                ["duplex"] = false,
                ["resolution"] = 300,
                ["pixel_format"] = "color"
            }
        };

        await backend.ConfigureAsync(scannerId, jobId, config);
        await backend.StartAsync(scannerId, jobId);

        var pages = new List<AcquiredPage>();
        await foreach (var page in backend.AcquireAsync(scannerId, jobId))
        {
            pages.Add(page);
        }

        // Exactement 2 pages acquises avant le 404
        Assert.Equal(2, pages.Count);
        Assert.Equal(1, pages[0].PageNumber);
        Assert.Equal(2, pages[1].PageNumber);
        Assert.Equal(300, pages[0].Dpi);
        Assert.Equal("color", pages[0].PixelFormat);
        Assert.Equal("adf", pages[0].Source);

        await backend.CancelAsync(scannerId, jobId);

        // Vérifier que DELETE a bien été appelé
        Assert.Contains(fakeHandler.RecordedRequests, r => r.Method == HttpMethod.Delete && r.RequestUri!.ToString().Contains("job-uuid-1234"));
    }

    [Fact]
    public async Task StartAsync_WhenServerReturns503_ThrowsScannerBusy()
    {
        var fakeHandler = new FakeEsclHttpMessageHandler();
        fakeHandler.Handler = req =>
        {
            if (req.Method == HttpMethod.Post)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent("Appareil occupé", Encoding.UTF8, "text/plain")
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        };

        var httpClient = new HttpClient(fakeHandler);
        var factory = new FakeHttpClientFactory(httpClient);
        var backend = new EsclScannerBackend(factory);

        var scannerId = "escl:192.168.1.50:8080";
        var jobId = "JOB-BUSY-001";

        await backend.ConfigureAsync(scannerId, jobId, new EffectiveConfiguration());

        var ex = await Assert.ThrowsAsync<ScanEngineException>(() => backend.StartAsync(scannerId, jobId));
        Assert.Equal(ErrorCode.ScannerBusy, ex.Code);
        Assert.True(ex.Recoverable);
    }

    private sealed class FakeEsclHttpMessageHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> RecordedRequests { get; } = [];
        public Func<HttpRequestMessage, Task<HttpResponseMessage>>? Handler { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RecordedRequests.Add(request);
            if (Handler is not null)
            {
                return Handler(request);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class FakeHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}
