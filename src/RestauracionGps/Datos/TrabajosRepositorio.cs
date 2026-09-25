using System.Text.Json;
using Dapper;
using MySqlConnector;
using RestauracionGps.Dominio;

namespace RestauracionGps.Datos;

/// <summary>
/// Persistencia del estado de los trabajos en dbv16_01.restauraciones y
/// dbv16_01.restauraciones_detalle, para que sobreviva a reinicios del contenedor.
/// Todas las fechas se guardan en UTC.
/// </summary>
public sealed class TrabajosRepositorio(Conexiones conexiones)
{
    private const string DdlTrabajos = """
        CREATE TABLE IF NOT EXISTS restauraciones (
          id            VARCHAR(16)  NOT NULL,
          placas        TEXT         NOT NULL,
          desde         DATE         NOT NULL,
          hasta         DATE         NOT NULL,
          ts_desde      BIGINT       NOT NULL,
          ts_hasta      BIGINT       NOT NULL,
          solicitante   VARCHAR(150) NULL,
          estado        VARCHAR(20)  NOT NULL,
          tabla_actual  VARCHAR(45)  NULL,
          error         TEXT         NULL,
          creado        DATETIME     NOT NULL COMMENT 'UTC',
          inicio        DATETIME     NULL     COMMENT 'UTC',
          fin           DATETIME     NULL     COMMENT 'UTC',
          PRIMARY KEY (id),
          KEY idx_creado (creado),
          KEY idx_estado (estado)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
        """;

    private const string DdlDetalle = """
        CREATE TABLE IF NOT EXISTS restauraciones_detalle (
          job_id            VARCHAR(16) NOT NULL,
          orden             INT         NOT NULL,
          tabla             VARCHAR(45) NOT NULL,
          periodo_ini       BIGINT      NOT NULL,
          periodo_fin       BIGINT      NOT NULL,
          estado            VARCHAR(20) NOT NULL,
          filas_insertadas  BIGINT      NULL,
          segundos          INT         NULL,
          tabla_creada      TINYINT(1)  NOT NULL DEFAULT 0 COMMENT '1 = este servicio creó la tabla en dbv16_01',
          mensaje           TEXT        NULL,
          inicio            DATETIME    NULL COMMENT 'UTC',
          fin               DATETIME    NULL COMMENT 'UTC',
          PRIMARY KEY (job_id, orden),
          KEY idx_tabla (tabla)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
        """;

    private const string ColumnasTrabajo = """
        id AS Id, placas AS PlacasJson, desde AS Desde, hasta AS Hasta,
        ts_desde AS TsDesde, ts_hasta AS TsHasta, solicitante AS Solicitante,
        estado AS Estado, tabla_actual AS TablaActual, error AS Error,
        creado AS Creado, inicio AS Inicio, fin AS Fin
        """;

    private const string ColumnasDetalle = """
        job_id AS JobId, orden AS Orden, tabla AS Tabla, periodo_ini AS PeriodoIni,
        periodo_fin AS PeriodoFin, estado AS Estado, filas_insertadas AS FilasInsertadas,
        segundos AS Segundos, tabla_creada AS TablaCreada, mensaje AS Mensaje,
        inicio AS Inicio, fin AS Fin
        """;

    public async Task AsegurarEsquemaAsync(CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirDbv16Async(ct);
        await cn.ExecuteAsync(new CommandDefinition(DdlTrabajos, cancellationToken: ct));
        await cn.ExecuteAsync(new CommandDefinition(DdlDetalle, cancellationToken: ct));
    }

    /// <summary>Inserta el trabajo y sus tablas. Asigna un id corto único.</summary>
    public async Task CrearAsync(Trabajo t, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirDbv16Async(ct);
        for (var intento = 1; ; intento++)
        {
            t.Id = Guid.NewGuid().ToString("N")[..8];
            await using var tx = await cn.BeginTransactionAsync(ct);
            try
            {
                await cn.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO restauraciones
                      (id, placas, desde, hasta, ts_desde, ts_hasta, solicitante, estado, creado)
                    VALUES (@Id, @Placas, @Desde, @Hasta, @TsDesde, @TsHasta, @Solicitante, @Estado, @Creado)
                    """,
                    new
                    {
                        t.Id,
                        Placas = JsonSerializer.Serialize(t.Placas),
                        Desde = t.Desde.ToDateTime(TimeOnly.MinValue),
                        Hasta = t.Hasta.ToDateTime(TimeOnly.MinValue),
                        t.TsDesde,
                        t.TsHasta,
                        t.Solicitante,
                        t.Estado,
                        t.Creado
                    }, tx, cancellationToken: ct));

                await cn.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO restauraciones_detalle (job_id, orden, tabla, periodo_ini, periodo_fin, estado)
                    VALUES (@JobId, @Orden, @Tabla, @PeriodoIni, @PeriodoFin, @Estado)
                    """,
                    t.Detalle.Select(d => new { JobId = t.Id, d.Orden, d.Tabla, d.PeriodoIni, d.PeriodoFin, d.Estado }),
                    tx, cancellationToken: ct));

                await tx.CommitAsync(ct);
                return;
            }
            catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.DuplicateKeyEntry && intento < 5)
            {
                await tx.RollbackAsync(ct);
            }
        }
    }

    public async Task<Trabajo?> ObtenerAsync(string id, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirDbv16Async(ct);
        var fila = await cn.QuerySingleOrDefaultAsync<FilaTrabajo>(new CommandDefinition(
            $"SELECT {ColumnasTrabajo} FROM restauraciones WHERE id = @id", new { id }, cancellationToken: ct));
        if (fila is null) return null;

        var detalle = await cn.QueryAsync<FilaDetalle>(new CommandDefinition(
            $"SELECT {ColumnasDetalle} FROM restauraciones_detalle WHERE job_id = @id ORDER BY orden",
            new { id }, cancellationToken: ct));
        return fila.ATrabajo(detalle);
    }

    public async Task<List<Trabajo>> ListarAsync(int limite, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirDbv16Async(ct);
        var filas = (await cn.QueryAsync<FilaTrabajo>(new CommandDefinition(
            $"SELECT {ColumnasTrabajo} FROM restauraciones ORDER BY creado DESC LIMIT @limite",
            new { limite }, cancellationToken: ct))).ToList();
        if (filas.Count == 0) return new();

        var detalle = (await cn.QueryAsync<FilaDetalle>(new CommandDefinition(
            $"SELECT {ColumnasDetalle} FROM restauraciones_detalle WHERE job_id IN @ids ORDER BY job_id, orden",
            new { ids = filas.Select(f => f.Id).ToArray() }, cancellationToken: ct)))
            .ToLookup(d => d.JobId);

        return filas.Select(f => f.ATrabajo(detalle[f.Id])).ToList();
    }

    /// <summary>Trabajos que quedaron sin terminar (para re-encolar al arrancar).</summary>
    public async Task<List<string>> IdsPendientesAsync(CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirDbv16Async(ct);
        var ids = await cn.QueryAsync<string>(new CommandDefinition(
            "SELECT id FROM restauraciones WHERE estado IN (@EnCola, @Procesando) ORDER BY creado",
            new { EstadoTrabajo.EnCola, EstadoTrabajo.Procesando }, cancellationToken: ct));
        return ids.ToList();
    }

    /// <summary>Las tablas que quedaron a medio camino (reinicio del contenedor) vuelven a PENDIENTE.</summary>
    public async Task ReiniciarTablasIntermediasAsync(string jobId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirDbv16Async(ct);
        await cn.ExecuteAsync(new CommandDefinition("""
            UPDATE restauraciones_detalle SET estado = @Pendiente
            WHERE job_id = @jobId AND estado IN (@Descargando, @Importando, @Filtrando)
            """,
            new { jobId, EstadoTabla.Pendiente, EstadoTabla.Descargando, EstadoTabla.Importando, EstadoTabla.Filtrando },
            cancellationToken: ct));
    }

    public async Task MarcarProcesandoAsync(string jobId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirDbv16Async(ct);
        await cn.ExecuteAsync(new CommandDefinition(
            "UPDATE restauraciones SET estado = @estado, inicio = COALESCE(inicio, @ahora) WHERE id = @jobId",
            new { jobId, estado = EstadoTrabajo.Procesando, ahora = DateTime.UtcNow }, cancellationToken: ct));
    }

    public async Task ActualizarTablaActualAsync(string jobId, string? tabla, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirDbv16Async(ct);
        await cn.ExecuteAsync(new CommandDefinition(
            "UPDATE restauraciones SET tabla_actual = @tabla WHERE id = @jobId",
            new { jobId, tabla }, cancellationToken: ct));
    }

    public async Task ActualizarEstadoTablaAsync(string jobId, int orden, string estado, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirDbv16Async(ct);
        await cn.ExecuteAsync(new CommandDefinition("""
            UPDATE restauraciones_detalle SET estado = @estado, inicio = COALESCE(inicio, @ahora)
            WHERE job_id = @jobId AND orden = @orden
            """,
            new { jobId, orden, estado, ahora = DateTime.UtcNow }, cancellationToken: ct));
    }

    public async Task MarcarTablaCreadaAsync(string jobId, int orden, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirDbv16Async(ct);
        await cn.ExecuteAsync(new CommandDefinition(
            "UPDATE restauraciones_detalle SET tabla_creada = 1 WHERE job_id = @jobId AND orden = @orden",
            new { jobId, orden }, cancellationToken: ct));
    }

    public async Task FinalizarTablaAsync(string jobId, int orden, string estado, long? filas, int? segundos,
        string? mensaje, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirDbv16Async(ct);
        await cn.ExecuteAsync(new CommandDefinition("""
            UPDATE restauraciones_detalle
               SET estado = @estado, filas_insertadas = @filas, segundos = @segundos, mensaje = @mensaje,
                   inicio = COALESCE(inicio, @ahora), fin = @ahora
             WHERE job_id = @jobId AND orden = @orden
            """,
            new { jobId, orden, estado, filas, segundos, mensaje = Recortar(mensaje), ahora = DateTime.UtcNow },
            cancellationToken: ct));
    }

    public async Task FinalizarTrabajoAsync(string jobId, string estado, string? error, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirDbv16Async(ct);
        await cn.ExecuteAsync(new CommandDefinition("""
            UPDATE restauraciones SET estado = @estado, error = @error, tabla_actual = NULL, fin = @ahora
            WHERE id = @jobId
            """,
            new { jobId, estado, error = Recortar(error), ahora = DateTime.UtcNow }, cancellationToken: ct));
    }

    /// <summary>
    /// Historial de restauraciones de una tabla (de cualquier trabajo), usado para saber
    /// si una tabla existente la creó este servicio y qué placas/rangos ya contiene.
    /// </summary>
    public async Task<List<RestauracionPrevia>> RestauracionesPreviasAsync(string tabla, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirDbv16Async(ct);
        var filas = await cn.QueryAsync<FilaPrevia>(new CommandDefinition("""
            SELECT r.placas AS PlacasJson, r.ts_desde AS TsDesde, r.ts_hasta AS TsHasta,
                   d.estado AS Estado, d.tabla_creada AS TablaCreada, d.inicio AS Inicio, d.fin AS Fin
              FROM restauraciones_detalle d
              JOIN restauraciones r ON r.id = d.job_id
             WHERE d.tabla = @tabla AND d.inicio IS NOT NULL
            """, new { tabla }, cancellationToken: ct));

        return filas.Select(f => new RestauracionPrevia(
            LeerPlacas(f.PlacasJson), f.TsDesde, f.TsHasta, f.Estado, f.TablaCreada,
            AUtc(f.Inicio), AUtc(f.Fin))).ToList();
    }

    private static List<string> LeerPlacas(string json) =>
        JsonSerializer.Deserialize<List<string>>(json) ?? new();

    private static DateTime? AUtc(DateTime? d) =>
        d is null ? null : DateTime.SpecifyKind(d.Value, DateTimeKind.Utc);

    private static string? Recortar(string? s) => s is { Length: > 4000 } ? s[..4000] : s;

    private sealed class FilaTrabajo
    {
        public string Id { get; set; } = "";
        public string PlacasJson { get; set; } = "[]";
        public DateTime Desde { get; set; }
        public DateTime Hasta { get; set; }
        public long TsDesde { get; set; }
        public long TsHasta { get; set; }
        public string? Solicitante { get; set; }
        public string Estado { get; set; } = "";
        public string? TablaActual { get; set; }
        public string? Error { get; set; }
        public DateTime Creado { get; set; }
        public DateTime? Inicio { get; set; }
        public DateTime? Fin { get; set; }

        public Trabajo ATrabajo(IEnumerable<FilaDetalle> detalle) => new()
        {
            Id = Id,
            Placas = LeerPlacas(PlacasJson),
            Desde = DateOnly.FromDateTime(Desde),
            Hasta = DateOnly.FromDateTime(Hasta),
            TsDesde = TsDesde,
            TsHasta = TsHasta,
            Solicitante = Solicitante,
            Estado = Estado,
            TablaActual = TablaActual,
            Error = Error,
            Creado = DateTime.SpecifyKind(Creado, DateTimeKind.Utc),
            Inicio = AUtc(Inicio),
            Fin = AUtc(Fin),
            Detalle = detalle.Select(d => new TablaTrabajo
            {
                Orden = d.Orden,
                Tabla = d.Tabla,
                PeriodoIni = d.PeriodoIni,
                PeriodoFin = d.PeriodoFin,
                Estado = d.Estado,
                FilasInsertadas = d.FilasInsertadas,
                Segundos = d.Segundos,
                TablaCreada = d.TablaCreada,
                Mensaje = d.Mensaje,
                Inicio = AUtc(d.Inicio),
                Fin = AUtc(d.Fin)
            }).ToList()
        };
    }

    private sealed class FilaDetalle
    {
        public string JobId { get; set; } = "";
        public int Orden { get; set; }
        public string Tabla { get; set; } = "";
        public long PeriodoIni { get; set; }
        public long PeriodoFin { get; set; }
        public string Estado { get; set; } = "";
        public long? FilasInsertadas { get; set; }
        public int? Segundos { get; set; }
        public bool TablaCreada { get; set; }
        public string? Mensaje { get; set; }
        public DateTime? Inicio { get; set; }
        public DateTime? Fin { get; set; }
    }

    private sealed class FilaPrevia
    {
        public string PlacasJson { get; set; } = "[]";
        public long TsDesde { get; set; }
        public long TsHasta { get; set; }
        public string Estado { get; set; } = "";
        public bool TablaCreada { get; set; }
        public DateTime? Inicio { get; set; }
        public DateTime? Fin { get; set; }
    }
}

public sealed record RestauracionPrevia(
    List<string> Placas, long TsDesde, long TsHasta, string Estado, bool TablaCreada,
    DateTime? InicioUtc, DateTime? FinUtc);
