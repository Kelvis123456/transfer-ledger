using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;

namespace TransferLedger.Api;

public enum Failure { None, InvalidInput, NotFound, InsufficientFunds, Conflict, IdempotencyMismatch }

public sealed record LedgerResult(Failure Failure, string? Message, int Status, JsonElement? Body, bool Replay)
{
    public static LedgerResult Fail(Failure f, string message) => new(f, message, f switch
    {
        Failure.InvalidInput => 400,
        Failure.NotFound => 404,
        Failure.InsufficientFunds => 422,
        Failure.IdempotencyMismatch => 422,
        _ => 409,
    }, null, false);
}

public sealed record Discrepancy(string Alias, long Balance, long EntriesSum);

public sealed record ReconciliationReport(bool Ok, long EntriesSum, IReadOnlyList<Discrepancy> Mismatches);

public sealed partial class LedgerService(NpgsqlDataSource db)
{
    public const long MaxAmount = 1_000_000_000_000; // 10 mil millones de pesos en centavos; evita desbordes al sumar

    [GeneratedRegex("^@?[a-z0-9][a-z0-9@._+-]{2,39}$")]
    private static partial Regex AliasRx();

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex KeyRx();

    /// <summary>Minúsculas y sin espacios en los extremos. Un teléfono con guiones o espacios se reduce a sus dígitos.</summary>
    public static string? NormalizeAlias(string? raw)
    {
        var a = raw?.Trim().ToLowerInvariant();
        if (a is null) return null;
        var digits = new string(a.Where(char.IsAsciiDigit).ToArray());
        if (digits.Length >= 10 && a.All(c => char.IsAsciiDigit(c) || c is '-' or ' ' or '(' or ')' or '+')) a = digits;
        return AliasRx().IsMatch(a) ? a : null;
    }

    public async Task<LedgerResult> CreateAccountAsync(string? alias)
    {
        var a = NormalizeAlias(alias);
        if (a is null || a.StartsWith('@'))
            return LedgerResult.Fail(Failure.InvalidInput, "El alias debe tener de 3 a 40 caracteres (letras, números y @ . _ + -) y no puede empezar por @.");
        await using var cmd = db.CreateCommand("INSERT INTO accounts (alias) VALUES ($1) ON CONFLICT (alias) DO NOTHING");
        cmd.Parameters.AddWithValue(a);
        return await cmd.ExecuteNonQueryAsync() == 0
            ? LedgerResult.Fail(Failure.Conflict, "Ese alias ya existe.")
            : new LedgerResult(Failure.None, null, 201, JsonSerializer.SerializeToElement(new { alias = a, balance = 0L }), false);
    }

    /// <summary>Un depósito es una transferencia desde @bank: así el dinero entra por partida doble y no por un UPDATE suelto.</summary>
    public Task<LedgerResult> DepositAsync(string? alias, long amount, string? key) =>
        TransferAsync("@bank", alias, amount, "depósito", key, allowSystemSource: true);

    public async Task<LedgerResult> TransferAsync(string? from, string? to, long amount, string? reference, string? idempotencyKey, bool allowSystemSource = false)
    {
        var f = NormalizeAlias(from);
        var t = NormalizeAlias(to);
        if (idempotencyKey is null || !KeyRx().IsMatch(idempotencyKey))
            return LedgerResult.Fail(Failure.InvalidInput, "Falta la cabecera Idempotency-Key (1 a 64 caracteres: letras, números, _ y -).");
        if (f is null || t is null) return LedgerResult.Fail(Failure.InvalidInput, "Alias inválido.");
        if (f.StartsWith('@') && !allowSystemSource) return LedgerResult.Fail(Failure.InvalidInput, "No se puede transferir desde una cuenta del sistema.");
        if (f == t) return LedgerResult.Fail(Failure.InvalidInput, "El origen y el destino son la misma cuenta.");
        if (amount is < 1 or > MaxAmount) return LedgerResult.Fail(Failure.InvalidInput, $"El monto debe estar entre 1 y {MaxAmount} centavos.");
        if (reference is { Length: > 140 }) return LedgerResult.Fail(Failure.InvalidInput, "La referencia admite hasta 140 caracteres.");

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{f}|{t}|{amount}|{reference}")));

        await using var conn = await db.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();

        // La clave se inserta dentro de la misma transacción que la transferencia. Una segunda petición con la misma clave
        // se queda esperando en el índice único hasta que la primera confirme o se revierta, y entonces o ve su respuesta
        // guardada o ejecuta ella: nunca hay dos transferencias con una clave.
        await using (var claim = new NpgsqlCommand(
            "INSERT INTO idempotency_keys (key, request_hash) VALUES ($1, $2) ON CONFLICT (key) DO NOTHING", conn, tx))
        {
            claim.Parameters.AddWithValue(idempotencyKey);
            claim.Parameters.AddWithValue(hash);
            if (await claim.ExecuteNonQueryAsync() == 0)
                return await ReplayAsync(conn, tx, idempotencyKey, hash);
        }

        // Las dos cuentas se bloquean en una sola consulta y en orden de id, así A->B y B->A a la vez no pueden esperarse
        // en círculo. Ojo: quitando el ORDER BY mi prueba de transferencias cruzadas sigue pasando (el plan recorre las
        // filas en un orden estable), así que esto es defensa explícita, no algo que la prueba demuestre.
        var accounts = new Dictionary<string, (long Id, long Balance, bool AllowNegative)>();
        await using (var lockCmd = new NpgsqlCommand(
            "SELECT id, alias, balance, allow_negative FROM accounts WHERE alias = ANY($1) ORDER BY id FOR UPDATE", conn, tx))
        {
            lockCmd.Parameters.AddWithValue(new[] { f, t });
            await using var r = await lockCmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) accounts[r.GetString(1)] = (r.GetInt64(0), r.GetInt64(2), r.GetBoolean(3));
        }
        if (!accounts.TryGetValue(f, out var src)) return LedgerResult.Fail(Failure.NotFound, $"No existe la cuenta {f}.");
        if (!accounts.TryGetValue(t, out var dst)) return LedgerResult.Fail(Failure.NotFound, $"No existe la cuenta {t}.");
        if (!src.AllowNegative && src.Balance < amount) return LedgerResult.Fail(Failure.InsufficientFunds, "Saldo insuficiente.");

        var id = Guid.NewGuid();
        await Exec(conn, tx, "INSERT INTO transfers (id, from_account, to_account, amount, reference) VALUES ($1, $2, $3, $4, $5)",
            id, src.Id, dst.Id, amount, (object?)reference ?? DBNull.Value);
        await Exec(conn, tx, "INSERT INTO entries (transfer_id, account_id, amount) VALUES ($1, $2, $3), ($1, $4, $5)",
            id, src.Id, -amount, dst.Id, amount);
        await Exec(conn, tx, "UPDATE accounts SET balance = balance - $2 WHERE id = $1", src.Id, amount);
        await Exec(conn, tx, "UPDATE accounts SET balance = balance + $2 WHERE id = $1", dst.Id, amount);

        var body = JsonSerializer.SerializeToElement(new { transferId = id, from = f, to = t, amount, reference });
        await Exec(conn, tx, "UPDATE idempotency_keys SET status_code = 201, response = $2::jsonb WHERE key = $1",
            idempotencyKey, body.GetRawText());
        await tx.CommitAsync();
        return new LedgerResult(Failure.None, null, 201, body, false);
    }

    private static async Task<LedgerResult> ReplayAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string key, string hash)
    {
        await using var cmd = new NpgsqlCommand("SELECT request_hash, status_code, response FROM idempotency_keys WHERE key = $1", conn, tx);
        cmd.Parameters.AddWithValue(key);
        await using var r = await cmd.ExecuteReaderAsync();
        await r.ReadAsync();
        if (r.GetString(0) != hash)
            return LedgerResult.Fail(Failure.IdempotencyMismatch, "Esa Idempotency-Key ya se usó con otra petición distinta.");
        return new LedgerResult(Failure.None, null, r.GetInt32(1), JsonDocument.Parse(r.GetString(2)).RootElement.Clone(), true);
    }

    private static async Task Exec(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, params object[] args)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        foreach (var a in args) cmd.Parameters.AddWithValue(a);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<JsonElement?> GetAccountAsync(string? alias)
    {
        var a = NormalizeAlias(alias);
        if (a is null) return null;
        await using var cmd = db.CreateCommand("SELECT id, balance FROM accounts WHERE alias = $1");
        cmd.Parameters.AddWithValue(a);
        long id, balance;
        await using (var r = await cmd.ExecuteReaderAsync())
        {
            if (!await r.ReadAsync()) return null;
            (id, balance) = (r.GetInt64(0), r.GetInt64(1));
        }
        await using var rows = db.CreateCommand(
            "SELECT transfer_id, amount FROM entries WHERE account_id = $1 ORDER BY id DESC LIMIT 20");
        rows.Parameters.AddWithValue(id);
        var entries = new List<object>();
        await using (var r = await rows.ExecuteReaderAsync())
            while (await r.ReadAsync()) entries.Add(new { transferId = r.GetGuid(0), amount = r.GetInt64(1) });
        return JsonSerializer.SerializeToElement(new { alias = a, balance, entries });
    }

    /// <summary>
    /// La comprobación que haría un contador: todos los asientos suman cero y el saldo de cada cuenta es la suma de sus
    /// asientos. No depende de que el código esté bien: lee solo los datos.
    /// </summary>
    public async Task<ReconciliationReport> ReconcileAsync()
    {
        await using var total = db.CreateCommand("SELECT COALESCE(SUM(amount), 0) FROM entries");
        var entriesSum = Convert.ToInt64(await total.ExecuteScalarAsync()); // SUM de bigint devuelve numeric

        var mismatches = new List<Discrepancy>();
        await using var cmd = db.CreateCommand(
            """
            SELECT a.alias, a.balance, COALESCE(SUM(e.amount), 0)
              FROM accounts a LEFT JOIN entries e ON e.account_id = a.id
             GROUP BY a.id, a.alias, a.balance
            HAVING a.balance <> COALESCE(SUM(e.amount), 0)
            """);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) mismatches.Add(new Discrepancy(r.GetString(0), r.GetInt64(1), r.GetInt64(2)));
        return new ReconciliationReport(entriesSum == 0 && mismatches.Count == 0, entriesSum, mismatches);
    }
}
