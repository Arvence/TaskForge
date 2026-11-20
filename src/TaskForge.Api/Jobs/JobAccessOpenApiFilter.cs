using Microsoft.OpenApi;

using Swashbuckle.AspNetCore.SwaggerGen;

namespace TaskForge.Api.Jobs;

internal sealed class JobAccessOpenApiFilter : IOperationFilter
{
    public const string SchemeName = "applicationKey";

    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (!context.ApiDescription.ActionDescriptor.EndpointMetadata.OfType<RequiresApplicationKey>().Any())
        {
            return;
        }

        operation.Security = [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference(SchemeName, context.Document)] = [] }];
        operation.Responses ??= new();
        operation.Responses.TryAdd("401", new OpenApiResponse { Description = "A valid application API key is required." });
    }
}
