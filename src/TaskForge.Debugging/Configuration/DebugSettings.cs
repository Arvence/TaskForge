using System.Text.Json;

namespace TaskForge.Debugging.Configuration;

internal sealed record DebugSettings(string Environment, string ApiBaseUrl)
{
    public static async Task<DebugSettings> LoadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"Debug settings were not found at '{path}'.");
        }

        await using FileStream stream = File.OpenRead(path);
        DebugSettings? settings = await JsonSerializer.DeserializeAsync<DebugSettings>(
            stream,
            new JsonSerializerOptions(JsonSerializerDefaults.Web),
            cancellationToken);

        return settings
            ?? throw new InvalidOperationException(
                "Debug settings could not be loaded.");
    }

    public Uri GetApiBaseUri()
    {
        if (!Uri.TryCreate(ApiBaseUrl, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp
                && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException(
                "apiBaseUrl must be an absolute HTTP or HTTPS URL.");
        }

        string normalized = ApiBaseUrl.EndsWith("/", StringComparison.Ordinal)
            ? ApiBaseUrl
            : $"{ApiBaseUrl}/";
        return new Uri(normalized, UriKind.Absolute);
    }
}
