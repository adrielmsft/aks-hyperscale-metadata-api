using MetadataApi.Contracts;
using MetadataApi.Validation;
using System.Text.Json.Nodes;
using Microsoft.OpenApi;

namespace MetadataApi.OpenApi;

internal static class OpenApiExtensions
{
    public static IServiceCollection AddMetadataOpenApi(this IServiceCollection services)
    {
        services.AddOpenApi(options =>
        {
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info = new OpenApiInfo
                {
                    Title = "Metadata API",
                    Version = "v1",
                    Description = "Application and maintainer metadata with idempotent association management. " +
                        "Send the configured API key as Authorization: Bearer <key>. " +
                        "Health probes (/health/live and /health/ready) are anonymous, return plain text, and are excluded from this reference. " +
                        "Unsupported HTTP methods return 405 ProblemDetails with an Allow header. " +
                        "Requests exceeding the configured body limit (default 65536 bytes) return 413, even on operations without a request body. " +
                        "429 responses include Retry-After; exhausted database lock waits return 503."
                };
                document.Servers = [];
                document.Components ??= new OpenApiComponents();
                document.Components.SecuritySchemes = new Dictionary<string, IOpenApiSecurityScheme>
                {
                    ["Bearer"] = new OpenApiSecurityScheme
                    {
                        Type = SecuritySchemeType.Http,
                        Scheme = "bearer",
                        Description = "The configured shared API key (not a JWT)."
                    }
                };
                document.Security = [new OpenApiSecurityRequirement
                {
                    [new OpenApiSecuritySchemeReference("Bearer", document)] = []
                }];
                foreach (var path in document.Paths.Where(path => path.Key.StartsWith("/api/v1/", StringComparison.Ordinal)).Select(path => path.Value))
                {
                    foreach (var operation in path.Operations!.Values)
                    {
                        // Binding failures are ProblemDetails; domain validation adds the errors dictionary.
                        operation.Responses!["400"].Content!["application/problem+json"].Schema = new OpenApiSchema
                        {
                            AnyOf = [new OpenApiSchemaReference("ProblemDetails", document),
                                new OpenApiSchemaReference("HttpValidationProblemDetails", document)]
                        };
                        ((OpenApiResponse)operation.Responses["401"]).Headers = new Dictionary<string, IOpenApiHeader>
                        {
                            ["WWW-Authenticate"] = new OpenApiHeader { Description = "Bearer", Schema = new OpenApiSchema { Type = JsonSchemaType.String } }
                        };
                        ((OpenApiResponse)operation.Responses["429"]).Headers = new Dictionary<string, IOpenApiHeader>
                        {
                            ["Retry-After"] = new OpenApiHeader { Description = "Seconds until the next rate-limit window.", Schema = new OpenApiSchema { Type = JsonSchemaType.Integer } }
                        };
                        if (operation.Responses.TryGetValue("201", out var created))
                        {
                            ((OpenApiResponse)created).Headers = new Dictionary<string, IOpenApiHeader>
                            {
                                ["Location"] = new OpenApiHeader { Description = "Relative URL of the created resource.", Schema = new OpenApiSchema { Type = JsonSchemaType.String } }
                            };
                        }
                        foreach (var parameter in (operation.Parameters ?? []).OfType<OpenApiParameter>())
                        {
                            if (parameter.In == ParameterLocation.Query && parameter.Name is "limit" or "offset")
                            {
                                var limit = parameter.Name == "limit";
                                parameter.Description = limit ? "Page size (default 20)." : "Number of resources to skip (default 0).";
                                parameter.Schema = new OpenApiSchema
                                {
                                    Type = JsonSchemaType.Integer,
                                    Format = "int32",
                                    Default = JsonValue.Create(limit ? PaginationValidation.DefaultLimit : 0),
                                    Minimum = limit ? "1" : "0",
                                    Maximum = (limit ? PaginationValidation.MaxLimit : PaginationValidation.MaxOffset).ToString(System.Globalization.CultureInfo.InvariantCulture)
                                };
                            }
                            else if (parameter.In == ParameterLocation.Path)
                            {
                                parameter.Description = "A non-empty GUID identifying an existing resource. Invalid IDs return 400.";
                            }
                        }
                    }
                }
                return Task.CompletedTask;
            });
            options.AddSchemaTransformer((schema, context, _) =>
            {
                var type = context.JsonTypeInfo.Type;
                if (type == typeof(CreateApplicationRequest) || type == typeof(UpdateApplicationRequest))
                {
                    Require("title");
                    schema.Examples = [JsonNode.Parse("""{"title":"Metadata API","version":"1.0.0","company":null,"website":"https://example.com","source":null,"license":"MIT","description":null}""")!];
                    schema.Description = "Title is required. Optional fields may be omitted or null; PUT clears omitted fields. " +
                        "Strings are trimmed; blank optional strings become null. Maximum normalized lengths: title/company 200, " +
                        "version/license 100, website/source 2048, description 4000. Version must be SemVer 2.0.0; " +
                        "website/source must be absolute HTTP(S) URLs without credentials. Only description permits tab/newline control characters.";
                }
                else if (type == typeof(CreateMaintainerRequest) || type == typeof(UpdateMaintainerRequest))
                {
                    Require("name");
                    Require("email");
                    schema.Examples = [JsonNode.Parse("""{"name":"Ada Lovelace","email":"ada@example.com"}""")!];
                    schema.Description = "Name and email are required, trimmed, non-blank strings with no control characters. " +
                        "Maximum normalized lengths: name 200, email 254. Email must be valid without a display name.";
                }
                return Task.CompletedTask;

                void Require(string property)
                {
                    schema.Required ??= new HashSet<string>();
                    schema.Required.Add(property);
                    if (schema.Properties?[property] is OpenApiSchema field)
                    {
                        field.Type = JsonSchemaType.String;
                    }
                }
            });
        });
        return services;
    }

    public static RouteGroupBuilder WithApiErrors(this RouteGroupBuilder group) => group
        .ProducesValidationProblem()
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
        .ProducesProblem(StatusCodes.Status429TooManyRequests)
        .ProducesProblem(StatusCodes.Status500InternalServerError)
        .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
}

