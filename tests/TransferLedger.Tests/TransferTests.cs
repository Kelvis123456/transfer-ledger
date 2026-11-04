using System.Net;
using System.Net.Http.Json;
using TransferLedger.Api;

namespace TransferLedger.Tests;

public class TransferTests : IClassFixture<LedgerApp>
{
    private readonly LedgerApp app;

    public TransferTests(LedgerApp app) => this.app = app;

    [Fact]
    public async Task Una_transferencia_mueve_el_saldo_y_deja_dos_asientos_que_suman_cero()
    {
        var a = await app.AccountAsync(10_000);
        var b = await app.AccountAsync();

        var res = await app.TransferAsync(a, b, 2_500, reference: "almuerzo");

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        Assert.Equal(7_500, await app.BalanceAsync(a));
        Assert.Equal(2_500, await app.BalanceAsync(b));
        var id = (await app.JsonAsync(res)).GetProperty("transferId").GetGuid();
        Assert.Equal(2, await app.ScalarAsync<long>("SELECT count(*) FROM entries WHERE transfer_id = $1", id));
        Assert.Equal(0, await app.ScalarAsync<long>("SELECT COALESCE(SUM(amount), 0) FROM entries WHERE transfer_id = $1", id));
    }

    [Fact]
    public async Task Saldo_insuficiente_da_422_y_no_cambia_nada()
    {
        var a = await app.AccountAsync(1_000);
        var b = await app.AccountAsync();

        var res = await app.TransferAsync(a, b, 1_001);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        Assert.Equal(1_000, await app.BalanceAsync(a));
        Assert.Equal(0, await app.BalanceAsync(b));
        Assert.Equal(0, await app.ScalarAsync<long>(
            "SELECT count(*) FROM entries e JOIN accounts x ON x.id = e.account_id WHERE x.alias = $1 AND e.amount < 0", a));
    }

    [Fact]
    public async Task Una_cuenta_que_no_existe_da_404_y_no_gasta_la_clave_de_idempotencia()
    {
        var a = await app.AccountAsync(1_000);
        var ghost = LedgerApp.NewAlias();
        var key = Guid.NewGuid().ToString("N");

        Assert.Equal(HttpStatusCode.NotFound, (await app.TransferAsync(a, ghost, 100, key)).StatusCode);

        (await app.Http.PostAsJsonAsync("/api/accounts", new { alias = ghost })).EnsureSuccessStatusCode();
        // El fallo se revirtió entero, clave incluida: el mismo reintento ahora sí funciona.
        Assert.Equal(HttpStatusCode.Created, (await app.TransferAsync(a, ghost, 100, key)).StatusCode);
        Assert.Equal(100, await app.BalanceAsync(ghost));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(LedgerService.MaxAmount + 1)]
    public async Task Un_monto_fuera_de_rango_da_400(long cents)
    {
        var a = await app.AccountAsync(1_000);
        var b = await app.AccountAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await app.TransferAsync(a, b, cents)).StatusCode);
    }

    [Fact]
    public async Task Transferir_a_la_misma_cuenta_da_400()
    {
        var a = await app.AccountAsync(1_000);
        Assert.Equal(HttpStatusCode.BadRequest, (await app.TransferAsync(a, a, 100)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await app.TransferAsync(a, a.ToUpperInvariant(), 100)).StatusCode);
    }

    [Fact]
    public async Task No_se_puede_transferir_desde_una_cuenta_del_sistema()
    {
        var a = await app.AccountAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await app.TransferAsync("@bank", a, 100)).StatusCode);
        Assert.Equal(0, await app.BalanceAsync(a));
    }

    [Fact]
    public async Task Una_referencia_demasiado_larga_da_400()
    {
        var a = await app.AccountAsync(1_000);
        var b = await app.AccountAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await app.TransferAsync(a, b, 100, reference: new string('x', 141))).StatusCode);
    }

    [Fact]
    public async Task Los_alias_se_normalizan_al_crear_y_al_transferir()
    {
        var alias = LedgerApp.NewAlias();
        (await app.Http.PostAsJsonAsync("/api/accounts", new { alias = $"  {alias.ToUpperInvariant()}  " })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await app.Http.PostAsJsonAsync("/api/accounts", new { alias })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await app.DepositAsync(alias.ToUpperInvariant(), 500)).StatusCode);
        Assert.Equal(500, await app.BalanceAsync(alias));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ab")]
    [InlineData("tiene espacios")]
    [InlineData("@banco")]
    [InlineData("a;drop table accounts")]
    public async Task Un_alias_invalido_o_reservado_no_crea_cuenta(string alias)
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await app.Http.PostAsJsonAsync("/api/accounts", new { alias })).StatusCode);
    }

    [Theory]
    [InlineData("809-555-1234", "8095551234")]
    [InlineData("(809) 555 1234", "8095551234")]
    [InlineData("+1 809 555 1234", "18095551234")]
    [InlineData("  Maria.Perez@Mail.com ", "maria.perez@mail.com")]
    public void NormalizeAlias_reduce_los_telefonos_a_digitos_y_baja_a_minusculas(string raw, string expected)
    {
        Assert.Equal(expected, LedgerService.NormalizeAlias(raw));
    }

    [Fact]
    public async Task Un_deposito_entra_por_partida_doble_desde_la_cuenta_del_banco()
    {
        var a = await app.AccountAsync();
        var before = await app.ScalarAsync<long>("SELECT balance FROM accounts WHERE alias = '@bank'");

        (await app.DepositAsync(a, 3_000)).EnsureSuccessStatusCode();

        Assert.Equal(3_000, await app.BalanceAsync(a));
        Assert.Equal(before - 3_000, await app.ScalarAsync<long>("SELECT balance FROM accounts WHERE alias = '@bank'"));
    }
}
