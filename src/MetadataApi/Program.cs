using MetadataApi.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<MetadataDbContext>(options =>
    options.UseMetadataSqlite(
        builder.Configuration["Database:Path"],
        builder.Environment.ContentRootPath));
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();

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

app.Run();

public partial class Program;
