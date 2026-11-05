English: [README.md](README.md)

# transfer-ledger

Un libro mayor de partida doble con transferencias entre cuentas por alias (un teléfono o un correo), con claves de idempotencia y una conciliación que comprueba que los números cuadran. Es una API en .NET 10 sobre PostgreSQL.

Lo hice para ver, con pruebas, qué hace falta para que mover dinero no se pueda hacer mal: que dos peticiones iguales no muevan el dinero dos veces, que mil peticiones a la vez no dejen una cuenta en negativo y que la base misma se niegue a guardar algo que no cuadra, aunque el código tenga un bug. Lo inspiró la idea de los pagos instantáneos por alias; no sigue la especificación de ningún sistema real.

**Stack:** .NET 10 · ASP.NET Core minimal APIs · Npgsql (SQL a mano, sin ORM) · PostgreSQL · xUnit + Testcontainers

## Cómo funciona

| Endpoint | Para qué |
|---|---|
| `POST /api/accounts` | Crea una cuenta con un alias |
| `POST /api/accounts/{alias}/deposit` | Acredita dinero desde la cuenta `@bank` |
| `POST /api/transfers` | Mueve dinero entre dos cuentas |
| `GET /api/accounts/{alias}` | Saldo y últimos 20 asientos |
| `GET /api/reconciliation` | Comprueba que los asientos suman cero y que cada saldo es la suma de los suyos |

Los montos van en centavos (`bigint`). Todo lo de `/api` pide `X-Api-Key`. Las transferencias y los depósitos piden además `Idempotency-Key`.

```
POST /api/transfers
Idempotency-Key: 7c1f...
{ "from": "8095551234", "to": "maria@mail.com", "cents": 250000, "reference": "alquiler" }

201 { "transferId": "...", "from": "8095551234", "to": "maria@mail.com", "amount": 250000 }
```

- **Partida doble.** Cada transferencia son dos asientos, uno negativo y otro positivo, que suman cero. Un depósito no es un `UPDATE` suelto: es una transferencia desde `@bank`, la única cuenta que puede quedar en negativo.
- **Idempotencia.** La clave se inserta dentro de la misma transacción que la transferencia. Una segunda petición con la misma clave espera en el índice único hasta que la primera confirme y entonces recibe la misma respuesta. La misma clave con otro monto o destino da 422.
- **Concurrencia.** Las dos cuentas se bloquean con `SELECT ... FOR UPDATE` y se comprueba el saldo bajo ese bloqueo.
- **La base como última defensa.** Un `CHECK` impide saldos negativos, un trigger diferido rechaza asientos que no suman cero al confirmar, y otro trigger vuelve `entries` y `transfers` solo de inserción.

## Cómo correrlo

Necesitas .NET 10 y Docker para los tests.

```bash
docker run -d --name ledger-db -e POSTGRES_PASSWORD=postgres -p 5433:5432 postgres:17-alpine
ConnectionStrings__Db="Host=localhost;Port=5433;Username=postgres;Password=postgres;Database=postgres" \
  dotnet run --project src/TransferLedger.Api
```

En desarrollo no pide `X-Api-Key`; fuera de desarrollo la app no arranca sin `Auth__ApiKey`.

```bash
dotnet test
```

Los tests levantan su propio Postgres con Testcontainers.

## Qué prueban los tests

38 pruebas contra un Postgres real:

- **Transferencias:** saldo y asientos, saldo insuficiente, cuenta inexistente (y que el fallo no gaste la clave), montos fuera de rango, misma cuenta, alias inválidos y normalización de teléfonos.
- **Idempotencia:** repetir, otro monto, otro destino, clave inválida y 40 peticiones simultáneas con la misma clave.
- **Concurrencia:** 60 transferencias contra un saldo para 30 (exactamente 30 pasan y el saldo nunca es negativo), transferencias cruzadas A→B y B→A a la vez sin interbloqueo, y varios destinos desde el mismo origen.
- **Integridad:** la conciliación detecta un saldo alterado a mano, la base rechaza asientos que no suman cero, `UPDATE` y `DELETE` sobre asientos y transferencias fallan, y un saldo negativo se rechaza aunque se salte el código.

Para comprobar que las pruebas pueden fallar quité a propósito partes del código. Sin `FOR UPDATE` fallan las de concurrencia y sin el `ON CONFLICT` de la clave fallan las de idempotencia. Sin el `ORDER BY id` del bloqueo todas siguen pasando: el plan de Postgres recorre las filas en un orden estable y las dos transacciones cruzadas no se bloquean entre sí. Lo dejé como defensa explícita y lo dice el comentario del código; no es algo que una prueba demuestre.

## Lo que no es

- **No sigue ninguna especificación real.** Ni la del sistema de pagos instantáneos del Banco Central ni la de ninguna red de pagos. No hay comisiones, límites diarios, detección de fraude ni verificación de que un alias pertenezca a quien dice.
- **Una sola moneda y sin usuarios.** Una clave de API compartida protege toda la API; no hay cuentas de cliente ni permisos por cuenta.
- **Los errores de negocio no se guardan.** Un 404 o un 422 revierte la transacción entera, clave incluida, así que reintentar después de corregir el problema funciona. Solo las transferencias exitosas quedan registradas con su clave, y esas claves no caducan.
- **No medí rendimiento.** Las pruebas de concurrencia usan decenas de peticiones, no miles, y no hay una prueba de carga. No sé cuántas transferencias por segundo aguanta, y no lo afirmo.
- **Sin límite de peticiones ni paginación** (`GET /accounts/{alias}` devuelve los últimos 20 asientos).
- **No está desplegado.**

Las decisiones y lo que descarté están en [docs/decisions.md](docs/decisions.md).
