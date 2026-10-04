// The default host: a Nightingale server backed by Polecat. A daemon or another
// host mounts the same pieces through the extension methods in Server and in
// this package; this file exists so the package also runs on its own
// (dotnet run) and so tests can host it through WebApplicationFactory<Program>.
// Three switches make it do one thing and exit instead of serving:
// --export-schema <file> writes the creation script of a new store;
// --schema-report prints what the configured database needs, exit code 0 when
// it is current and 1 when it is not; --apply-schema brings it up to date.
using Dracocephalum.Nightingale.Server;
using Dracocephalum.Nightingale.Server.Polecat;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddNightingaleServer();
builder.Services.AddNightingalePolecat(builder.Configuration);

var app = builder.Build();
if (SchemaExport.TryGetPath(args, out var schemaPath))
{
    await SchemaExport.ExportAsync(app.Services, schemaPath, CancellationToken.None);
    return 0;
}

if (args.Contains(SchemaSwitches.Report))
{
    var report = await app.Services.GetNightingaleSchemaReportAsync();
    Console.WriteLine(report.Describe());
    return report.IsCurrent ? 0 : 1;
}

if (args.Contains(SchemaSwitches.Apply))
{
    await app.Services.ApplyNightingaleSchemaAsync();
    Console.WriteLine((await app.Services.GetNightingaleSchemaReportAsync()).Describe());
    return 0;
}

app.MapNightingaleServer();
app.Run();
return 0;
