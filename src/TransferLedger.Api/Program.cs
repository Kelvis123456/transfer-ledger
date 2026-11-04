using System.Security.Cryptography;
using System.Text;
using Npgsql;
using TransferLedger.Api;

var builder = WebApplication.CreateBuilder(args);

var connection = builder.Configuration.GetConnectionString("Db")
    ?? throw new InvalidOperationException("Falta ConnectionStrings:Db.");
builder.Services.AddSingleton(new NpgsqlDataSourceBuilder(connection).Build());
builder.Services.AddSingleton<LedgerService>();

// Sin clave, quien llegue podría mover dinero: en producción mejor no arrancar.
if (string.IsNullOrWhiteSpace(builder.Configuration["Auth:ApiKey"]) && !builder.Environment.IsDevelopment() && !builder.Environment.IsEnvironment("Testing"))
    throw new InvalidOperationException("Falta Auth:ApiKey.");

var app = builder.Build();
await Schema.MigrateAsync(app.Services.GetRequiredService<NpgsqlDataSource>());

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

var api = app.MapGroup("/api").AddEndpointFilter(RequireApiKey);

api.MapPost("/accounts", async (CreateAccount body, LedgerService ledger) => Respond(await ledger.CreateAccountAsync(body.Alias)));

api.MapGet("/accounts/{alias}", async (string alias, LedgerService ledger) =>
    await ledger.GetAccountAsync(alias) is { } account ? Results.Json(account) : Results.NotFound(new { error = "No existe la cuenta." }));

api.MapPost("/accounts/{alias}/deposit", async (string alias, Amount body, HttpRequest req, LedgerService ledger) =>
    Respond(await ledger.DepositAsync(alias, body.Cents, req.Headers["Idempotency-Key"].FirstOrDefault())));

api.MapPost("/transfers", async (NewTransfer body, HttpRequest req, LedgerService ledger) =>
    Respond(await ledger.TransferAsync(body.From, body.To, body.Cents, body.Reference, req.Headers["Idempotency-Key"].FirstOrDefault())));

api.MapGet("/reconciliation", async (LedgerService ledger) => Results.Ok(await ledger.ReconcileAsync()));

app.Run();

static IResult Respond(LedgerResult r)
{
    if (r.Failure != Failure.None) return Results.Json(new { error = r.Message }, statusCode: r.Status);
    return Results.Json(r.Body, statusCode: r.Status, contentType: "application/json");
}

static async ValueTask<object?> RequireApiKey(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
{
    var expected = ctx.HttpContext.RequestServices.GetRequiredService<IConfiguration>()["Auth:ApiKey"];
    var env = ctx.HttpContext.RequestServices.GetRequiredService<IHostEnvironment>();
    if (string.IsNullOrWhiteSpace(expected))
        return env.IsDevelopment() || env.IsEnvironment("Testing") ? await next(ctx) : Results.StatusCode(503);

    var given = ctx.HttpContext.Request.Headers["X-Api-Key"].FirstOrDefault() ?? "";
    var a = Encoding.UTF8.GetBytes(given);
    var b = Encoding.UTF8.GetBytes(expected);
    return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b)
        ? await next(ctx)
        : Results.Json(new { error = "Falta la clave de API o no es correcta." }, statusCode: 401);
}

public record CreateAccount(string? Alias);
public record Amount(long Cents);
public record NewTransfer(string? From, string? To, long Cents, string? Reference);

public partial class Program;
