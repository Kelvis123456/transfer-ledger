using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;

namespace TransferLedger.Tests;

public class ConcurrencyTests : IClassFixture<LedgerApp>
{
    private readonly LedgerApp app;

    public ConcurrencyTests(LedgerApp app) => this.app = app;

    [Fact]
    public async Task Sesenta_transferencias_contra_un_saldo_para_treinta_nunca_dejan_la_cuenta_en_negativo()
    {
        var a = await app.AccountAsync(30_000); // alcanza para 30 de 1.000
        var b = await app.AccountAsync();

        var responses = await Task.WhenAll(Enumerable.Range(0, 60).Select(_ => app.TransferAsync(a, b, 1_000)));

        Assert.Equal(30, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(30, responses.Count(r => r.StatusCode == HttpStatusCode.UnprocessableEntity));
        Assert.Equal(0, await app.BalanceAsync(a));
        Assert.Equal(30_000, await app.BalanceAsync(b));
        Assert.True((await app.JsonAsync(await app.Http.GetAsync("/api/reconciliation"))).GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Transferencias_cruzadas_a_la_vez_no_se_bloquean_entre_si_y_conservan_el_total()
    {
        var a = await app.AccountAsync(1_000_000);
        var b = await app.AccountAsync(1_000_000);

        var work = Enumerable.Range(0, 30).SelectMany(_ => new[] { app.TransferAsync(a, b, 100), app.TransferAsync(b, a, 100) });
        var done = Task.WhenAll(work);
        Assert.Same(done, await Task.WhenAny(done, Task.Delay(TimeSpan.FromSeconds(25)))); // un interbloqueo se vería como cuelgue

        Assert.All(await done, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        Assert.Equal(2_000_000, await app.BalanceAsync(a) + await app.BalanceAsync(b));
        Assert.Equal(1_000_000, await app.BalanceAsync(a)); // 30 de ida y 30 de vuelta por el mismo monto
    }

    [Fact]
    public async Task Varios_destinos_a_la_vez_desde_el_mismo_origen_suman_exactamente_lo_enviado()
    {
        var src = await app.AccountAsync(500_000);
        var targets = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => app.AccountAsync()));

        var responses = await Task.WhenAll(Enumerable.Range(0, 50).Select(i => app.TransferAsync(src, targets[i % 10], 200)));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        Assert.Equal(500_000 - 50 * 200, await app.BalanceAsync(src));
        var received = 0L;
        foreach (var t in targets) received += await app.BalanceAsync(t);
        Assert.Equal(50 * 200, received);
    }
}

public class IntegrityTests : IClassFixture<LedgerApp>
{
    private readonly LedgerApp app;

    public IntegrityTests(LedgerApp app) => this.app = app;

    [Fact]
    public async Task La_conciliacion_esta_bien_despues_de_operar()
    {
        var a = await app.AccountAsync(5_000);
        var b = await app.AccountAsync();
        await app.TransferAsync(a, b, 1_234);
        await app.TransferAsync(b, a, 34);

        var report = await app.JsonAsync(await app.Http.GetAsync("/api/reconciliation"));

        Assert.True(report.GetProperty("ok").GetBoolean());
        Assert.Equal(0, report.GetProperty("entriesSum").GetInt64());
        Assert.Empty(report.GetProperty("mismatches").EnumerateArray());
    }

    [Fact]
    public async Task La_conciliacion_detecta_un_saldo_alterado_a_mano()
    {
        var a = await app.AccountAsync(5_000);
        await app.ExecAsync("UPDATE accounts SET balance = balance + 1 WHERE alias = $1", a); // alguien tocó la base

        var report = await app.JsonAsync(await app.Http.GetAsync("/api/reconciliation"));

        Assert.False(report.GetProperty("ok").GetBoolean());
        var mismatch = report.GetProperty("mismatches").EnumerateArray().Single(m => m.GetProperty("alias").GetString() == a);
        Assert.Equal(5_001, mismatch.GetProperty("balance").GetInt64());
        Assert.Equal(5_000, mismatch.GetProperty("entriesSum").GetInt64());
        await app.ExecAsync("UPDATE accounts SET balance = balance - 1 WHERE alias = $1", a); // se deja como estaba
    }

    [Fact]
    public async Task La_base_rechaza_asientos_que_no_suman_cero()
    {
        var a = await app.AccountAsync();
        var transferId = Guid.NewGuid();
        var ex = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var conn = new NpgsqlConnection(app.ConnectionString);
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();
            await Run(conn, tx, "INSERT INTO transfers (id, from_account, to_account, amount) SELECT $1, (SELECT id FROM accounts WHERE alias = '@bank'), id, 100 FROM accounts WHERE alias = $2", transferId, a);
            await Run(conn, tx, "INSERT INTO entries (transfer_id, account_id, amount) SELECT $1, id, 100 FROM accounts WHERE alias = $2", transferId, a); // falta la contrapartida
            await tx.CommitAsync(); // el trigger diferido salta aquí
        });
        Assert.Contains("no suman cero", ex.MessageText);
        Assert.Equal(0, await app.ScalarAsync<long>("SELECT count(*) FROM entries WHERE transfer_id = $1", transferId));
    }

    [Fact]
    public async Task Los_asientos_y_las_transferencias_no_se_pueden_editar_ni_borrar()
    {
        var a = await app.AccountAsync(1_000);
        var b = await app.AccountAsync();
        await app.TransferAsync(a, b, 100);

        await Assert.ThrowsAsync<PostgresException>(() => app.ExecAsync("UPDATE entries SET amount = amount + 1"));
        await Assert.ThrowsAsync<PostgresException>(() => app.ExecAsync("DELETE FROM entries"));
        await Assert.ThrowsAsync<PostgresException>(() => app.ExecAsync("UPDATE transfers SET amount = 1"));
        await Assert.ThrowsAsync<PostgresException>(() => app.ExecAsync("DELETE FROM transfers"));
    }

    [Fact]
    public async Task La_base_impide_un_saldo_negativo_aunque_se_salte_el_codigo()
    {
        var a = await app.AccountAsync(100);
        await Assert.ThrowsAsync<PostgresException>(() => app.ExecAsync("UPDATE accounts SET balance = -1 WHERE alias = $1", a));
        Assert.Equal(100, await app.BalanceAsync(a));
    }

    private static async Task Run(NpgsqlConnection c, NpgsqlTransaction t, string sql, params object[] args)
    {
        await using var cmd = new NpgsqlCommand(sql, c, t);
        foreach (var x in args) cmd.Parameters.AddWithValue(x);
        await cmd.ExecuteNonQueryAsync();
    }
}

public class AuthTests
{
    [Fact]
    public async Task Con_clave_configurada_se_exige_en_la_api_pero_no_en_health()
    {
        var app = new LedgerApp();
        await app.StartAsync("clave-secreta");
        try
        {
        Assert.Equal(HttpStatusCode.OK, (await app.Http.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.Http.GetAsync("/api/reconciliation")).StatusCode);

        var wrong = new HttpRequestMessage(HttpMethod.Get, "/api/reconciliation");
        wrong.Headers.Add("X-Api-Key", "otra");
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.Http.SendAsync(wrong)).StatusCode);

        var right = new HttpRequestMessage(HttpMethod.Get, "/api/reconciliation");
        right.Headers.Add("X-Api-Key", "clave-secreta");
        Assert.Equal(HttpStatusCode.OK, (await app.Http.SendAsync(right)).StatusCode);
        }
        finally
        {
            await app.DisposeAsync();
        }
    }
}
