# Pangram AI-Detection MCP Server

This is an ASP.NET Core MCP server that lets an LLM agent check text and documents with the Pangram API. Detection results are probabilistic signals and should not be treated as proof of authorship.

## Configuration

Set the API key with either the conventional .NET key:

```powershell
$env:Pangram__ApiKey = "your-pangram-api-key"
```

or the deployment-friendly alias:

```powershell
$env:PANGRAM_API_KEY = "your-pangram-api-key"
```

The key can also be stored in user secrets under `Pangram:ApiKey`. The server validates it during startup. Optional settings are configured under `Pangram` in `appsettings.json` or environment variables:

```json
{
  "Pangram": {
    "DefaultModel": "default",
    "AllowPublicDashboardLinks": false,
    "UploadRoot": "C:/path/to/documents",
    "DetectionPollTimeout": "00:01:00",
    "WordsPerBillableUnit": 100,
    "MaxBulkBillableUnits": 1000
  }
}
```

`UploadRoot` is optional. The file-upload tool is not advertised unless it is set. Any requested file must resolve beneath that directory; traversal, missing files, and links outside the root are rejected. Public dashboard links are disabled unless `AllowPublicDashboardLinks` is explicitly enabled.

## MCP tools

The server exposes:

- `pangram_list_models` — lists model selectors available to the API key.
- `pangram_detect_text` — submits one passage and polls until success, failure, or the configured timeout. Returns `pending` with a `task_id` when polling times out.
- `pangram_get_detection` — reads a task status or completed result.
- `pangram_submit_bulk` — submits many `{ "id": "optional-id", "text": "..." }` items without waiting. The server estimates billable units and rejects oversized jobs before submission.
- `pangram_get_bulk_status` — reads bulk progress. `succeeded`, `failed`, and `partial` are terminal.
- `pangram_get_bulk_results` — pages compact per-item summaries with `next_offset`.
- `pangram_upload_files` — uploads allowlisted local document paths to Pangram's file endpoint. Pangram's file API uses its default model and primarily returns dashboard-oriented results.

All results are structured and contain `status`: `ok`, `pending`, `failed`, or `error`. Text results include the overall verdict, AI/AI-assisted/human fractions, segment counts, and optional short window excerpts. Window offsets refer to Pangram's returned normalized text.

## Running locally

```powershell
dotnet run --project .\AIDetectorMcp\AIDetectorMcp.csproj --launch-profile http
```

Configure an MCP client against `http://localhost:6104` or, with a trusted development certificate, `https://localhost:5258`.

Bulk results are retained by Pangram for 48 hours after completion. Bulk submission and file upload consume credits; MCP clients should request confirmation for those tools when their host supports approvals.

## References

- [Pangram API overview](https://docs.pangram.com/api-reference/introduction)
- [Pangram AI detection](https://docs.pangram.com/api-reference/ai-detection)
- [Pangram Bulk API](https://docs.pangram.com/api-reference/bulk-api)
- [Pangram File Upload](https://docs.pangram.com/api-reference/file-external)
- [MCP C# SDK](https://csharp.sdk.modelcontextprotocol.io/)
