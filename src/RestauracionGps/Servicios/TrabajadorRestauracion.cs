using System.Diagnostics;
using Microsoft.Extensions.Options;
using RestauracionGps.Configuracion;
using RestauracionGps.Datos;
using RestauracionGps.Dominio;

namespace RestauracionGps.Servicios;

/// <summary>
/// Procesa los trabajos de la cola de a uno, y dentro de cada trabajo las tablas de a una
/// (nunca en paralelo) para no saturar disco ni MySQL.
/// </summary>
public sealed class TrabajadorRestauracion(
    ColaTrabajos cola,
    TrabajosRepositorio trabajos,
    Disponibilidad disponibilidad,
    Restaurador restaurador,
    EstadoServicio estadoServicio,
    IOptions<RestauracionOptions> opt,
    ILogger<TrabajadorRestauracion> log) : BackgroundService
{
    private readonly RestauracionOptions _opt = opt.Value;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Yield();
        await InicializarAsync(ct);

        await foreach (var id in cola.LeerAsync(ct))
        {
            try
            {
                await ProcesarAsync(id, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                log.LogWarning("[{Job}] Interrumpido por apagado del servicio; se retomará al reiniciar", id);
                throw;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "[{Job}] Error inesperado procesando el trabajo", id);
                try { await trabajos.FinalizarTrabajoAsync(id, EstadoTrabajo.Error, ex.Message, CancellationToken.None); }
                catch (Exception ex2) { log.LogError(ex2, "[{Job}] No se pudo marcar el trabajo como ERROR", id); }
            }
        }
    }

    /// <summary>Crea las tablas de estado, limpia restos de ejecuciones anteriores y re-encola lo pendiente.</summary>
    private async Task InicializarAsync(CancellationToken ct)
    {
        for (var intento = 1; ; intento++)
        {
            try
            {
                await trabajos.AsegurarEsquemaAsync(ct);
                LimpiarDirectorioTemporal();

                var pendientes = await trabajos.IdsPendientesAsync(ct);
                foreach (var id in pendientes) cola.Encolar(id);
                if (pendientes.Count > 0)
                    log.LogInformation("Re-encolados {N} trabajos pendientes: {Ids}", pendientes.Count, string.Join(", ", pendientes));

                estadoServicio.Listo = true;
                log.LogInformation("Servicio de restauración listo. Temporales en {Dir}", _opt.DirectorioTemporal);
                return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                var espera = TimeSpan.FromSeconds(Math.Min(60, 5 * intento));
                log.LogError(ex, "Fallo al inicializar (intento {Intento}); reintento en {Seg}s", intento, espera.TotalSeconds);
                await Task.Delay(espera, ct);
            }
        }
    }

    private void LimpiarDirectorioTemporal()
    {
        Directory.CreateDirectory(_opt.DirectorioTemporal);
        var huerfanos = Directory.EnumerateFiles(_opt.DirectorioTemporal, "*.sql.gz")
            .Concat(Directory.EnumerateFiles(_opt.DirectorioTemporal, "*.tuplas"))
            .ToList();
        foreach (var f in huerfanos)
        {
            try
            {
                File.Delete(f);
                log.LogInformation("Temporal huérfano eliminado: {Archivo}", f);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "No se pudo borrar {Archivo}", f);
            }
        }
    }

    private async Task ProcesarAsync(string id, CancellationToken ct)
    {
        var trabajo = await trabajos.ObtenerAsync(id, ct);
        if (trabajo is null || EstadoTrabajo.EsFinal(trabajo.Estado)) return;

        // Si el contenedor se reinició a mitad de una tabla, esa tabla vuelve a empezar.
        await trabajos.ReiniciarTablasIntermediasAsync(id, ct);
        trabajo = await trabajos.ObtenerAsync(id, ct) ?? throw new InvalidOperationException("Trabajo desaparecido");
        await trabajos.MarcarProcesandoAsync(id, ct);

        var swTrabajo = Stopwatch.StartNew();
        log.LogInformation("[{Job}] Inicio. Solicitante={Solicitante} Placas=[{Placas}] Rango={Desde}..{Hasta} ({TsDesde}..{TsHasta}) Tablas=[{Tablas}]",
            id, trabajo.Solicitante ?? "-", string.Join(",", trabajo.Placas), trabajo.Desde, trabajo.Hasta,
            trabajo.TsDesde, trabajo.TsHasta, string.Join(",", trabajo.Detalle.Select(d => d.Tabla)));

        foreach (var t in trabajo.Detalle.Where(d => !EstadoTabla.EsFinal(d.Estado)))
        {
            await trabajos.ActualizarTablaActualAsync(id, t.Tabla, ct);
            var sw = Stopwatch.StartNew();
            try
            {
                var (disponible, motivo) = await disponibilidad.EvaluarAsync(trabajo, t, ct);
                if (disponible)
                {
                    t.Estado = EstadoTabla.Omitida;
                    await trabajos.FinalizarTablaAsync(id, t.Orden, t.Estado, null, 0, motivo, ct);
                    log.LogInformation("[{Job}] {Tabla}: omitida ({Motivo})", id, t.Tabla, motivo);
                    continue;
                }

                log.LogInformation("[{Job}] {Tabla}: restaurando ({N}/{Total})",
                    id, t.Tabla, t.Orden, trabajo.Detalle.Count);
                var filas = await restaurador.RestaurarAsync(trabajo, t, ct);
                t.Estado = EstadoTabla.Ok;
                await trabajos.FinalizarTablaAsync(id, t.Orden, t.Estado, filas, (int)sw.Elapsed.TotalSeconds, null, ct);
                log.LogInformation("[{Job}] {Tabla}: OK, {Filas} filas en {Seg:F0}s", id, t.Tabla, filas, sw.Elapsed.TotalSeconds);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Una tabla fallida no aborta el trabajo: se registra y se sigue con la siguiente.
                t.Estado = EstadoTabla.Error;
                log.LogError(ex, "[{Job}] {Tabla}: ERROR tras {Seg:F0}s", id, t.Tabla, sw.Elapsed.TotalSeconds);
                await trabajos.FinalizarTablaAsync(id, t.Orden, t.Estado, null, (int)sw.Elapsed.TotalSeconds,
                    ex.Message, CancellationToken.None);
            }
        }

        var errores = trabajo.Detalle.Count(d => d.Estado == EstadoTabla.Error);
        var estadoFinal = errores == 0 ? EstadoTrabajo.Ok
            : errores == trabajo.Detalle.Count ? EstadoTrabajo.Error
            : EstadoTrabajo.Parcial;
        var mensaje = errores == 0 ? null : $"{errores} de {trabajo.Detalle.Count} tablas fallaron";

        await trabajos.FinalizarTrabajoAsync(id, estadoFinal, mensaje, ct);
        log.LogInformation("[{Job}] Fin: {Estado} en {Min:F1} min. {Resumen}", id, estadoFinal, swTrabajo.Elapsed.TotalMinutes,
            string.Join(", ", trabajo.Detalle.Select(d => $"{d.Tabla}={d.Estado}")));
    }
}

/// <summary>Estado compartido para /health.</summary>
public sealed class EstadoServicio
{
    public volatile bool Listo;
}
