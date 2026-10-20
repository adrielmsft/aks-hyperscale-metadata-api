using System.Text.Json.Serialization;
using MetadataApi.Endpoints;
using MetadataApi.Middleware;
using MetadataApi.Persistence;
using MetadataApi.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<MetadataDbContext>(options =>
    options.UseMetadataSqlite(
        builder.Configuration["Database:Path"],
        builder.Environment.ContentRootPath));
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<DatabaseExceptionHandler>();
builder.Services.AddHealthChecks();
builder.Services.AddScoped<IApplicationService, ApplicationService>();
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

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapHealthChecks("/health/live");
app.MapApplications();

app.Run();

public partial class Program;
