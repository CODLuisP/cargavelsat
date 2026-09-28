# Restauración de data histórica GPS

Microservicio .NET 8 (Minimal API) que restaura data histórica de GPS desde Cloudflare R2
(bucket `data-semanal`) hacia `dbv16_01`, filtrando por placa y rango de fechas.

Cuando un cliente pide data de hace más de 6 meses, la tabla `dbv16_01.gps_YYYYMMNN` ya no existe.
El servicio descarga el dump `gps_YYYYMMNN.sql.gz`, lo importa en una base scratch (`restore_tmp`),
copia **solo** las placas y fechas pedidas a `dbv16_01.gps_YYYYMMNN` y limpia. El cliente descarga
luego desde la web de rastreo, como siempre.

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
    Restaurador.cs            descarga -> importa -> crea destino -> INSERT IGNORE -> limpia
    R2Descargador.cs          descarga de R2 (S3) con reintentos
    ImportadorMysql.cs        gzip -dc archivo | mysql restore_tmp
    Disponibilidad.cs         decide si una tabla ya está disponible y se puede saltar
```

## 2. Preparación de MySQL (una sola vez)

Conectarse como root al contenedor `mysql-gts`:

```bash
docker exec -it mysql-gts mysql -uroot -p
```

```sql
CREATE DATABASE IF NOT EXISTS restore_tmp
  DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_general_ci;
GRANT ALL PRIVILEGES ON restore_tmp.* TO 'gtsuser'@'%';
FLUSH PRIVILEGES;
```

Además `gtsuser` necesita (normalmente ya los tiene):

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

Reglas:

- `desde` → 00:00:00 y `hasta` → 23:59:59 de ese día, en hora **America/Lima**.
- Fechas en formato `yyyy-MM-dd`, `desde <= hasta`, rango máximo `Restauracion__RangoMaximoDias` (62 por defecto).
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
4. Restauración de una tabla:
   1. Verifica ≥ `EspacioMinimoGb` (5 GB) libres en `/data/tmp`.
   2. Descarga `{tabla}.sql.gz` de R2 (reintentos con espera 5 s, 15 s, 45 s; no reintenta si el objeto no existe).
   3. Crea `restore_tmp.{tabla}` vacía (`LIKE gts.eventdata`) **sin llave primaria ni índices**.
      Los dumps cargados desde MySQL 5.6 (todo 2025 y enero–mayo 2026) no traen `CREATE TABLE` y tienen
      filas duplicadas; así importan igual. Si el dump trae su propio `CREATE TABLE`, reemplaza esta tabla.
   4. Revisa que el dump no tenga `USE`/`CREATE DATABASE` (escribiría fuera de `restore_tmp`) e importa:
      `gzip -dc archivo | mysql ... restore_tmp`. El SQL del dump no se modifica.
   5. `CREATE TABLE IF NOT EXISTS dbv16_01.{tabla} LIKE gts.eventdata`.
   6. `INSERT IGNORE INTO dbv16_01.{tabla} (...) SELECT ... FROM restore_tmp.{tabla} WHERE deviceID IN (...) AND timestamp BETWEEN desde AND hasta`. Aquí se descartan los duplicados del dump.
      (usa las columnas comunes entre el dump y `eventdata`, por si algún dump antiguo difiere; lo avisa en el log).
   7. Siempre (`finally`): `DROP TABLE restore_tmp.{tabla}` y borra el archivo temporal.
5. Si una tabla falla se registra el error y se sigue con la siguiente.
6. Al arrancar: crea las tablas de estado, borra temporales y tablas huérfanas de `restore_tmp`
   y re-encola los trabajos `EN_COLA`/`PROCESANDO`.

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
2026-09-23 22:13:40 info: ...Restaurador[0] [a1b2c3d4] gps_20250301: importado en restore_tmp en 128s
2026-09-23 22:14:00 info: ...Restaurador[0] [a1b2c3d4] gps_20250301: 48213 filas insertadas en 20s
2026-09-23 22:14:00 info: ...TrabajadorRestauracion[0] [a1b2c3d4] gps_20250301: OK, 48213 filas en 240s
```

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
| `ConnectionStrings__Dbv16` | — | Conexión a `dbv16_01` (también la usa el cliente `mysql` para importar) |
| `R2__ServiceUrl`, `R2__AccessKeyId`, `R2__SecretAccessKey` | — | Credenciales de R2 |
| `R2__Bucket` / `R2__Region` | `data-semanal` / `auto` | |
| `R2__IntentosDescarga` | 4 | Intentos por archivo |
| `Restauracion__ApiKey` | — | Valor del header `X-Api-Key` (mín. 16 caracteres) |
| `Restauracion__RangoMaximoDias` | 62 | |
| `Restauracion__EspacioMinimoGb` | 5 | Espacio libre mínimo antes de cada descarga |
| `Restauracion__BaseScratch` | `restore_tmp` | |
| `Restauracion__MinutosPorTabla` | 4 | Solo para `estimadoMinutos` |
| `Restauracion__TimeoutImportacionMinutos` | 90 | Límite del `gzip \| mysql` por tabla |
| `Restauracion__TimeoutInsertSegundos` | 1800 | Límite del `INSERT ... SELECT` |
| `Restauracion__MysqlArgsExtra` | vacío | Argumentos extra para el cliente `mysql` |
| `Restauracion__MaximoPlacas` | 200 | |

## 8. Problemas frecuentes

- **`La base scratch 'restore_tmp' no existe`**: falta el paso 2.
- **`Unknown MySQL server host 'mysql-gts'`**: el contenedor no está en la red `app-net`.
- **Error de autenticación del cliente `mysql` al importar** (p. ej. `caching_sha2_password`):
  `default-mysql-client` en Debian 12 es el cliente de MariaDB. Si `gtsuser` usa `caching_sha2_password`,
  probar con `Restauracion__MysqlArgsExtra=--ssl` en `.env`, o cambiar el usuario a
  `mysql_native_password`. Para probar a mano dentro del contenedor:
  `docker exec -it restauracion-gps bash -c 'MYSQL_PWD=... mysql -h mysql-gts -u gtsuser -e "select 1" restore_tmp'`.
- **`No existe el objeto 'gps_XXXX.sql.gz'`**: el periodo figura en `historicos` pero aún no se subió a R2
  (por ejemplo, el periodo actual). La tabla queda en `ERROR` y el trabajo sigue con las demás.
- **`La estructura del dump ... no coincide con gts.eventdata`**: el dump trae otra cantidad de columnas (MySQL ERROR 1136). Hay que revisar ese archivo a mano.
- **`Espacio insuficiente`**: liberar disco en el host (el volumen `restauracion-tmp` vive en `/var/lib/docker/volumes`).
  Ojo: la importación también ocupa espacio en el servidor MySQL mientras dura.
