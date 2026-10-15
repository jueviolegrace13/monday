using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Monday.Agent;
using Monday.Api.Endpoints;
using Monday.Api.Security;
using Monday.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddMonday(builder.Configuration);

// Monday watches itself: its own agent records every request it serves (never bodies, queries,
// headers or IPs). Locally it reports in-process; deployed, set Monday:Agent:Self=true and its key.
builder.Services.AddMondayAgent(builder.Configuration, o => o.ExcludeRoutes.Add("/ingest/traffic"));
if (builder.Environment.IsDevelopment() || builder.Configuration.GetValue<bool>("Monday:Agent:Self"))
{
    builder.Services.AddSingleton<ITraceSink, SelfTraceSink>();
}
builder.Services.AddMondayUserAuthentication(builder.Environment);
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ErrorHandling>();
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

// One token bucket per API key: a noisy app slows itself down, never Monday or other projects.
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy(IngestEndpoints.RateLimitPolicy, http => RateLimitPartition.GetTokenBucketLimiter(
        ReporterKey.PartitionOf(http),
        _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = 200,
            TokensPerPeriod = 50,
            ReplenishmentPeriod = TimeSpan.FromSeconds(1),
            QueueLimit = 0,
        }));
});

// Uploads are graphs and event batches, never files: cap the body size.
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 8 * 1024 * 1024);

var app = builder.Build();

await app.Services.MigrateMondayAsync();
await DevBootstrap.RunAsync(app);

app.UseMondayAgent();
app.UseExceptionHandler();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapGet("/health", () => Results.Ok(new { status = "ok" })).WithTags("Health").AllowAnonymous();
app.MapIngest();
app.MapProjects();
app.MapTraffic();

app.Run();

/// <summary>Entry point, visible to integration tests.</summary>
public partial class Program;
