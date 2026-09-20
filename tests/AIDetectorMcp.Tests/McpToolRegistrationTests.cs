using System.Reflection;
using AIDetectorMcp.Pangram;
using ModelContextProtocol.Server;

namespace AIDetectorMcp.Tests;

public sealed class McpToolRegistrationTests
{
    [Fact]
    public void ToolAttributesExposeStableSnakeCaseNames()
    {
        var names = new[] { typeof(PangramMcpTools), typeof(PangramFileMcpTools) }
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            .Select(method => method.GetCustomAttributes<McpServerToolAttribute>().SingleOrDefault()?.Name)
            .Where(name => name is not null)
            .OrderBy(name => name)
            .ToArray();

        Assert.Equal(
            new[]
            {
                "pangram_detect_text",
                "pangram_get_bulk_results",
                "pangram_get_bulk_status",
                "pangram_get_detection",
                "pangram_list_models",
                "pangram_submit_bulk",
                "pangram_upload_files",
            },
            names);
    }

    [Fact]
    public void ToolsWithOutputSchemasEnableStructuredContent()
    {
        var tools = new[] { typeof(PangramMcpTools), typeof(PangramFileMcpTools) }
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            .Select(method => method.GetCustomAttributes<McpServerToolAttribute>().SingleOrDefault())
            .Where(attribute => attribute is not null)
            .ToArray();

        Assert.All(tools, tool => Assert.True(tool!.UseStructuredContent));
    }
}
