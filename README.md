Español: [README.es.md](README.es.md)

# transfer-ledger

A double-entry ledger with transfers between accounts by alias (a phone number or an email), idempotency keys, and a reconciliation that checks the numbers add up. It's a .NET 10 API on PostgreSQL.

I built it to see, with tests, what it takes to make moving money hard to get wrong: two identical requests must not move the money twice, a thousand simultaneous requests must not leave an account negative, and the database itself must refuse to store something that doesn't balance, even if the code has a bug. It was inspired by the idea of instant payments by alias; it doesn't follow the specification of any real system.

**Stack:** .NET 10 · ASP.NET Core minimal APIs · Npgsql (hand-written SQL, no ORM) · PostgreSQL · xUnit + Testcontainers

## How it works

| Endpoint | Purpose |
|---|---|
| `POST /api/accounts` | Creates an account with an alias |
| `POST /api/accounts/{alias}/deposit` | Credits money from the `@bank` account |
| `POST /api/transfers` | Moves money between two accounts |
| `GET /api/accounts/{alias}` | Balance and last 20 entries |
| `GET /api/reconciliation` | Checks that entries sum to zero and that each balance is the sum of its own entries |

Amounts are in cents (`bigint`). Everything under `/api` requires `X-Api-Key`. Transfers and deposits also require `Idempotency-Key`.

```
POST /api/transfers
Idempotency-Key: 7c1f...
{ "from": "8095551234", "to": "maria@mail.com", "cents": 250000, "reference": "alquiler" }

201 { "transferId": "...", "from": "8095551234", "to": "maria@mail.com", "amount": 250000 }
```

- **Double entry.** Every transfer is two entries, one negative and one positive, that sum to zero. A deposit isn't a loose `UPDATE`: it is a transfer from `@bank`, the only account that can go negative.
- **Idempotency.** The key is inserted inside the same transaction as the transfer. A second request with the same key waits on the unique index until the first one commits and then gets the same response. The same key with a different amount or destination returns 422.
- **Concurrency.** Both accounts are locked with `SELECT ... FOR UPDATE` and the balance is checked under that lock.
- **The database as the last line of defense.** A `CHECK` prevents negative balances, a deferred trigger rejects entries that don't sum to zero at commit, and another trigger makes `entries` and `transfers` insert-only.

## How to run it

You need .NET 10, and Docker for the tests.

```bash
docker run -d --name ledger-db -e POSTGRES_PASSWORD=postgres -p 5433:5432 postgres:17-alpine
ConnectionStrings__Db="Host=localhost;Port=5433;Username=postgres;Password=postgres;Database=postgres" \
  dotnet run --project src/TransferLedger.Api
```

In development it doesn't require `X-Api-Key`; outside development the app won't start without `Auth__ApiKey`.

```bash
dotnet test
```

The tests start their own Postgres with Testcontainers.

## What the tests check

38 tests against a real Postgres:

- **Transfers:** balance and entries, insufficient funds, nonexistent account (and that the failure doesn't consume the key), amounts out of range, same account, invalid aliases and phone number normalization.
- **Idempotency:** repeat, different amount, different destination, invalid key and 40 simultaneous requests with the same key.
- **Concurrency:** 60 transfers against a balance that covers 30 (exactly 30 succeed and the balance is never negative), crossed A→B and B→A transfers at the same time with no deadlock, and several destinations from the same source.
- **Integrity:** reconciliation detects a balance altered by hand, the database rejects entries that don't sum to zero, `UPDATE` and `DELETE` on entries and transfers fail, and a negative balance is rejected even if the code is bypassed.

To check that the tests can fail, I deliberately removed parts of the code. Without `FOR UPDATE` the concurrency tests fail, and without the key's `ON CONFLICT` the idempotency tests fail. Without the `ORDER BY id` in the locking, all of them still pass: Postgres's plan walks the rows in a stable order and the two crossed transactions don't block each other. I kept it as an explicit defense and the code comment says so; it isn't something a test demonstrates.

## What it is not

- **It doesn't follow any real specification.** Not the Central Bank's instant payment system nor that of any payment network. There are no fees, daily limits, fraud detection, or verification that an alias belongs to whoever claims it.
- **One currency and no users.** A single shared API key protects the whole API; there are no customer accounts and no per-account permissions.
- **Business errors aren't stored.** A 404 or a 422 rolls back the whole transaction, key included, so retrying after fixing the problem works. Only successful transfers are recorded with their key, and those keys don't expire.
- **I didn't measure performance.** The concurrency tests use dozens of requests, not thousands, and there is no load test. I don't know how many transfers per second it handles, and I don't claim to.
- **No rate limiting or pagination** (`GET /accounts/{alias}` returns the last 20 entries).
- **It isn't deployed.**

The decisions and what I ruled out are in [docs/decisions.md](docs/decisions.md).
