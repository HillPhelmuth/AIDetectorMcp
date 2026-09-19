using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIDetectorMcp.Pangram;

internal static class PangramJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

public static class PangramStages
{
    public const string Success = "STAGE_SUCCESS";
    public const string Failed = "STAGE_FAILED";

    public static bool IsTerminal(string? stage) => stage is Success or Failed;
}

public static class PangramBulkStatuses
{
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Partial = "partial";

    public static bool IsTerminal(string? status) => status is Succeeded or Failed or Partial;
}

public sealed record ModelsResponse(IReadOnlyList<string> Models);

public sealed record CreateTaskRequest(string Text, string Model, bool PublicDashboardLink);

public sealed record CreateTaskResponse(string TaskId);

public sealed record DetectionWindow(
    string? Text,
    string? Label,
    double AiAssistanceScore,
    string? Confidence,
    int StartIndex,
    int EndIndex,
    int WordCount,
    int TokenLength,
    bool? IsHumanized,
    double? HumanizerScore);

public sealed record TaskResult
{
    public string? TaskId { get; init; }
    public string? Stage { get; init; }
    public string? Text { get; init; }
    public string? Version { get; init; }
    public string? Headline { get; init; }
    public string? Prediction { get; init; }
    public string? PredictionShort { get; init; }
    public double FractionAi { get; init; }
    public double FractionAiAssisted { get; init; }
    public double FractionHuman { get; init; }
    public int NumAiSegments { get; init; }
    public int NumAiAssistedSegments { get; init; }
    public int NumHumanSegments { get; init; }
    public string? DashboardLink { get; init; }
    public IReadOnlyList<DetectionWindow>? Windows { get; init; }
}

public sealed record BulkItemInput(string? Id, string Text);

public sealed record CreateBulkRequest
{
    public IReadOnlyList<string>? Text { get; init; }
    public IReadOnlyList<BulkItemInput>? Items { get; init; }
    public required string Model { get; init; }
}

public sealed record BulkItemRef(int Index, string? Id, string? TaskId, string? Stage, string? Error);

public sealed record CreateBulkResponse(
    string BulkId,
    string Status,
    int TotalItems,
    IReadOnlyList<BulkItemRef> AcceptedItems,
    IReadOnlyList<BulkItemRef> FailedItems);

public sealed record BulkStatusResponse
{
    public required string BulkId { get; init; }
    public required string Status { get; init; }
    public int TotalItems { get; init; }
    public int Accepted { get; init; }
    public int Succeeded { get; init; }
    public int Failed { get; init; }
    public string? CreatedAt { get; init; }
    public string? CompletedAt { get; init; }

    [JsonIgnore]
    public DateTimeOffset? CreatedAtUtc => EpochString.Parse(CreatedAt);

    [JsonIgnore]
    public DateTimeOffset? CompletedAtUtc => EpochString.Parse(CompletedAt);
}

public sealed record BulkResultItem(int Index, string? Id, string? TaskId, string? Stage, string? Error, TaskResult? Result);

public sealed record BulkResultsResponse(
    string BulkId,
    int Offset,
    int Limit,
    int TotalItems,
    IReadOnlyList<BulkResultItem> Items,
    IReadOnlyList<BulkItemRef> FailedItems);

public sealed class FileUploadResult
{
    public string? PublicDashboardLink { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; set; }
}

internal static class EpochString
{
    public static DateTimeOffset? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            return null;
        }

        return DateTimeOffset.UnixEpoch.AddTicks((long)(seconds * TimeSpan.TicksPerSecond));
    }
}
