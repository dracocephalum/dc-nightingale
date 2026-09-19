// The default host: a Nightingale server backed by Polecat. A daemon or another
// host mounts the same pieces through the extension methods in Server and in
// this package; this file exists so the package also runs on its own
// (dotnet run) and so tests can host it through WebApplicationFactory<Program>.
// With --export-schema <file> it writes the creation script it would apply and
// exits instead of serving.
using Dracocephalum.Nightingale.Server;
using Dracocephalum.Nightingale.Server.Polecat;
using Polecat;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddNightingaleServer();
builder.Services.AddNightingalePolecat(builder.Configuration);

var app = builder.Build();
if (SchemaExport.TryGetPath(args, out var schemaPath))
{
    await SchemaExport.ExportAsync(app.Services.GetRequiredService<IDocumentStore>(), schemaPath, CancellationToken.None);
    return;
}

app.MapNightingaleServer();
app.Run();
