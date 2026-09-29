# Restauración de data histórica GPS

Microservicio .NET 8 (Minimal API) que restaura data histórica de GPS desde Cloudflare R2
(bucket `data-semanal`) hacia `dbv16_01`, filtrando por placa y rango de fechas.

Cuando un cliente pide data de hace más de 6 meses, la tabla `dbv16_01.gps_YYYYMMNN` ya no existe.
El servicio descarga el dump `gps_YYYYMMNN.sql.gz`, lo lee en streaming quedándose **solo** con las filas
de las placas y fechas pedidas, las inserta en `dbv16_01.gps_YYYYMMNN` y limpia. El dump nunca se importa
completo. El cliente descarga luego desde la web de rastreo, como siempre.

> Las tablas restauradas las borra el cron de retención la madrugada siguiente. Es lo esperado.

---

## 1. Estructura

```
Dockerfile
docker-compose.yml
.env.example                  plantilla de variables (copiar a .env)
src/RestauracionGps/
  Program.cs                  arranque, DI, configuración
  appsettings.json            estructura de configuración (sin secretos)
  Api/                        endpoints, contratos JSON, API key
  Datos/                      acceso a MySQL (historicos, gps_*, estado de trabajos)
  Servicios/
    TrabajadorRestauracion.cs BackgroundService: procesa la cola, de a un trabajo y de a una tabla
    Restaurador.cs            descarga -> crea destino -> lee y filtra el dump -> INSERT IGNORE -> limpia
    R2Descargador.cs          descarga de R2 (S3) con reintentos
    LectorDumpSql.cs          parser en streaming del .sql.gz (tuplas, comillas, escapes, verificación)
    ArchivoTuplas.cs          archivo temporal con las tuplas aceptadas
    Disponibilidad.cs         decide si una tabla ya está disponible y se puede saltar
tests/RestauracionGps.Tests/  tests del parser (xUnit)
```

Para correr los tests: `dotnet test` (desde la raíz, con el SDK de .NET 8 o superior).

## 2. Permisos en MySQL

No hace falta ninguna base auxiliar. `gtsuser` necesita (normalmente ya los tiene):

- `SELECT` en `gts.historicos` y `gts.eventdata` (para `CREATE TABLE ... LIKE gts.eventdata`).
- `CREATE`, `INSERT`, `SELECT`, `UPDATE` en `dbv16_01`.

Al arrancar, el servicio crea sus propias tablas de estado en `dbv16_01`:

| Tabla | Contenido |
|---|---|
| `restauraciones` | un registro por trabajo (placas, rango, estado, tiempos) |
| `restauraciones_detalle` | una fila por tabla de cada trabajo (estado, filas insertadas, segundos, error) |

⚠️ **Verifica que el cron de retención solo borre tablas `gps_*`** y no toque estas dos.

## 3. Despliegue (Ubuntu 24.04)

```bash
# 1. Copiar el proyecto al servidor, por ejemplo a /opt/restauracion-gps
cd /opt/restauracion-gps

# 2. Crear el archivo de variables (no se comitea)
cp .env.example .env
nano .env            # completar credenciales MySQL, R2 y la API key
chmod 600 .env

# 3. Verificar que la red de mysql-gts existe
docker network inspect app-net > /dev/null && echo "app-net OK"

# 4. Construir y levantar
docker compose up -d --build

# 5. Comprobar
curl -s http://localhost:3009/health
```

Para actualizar después de un cambio de código: `docker compose up -d --build`.
Si el contenedor se reinicia en medio de un trabajo, lo retoma solo desde la tabla que estaba procesando.

Generar una API key: `openssl rand -hex 24`.

## 4. API

Todas las rutas `/api/*` requieren el header `X-Api-Key`. `/health` no lo requiere.

```bash
export API=http://localhost:3009
export KEY=<valor de Restauracion__ApiKey>
```

### Crear una restauración — `POST /api/restauracion`

```bash
curl -s -X POST "$API/api/restauracion" \
  -H "X-Api-Key: $KEY" -H "Content-Type: application/json" \
  -d '{
        "placas": ["ABC123", "XYZ789"],
        "desde": "2025-03-01",
        "hasta": "2025-03-15",
        "solicitante": "jperez - ticket 4521"
      }'
```

Responde **202 Accepted** de inmediato; el trabajo corre en segundo plano:

```json
{
  "jobId": "a1b2c3d4",
  "estado": "EN_COLA",
  "tablasAProcesar": ["gps_20250301", "gps_20250302", "gps_20250303"],
  "estimadoMinutos": 12,
  "trabajosEnColaAntes": 0
}
```

También se puede pedir un rango con hora, por ejemplo el 27/09 de 08:00 a 18:30:

```bash
curl -s -X POST "$API/api/restauracion" \
  -H "X-Api-Key: $KEY" -H "Content-Type: application/json" \
  -d '{
        "placas": ["ABC123"],
        "desde": "2025-09-27T08:00",
        "hasta": "2025-09-27T18:30",
        "solicitante": "jperez - ticket 4530"
      }'
```

Formatos aceptados en `desde` y `hasta` (siempre en hora **America/Lima**; se pueden combinar):

| Valor | `desde` se interpreta como | `hasta` se interpreta como |
|---|---|---|
| `2025-06-29` (solo fecha) | `2025-06-29 00:00:00` | `2025-06-29 23:59:59` |
| `2025-06-29T08:30` | `2025-06-29 08:30:00` | `2025-06-29 08:30:00` |
| `2025-06-29T08:30:00` | `2025-06-29 08:30:00` | `2025-06-29 08:30:00` |

Solo cuando viene sin hora se completa a 00:00:00 / 23:59:59; si trae hora se respeta tal cual
(`"hasta": "2025-06-30T18:30"` termina a las 18:30:00, no al final del día). No se aceptan otros
formatos (espacio en lugar de `T`, `dd/MM/yyyy`, zona horaria `Z` u offset).

Reglas:

- `desde` debe ser estrictamente anterior a `hasta`.
- La duración (`hasta - desde`) no puede superar `Restauracion__RangoMaximoDias` (62 días por defecto).
  Con fechas sin hora, eso equivale a un máximo de 62 días calendario (p. ej. `2025-01-01` a `2025-03-03`).
- Al menos una placa (se quitan espacios y duplicados). `deviceID` = placa.
- Si el rango no tiene periodos en `gts.historicos` → **422**.
- Errores de validación → **400** `{"error": "..."}`. API key incorrecta → **401**.

### Consultar un trabajo — `GET /api/restauracion/{jobId}`

```bash
curl -s "$API/api/restauracion/a1b2c3d4" -H "X-Api-Key: $KEY"
```

```json
{
  "jobId": "a1b2c3d4",
  "estado": "PROCESANDO",
  "solicitante": "jperez - ticket 4521",
  "placas": ["ABC123", "XYZ789"],
  "desde": "2025-03-01",
  "hasta": "2025-03-15",
  "tablaActual": "gps_20250302",
  "progreso": { "completadas": 1, "total": 3 },
  "filasInsertadas": 48213,
  "creado": "2026-09-23T22:09:58-05:00",
  "inicio": "2026-09-23T22:10:00-05:00",
  "detalle": [
    { "tabla": "gps_20250301", "estado": "OK", "filasInsertadas": 48213, "segundos": 240 },
    { "tabla": "gps_20250302", "estado": "DESCARGANDO" },
    { "tabla": "gps_20250303", "estado": "PENDIENTE" }
  ]
}
```

`desde` y `hasta` se devuelven como se pidieron (hora Lima): `"2025-03-01"` si vinieron sin hora, o
`"2025-09-27T08:00:00"` si trajeron hora. El listado usa el mismo formato.

Estados del **trabajo**: `EN_COLA`, `PROCESANDO`, `OK`, `PARCIAL` (algunas tablas fallaron), `ERROR` (fallaron todas).

Estados de cada **tabla**: `PENDIENTE`, `DESCARGANDO`, `IMPORTANDO`, `FILTRANDO`, `OK`,
`OMITIDA` (la data ya estaba disponible, ver abajo), `ERROR` (con el motivo en `mensaje`).

### Listar los últimos trabajos — `GET /api/restauracion`

```bash
curl -s "$API/api/restauracion?limite=20" -H "X-Api-Key: $KEY"
curl -s "$API/api/restauracion?limite=5&detalle=true" -H "X-Api-Key: $KEY"   # con detalle por tabla
```

### Salud — `GET /health`

```bash
curl -s "$API/health"
```

```json
{ "estado": "ok", "listo": true, "mysql": "ok", "colaPendientes": 0, "enProceso": "a1b2c3d4", "discoLibreGb": 212.4, "hora": "2026-09-24T10:00:00-05:00" }
```

Devuelve **503** si MySQL no responde o el servicio aún no terminó de inicializar.

## 5. Cómo funciona

1. Convierte las fechas a epoch (America/Lima) y busca los periodos en `gts.historicos`
   (`CAST(timeini/timefin AS UNSIGNED)`).
2. Encola el trabajo (persistido en `dbv16_01.restauraciones`). Un único `BackgroundService` con
   `Channel<T>` procesa **un trabajo a la vez** y, dentro de él, **una tabla a la vez**.
3. Por cada tabla decide si ya está disponible (**OMITIDA**):
   - La tabla no existe en `dbv16_01` → se restaura.
   - Existe y **no** la creó este servicio → es una tabla histórica original (últimos 6 meses), completa → se omite.
   - Existe y la creó este servicio (restauración anterior que el cron aún no borró) → se omite solo si
     cada placa pedida ya se restauró antes con éxito cubriendo el rango pedido. Si no, se vuelve a
     restaurar (con `INSERT IGNORE` es seguro repetir). Así no se pierden placas/fechas nuevas cuando
     una restauración previa trajo otras.
4. Restauración de una tabla (estado de la tabla entre paréntesis):
   1. Verifica ≥ `EspacioMinimoGb` (5 GB) libres en `/data/tmp`.
   2. (`DESCARGANDO`) Descarga `{tabla}.sql.gz` de R2 (reintentos con espera 5 s, 15 s, 45 s; no reintenta
      si el objeto no existe).
   3. `CREATE TABLE IF NOT EXISTS dbv16_01.{tabla} LIKE gts.eventdata`, y verifica que sus columnas 2 y 3
      sean `deviceID` y `timestamp`.
   4. (`IMPORTANDO`) Lee el `.sql.gz` en streaming (`GZipStream`, sin cargarlo en memoria) y recorre cada
      `INSERT INTO `{tabla}` VALUES (...),(...);`. Por cada tupla lee `deviceID` (campo 2) y `timestamp`
      (campo 3): si la placa está entre las pedidas y el timestamp en el rango, guarda el texto de la tupla
      **tal cual** en un archivo temporal; si no, la descarta. Ver "Cómo se leen los dumps" abajo.
   5. (`FILTRANDO`) Inserta las tuplas guardadas con `INSERT IGNORE INTO dbv16_01.{tabla} VALUES ...` en lotes
      de `TuplasPorLote` (500), todos en una sola transacción. La PK descarta las filas que ya existían y los
      duplicados que traen algunos dumps.
   6. Siempre (`finally`): borra el `.sql.gz` y el archivo de tuplas.
5. Si una tabla falla se registra el error y se sigue con la siguiente.
6. Al arrancar: crea las tablas de estado, borra temporales huérfanos y re-encola los trabajos
   `EN_COLA`/`PROCESANDO`.

### Cómo se leen los dumps

El parser (`LectorDumpSql`) recorre el dump byte a byte llevando el estado de comillas, así que las
direcciones con comas, paréntesis y apóstrofes (`'Av. Los Incas (Km 12), Ate'`, `'O\'Higgins'`,
`'O''Higgins'`, `'C:\\'`) no rompen la separación de tuplas ni de campos: `),(` y `,` solo cuentan
fuera de comillas, y `\'`, `\\` y `''` no cierran la comilla. El texto de cada tupla se reinserta sin
reformatear, en el charset que declara el dump (`SET NAMES`), y se respeta su `SET TIME_ZONE`.

Acepta los dumps con `CREATE TABLE` (los de nuestro script) y sin él (los cargados desde MySQL 5.6, que
empiezan en `LOCK TABLES` + `INSERT` y traen filas duplicadas). También acepta `INSERT IGNORE`/`REPLACE`.

**Verificación integrada.** Si algo no cuadra, la tabla se aborta con `ERROR` y un mensaje claro, **sin
insertar nada** (primero se valida el dump completo y recién después se inserta):

- cada tupla debe empezar con `(`, terminar con `)` y estar seguida de `,` o `;`;
- cada tupla debe tener exactamente tantos campos como columnas tiene `gts.eventdata` (si no, la estructura
  del dump no coincide);
- `deviceID` debe ser una cadena y `timestamp` un entero;
- el archivo debe terminar fuera de toda sentencia y comilla (si no, está truncado);
- no se aceptan `INSERT` con lista de columnas (`mysqldump --complete-insert`) ni charsets desconocidos.

El log registra por tabla: tuplas totales leídas, tuplas que pasaron el filtro, filas insertadas y el
tiempo de cada fase.

## 6. Logs

```bash
docker logs -f restauracion-gps                        # en vivo
docker logs --since 2h restauracion-gps                # últimas 2 horas
docker logs restauracion-gps 2>&1 | grep a1b2c3d4      # todo lo de un trabajo
docker logs restauracion-gps 2>&1 | grep -E "fail|warn" # errores y avisos
```

Cada paso queda registrado con su tiempo, por ejemplo:

```
2026-09-23 22:10:00 info: ...TrabajadorRestauracion[0] [a1b2c3d4] Inicio. Solicitante=jperez Placas=[ABC123,XYZ789] Rango=2025-03-01..2025-03-15 ...
2026-09-23 22:11:32 info: ...Restaurador[0] [a1b2c3d4] gps_20250301: descargado 312.4 MB en 92s
2026-09-23 22:11:52 info: ...Restaurador[0] [a1b2c3d4] gps_20250301: leídos 1.0 GB, 1402311 tuplas, 812 aceptadas
2026-09-23 22:12:09 info: ...Restaurador[0] [a1b2c3d4] gps_20250301: dump leído en 37s (2104 MB descomprimidos, 57 MB/s). Sentencias INSERT: 2210. Tuplas totales: 2951877. Pasaron el filtro: 1578. Charset: utf8mb4. Zona: +00:00
2026-09-23 22:12:10 info: ...Restaurador[0] [a1b2c3d4] gps_20250301: 1578 filas insertadas en 0.6s (0 de 1578 ya existían o eran duplicadas)
2026-09-23 22:12:10 info: ...TrabajadorRestauracion[0] [a1b2c3d4] gps_20250301: OK, 1578 filas en 130s
```

(Los números del ejemplo son ilustrativos.)

La rotación está configurada en `docker-compose.yml` (5 archivos de 20 MB). El historial de trabajos
también se puede consultar en MySQL:

```sql
SELECT * FROM dbv16_01.restauraciones ORDER BY creado DESC LIMIT 20;
SELECT * FROM dbv16_01.restauraciones_detalle WHERE job_id = 'a1b2c3d4' ORDER BY orden;
```

(Las fechas de esas tablas están en **UTC**; la API las devuelve en hora Lima.)

## 7. Configuración

Todo se lee de variables de entorno (archivo `.env`). `Seccion__Clave` equivale a `Seccion:Clave`.

| Variable | Defecto | Descripción |
|---|---|---|
| `ConnectionStrings__Gts` | — | Conexión a `gts` |
| `ConnectionStrings__Dbv16` | — | Conexión a `dbv16_01` |
| `R2__ServiceUrl`, `R2__AccessKeyId`, `R2__SecretAccessKey` | — | Credenciales de R2 |
| `R2__Bucket` / `R2__Region` | `data-semanal` / `auto` | |
| `R2__IntentosDescarga` | 4 | Intentos por archivo |
| `Restauracion__ApiKey` | — | Valor del header `X-Api-Key` (mín. 16 caracteres) |
| `Restauracion__RangoMaximoDias` | 62 | |
| `Restauracion__EspacioMinimoGb` | 5 | Espacio libre mínimo antes de cada descarga |
| `Restauracion__MinutosPorTabla` | 4 | Solo para `estimadoMinutos` |
| `Restauracion__TuplasPorLote` | 500 | Tuplas por `INSERT IGNORE` al insertar lo filtrado |
| `Restauracion__TimeoutInsertSegundos` | 1800 | Límite de cada lote `INSERT` |
| `Restauracion__MaximoPlacas` | 200 | |

## 8. Problemas frecuentes

- **`Unknown MySQL server host 'mysql-gts'`**: el contenedor no está en la red `app-net`.
- **`No existe el objeto 'gps_XXXX.sql.gz'`**: el periodo figura en `historicos` pero aún no se subió a R2
  (por ejemplo, el periodo actual). La tabla queda en `ERROR` y el trabajo sigue con las demás.
- **`... la estructura del dump no coincide con gts.eventdata`**: las tuplas del dump tienen otra cantidad
  de campos que las columnas de `gts.eventdata`. No se insertó nada; hay que revisar ese archivo a mano.
- **`Dump de gps_XXXX malformado ...` / `terminó dentro de una comilla` / `terminó en medio de una sentencia
  INSERT`**: el archivo está truncado o tiene un formato inesperado. El mensaje indica la tupla y el byte
  (del archivo descomprimido) donde se detectó. No se insertó nada.
- **`Espacio insuficiente`**: liberar disco en el host (el volumen `restauracion-tmp` vive en `/var/lib/docker/volumes`).

## 9. Verificación al desplegar

Conteos exactos medidos con el método anterior (importación completa) para `gps_20250605`. El método
actual debe devolver exactamente los mismos `filasInsertadas`, sobre una tabla que no exista todavía en
`dbv16_01` (si existe con esas filas, `INSERT IGNORE` insertará 0 y la tabla puede salir `OMITIDA`):

| Placa | Rango | Filas |
|---|---|---|
| `bam-754` | `2025-06-29` a `2025-06-30` | 2,172 |
| `c4b-849` | `2025-06-29` a `2025-06-30` | 1,975 |
| `aar-555` | `2025-06-29` a `2025-06-30` | 139 |
| `h1k-422` | `2025-06-29T08:00` a `2025-06-29T12:00` | 11 |

```bash
curl -s -X POST "$API/api/restauracion" -H "X-Api-Key: $KEY" -H "Content-Type: application/json" \
  -d '{"placas":["bam-754"],"desde":"2025-06-29","hasta":"2025-06-30","solicitante":"verificacion"}'
```

Comparar con `filasInsertadas` de la fila `gps_20250605` en el `detalle` del trabajo. En el log, la línea
`dump leído en ...` muestra también las tuplas totales y las que pasaron el filtro.
