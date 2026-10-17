using WaybackViewer;
using WaybackViewer.Services;
using WaybackViewer.Storage;

var app = AppBuilder.Build(args);

// dotnet run -- import-legacy "<path to old App_Data\wayback-cache>"
if (args.Length > 0 && args[0].Equals("import-legacy", StringComparison.OrdinalIgnoreCase))
{
    var folder = args.Length > 1 ? args[1] : Path.Combine("App_Data", "wayback-cache");
    await app.Services.GetRequiredService<IArchiveStore>().InitializeAsync();
    var report = await app.Services.GetRequiredService<LegacyCacheImporter>()
        .ImportAsync(folder, new Progress<string>(Console.WriteLine));
    Console.WriteLine($"Done. Files: {report.Files}, capture lists: {report.CaptureLists}, captures: {report.Captures}, " +
                      $"imported: {report.Imported}, unusable: {report.Invalid}, no cached page: {report.Missing}.");
    return 0;
}

await app.RunAsync();
return 0;
