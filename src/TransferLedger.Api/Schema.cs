using Npgsql;

namespace TransferLedger.Api;

public static class Schema
{
    // Idempotente: se puede correr en cada arranque.
    public const string Sql = """
        CREATE TABLE IF NOT EXISTS accounts (
            id             bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            alias          text        NOT NULL UNIQUE,
            balance        bigint      NOT NULL DEFAULT 0,   -- centavos
            allow_negative boolean     NOT NULL DEFAULT false,
            created_at     timestamptz NOT NULL DEFAULT now(),
            -- La base es la última defensa: aunque el código falle, un saldo no baja de cero.
            CONSTRAINT balance_non_negative CHECK (allow_negative OR balance >= 0)
        );

        CREATE TABLE IF NOT EXISTS transfers (
            id           uuid        PRIMARY KEY,
            from_account bigint      NOT NULL REFERENCES accounts(id),
            to_account   bigint      NOT NULL REFERENCES accounts(id),
            amount       bigint      NOT NULL CHECK (amount > 0),
            reference    text,
            created_at   timestamptz NOT NULL DEFAULT now(),
            CHECK (from_account <> to_account)
        );

        -- Partida doble: cada transferencia son dos asientos que suman cero (débito negativo, crédito positivo).
        CREATE TABLE IF NOT EXISTS entries (
            id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            transfer_id uuid   NOT NULL REFERENCES transfers(id),
            account_id  bigint NOT NULL REFERENCES accounts(id),
            amount      bigint NOT NULL CHECK (amount <> 0)
        );
        CREATE INDEX IF NOT EXISTS entries_account ON entries (account_id);
        CREATE INDEX IF NOT EXISTS entries_transfer ON entries (transfer_id);

        CREATE TABLE IF NOT EXISTS idempotency_keys (
            key          text        PRIMARY KEY,
            request_hash text        NOT NULL,
            status_code  integer     NOT NULL DEFAULT 0,
            response     jsonb       NOT NULL DEFAULT '{}',
            created_at   timestamptz NOT NULL DEFAULT now()
        );

        CREATE OR REPLACE FUNCTION entries_must_sum_to_zero() RETURNS trigger AS $$
        BEGIN
            IF (SELECT COALESCE(SUM(amount), 0) FROM entries WHERE transfer_id = NEW.transfer_id) <> 0 THEN
                RAISE EXCEPTION 'los asientos de la transferencia % no suman cero', NEW.transfer_id;
            END IF;
            RETURN NULL;
        END $$ LANGUAGE plpgsql;

        CREATE OR REPLACE FUNCTION ledger_is_append_only() RETURNS trigger AS $$
        BEGIN
            RAISE EXCEPTION 'la tabla % es solo de inserción', TG_TABLE_NAME;
        END $$ LANGUAGE plpgsql;

        DO $$
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'entries_zero_sum') THEN
                -- DEFERRABLE INITIALLY DEFERRED: se comprueba al confirmar, cuando ya están los dos asientos.
                CREATE CONSTRAINT TRIGGER entries_zero_sum AFTER INSERT ON entries
                    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION entries_must_sum_to_zero();
            END IF;
            IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'entries_append_only') THEN
                CREATE TRIGGER entries_append_only BEFORE UPDATE OR DELETE ON entries
                    FOR EACH ROW EXECUTE FUNCTION ledger_is_append_only();
            END IF;
            IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'transfers_append_only') THEN
                CREATE TRIGGER transfers_append_only BEFORE UPDATE OR DELETE ON transfers
                    FOR EACH ROW EXECUTE FUNCTION ledger_is_append_only();
            END IF;
        END $$;

        -- La cuenta del banco puede quedar en negativo: de ahí salen los depósitos, y así todo sigue siendo partida doble.
        INSERT INTO accounts (alias, allow_negative) VALUES ('@bank', true) ON CONFLICT (alias) DO NOTHING;
        """;

    public static async Task MigrateAsync(NpgsqlDataSource db)
    {
        await using var cmd = db.CreateCommand(Sql);
        await cmd.ExecuteNonQueryAsync();
    }
}
