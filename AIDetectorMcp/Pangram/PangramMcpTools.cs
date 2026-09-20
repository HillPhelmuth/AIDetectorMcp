using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using ModelContextProtocol.Server;
using Microsoft.Extensions.Options;

namespace AIDetectorMcp.Pangram;

/// <summary>
/// MCP-native Pangram tools. The HTTP client is kept separate so this class only
/// handles MCP schemas, validation, and compact model-facing results.
/// </summary>
public sealed class PangramMcpTools(
    PangramClient client,
    IOptions<PangramOptions> options)
{
    private readonly PangramOptions _options = options.Value;

    public const string ListModelsName = "pangram_list_models";
    public const string DetectTextName = "pangram_detect_text";
    public const string GetDetectionName = "pangram_get_detection";
    public const string SubmitBulkName = "pangram_submit_bulk";
    public const string GetBulkStatusName = "pangram_get_bulk_status";
    public const string GetBulkResultsName = "pangram_get_bulk_results";

    public static JsonSerializerOptions SerializerOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    [McpServerTool(
        Name = ListModelsName,
        Title = "List Pangram detection models",
        ReadOnly = true,
        Idempotent = true,
        Destructive = false,
        OpenWorld = true
        )]
    [Description(
        "List the Pangram AI-detection model selectors available to this account, in server order. " +
        "Free and non-blocking. Use this before passing an explicit model to another Pangram tool.")]
    public async Task<ModelsToolResult> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        var result = await client.ListModelsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? new ModelsToolResult(ToolStatus.Ok, result.Value, _options.DefaultModel, null)
            : new ModelsToolResult(ToolStatus.Error, null, _options.DefaultModel, result.Error);
    }

    [McpServerTool(
        Name = DetectTextName,
        Title = "Detect AI-generated text",
        ReadOnly = false,
        Idempotent = false,
        Destructive = false,
        OpenWorld = true
        )]
    [Description(
        """
        Analyze one passage for AI-generated or AI-assisted writing using Pangram. Consumes credits and blocks while Pangram processes the task, up to the configured timeout. If status is pending, call pangram_get_detection with the returned task_id. Returns AI, AI-assisted, and human fractions, segment counts, labels, confidence, and offsets. Pangram detection is a probabilistic signal, not proof.
        """)]
    public async Task<DetectionToolResult> DetectTextAsync(
        [Description("The non-empty text to analyze.")] string? text,
        [Description("Optional selector from pangram_list_models. Omit to use the configured default.")] string? model = null,
        [Description("Include short per-window text excerpts. Defaults to false to reduce response size.")] bool include_excerpts = false,
        [Description("Request a public dashboard link. Only honored when the host allows public links; the link exposes submitted text publicly.")] bool public_dashboard_link = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return DetectionError(null, new ToolError(null, "Text is empty.", "Provide non-empty text."));
        }

        if (public_dashboard_link && !_options.AllowPublicDashboardLinks)
        {
            return DetectionError(null, new ToolError(
                null,
                "Public dashboard links are disabled by the host.",
                "Retry without public_dashboard_link."));
        }

        var resolvedModel = await client.ResolveModelAsync(model, cancellationToken).ConfigureAwait(false);
        if (!resolvedModel.IsSuccess)
        {
            return DetectionError(null, resolvedModel.Error!);
        }

        var created = await client.CreateTaskAsync(
            text,
            resolvedModel.Value!,
            public_dashboard_link,
            cancellationToken).ConfigureAwait(false);
        if (!created.IsSuccess)
        {
            return DetectionError(null, created.Error!);
        }

        var taskId = created.Value!.TaskId;
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return DetectionError(null, new ToolError(
                null,
                "Pangram returned no task id.",
                "Do not resubmit automatically; inspect Pangram availability."));
        }

        var result = await client.WaitForTaskAsync(
            taskId,
            _options.DetectionPollTimeout,
            cancellationToken).ConfigureAwait(false);

        return ToDetectionResult(taskId, result, include_excerpts);
    }

    [McpServerTool(
        Name = GetDetectionName,
        Title = "Get Pangram detection result",
        ReadOnly = true,
        Idempotent = true,
        Destructive = false,
        OpenWorld = true
        )]
    [Description(
        "Fetch the current status or compact result of a Pangram detection task started by pangram_detect_text. Free and non-blocking. Use when a previous call returned pending.")]
    public async Task<DetectionToolResult> GetDetectionAsync(
        [Description("The task_id returned by pangram_detect_text.")] string? task_id,
        [Description("Include short per-window text excerpts. Defaults to false.")] bool include_excerpts = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(task_id))
        {
            return DetectionError(null, new ToolError(null, "task_id is required.", null));
        }

        var result = await client.GetTaskAsync(task_id, cancellationToken).ConfigureAwait(false);
        return ToDetectionResult(task_id, result, include_excerpts);
    }

    public sealed record BulkTextItem(
        [property: Description("The non-empty text to analyze.")] string Text,
        [property: Description("Optional caller-defined id, unique within this bulk job.")] string? Id = null);

    [McpServerTool(
        Name = SubmitBulkName,
        Title = "Submit Pangram bulk detection",
        ReadOnly = false,
        Idempotent = false,
        Destructive = false,
        OpenWorld = true
        )]
    [Description(
        """
        Submit many passages to Pangram as one asynchronous bulk AI-detection job.
        Consumes credits and returns immediately; it never waits for completion.
        The server estimates billable units and refuses jobs over its configured ceiling before submission.
        Poll pangram_get_bulk_status until terminal, then page pangram_get_bulk_results.
        Bulk metadata and results are retained for 48 hours after completion.
        """)]
    public async Task<BulkSubmitToolResult> SubmitBulkAsync(
        [Description("Passages to analyze. Add unique ids when results must be matched to caller records.")] IReadOnlyList<BulkTextItem>? items,
        [Description("Optional selector from pangram_list_models. Applies to the entire job.")] string? model = null,
        CancellationToken cancellationToken = default)
    {
        if (items is null || items.Count == 0)
        {
            return BulkSubmitError(0, 0, new ToolError(null, "No items supplied.", null));
        }

        if (items.Any(item => item is null || string.IsNullOrWhiteSpace(item.Text)))
        {
            return BulkSubmitError(
                items.Count,
                0,
                new ToolError(null, "Every bulk item must contain non-empty text.", null));
        }

        var estimate = client.EstimateBillableUnits(items.Select(item => item.Text));
        if (estimate > _options.MaxBulkBillableUnits)
        {
            return BulkSubmitError(
                items.Count,
                estimate,
                new ToolError(
                    413,
                    $"Estimated {estimate} billable units exceeds the per-job limit of {_options.MaxBulkBillableUnits}.",
                    "Split the items into smaller jobs and submit them separately."));
        }

        var resolvedModel = await client.ResolveModelAsync(model, cancellationToken).ConfigureAwait(false);
        if (!resolvedModel.IsSuccess)
        {
            return BulkSubmitError(items.Count, estimate, resolvedModel.Error!);
        }

        var anyIds = items.Any(item => !string.IsNullOrWhiteSpace(item.Id));
        var request = anyIds
            ? new CreateBulkRequest
            {
                Items = items.Select(item => new BulkItemInput(
                    string.IsNullOrWhiteSpace(item.Id) ? null : item.Id,
                    item.Text)).ToList(),
                Model = resolvedModel.Value!,
            }
            : new CreateBulkRequest
            {
                Text = items.Select(item => item.Text).ToList(),
                Model = resolvedModel.Value!,
            };

        var result = await client.CreateBulkAsync(request, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return BulkSubmitError(items.Count, estimate, result.Error!);
        }

        var value = result.Value!;
        var status = value.Status == PangramBulkStatuses.Failed ? ToolStatus.Failed : ToolStatus.Ok;
        return new BulkSubmitToolResult(
            status,
            value.BulkId,
            value.Status,
            value.TotalItems,
            value.AcceptedItems?.Count ?? 0,
            value.FailedItems ?? [],
            estimate,
            null);
    }

    [McpServerTool(
        Name = GetBulkStatusName,
        Title = "Get Pangram bulk status",
        ReadOnly = true,
        Idempotent = true,
        Destructive = false,
        OpenWorld = true
        )]
    [Description(
        "Check progress for a Pangram bulk job. Free and non-blocking. " +
        "is_terminal is true for succeeded, failed, and partial; stop polling once it is true.")]
    public async Task<BulkStatusToolResult> GetBulkStatusAsync(
        [Description("The bulk_id returned by pangram_submit_bulk.")] string? bulk_id,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(bulk_id))
        {
            return BulkStatusError(null, new ToolError(null, "bulk_id is required.", null));
        }

        var result = await client.GetBulkStatusAsync(bulk_id, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return BulkStatusError(bulk_id, result.Error!);
        }

        var value = result.Value!;
        var terminal = PangramBulkStatuses.IsTerminal(value.Status);
        var status = !terminal
            ? ToolStatus.Pending
            : value.Status == PangramBulkStatuses.Failed ? ToolStatus.Failed : ToolStatus.Ok;

        return new BulkStatusToolResult(
            status,
            value.BulkId,
            value.Status,
            terminal,
            value.TotalItems,
            value.Accepted,
            value.Succeeded,
            value.Failed,
            value.CreatedAtUtc,
            value.CompletedAtUtc,
            null);
    }

    [McpServerTool(
        Name = GetBulkResultsName,
        Title = "Get Pangram bulk results",
        ReadOnly = true,
        Idempotent = true,
        Destructive = false,
        OpenWorld = true
        )]
    [Description(
        "Page through compact results for a Pangram bulk job. Free. " +
        "Use next_offset to fetch the next page; it is null on the last page. " +
        "Results include per-item verdict summaries and failures, not full window text.")]
    public async Task<BulkResultsToolResult> GetBulkResultsAsync(
        [Description("The bulk_id returned by pangram_submit_bulk.")] string? bulk_id,
        [Description("Zero-based item offset.")] int offset = 0,
        [Description("Page size from 1 to 1000; 25 to 100 is recommended to limit response size.")] int limit = 50,
        [Description("Include per-segment summaries for each successful item. Defaults to false.")] bool include_segments = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(bulk_id))
        {
            return BulkResultsError(null, Math.Max(0, offset), Math.Clamp(limit, 1, 1000), new ToolError(null, "bulk_id is required.", null));
        }

        var normalizedOffset = Math.Max(0, offset);
        var normalizedLimit = Math.Clamp(limit, 1, 1000);
        var result = await client.GetBulkResultsAsync(
            bulk_id,
            normalizedOffset,
            normalizedLimit,
            cancellationToken).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            return BulkResultsError(bulk_id, normalizedOffset, normalizedLimit, result.Error!);
        }

        var value = result.Value!;
        var items = (value.Items ?? []).Select(item => new BulkItemSummary(
            item.Index,
            item.Id,
            item.Stage,
            item.Error,
            item.Result is { Stage: PangramStages.Success } task
                ? Summarize(task, includeWindows: include_segments, includeExcerpts: false)
                : null)).ToList();

        var next = value.Offset + value.Limit;
        int? nextOffset = next < value.TotalItems ? next : null;
        var anyPending = items.Any(item => !PangramStages.IsTerminal(item.Stage));

        return new BulkResultsToolResult(
            anyPending ? ToolStatus.Pending : ToolStatus.Ok,
            value.BulkId,
            value.Offset,
            value.Limit,
            value.TotalItems,
            nextOffset,
            items,
            value.FailedItems ?? [],
            null);
    }

    private DetectionToolResult ToDetectionResult(
        string taskId,
        PangramResult<TaskResult> result,
        bool includeExcerpts)
    {
        if (!result.IsSuccess)
        {
            return DetectionError(taskId, result.Error!);
        }

        var task = result.Value!;
        return task.Stage switch
        {
            PangramStages.Success => new DetectionToolResult(
                ToolStatus.Ok,
                taskId,
                task.Stage,
                Summarize(task, includeWindows: true, includeExcerpts),
                null,
                null),
            PangramStages.Failed => new DetectionToolResult(
                ToolStatus.Failed,
                taskId,
                task.Stage,
                null,
                task.Headline,
                null),
            _ => new DetectionToolResult(ToolStatus.Pending, taskId, task.Stage, null, null, null),
        };
    }

    private DetectionSummary Summarize(
        TaskResult task,
        bool includeWindows,
        bool includeExcerpts)
    {
        IReadOnlyList<WindowSummary> windows = !includeWindows || task.Windows is null
            ? []
            : task.Windows.Select(window => new WindowSummary(
                window.StartIndex,
                window.EndIndex,
                window.Label,
                window.AiAssistanceScore,
                window.Confidence,
                window.IsHumanized,
                includeExcerpts ? Excerpt(window.Text) : null)).ToList();

        return new DetectionSummary(
            task.PredictionShort,
            task.Headline,
            task.Prediction,
            task.Version,
            task.FractionAi,
            task.FractionAiAssisted,
            task.FractionHuman,
            task.NumAiSegments,
            task.NumAiAssistedSegments,
            task.NumHumanSegments,
            task.DashboardLink,
            windows);
    }

    private string? Excerpt(string? text)
    {
        if (string.IsNullOrEmpty(text) || _options.WindowExcerptLength == 0)
        {
            return null;
        }

        var trimmed = text.Trim();
        return trimmed.Length <= _options.WindowExcerptLength
            ? trimmed
            : trimmed[.._options.WindowExcerptLength] + "...";
    }

    private static DetectionToolResult DetectionError(string? taskId, ToolError error) =>
        new(ToolStatus.Error, taskId, null, null, null, error);

    private static BulkSubmitToolResult BulkSubmitError(int total, int estimate, ToolError error) =>
        new(ToolStatus.Error, null, null, total, 0, null, estimate, error);

    private static BulkStatusToolResult BulkStatusError(string? bulkId, ToolError error) =>
        new(ToolStatus.Error, bulkId, null, false, 0, 0, 0, 0, null, null, error);

    private static BulkResultsToolResult BulkResultsError(
        string? bulkId,
        int offset,
        int limit,
        ToolError error) =>
        new(ToolStatus.Error, bulkId, offset, limit, 0, null, null, null, error);
}

/// <summary>Separately registered so file upload is not advertised without UploadRoot.</summary>
public sealed class PangramFileMcpTools(
    PangramClient client,
    IOptions<PangramOptions> options)
{
    private readonly PangramOptions _options = options.Value;

    [McpServerTool(
        Name = "pangram_upload_files",
        Title = "Upload files to Pangram",
        ReadOnly = false,
        Idempotent = false,
        Destructive = false,
        OpenWorld = true
        )]
    [Description(
        "Upload local document files to Pangram for AI detection. Consumes credits and sends file contents " +
        "to a third party. Only files beneath the host-configured UploadRoot are allowed. " +
        "The endpoint uses Pangram's default model and returns per-file dashboard-oriented results; " +
        "use pangram_detect_text when segment-level text results are needed.")]
    public async Task<FileUploadToolResult> UploadFilesAsync(
        [Description("File paths, absolute or relative to the configured UploadRoot.")] IReadOnlyList<string>? file_paths,
        [Description("Request public dashboard links. Only honored when the host allows public links.")] bool public_dashboard_link = false,
        CancellationToken cancellationToken = default)
    {
        if (public_dashboard_link && !_options.AllowPublicDashboardLinks)
        {
            return new FileUploadToolResult(
                ToolStatus.Error,
                null,
                new ToolError(null, "Public dashboard links are disabled by the host.", "Retry without public_dashboard_link."));
        }

        var resolved = ResolveUploadPaths(file_paths);
        if (!resolved.IsSuccess)
        {
            return new FileUploadToolResult(ToolStatus.Error, null, resolved.Error);
        }

        try
        {
            var result = await client.UploadFilesAsync(
                resolved.Value!,
                public_dashboard_link,
                cancellationToken).ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                return new FileUploadToolResult(ToolStatus.Error, null, result.Error);
            }

            var paths = resolved.Value!;
            var responses = result.Value!;
            var files = new List<FileUploadItemSummary>(Math.Max(paths.Count, responses.Count));
            for (var index = 0; index < Math.Max(paths.Count, responses.Count); index++)
            {
                var name = index < paths.Count
                    ? Path.GetFileName(paths[index])
                    : $"(unmatched result {index})";
                var response = index < responses.Count ? responses[index] : null;
                files.Add(new FileUploadItemSummary(
                    name,
                    response?.PublicDashboardLink,
                    Flatten(response?.AdditionalData)));
            }

            return new FileUploadToolResult(ToolStatus.Ok, files, null);
        }
        catch (IOException ex)
        {
            return new FileUploadToolResult(
                ToolStatus.Error,
                null,
                new ToolError(null, $"Could not read the selected files: {ex.Message}", null));
        }
        catch (UnauthorizedAccessException ex)
        {
            return new FileUploadToolResult(
                ToolStatus.Error,
                null,
                new ToolError(null, $"Could not read the selected files: {ex.Message}", null));
        }
    }

    private PangramResult<IReadOnlyList<string>> ResolveUploadPaths(IReadOnlyList<string>? filePaths)
    {
        if (string.IsNullOrWhiteSpace(_options.UploadRoot))
        {
            return PangramResult<IReadOnlyList<string>>.Fail(new ToolError(
                null,
                "File upload is disabled by the host.",
                "Configure Pangram:UploadRoot before using this tool."));
        }

        if (filePaths is null || filePaths.Count == 0)
        {
            return PangramResult<IReadOnlyList<string>>.Fail(new ToolError(null, "No files supplied.", null));
        }

        if (filePaths.Count > _options.MaxFilesPerUpload)
        {
            return PangramResult<IReadOnlyList<string>>.Fail(new ToolError(
                null,
                $"Too many files ({filePaths.Count}); the limit is {_options.MaxFilesPerUpload} per call.",
                "Upload in smaller batches."));
        }

        var root = Path.GetFullPath(_options.UploadRoot);
        var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar) || root.EndsWith(Path.AltDirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        var resolved = new List<string>(filePaths.Count);
        foreach (var raw in filePaths)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return PangramResult<IReadOnlyList<string>>.Fail(new ToolError(null, "Empty file path.", null));
            }

            var fullPath = Path.GetFullPath(Path.Combine(root, raw));
            if (!IsUnderRoot(fullPath, root, rootPrefix, comparison))
            {
                return PangramResult<IReadOnlyList<string>>.Fail(new ToolError(
                    null,
                    $"'{raw}' is outside the allowed upload directory.",
                    "Only files under the configured upload directory can be sent."));
            }

            var info = new FileInfo(fullPath);
            if (!info.Exists)
            {
                return PangramResult<IReadOnlyList<string>>.Fail(new ToolError(null, $"File not found: '{raw}'.", null));
            }

            if (info.LinkTarget is not null)
            {
                var target = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
                if (target is null || !IsUnderRoot(target, root, rootPrefix, comparison))
                {
                    return PangramResult<IReadOnlyList<string>>.Fail(new ToolError(
                        null,
                        $"'{raw}' links outside the allowed upload directory.",
                        "Only files whose resolved target is beneath the configured upload directory can be sent."));
                }
            }

            for (var directory = info.Directory;
                 directory is not null && !string.Equals(directory.FullName, root, comparison);
                 directory = directory.Parent)
            {
                if (directory.LinkTarget is null)
                {
                    continue;
                }

                var target = directory.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
                if (target is null || !IsUnderRoot(target, root, rootPrefix, comparison))
                {
                    return PangramResult<IReadOnlyList<string>>.Fail(new ToolError(
                        null,
                        $"'{raw}' uses a directory link outside the allowed upload directory.",
                        "Only files beneath the configured upload directory can be sent."));
                }
            }

            resolved.Add(fullPath);
        }

        return PangramResult<IReadOnlyList<string>>.Ok(resolved);
    }

    private static bool IsUnderRoot(
        string path,
        string root,
        string rootPrefix,
        StringComparison comparison) =>
        string.Equals(path, root, comparison) || path.StartsWith(rootPrefix, comparison);

    private static IReadOnlyDictionary<string, string>? Flatten(Dictionary<string, JsonElement>? extra) =>
        extra is null || extra.Count == 0
            ? null
            : extra.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.ValueKind == JsonValueKind.String
                    ? pair.Value.GetString() ?? string.Empty
                    : pair.Value.GetRawText());
}
