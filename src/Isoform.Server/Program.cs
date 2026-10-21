// The playground with a native kernel: dotnet run --project src/Isoform.Server [-- --port 5250 --root <wwwroot> --allow-origin <origin>]
// Serves the page and, at /kernel, a WebSocket per kernel session (KernelSocket). Localhost only.
using Isoform.Server;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
var port = builder.Configuration.GetValue("port", 5250);
// Kestrel picks a free port (0) only on an address, not on "localhost".
builder.WebHost.UseUrls(FormattableString.Invariant($"http://{(port == 0 ? "127.0.0.1" : "localhost")}:{port}"));
var root = Root(builder.Configuration["root"]);

var app = builder.Build();
app.Use((context, next) =>
{
    // The page reads this from its navigation timing to know a kernel is here (main.js). no-store: a fresh publish
    // is always what the browser loads.
    context.Response.Headers["Server-Timing"] = "isoform-kernel";
    context.Response.Headers.CacheControl = "no-store";
    return next(context);
});
app.UseWebSockets();
// A browser names the page that opens a socket. Only this server's own page, or an origin given with --allow-origin
// (a hosted playground using ?kernel=), may use the kernel: any other site the user visits could otherwise reach it.
// Clients that are not browsers send no origin.
var allowed = (builder.Configuration["allow-origin"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
app.Map("/kernel", context =>
{
    var origin = context.Request.Headers.Origin.ToString();
    if (origin.Length > 0 && !allowed.Contains(origin, StringComparer.OrdinalIgnoreCase)
        && !(Uri.TryCreate(origin, UriKind.Absolute, out var from) && from.Authority.Equals(context.Request.Host.Value, StringComparison.OrdinalIgnoreCase)))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }
    return KernelSocket.Run(context);
});
var files = new PhysicalFileProvider(root);
app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
app.UseStaticFiles(new StaticFileOptions { FileProvider = files, ServeUnknownFileTypes = true });

await app.StartAsync();
var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault();
Console.WriteLine($"Isoform playground at {address}/ with the native kernel on {Environment.ProcessorCount} threads, serving {root}");
await app.WaitForShutdownAsync();

// The published playground (dotnet publish src/Isoform.Playground -c Release -o out/playground) above this program's
// folder, else the page's sources: with the native kernel the page does not need the WebAssembly build. A publish
// folder given as --root stands for its wwwroot.
static string Root(string? given)
{
    if (given is not null)
    {
        var path = Path.GetFullPath(given);
        return File.Exists(Path.Combine(path, "index.html")) || !Directory.Exists(Path.Combine(path, "wwwroot")) ? path : Path.Combine(path, "wwwroot");
    }
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        foreach (var candidate in (string[])[Path.Combine(dir.FullName, "out", "playground", "wwwroot"), Path.Combine(dir.FullName, "src", "Isoform.Playground", "wwwroot")])
            if (Directory.Exists(candidate)) return candidate;
    throw new DirectoryNotFoundException("No playground to serve: give its wwwroot with --root.");
}
