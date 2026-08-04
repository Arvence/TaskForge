using System.Text.Json;

const string SettingsFileName = "debugsettings.json";

string settingsPath = Path.Combine(AppContext.BaseDirectory, SettingsFileName);

if (!File.Exists(settingsPath))
{
    Console.Error.WriteLine($"Debug settings were not found at '{settingsPath}'.");
    return 1;
}

await using FileStream settingsStream = File.OpenRead(settingsPath);
DebugSettings? settings = await JsonSerializer.DeserializeAsync<DebugSettings>(
    settingsStream,
    new JsonSerializerOptions(JsonSerializerDefaults.Web));

if (settings is null)
{
    Console.Error.WriteLine("Debug settings could not be loaded.");
    return 1;
}

Console.WriteLine("TaskForge Debugging");
Console.WriteLine($"Environment: {settings.Environment}");
Console.WriteLine($"TaskForge API: {settings.ApiBaseUrl}");

return 0;

internal sealed record DebugSettings(
    string Environment,
    string ApiBaseUrl);
