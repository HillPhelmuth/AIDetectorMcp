using System.Net;
using System.Text;
using AIDetectorMcp.Pangram;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AIDetectorMcp.Tests;

internal sealed class StubHttpMessageHandler(
    Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return await responder(request).ConfigureAwait(false);
    }
}

internal sealed class StubHttpClientFactory : IHttpClientFactory
{
    private readonly HttpClient _client;

    public StubHttpClientFactory(HttpMessageHandler handler)
    {
        _client = new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = new Uri("https://pangram.test/"),
        };
        _client.DefaultRequestHeaders.Add("x-api-key", "test-key");
    }

    public HttpClient CreateClient(string name) => _client;
}

internal static class PangramTestHelpers
{
    public static PangramOptions Options(Action<PangramOptions>? configure = null)
    {
        var options = new PangramOptions
        {
            ApiKey = "test-key",
            DetectionPollTimeout = TimeSpan.FromSeconds(1),
            InitialPollDelay = TimeSpan.FromMilliseconds(1),
            MaxPollDelay = TimeSpan.FromMilliseconds(1),
        };
        configure?.Invoke(options);
        return options;
    }

    public static PangramClient Client(
        HttpMessageHandler handler,
        PangramOptions? options = null) =>
        new(
            new StubHttpClientFactory(handler),
            Microsoft.Extensions.Options.Options.Create(options ?? Options()),
            TimeProvider.System,
            NullLogger<PangramClient>.Instance);

    public static PangramMcpTools Tools(
        HttpMessageHandler handler,
        PangramOptions? options = null)
    {
        var value = options ?? Options();
        return new PangramMcpTools(Client(handler, value), Microsoft.Extensions.Options.Options.Create(value));
    }

    public static PangramFileMcpTools FileTools(
        HttpMessageHandler handler,
        PangramOptions options) =>
        new(PangramTestHelpers.Client(handler, options), Microsoft.Extensions.Options.Options.Create(options));

    public static HttpResponseMessage Json(
        string json,
        HttpStatusCode statusCode = HttpStatusCode.OK) =>
        new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    public static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "AIDetectorMcpTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
