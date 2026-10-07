using Nabo.Recipes;
using Microsoft.AspNetCore.Authentication;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
builder.WebHost.UseUrls($"http://[::]:{port}");
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 262144);
var database = builder.Configuration["DATABASE_URL"] ?? throw new InvalidOperationException("Set DATABASE_URL.");
builder.Services.AddSingleton(NpgsqlDataSource.Create(DatabaseConnection.Parse(database)));
builder.Services.AddSingleton<RecipeStore>();
var authBase = builder.Configuration["AUTH_API_BASE_URL"] ?? "https://naboback-production.up.railway.app";
if (!Uri.TryCreate(authBase.TrimEnd('/') + "/", UriKind.Absolute, out var authUri) || authUri.Scheme is not ("http" or "https"))
    throw new InvalidOperationException("Invalid AUTH_API_BASE_URL.");
builder.Services.AddHttpClient("nabo-auth", c => { c.BaseAddress = authUri; c.Timeout = TimeSpan.FromSeconds(10); })
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddAuthentication("Nabo").AddScheme<AuthenticationSchemeOptions, NaboAuthentication>("Nabo", null);
builder.Services.AddAuthorization();
builder.Services.AddControllers();
builder.Services.AddOpenApi();
var app = builder.Build();
app.UseExceptionHandler(handler => handler.Run(async context =>
{
    context.Response.StatusCode = 503;
    context.Response.Headers.CacheControl = "no-store";
    await context.Response.WriteAsJsonAsync(new { message = "Сервис рецептов временно недоступен. Попробуйте ещё раз." });
}));
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapOpenApi();
app.MapGet("/health", async (RecipeStore store, CancellationToken ct) =>
{
    await store.Check(ct);
    return Results.Ok(new { status = "ok" });
});
// Serialize schema initialization across replicas. Never migrate primary Nabo tables.
await app.Services.GetRequiredService<RecipeStore>().Initialize(builder.Configuration["RECIPE_ADMIN_PHONE"], CancellationToken.None);
await app.RunAsync();
public partial class Program { }
