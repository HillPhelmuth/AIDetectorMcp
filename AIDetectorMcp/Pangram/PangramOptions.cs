using System.ComponentModel.DataAnnotations;

namespace AIDetectorMcp.Pangram;

/// <summary>
/// Host-controlled Pangram configuration. Policy settings are intentionally not
/// controlled by MCP tool arguments.
/// </summary>
public sealed class PangramOptions
{
    public const string SectionName = "Pangram";

    [Required(AllowEmptyStrings = false)]
    public string ApiKey { get; set; } = string.Empty;

    public Uri TextBaseUrl { get; set; } = new("https://text.external-api.pangram.com");

    public Uri FileBaseUrl { get; set; } = new("https://file-external.api.pangram.com");

    [Required(AllowEmptyStrings = false)]
    public string DefaultModel { get; set; } = "default";

    /// <summary>Whether tools may request public dashboard links that expose submitted content.</summary>
    public bool AllowPublicDashboardLinks { get; set; }

    /// <summary>Only files beneath this directory may be sent. Null disables file upload.</summary>
    public string? UploadRoot { get; set; }

    [Range(1, 100)]
    public int MaxFilesPerUpload { get; set; } = 10;

    public TimeSpan DetectionPollTimeout { get; set; } = TimeSpan.FromSeconds(60);

    public TimeSpan InitialPollDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    public TimeSpan MaxPollDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>100 for Pangram 4, or 1000 for Pangram 3.</summary>
    [Range(1, 10_000)]
    public int WordsPerBillableUnit { get; set; } = 100;

    [Range(1, 1000)]
    public int MaxBulkBillableUnits { get; set; } = 1000;

    [Range(0, 2000)]
    public int WindowExcerptLength { get; set; } = 120;

    public TimeSpan ModelCatalogCacheDuration { get; set; } = TimeSpan.FromMinutes(10);
}
