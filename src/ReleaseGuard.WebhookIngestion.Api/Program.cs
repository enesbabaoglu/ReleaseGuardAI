using ReleaseGuard.WebhookIngestion.Api;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/health", () => TypedResults.Ok(ServiceStatus.Ready()));

app.Run();

public partial class Program;

