# Decisiones

Cada entrada dice qué elegí, qué descarté y por qué.

## 2026-11-03 · Partida doble en vez de solo un saldo

**Elegí:** cada movimiento son dos asientos que suman cero, y el saldo de la cuenta se mantiene aparte y se puede comprobar contra ellos.

**Descarté:** una tabla de cuentas con un `balance` que se actualiza y nada más. Es más corto, pero cuando algo no cuadra no hay con qué comparar. Con los asientos, la conciliación es una consulta que no confía en el código: suma todos los asientos (debe dar cero) y compara el saldo de cada cuenta con la suma de los suyos. Hay una prueba que altera un saldo a mano y comprueba que la conciliación lo señala.

Los depósitos salen de una cuenta `@bank` que puede quedar en negativo. Así el dinero nunca aparece de la nada: toda entrada tiene su contrapartida.

## 2026-11-03 · Que la base se niegue a guardar algo que no cuadra

El código comprueba el saldo, pero también hay un `CHECK (allow_negative OR balance >= 0)`. Si mañana alguien rompe la comprobación del código, la base sigue protegiendo. Un trigger diferido (se evalúa al confirmar, cuando ya están los dos asientos) rechaza cualquier transferencia cuyos asientos no sumen cero, y otro trigger impide `UPDATE` y `DELETE` sobre asientos y transferencias: lo que ya se escribió no se corrige, se compensa con otro movimiento. Hay una prueba para cada una de estas tres.

## 2026-11-03 · La clave de idempotencia dentro de la transacción

**Elegí:** insertar la clave en la misma transacción que la transferencia, con `ON CONFLICT DO NOTHING`.

**Descarté:**
- Comprobar primero si existe y luego insertar: entre las dos hay una ventana donde se cuelan dos peticiones iguales.
- Guardar la clave en otra transacción antes de empezar: si la transferencia falla, queda una clave "gastada" sin transferencia.

Con la clave dentro de la transacción, una segunda petición idéntica espera en el índice único hasta que la primera confirme o se revierta. Si confirmó, ve la respuesta guardada. Si se revirtió, ejecuta ella. Con 40 peticiones simultáneas queda una sola transferencia; sin el `ON CONFLICT` fallan 5 pruebas.

La clave también guarda un hash de la petición. La misma clave con otro monto o destino es un error del cliente (422), no una repetición.

**Lo que decidí no guardar:** los errores de negocio. Un 404 o un 422 revierte todo, clave incluida, así que reintentar después de corregir el problema (crear la cuenta que faltaba, por ejemplo) funciona. A cambio, esos reintentos se evalúan de nuevo. Las claves de transferencias exitosas no caducan; no escribí la limpieza.

## 2026-11-03 · Bloqueo de las dos cuentas bajo `FOR UPDATE`

El saldo se comprueba después de bloquear la fila, no antes: si no, dos transferencias podrían ver el mismo saldo y gastarlo las dos. Con 60 transferencias contra un saldo para 30, pasan exactamente 30; sin el `FOR UPDATE` la prueba falla.

Las dos cuentas se piden en una sola consulta con `ORDER BY id`, para que A→B y B→A a la vez no se esperen en círculo. **No pude demostrar que ese `ORDER BY` haga falta:** al quitarlo, la prueba de transferencias cruzadas siguió pasando, porque Postgres recorre las filas en un orden estable. Lo dejé como defensa explícita (el comentario del código lo dice) y no como algo comprobado.

## 2026-11-03 · SQL a mano con Npgsql

Sin ORM. Lo que importa aquí son tres o cuatro sentencias concretas (el bloqueo, el `ON CONFLICT`, los triggers), y un ORM las escondería justo cuando hay que verlas. Descarté Dapper por lo mismo: con ADO.NET el costo es poco más y no hay otra dependencia.

## 2026-11-03 · Centavos en `bigint`, tope por transferencia

Nada de `decimal` ni `double` para dinero en el borde: el monto llega como entero de centavos. Cada transferencia tiene un tope de 10 mil millones de pesos para que ninguna suma pueda desbordar un `bigint`.

## 2026-11-03 · Pruebas contra un Postgres real

Con Testcontainers, una base nueva por clase de pruebas dentro de un mismo contenedor. Un mock no puede probar un bloqueo de fila, un trigger diferido ni lo que pasa con 40 peticiones a la vez. Costo: unos segundos de arranque y Docker en el CI.

Encontré dos errores propios al correr las pruebas por primera vez. El alias `@bank` no pasaba mi propia validación, así que ningún depósito funcionaba (27 pruebas fallando). Y `SUM(bigint)` devuelve `numeric` en Postgres, que el código intentaba convertir a `long` sin más; lo vi en la conciliación.

## 2026-11-03 · Lo que esto no es

No sigue la especificación de ningún sistema de pagos real y no la he leído. Lo llamo "inspirado en la idea de pagos por alias" y nada más. No hay comisiones, límites, fraude, ni identidad verificada detrás de un alias. No medí rendimiento.
