using GameStore.Api.Db;
using GameStore.Api.Endpoints;
using MySqlConnector;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Store")
    ?? throw new InvalidOperationException("Configure ConnectionStrings:Store");

builder.Services.AddSingleton(new MySqlDataSourceBuilder(connectionString).Build());
builder.Services.Configure<PaymentOptions>(builder.Configuration.GetSection("Payments"));
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Configuration.GetValue("Database:MigrateOnStartup", true))
{
    var folder = Path.Combine(AppContext.BaseDirectory, "db", "migrations");
    var applied = await Migrator.MigrateAsync(connectionString, folder);
    if (applied.Count > 0) app.Logger.LogInformation("Migrations aplicadas: {Versions}", string.Join(", ", applied));
}

app.UseExceptionHandler();
app.UseStatusCodePages();

app.MapOpenApi();
app.MapScalarApiReference(o => o.WithTitle("Game Store API"));
app.MapGet("/", () => Results.Redirect("/scalar")).ExcludeFromDescription();
app.MapGet("/health", () => Results.Ok(new { status = "ok" })).WithTags("Saúde");
app.MapStoreEndpoints();

app.Run();

public partial class Program;
