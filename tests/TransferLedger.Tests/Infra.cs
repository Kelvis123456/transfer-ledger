using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Testcontainers.PostgreSql;

namespace TransferLedger.Tests;

/// <summary>Un solo Postgres real para toda la corrida; cada clase de pruebas crea su propia base dentro de él.</summary>
public static class Postgres
{
    private static readonly Lazy<Task<PostgreSqlContainer>> Container = new(async () =>
    {
        var c = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await c.StartAsync();
        return c;
    });

    private static int _n;

    public static async Task<string> NewDatabaseAsync()
    {
        var container = await Container.Value;
        var name = $"t{Interlocked.Increment(ref _n)}_{Guid.NewGuid():N}";
        await using (var admin = new NpgsqlConnection(container.GetConnectionString()))
        {
            await admin.OpenAsync();
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE {name}", admin);
            await cmd.ExecuteNonQueryAsync();
        }
        var b = new NpgsqlConnectionStringBuilder(container.GetConnectionString()) { Database = name, MaxPoolSize = 40 };
        return b.ConnectionString;
    }
}

public sealed class LedgerApp : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;
    public string ConnectionString { get; private set; } = "";
    public HttpClient Http { get; private set; } = null!;

    public async Task InitializeAsync() => await StartAsync(apiKey: null);

    public async Task StartAsync(string? apiKey)
    {
        ConnectionString = await Postgres.NewDatabaseAsync();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing");
            b.UseSetting("ConnectionStrings:Db", ConnectionString);
            if (apiKey is not null) b.UseSetting("Auth:ApiKey", apiKey);
        });
        Http = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        Http.Dispose();
        await _factory.DisposeAsync();
    }

    // ---- ayudas de prueba ----
    private static int _alias;
    public static string NewAlias() => $"user{Interlocked.Increment(ref _alias)}{Guid.NewGuid():N}"[..20];

    public async Task<string> AccountAsync(long fundedCents = 0)
    {
        var alias = NewAlias();
        (await Http.PostAsJsonAsync("/api/accounts", new { alias })).EnsureSuccessStatusCode();
        if (fundedCents > 0) (await DepositAsync(alias, fundedCents)).EnsureSuccessStatusCode();
        return alias;
    }

    public Task<HttpResponseMessage> DepositAsync(string alias, long cents, string? key = null) =>
        SendAsync($"/api/accounts/{alias}/deposit", new { cents }, key ?? Guid.NewGuid().ToString("N"));

    public Task<HttpResponseMessage> TransferAsync(string from, string to, long cents, string? key = null, string? reference = null) =>
        SendAsync("/api/transfers", new { from, to, cents, reference }, key ?? Guid.NewGuid().ToString("N"));

    public Task<HttpResponseMessage> SendAsync(string url, object body, string? key)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        if (key is not null) req.Headers.Add("Idempotency-Key", key);
        return Http.SendAsync(req);
    }

    public async Task<long> BalanceAsync(string alias)
    {
        var json = await Http.GetFromJsonAsync<JsonElement>($"/api/accounts/{alias}");
        return json.GetProperty("balance").GetInt64();
    }

    public async Task<T> ScalarAsync<T>(string sql, params object[] args)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var a in args) cmd.Parameters.AddWithValue(a);
        return (T)Convert.ChangeType((await cmd.ExecuteScalarAsync())!, typeof(T)); // SUM devuelve numeric, no bigint
    }

    public async Task ExecAsync(string sql, params object[] args)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var a in args) cmd.Parameters.AddWithValue(a);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<JsonElement> JsonAsync(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();
}
