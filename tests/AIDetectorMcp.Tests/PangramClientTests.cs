using System.Net;
using System.Text.Json;
using AIDetectorMcp.Pangram;

namespace AIDetectorMcp.Tests;

public sealed class PangramClientTests
{
    [Fact]
    public async Task ListModels_IsCachedUntilExpiration()
    {
        var calls = 0;
        var handler = new StubHttpMessageHandler(_ =>
        {
            calls++;
            return Task.FromResult(PangramTestHelpers.Json("{\"models\":[\"default\",\"pangram-4\"]}"));
        });
        var client = PangramTestHelpers.Client(handler, PangramTestHelpers.Options(o =>
            o.ModelCatalogCacheDuration = TimeSpan.FromMinutes(10)));

        var first = await client.ListModelsAsync();
        var second = await client.ListModelsAsync();

        Assert.True(first.IsSuccess);
        Assert.Equal(new[] { "default", "pangram-4" }, first.Value);
        Assert.Equal(first.Value, second.Value);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task TaskPollingReturnsSuccessWithWindowSummary()
    {
        var handler = new StubHttpMessageHandler(async request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                return PangramTestHelpers.Json("{\"task_id\":\"task-1\"}");
            }

            await Task.CompletedTask;
            return PangramTestHelpers.Json("""
                {
                  "task_id":"task-1",
                  "stage":"STAGE_SUCCESS",
                  "text":"Generated passage.",
                  "version":"4.0",
                  "headline":"AI Generated",
                  "prediction":"The text is likely AI-generated.",
                  "prediction_short":"AI",
                  "fraction_ai":0.8,
                  "fraction_ai_assisted":0.1,
                  "fraction_human":0.1,
                  "num_ai_segments":1,
                  "num_ai_assisted_segments":1,
                  "num_human_segments":1,
                  "windows":[{
                    "text":"Generated passage.",
                    "label":"AI-Generated",
                    "ai_assistance_score":0.8,
                    "confidence":"High",
                    "start_index":0,
                    "end_index":18,
                    "word_count":2,
                    "token_length":4,
                    "is_humanized":false,
                    "humanizer_score":0.0
                  }]
                }
                """);
        });
        var tools = PangramTestHelpers.Tools(handler);

        var result = await tools.DetectTextAsync("Generated passage.", include_excerpts: true);

        Assert.Equal(ToolStatus.Ok, result.Status);
        Assert.Equal("task-1", result.TaskId);
        Assert.Equal("AI", result.Result!.Verdict);
        Assert.Equal(0.8, result.Result.FractionAi);
        Assert.Equal("Generated passage.", result.Result.Windows.Single().Excerpt);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("test-key", handler.Requests[0].Headers.GetValues("x-api-key").Single());
    }

    [Fact]
    public async Task FailedTaskIsReturnedAsFailedData()
    {
        var handler = new StubHttpMessageHandler(request => Task.FromResult(
            request.Method == HttpMethod.Post
                ? PangramTestHelpers.Json("{\"task_id\":\"task-failed\"}")
                : PangramTestHelpers.Json("""
                    {
                      "task_id":"task-failed",
                      "stage":"STAGE_FAILED",
                      "headline":"preprocessing: no valid text"
                    }
                    """)));
        var tools = PangramTestHelpers.Tools(handler);

        var result = await tools.DetectTextAsync("some text");

        Assert.Equal(ToolStatus.Failed, result.Status);
        Assert.Equal("preprocessing: no valid text", result.FailureReason);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task UnknownStageReturnsPendingWhenTimeoutExpires()
    {
        var handler = new StubHttpMessageHandler(request => Task.FromResult(
            request.Method == HttpMethod.Post
                ? PangramTestHelpers.Json("{\"task_id\":\"task-pending\"}")
                : PangramTestHelpers.Json("{\"task_id\":\"task-pending\",\"stage\":\"STAGE_NEW\"}")));
        var tools = PangramTestHelpers.Tools(handler, PangramTestHelpers.Options(o =>
            o.DetectionPollTimeout = TimeSpan.Zero));

        var result = await tools.DetectTextAsync("some text");

        Assert.Equal(ToolStatus.Pending, result.Status);
        Assert.Equal("task-pending", result.TaskId);
        Assert.Equal("STAGE_NEW", result.Stage);
    }

    [Fact]
    public async Task BulkRequestUsesTextShapeWhenNoIdsArePresent()
    {
        string? body = null;
        var handler = new StubHttpMessageHandler(async request =>
        {
            body = await request.Content!.ReadAsStringAsync();
            return PangramTestHelpers.Json("""
                {
                  "bulk_id":"bulk-1",
                  "status":"queued",
                  "total_items":2,
                  "accepted_items":[
                    {"index":0,"task_id":"task-1"},
                    {"index":1,"task_id":"task-2"}
                  ],
                  "failed_items":[]
                }
                """);
        });
        var tools = PangramTestHelpers.Tools(handler);

        var result = await tools.SubmitBulkAsync([
            new PangramMcpTools.BulkTextItem("first passage"),
            new PangramMcpTools.BulkTextItem("second passage"),
        ]);

        using var document = JsonDocument.Parse(body!);
        Assert.True(document.RootElement.TryGetProperty("text", out var text));
        Assert.False(document.RootElement.TryGetProperty("items", out _));
        Assert.Equal(2, text.GetArrayLength());
        Assert.Equal(ToolStatus.Ok, result.Status);
        Assert.Equal("bulk-1", result.BulkId);
    }

    [Fact]
    public async Task DuplicateBulkIdsAreRejectedBeforeSubmission()
    {
        var handler = new StubHttpMessageHandler(_ =>
            Task.FromResult(PangramTestHelpers.Json("{}")));
        var client = PangramTestHelpers.Client(handler);

        var result = await client.CreateBulkAsync(new CreateBulkRequest
        {
            Model = "default",
            Items = [
                new BulkItemInput("same", "first"),
                new BulkItemInput("same", "second"),
            ],
        });

        Assert.False(result.IsSuccess);
        Assert.Contains("Duplicate item ids", result.Error!.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task BulkUnitCeilingIsCheckedBeforeSubmission()
    {
        var handler = new StubHttpMessageHandler(_ =>
            Task.FromResult(PangramTestHelpers.Json("{}")));
        var tools = PangramTestHelpers.Tools(handler, PangramTestHelpers.Options(o =>
            o.MaxBulkBillableUnits = 1));

        var result = await tools.SubmitBulkAsync([
            new PangramMcpTools.BulkTextItem("one"),
            new PangramMcpTools.BulkTextItem("two"),
        ]);

        Assert.Equal(ToolStatus.Error, result.Status);
        Assert.Equal(413, result.Error!.HttpStatus);
        Assert.Equal(2, result.EstimatedBillableUnits);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task PartialBulkStatusIsTerminal()
    {
        var handler = new StubHttpMessageHandler(_ => Task.FromResult(PangramTestHelpers.Json("""
            {
              "bulk_id":"bulk-1",
              "status":"partial",
              "total_items":3,
              "accepted":2,
              "succeeded":2,
              "failed":1,
              "created_at":"1760000000.0",
              "completed_at":"1760000030.0"
            }
            """)));
        var tools = PangramTestHelpers.Tools(handler);

        var result = await tools.GetBulkStatusAsync("bulk-1");

        Assert.Equal(ToolStatus.Ok, result.Status);
        Assert.True(result.IsTerminal);
        Assert.Equal("partial", result.BulkStatus);
        Assert.Equal(2, result.Succeeded);
        Assert.NotNull(result.CompletedAtUtc);
    }

    [Fact]
    public async Task BulkResultsArePagedAndTrimmed()
    {
        var handler = new StubHttpMessageHandler(_ => Task.FromResult(PangramTestHelpers.Json("""
            {
              "bulk_id":"bulk-1",
              "offset":0,
              "limit":2,
              "total_items":3,
              "items":[
                {
                  "index":0,
                  "id":"row-1",
                  "stage":"STAGE_SUCCESS",
                  "result":{
                    "stage":"STAGE_SUCCESS",
                    "prediction_short":"Mixed",
                    "windows":[{
                      "text":"This is a long window excerpt.",
                      "label":"AI-Assisted",
                      "ai_assistance_score":0.5,
                      "confidence":"Medium",
                      "start_index":0,
                      "end_index":31,
                      "word_count":6,
                      "token_length":8
                    }]
                  }
                },
                {"index":1,"id":"row-2","stage":"STAGE_PREPROCESSING","result":null}
              ],
              "failed_items":[]
            }
            """)));
        var tools = PangramTestHelpers.Tools(handler);

        var result = await tools.GetBulkResultsAsync("bulk-1", limit: 2, include_segments: true);

        Assert.Equal(ToolStatus.Pending, result.Status);
        Assert.Equal(2, result.Items!.Count);
        Assert.Equal(2, result.NextOffset);
        Assert.Equal("Mixed", result.Items[0].Result!.Verdict);
        Assert.Null(result.Items[0].Result!.Windows.Single().Excerpt);
    }

    [Fact]
    public async Task PaymentErrorIsReturnedWithoutRetryingRequest()
    {
        var handler = new StubHttpMessageHandler(_ => Task.FromResult(
            PangramTestHelpers.Json("{\"detail\":\"insufficient credits\"}", HttpStatusCode.PaymentRequired)));
        var client = PangramTestHelpers.Client(handler);

        var result = await client.CreateTaskAsync("text", "default", false);

        Assert.False(result.IsSuccess);
        Assert.Equal(402, result.Error!.HttpStatus);
        Assert.Contains("insufficient credits", result.Error.Message);
        Assert.Single(handler.Requests);
    }
}
