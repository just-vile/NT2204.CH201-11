using Microsoft.Extensions.Http.Resilience;
using Saga.Shared.Infrastructure;
using Saga.WebUI.Components;
using Saga.WebUI.Services;

const string ServiceName = "web-ui";

var builder = WebApplication.CreateBuilder(args);
builder.UseSagaSerilog(ServiceName);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddAntiforgery();
builder.Services.AddSagaJsonDefaults();
builder.Services.AddSagaOpenTelemetry(builder.Configuration, ServiceName);
builder.Services.AddHealthChecks(); // process-only liveness/readiness

builder.Services.AddHttpContextAccessor();
builder.Services.AddTransient<CorrelationForwardingHandler>();

// Typed clients per backend service. URLs come from Apis:* (env: Apis__Order, etc.).
var apis = builder.Configuration.GetSection("Apis");
builder.Services.AddHttpClient<OrderApiClient>(c => Configure(c, apis["Order"], "http://localhost:5001"))
    .AddHttpMessageHandler<CorrelationForwardingHandler>()
    .AddStandardResilienceHandler(ConfigureResilience);
builder.Services.AddHttpClient<PaymentApiClient>(c => Configure(c, apis["Payment"], "http://localhost:5002"))
    .AddHttpMessageHandler<CorrelationForwardingHandler>()
    .AddStandardResilienceHandler(ConfigureResilience);
builder.Services.AddHttpClient<InventoryApiClient>(c => Configure(c, apis["Inventory"], "http://localhost:5003"))
    .AddHttpMessageHandler<CorrelationForwardingHandler>()
    .AddStandardResilienceHandler(ConfigureResilience);
builder.Services.AddHttpClient<ShippingApiClient>(c => Configure(c, apis["Shipping"], "http://localhost:5004"))
    .AddHttpMessageHandler<CorrelationForwardingHandler>()
    .AddStandardResilienceHandler(ConfigureResilience);

builder.Services.AddSingleton<OrderTracker>();

var app = builder.Build();

app.UseStaticFiles();
app.UseCorrelationId();
app.UseSagaPrometheusEndpoint();
app.MapSagaHealthChecks();

app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

static void Configure(HttpClient client, string? configured, string fallback)
{
    client.BaseAddress = new Uri(configured ?? fallback);
    // Leave HttpClient.Timeout at its default (Timeout.InfiniteTimeSpan via Polly);
    // the Polly StandardResilienceHandler owns total + per-attempt timeouts.
    client.Timeout = Timeout.InfiniteTimeSpan;
}

// Tight budgets so a downed backend turns into a fast "unavailable" card in the UI
// instead of a 30 s page-wide hang on every poll.
static void ConfigureResilience(HttpStandardResilienceOptions o)
{
    o.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(5);
    o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(2);
    o.Retry.MaxRetryAttempts = 2;
    o.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(10);
    o.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(5);
}
