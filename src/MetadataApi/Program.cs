using System.Text.Json.Serialization;
using MetadataApi.Authentication;
using MetadataApi.Endpoints;
using MetadataApi.Middleware;
using MetadataApi.Persistence;
using MetadataApi.Reliability;
using MetadataApi.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<MetadataDbContext>(options =>
    options.UseMetadataSqlite(
        builder.Configuration["Database:Path"],
        builder.Environment.ContentRootPath));
builder.Services.AddApiKeyAuthentication(builder.Configuration);
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<DatabaseExceptionHandler>();
builder.AddReliability();
builder.Services.AddScoped<IApplicationService, ApplicationService>();
builder.Services.AddScoped<IMaintainerService, MaintainerService>();
builder.Services.AddScoped<IApplicationMaintainerService, ApplicationMaintainerService>();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
    options.SerializerOptions.PropertyNameCaseInsensitive = false;
    options.SerializerOptions.NumberHandling = JsonNumberHandling.Strict;
    options.SerializerOptions.AllowDuplicateProperties = false;
});
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = false);

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var database = scope.ServiceProvider.GetRequiredService<MetadataDbContext>();
    await MetadataDatabase.InitializeAsync(database);
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseReliability();
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
}

app.MapApplications();
app.MapMaintainers();
app.MapApplicationMaintainers();

app.Run();

public partial class Program;
