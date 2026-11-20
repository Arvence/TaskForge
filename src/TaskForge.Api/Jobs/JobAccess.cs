using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

using TaskForge.Domain.Jobs;

namespace TaskForge.Api.Jobs;

internal sealed class JobAccess
{
    private static readonly object ApplicationKey = new();
    private readonly Dictionary<string, byte[]> _keys = new(StringComparer.Ordinal);

    public JobAccess(IConfiguration configuration)
    {
        foreach (IConfigurationSection application in configuration.GetSection("JobAccess:Applications").GetChildren())
        {
            string id = JobApplicationId.Normalize(application.Key);
            string? key = application["ApiKey"];
            if (key is null || !IsValidKey(key))
            {
                throw new InvalidOperationException($"Configure a valid API key for application '{id}'.");
            }

            byte[] hash = Hash(key);
            if (_keys.Values.Any(existing => CryptographicOperations.FixedTimeEquals(existing, hash)) || !_keys.TryAdd(id, hash))
            {
                throw new InvalidOperationException("Application IDs and API keys must be unique.");
            }
        }
    }

    private string? Authenticate(HttpContext context)
    {
        var values = context.Request.Headers.Authorization;
        if (values.Count != 1 || !AuthenticationHeaderValue.TryParse(values[0], out var authorization)
            || !authorization.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase)
            || authorization.Parameter is not { } key || !IsValidKey(key))
        {
            return null;
        }

        byte[] hash = Hash(key);
        return _keys.FirstOrDefault(entry => CryptographicOperations.FixedTimeEquals(entry.Value, hash)).Key;
    }

    public async Task AuthorizeAsync(HttpContext context, RequestDelegate next)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<RequiresApplicationKey>() is not null)
        {
            context.Response.Headers.CacheControl = "no-store";
            if (Authenticate(context) is not { } applicationId)
            {
                context.Response.Headers.WWWAuthenticate = "Bearer";
                await Results.Problem(statusCode: StatusCodes.Status401Unauthorized, detail: "A valid TaskForge application API key is required.").ExecuteAsync(context);
                return;
            }

            context.Items[ApplicationKey] = applicationId;
        }

        await next(context);
    }

    public static string RequireApplication(HttpContext context) =>
        context.Items[ApplicationKey] as string ?? throw new InvalidOperationException("The request has no authenticated application.");

    public static bool MayAccess(HttpContext context, string? applicationId) =>
        JobApplicationId.IsValid(applicationId) && RequireApplication(context) == JobApplicationId.Normalize(applicationId!);

    public static bool ClaimsOtherApplication(HttpContext context, string? applicationId) =>
        JobApplicationId.IsValid(applicationId) && !MayAccess(context, applicationId);

    public static IResult Forbidden() => Results.Problem(statusCode: StatusCodes.Status403Forbidden, detail: "The API key does not belong to the requested application.");

    private static bool IsValidKey(string value) =>
        value.Length is >= 32 and <= 128 && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.ASCII.GetBytes(value));
}

internal sealed class RequiresApplicationKey;
