using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;

namespace AIDetectorMcp.Pangram;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddPangramDetection(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<PangramOptions>()
            .Bind(configuration.GetSection(PangramOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);

        services.AddHttpClient(
                PangramClient.TextClientName,
                ConfigureClient(options => options.TextBaseUrl, TimeSpan.FromMinutes(2)))
            .AddResilienceHandler(
                "pangram-text",
                builder => ConfigurePipeline(builder, TimeSpan.FromSeconds(30)));

        services.AddHttpClient(
                PangramClient.FileClientName,
                ConfigureClient(options => options.FileBaseUrl, TimeSpan.FromMinutes(6)))
            .AddResilienceHandler(
                "pangram-file",
                builder => ConfigurePipeline(builder, TimeSpan.FromMinutes(5)));

        services.TryAddSingleton<PangramClient>();
        services.TryAddSingleton<PangramMcpTools>();
        services.TryAddSingleton<PangramFileMcpTools>();
        return services;
    }

    private static Action<IServiceProvider, HttpClient> ConfigureClient(
        Func<PangramOptions, Uri> baseUrl,
        TimeSpan timeout) =>
        (serviceProvider, httpClient) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<PangramOptions>>().Value;
            var uri = baseUrl(options).ToString();
            httpClient.BaseAddress = new Uri(uri.EndsWith('/') ? uri : uri + "/");
            httpClient.Timeout = timeout;
            httpClient.DefaultRequestHeaders.Add("x-api-key", options.ApiKey);
        };

    private static void ConfigurePipeline(
        ResiliencePipelineBuilder<HttpResponseMessage> builder,
        TimeSpan attemptTimeout)
    {
        builder.AddRetry(new HttpRetryStrategyOptions
        {
            MaxRetryAttempts = 3,
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,
            Delay = TimeSpan.FromSeconds(1),
            ShouldRetryAfterHeader = true,
            ShouldHandle = args => ValueTask.FromResult(ShouldRetry(args.Outcome, args.Context)),
        });

        builder.AddTimeout(attemptTimeout);
    }

    private static bool ShouldRetry(
        Outcome<HttpResponseMessage> outcome,
        ResilienceContext context)
    {
        if (outcome.Result is { } response)
        {
            var status = response.StatusCode;
            if (status is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            {
                return true;
            }

            if ((int)status >= 500)
            {
                return response.RequestMessage?.Method == HttpMethod.Get;
            }

            return false;
        }

        if (outcome.Exception is HttpRequestException or Polly.Timeout.TimeoutRejectedException)
        {
            return context.GetRequestMessage()?.Method == HttpMethod.Get;
        }

        return false;
    }
}
