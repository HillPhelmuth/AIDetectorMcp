using AIDetectorMcp.Pangram;

var builder = WebApplication.CreateBuilder(args);

// Support the conventional single-variable deployment configuration while also
// allowing the normal .NET Pangram:ApiKey / Pangram__ApiKey configuration path.
if (string.IsNullOrWhiteSpace(builder.Configuration["Pangram:ApiKey"]) &&
    !string.IsNullOrWhiteSpace(builder.Configuration["PANGRAM_API_KEY"]))
{
    builder.Configuration["Pangram:ApiKey"] = builder.Configuration["PANGRAM_API_KEY"];
}

builder.Services.AddPangramDetection(builder.Configuration);

// Add the MCP services: the transport to use (http) and the tools to register.
var mcp = builder.Services
    .AddMcpServer()
    .WithHttpTransport(options =>
    {
        // Stateless mode is recommended for servers that don't need
        // server-to-client requests like sampling or elicitation.
        // See https://csharp.sdk.modelcontextprotocol.io/concepts/transports/transports.html for details.
        options.Stateless = true;
    })
    .WithTools<PangramMcpTools>(PangramMcpTools.SerializerOptions);

// Do not advertise a path-reading tool unless the host has explicitly supplied
// an allowlisted directory for it.
if (!string.IsNullOrWhiteSpace(builder.Configuration["Pangram:UploadRoot"]))
{
    mcp.WithTools<PangramFileMcpTools>(PangramMcpTools.SerializerOptions);
}

var app = builder.Build();
app.MapMcp();

app.Run();
