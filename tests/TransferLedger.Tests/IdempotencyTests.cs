using System.Net;

namespace TransferLedger.Tests;

public class IdempotencyTests : IClassFixture<LedgerApp>
{
    private readonly LedgerApp app;

    public IdempotencyTests(LedgerApp app) => this.app = app;

    [Fact]
    public async Task Repetir_la_misma_peticion_devuelve_la_misma_respuesta_y_mueve_el_dinero_una_vez()
    {
        var a = await app.AccountAsync(10_000);
        var b = await app.AccountAsync();
        var key = Guid.NewGuid().ToString("N");

        var first = await app.TransferAsync(a, b, 1_000, key);
        var second = await app.TransferAsync(a, b, 1_000, key);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal((await app.JsonAsync(first)).GetProperty("transferId").GetGuid(), (await app.JsonAsync(second)).GetProperty("transferId").GetGuid());
        Assert.Equal(9_000, await app.BalanceAsync(a));
        Assert.Equal(1_000, await app.BalanceAsync(b));
    }

    [Fact]
    public async Task La_misma_clave_con_otro_monto_da_422_y_no_mueve_nada()
    {
        var a = await app.AccountAsync(10_000);
        var b = await app.AccountAsync();
        var key = Guid.NewGuid().ToString("N");
        await app.TransferAsync(a, b, 1_000, key);

        var res = await app.TransferAsync(a, b, 2_000, key);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        Assert.Equal(9_000, await app.BalanceAsync(a));
    }

    [Fact]
    public async Task La_misma_clave_con_otro_destino_tambien_se_rechaza()
    {
        var a = await app.AccountAsync(10_000);
        var b = await app.AccountAsync();
        var c = await app.AccountAsync();
        var key = Guid.NewGuid().ToString("N");
        await app.TransferAsync(a, b, 1_000, key);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await app.TransferAsync(a, c, 1_000, key)).StatusCode);
        Assert.Equal(0, await app.BalanceAsync(c));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("tiene espacios")]
    [InlineData("a/b")]
    public async Task Sin_una_clave_valida_da_400(string? key)
    {
        var a = await app.AccountAsync(1_000);
        var b = await app.AccountAsync();
        var res = await app.SendAsync("/api/transfers", new { from = a, to = b, cents = 100 }, key);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal(1_000, await app.BalanceAsync(a));
    }

    [Fact]
    public async Task Un_deposito_repetido_con_la_misma_clave_acredita_una_sola_vez()
    {
        var a = await app.AccountAsync();
        var key = Guid.NewGuid().ToString("N");
        await app.DepositAsync(a, 700, key);
        await app.DepositAsync(a, 700, key);
        Assert.Equal(700, await app.BalanceAsync(a));
    }

    [Fact]
    public async Task Cuarenta_peticiones_simultaneas_con_la_misma_clave_dejan_una_sola_transferencia()
    {
        var a = await app.AccountAsync(100_000);
        var b = await app.AccountAsync();
        var key = Guid.NewGuid().ToString("N");

        var responses = await Task.WhenAll(Enumerable.Range(0, 40).Select(_ => app.TransferAsync(a, b, 1_000, key)));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        var ids = (await Task.WhenAll(responses.Select(app.JsonAsync))).Select(j => j.GetProperty("transferId").GetGuid()).Distinct();
        Assert.Single(ids);
        Assert.Equal(99_000, await app.BalanceAsync(a));
        Assert.Equal(1, await app.ScalarAsync<long>(
            "SELECT count(*) FROM transfers t JOIN accounts x ON x.id = t.from_account WHERE x.alias = $1", a));
    }
}
