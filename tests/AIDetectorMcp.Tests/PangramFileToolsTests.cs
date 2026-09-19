using System.Text.Json;
using AIDetectorMcp.Pangram;

namespace AIDetectorMcp.Tests;

public sealed class PangramFileToolsTests
{
    [Fact]
    public async Task UploadRejectsFileOutsideConfiguredRoot()
    {
        var root = PangramTestHelpers.TempDirectory();
        var outside = Path.Combine(Path.GetTempPath(), $"outside-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(outside, "text");
        try
        {
            var handler = new StubHttpMessageHandler(_ =>
                Task.FromResult(PangramTestHelpers.Json("[]")));
            var tools = PangramTestHelpers.FileTools(
                handler,
                PangramTestHelpers.Options(o => o.UploadRoot = root));

            var result = await tools.UploadFilesAsync([outside]);

            Assert.Equal(ToolStatus.Error, result.Status);
            Assert.Contains("outside", result.Error!.Message);
            Assert.Empty(handler.Requests);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            File.Delete(outside);
        }
    }

    [Fact]
    public async Task UploadRejectsMissingFile()
    {
        var root = PangramTestHelpers.TempDirectory();
        try
        {
            var handler = new StubHttpMessageHandler(_ =>
                Task.FromResult(PangramTestHelpers.Json("[]")));
            var tools = PangramTestHelpers.FileTools(
                handler,
                PangramTestHelpers.Options(o => o.UploadRoot = root));

            var result = await tools.UploadFilesAsync(["missing.txt"]);

            Assert.Equal(ToolStatus.Error, result.Status);
            Assert.Contains("File not found", result.Error!.Message);
            Assert.Empty(handler.Requests);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task UploadRejectsExternalSymbolicLinkWhenSupported()
    {
        var root = PangramTestHelpers.TempDirectory();
        var outside = Path.Combine(Path.GetTempPath(), $"outside-{Guid.NewGuid():N}.txt");
        var link = Path.Combine(root, "linked.txt");
        await File.WriteAllTextAsync(outside, "secret");
        try
        {
            try
            {
                File.CreateSymbolicLink(link, outside);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
            {
                return;
            }

            var handler = new StubHttpMessageHandler(_ =>
                Task.FromResult(PangramTestHelpers.Json("[]")));
            var tools = PangramTestHelpers.FileTools(
                handler,
                PangramTestHelpers.Options(o => o.UploadRoot = root));

            var result = await tools.UploadFilesAsync([link]);

            Assert.Equal(ToolStatus.Error, result.Status);
            Assert.Contains("links outside", result.Error!.Message);
            Assert.Empty(handler.Requests);
        }
        finally
        {
            if (File.Exists(link) || Directory.Exists(link))
            {
                File.Delete(link);
            }
            Directory.Delete(root, recursive: true);
            File.Delete(outside);
        }
    }

    [Fact]
    public async Task UploadUsesRepeatedFilesFieldAndPreservesAdditionalFields()
    {
        var root = PangramTestHelpers.TempDirectory();
        var first = Path.Combine(root, "first.txt");
        var second = Path.Combine(root, "second.txt");
        await File.WriteAllTextAsync(first, "first document");
        await File.WriteAllTextAsync(second, "second document");
        string? body = null;
        try
        {
            var handler = new StubHttpMessageHandler(async request =>
            {
                body = await request.Content!.ReadAsStringAsync();
                return PangramTestHelpers.Json("""
                    [
                      {"public_dashboard_link":"https://www.pangram.com/history/1","extra":"kept"},
                      {"public_dashboard_link":"https://www.pangram.com/history/2"}
                    ]
                    """);
            });
            var tools = PangramTestHelpers.FileTools(
                handler,
                PangramTestHelpers.Options(o =>
                {
                    o.UploadRoot = root;
                    o.AllowPublicDashboardLinks = true;
                }));

            var result = await tools.UploadFilesAsync([first, second], public_dashboard_link: true);

            Assert.Equal(ToolStatus.Ok, result.Status);
            Assert.Equal(2, result.Files!.Count);
            Assert.Equal("https://www.pangram.com/history/1", result.Files[0].PublicDashboardLink);
            Assert.Equal("kept", result.Files[0].OtherFields!["extra"]);
            Assert.NotNull(body);
            Assert.Equal(2, CountOccurrences(body!, "name=files"));
            Assert.Contains("name=public_dashboard_link", body);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static int CountOccurrences(string value, string search)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(search, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += search.Length;
        }

        return count;
    }
}
