namespace AIDetectorMcp.Pangram;

public static class ToolStatus
{
    public const string Ok = "ok";
    public const string Pending = "pending";
    public const string Failed = "failed";
    public const string Error = "error";
}

public sealed record ToolError(int? HttpStatus, string Message, string? Hint);

public sealed record WindowSummary(
    int StartIndex,
    int EndIndex,
    string? Label,
    double AiAssistanceScore,
    string? Confidence,
    bool? IsHumanized,
    string? Excerpt);

public sealed record DetectionSummary(
    string? Verdict,
    string? Headline,
    string? Prediction,
    string? ModelVersion,
    double FractionAi,
    double FractionAiAssisted,
    double FractionHuman,
    int AiSegments,
    int AiAssistedSegments,
    int HumanSegments,
    string? DashboardLink,
    IReadOnlyList<WindowSummary> Windows);

public sealed record ModelsToolResult(string Status, IReadOnlyList<string>? Models, string? DefaultModel, ToolError? Error);

public sealed record DetectionToolResult(
    string Status,
    string? TaskId,
    string? Stage,
    DetectionSummary? Result,
    string? FailureReason,
    ToolError? Error);

public sealed record BulkSubmitToolResult(
    string Status,
    string? BulkId,
    string? BulkStatus,
    int TotalItems,
    int AcceptedCount,
    IReadOnlyList<BulkItemRef>? RejectedItems,
    int EstimatedBillableUnits,
    ToolError? Error);

public sealed record BulkStatusToolResult(
    string Status,
    string? BulkId,
    string? BulkStatus,
    bool IsTerminal,
    int TotalItems,
    int Accepted,
    int Succeeded,
    int Failed,
    DateTimeOffset? CreatedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    ToolError? Error);

public sealed record BulkItemSummary(
    int Index,
    string? Id,
    string? Stage,
    string? Error,
    DetectionSummary? Result);

public sealed record BulkResultsToolResult(
    string Status,
    string? BulkId,
    int Offset,
    int Limit,
    int TotalItems,
    int? NextOffset,
    IReadOnlyList<BulkItemSummary>? Items,
    IReadOnlyList<BulkItemRef>? FailedItems,
    ToolError? Error);

public sealed record FileUploadItemSummary(
    string FileName,
    string? PublicDashboardLink,
    IReadOnlyDictionary<string, string>? OtherFields);

public sealed record FileUploadToolResult(
    string Status,
    IReadOnlyList<FileUploadItemSummary>? Files,
    ToolError? Error);
