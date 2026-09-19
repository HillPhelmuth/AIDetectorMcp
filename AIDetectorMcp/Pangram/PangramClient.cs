using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Timeout;

namespace AIDetectorMcp.Pangram;

public readonly record struct PangramResult<T>(T? Value, ToolError? Error)
{
    public bool IsSuccess => Error is null;

    public static PangramResult<T> Ok(T value) => new(value, null);

    public static PangramResult<T> Fail(ToolError error) => new(default, error);
}

/// <summary>
/// Thin Pangram REST client. It contains no MCP concerns so it can be tested
/// independently and reused by other hosts.
/// </summary>
public sealed class PangramClient(
    IHttpClientFactory httpClientFactory,
    IOptions<PangramOptions> options,
    TimeProvider timeProvider,
    ILogger<PangramClient> logger)
{
    public const string TextClientName = "Pangram.Text";
    public const string FileClientName = "Pangram.File";

    private readonly PangramOptions _options = options.Value;
    private readonly SemaphoreSlim _modelsLock = new(1, 1);
    private IReadOnlyList<string>? _cachedModels;
    private DateTimeOffset _modelsExpireAt;

    private HttpClient TextClient => httpClientFactory.CreateClient(TextClientName);
    private HttpClient FileClient => httpClientFactory.CreateClient(FileClientName);

    public async Task<PangramResult<IReadOnlyList<string>>> ListModelsAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        if (!forceRefresh && _cachedModels is not null && timeProvider.GetUtcNow() < _modelsExpireAt)
        {
            return PangramResult<IReadOnlyList<string>>.Ok(_cachedModels);
        }

        await _modelsLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!forceRefresh && _cachedModels is not null && timeProvider.GetUtcNow() < _modelsExpireAt)
            {
                return PangramResult<IReadOnlyList<string>>.Ok(_cachedModels);
            }

            var result = await SendJsonAsync<ModelsResponse>(
                TextClient,
                HttpMethod.Get,
                "models",
                body: null,
                cancellationToken).ConfigureAwait(false);

            if (!result.IsSuccess)
            {
                return PangramResult<IReadOnlyList<string>>.Fail(result.Error!);
            }

            _cachedModels = result.Value!.Models ?? [];
            _modelsExpireAt = timeProvider.GetUtcNow() + _options.ModelCatalogCacheDuration;
            return PangramResult<IReadOnlyList<string>>.Ok(_cachedModels);
        }
        finally
        {
            _modelsLock.Release();
        }
    }

    public async Task<PangramResult<string>> ResolveModelAsync(
        string? requested,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(requested))
        {
            return PangramResult<string>.Ok(_options.DefaultModel);
        }

        var models = await ListModelsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!models.IsSuccess)
        {
            return PangramResult<string>.Fail(models.Error!);
        }

        return models.Value!.Contains(requested, StringComparer.Ordinal)
            ? PangramResult<string>.Ok(requested)
            : PangramResult<string>.Fail(new ToolError(
                null,
                $"Model '{requested}' is not available to this API key.",
                $"Use one of: {string.Join(", ", models.Value!)}"));
    }

    public Task<PangramResult<CreateTaskResponse>> CreateTaskAsync(
        string text,
        string model,
        bool publicDashboardLink,
        CancellationToken cancellationToken = default) =>
        SendJsonAsync<CreateTaskResponse>(
            TextClient,
            HttpMethod.Post,
            "task",
            new CreateTaskRequest(text, model, publicDashboardLink),
            cancellationToken);

    public Task<PangramResult<TaskResult>> GetTaskAsync(
        string taskId,
        CancellationToken cancellationToken = default) =>
        SendJsonAsync<TaskResult>(
            TextClient,
            HttpMethod.Get,
            $"task/{Uri.EscapeDataString(taskId)}",
            body: null,
            cancellationToken);

    public async Task<PangramResult<TaskResult>> WaitForTaskAsync(
        string taskId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var deadline = timeProvider.GetUtcNow() + timeout;
        var delay = _options.InitialPollDelay;

        while (true)
        {
            var snapshot = await GetTaskAsync(taskId, cancellationToken).ConfigureAwait(false);
            if (!snapshot.IsSuccess || PangramStages.IsTerminal(snapshot.Value!.Stage))
            {
                return snapshot;
            }

            var remaining = deadline - timeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                return snapshot;
            }

            await Task.Delay(
                delay < remaining ? delay : remaining,
                timeProvider,
                cancellationToken).ConfigureAwait(false);

            delay = TimeSpan.FromMilliseconds(Math.Min(
                delay.TotalMilliseconds * 1.5,
                _options.MaxPollDelay.TotalMilliseconds));
        }
    }

    public int EstimateBillableUnits(IEnumerable<string> texts)
    {
        var block = Math.Max(1, _options.WordsPerBillableUnit);
        long total = 0;

        foreach (var text in texts)
        {
            var words = CountWords(text);
            total += Math.Max(1, (words + block - 1) / block);
            if (total >= int.MaxValue)
            {
                return int.MaxValue;
            }
        }

        return (int)total;
    }

    public async Task<PangramResult<CreateBulkResponse>> CreateBulkAsync(
        CreateBulkRequest request,
        CancellationToken cancellationToken = default)
    {
        var hasText = request.Text is { Count: > 0 };
        var hasItems = request.Items is { Count: > 0 };
        if (hasText == hasItems)
        {
            return PangramResult<CreateBulkResponse>.Fail(new ToolError(
                null,
                "Bulk request must contain exactly one of 'text' or 'items', and it must be non-empty.",
                null));
        }

        if (hasItems)
        {
            var duplicates = request.Items!
                .Where(item => !string.IsNullOrEmpty(item.Id))
                .GroupBy(item => item.Id!, StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToList();

            if (duplicates.Count > 0)
            {
                return PangramResult<CreateBulkResponse>.Fail(new ToolError(
                    null,
                    $"Duplicate item ids: {string.Join(", ", duplicates)}",
                    "Item ids must be unique within a bulk job."));
            }
        }

        var result = await SendJsonAsync<CreateBulkResponse>(
            TextClient,
            HttpMethod.Post,
            "bulk",
            request,
            cancellationToken).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            logger.LogInformation(
                "Pangram bulk job {BulkId} created with status {Status}",
                result.Value!.BulkId,
                result.Value.Status);
        }

        return result;
    }

    public Task<PangramResult<BulkStatusResponse>> GetBulkStatusAsync(
        string bulkId,
        CancellationToken cancellationToken = default) =>
        SendJsonAsync<BulkStatusResponse>(
            TextClient,
            HttpMethod.Get,
            $"bulk/{Uri.EscapeDataString(bulkId)}",
            body: null,
            cancellationToken);

    public Task<PangramResult<BulkResultsResponse>> GetBulkResultsAsync(
        string bulkId,
        int offset,
        int limit,
        CancellationToken cancellationToken = default)
    {
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, 1000);

        return SendJsonAsync<BulkResultsResponse>(
            TextClient,
            HttpMethod.Get,
            $"bulk/{Uri.EscapeDataString(bulkId)}/results?offset={offset}&limit={limit}",
            body: null,
            cancellationToken);
    }

    public async Task<PangramResult<IReadOnlyList<FileUploadResult>>> UploadFilesAsync(
        IReadOnlyList<string> fullPaths,
        bool publicDashboardLink,
        CancellationToken cancellationToken = default)
    {
        using var form = new MultipartFormDataContent();
        foreach (var path in fullPaths)
        {
            var stream = File.OpenRead(path);
            var part = new StreamContent(stream);
            part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(part, "files", Path.GetFileName(path));
        }

        form.Add(new StringContent(publicDashboardLink ? "true" : "false"), "public_dashboard_link");

        using var request = new HttpRequestMessage(HttpMethod.Post, string.Empty)
        {
            Content = form,
        };

        return await SendAsync<IReadOnlyList<FileUploadResult>>(
            FileClient,
            request,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<PangramResult<T>> SendJsonAsync<T>(
        HttpClient client,
        HttpMethod method,
        string relativeUrl,
        object? body,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, relativeUrl);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType(), options: PangramJson.Options);
        }

        return await SendAsync<T>(client, request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<PangramResult<T>> SendAsync<T>(
        HttpClient client,
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var bodyText = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var error = MapError(response.StatusCode, bodyText);
                logger.LogWarning(
                    "Pangram {Method} {Url} failed: {Status} {Message}",
                    request.Method,
                    request.RequestUri,
                    (int)response.StatusCode,
                    error.Message);
                return PangramResult<T>.Fail(error);
            }

            var value = await response.Content.ReadFromJsonAsync<T>(PangramJson.Options, cancellationToken)
                .ConfigureAwait(false);

            return value is null
                ? PangramResult<T>.Fail(new ToolError(
                    (int)response.StatusCode,
                    "Pangram returned an empty response body.",
                    null))
                : PangramResult<T>.Ok(value);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is HttpRequestException or TimeoutRejectedException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(
                ex,
                "Pangram {Method} {Url} failed without a usable response",
                request.Method,
                request.RequestUri);

            var hint = request.Method == HttpMethod.Post
                ? "The request may or may not have been accepted. Do not blindly resubmit; it could be billed twice."
                : "Transient failure. It is safe to try this read again shortly.";

            return PangramResult<T>.Fail(new ToolError(
                null,
                $"Could not reach Pangram: {ex.GetType().Name}",
                hint));
        }
    }

    internal static ToolError MapError(HttpStatusCode status, string? body)
    {
        var code = (int)status;
        var detail = ExtractDetail(body);
        var (message, hint) = code switch
        {
            400 => ("Pangram rejected the request as malformed.", "Check the input; this is likely a tool bug."),
            401 => ("Pangram API key is missing or invalid.", "This is a configuration problem. Stop and tell the user."),
            402 => ("The Pangram account is out of credits.", "Stop and tell the user; retrying will not help."),
            403 => ("The model is not enabled for this key, or the job belongs to a different key.", "Call pangram_list_models and pick an available model."),
            404 => ("Task or bulk job not found.", "Check the id. Bulk jobs are only retained for 48 hours after completion."),
            413 => ("Payload too large.", "Split the bulk job into smaller jobs, or upload a smaller file."),
            415 => ("Unsupported file type.", "Convert the file, or extract its text and use pangram_detect_text."),
            422 => ("Pangram could not validate the request.", "Read the detail, fix the input, and do not resubmit unchanged."),
            429 => ("Rate limited by Pangram.", "Wait before trying again."),
            503 => ("The selected model is temporarily unavailable.", "Try again later or pick another model."),
            >= 500 => ("Pangram had a server error.", "Do not resubmit POSTs blindly; reads can be retried later."),
            _ => ($"Unexpected HTTP {code} from Pangram.", null),
        };

        return new ToolError(
            code,
            detail is null ? message : $"{message} Detail: {detail}",
            hint);
    }

    private static string? ExtractDetail(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("detail", out var detail))
            {
                if (detail.ValueKind == JsonValueKind.String)
                {
                    return detail.GetString();
                }

                if (detail.ValueKind == JsonValueKind.Array)
                {
                    var messages = detail.EnumerateArray().Select(item =>
                    {
                        if (item.ValueKind != JsonValueKind.Object ||
                            !item.TryGetProperty("msg", out var message))
                        {
                            return item.ToString();
                        }

                        var location = item.TryGetProperty("loc", out var loc)
                            ? string.Join('.', loc.EnumerateArray().Select(part => part.ToString())) + ": "
                            : string.Empty;

                        return location + (message.GetString() ?? message.ToString());
                    });

                    return string.Join("; ", messages);
                }
            }
        }
        catch (JsonException)
        {
            // Fall through to the bounded raw response below.
        }

        return body.Length <= 300 ? body : body[..300] + "...";
    }

    private static int CountWords(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var count = 0;
        var inWord = false;
        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character))
            {
                inWord = false;
            }
            else if (!inWord)
            {
                inWord = true;
                count++;
            }
        }

        return count;
    }
}
