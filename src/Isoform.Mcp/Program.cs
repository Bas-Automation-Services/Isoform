using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);
// stdout carries the protocol, so every log line goes to stderr.
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services
    .AddMcpServer(o =>
    {
        o.ServerInfo = new() { Name = "isoform", Version = typeof(Isoform.Mcp.IsoformTools).Assembly.GetName().Version!.ToString() };
        o.ServerInstructions = "Isoform is an implicit CAD kernel driven by text documents. Call describe_format once to learn the " +
                               "format, validate while writing, regenerate to see faces and mass properties, apply_edits to change parameters, " +
                               "build to make a design of plates and tubes and verify to check an assembly against its design.";
    })
    .WithStdioServerTransport()
    .WithToolsFromAssembly();
await builder.Build().RunAsync();
