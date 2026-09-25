using RestauracionGps.Datos;
using RestauracionGps.Dominio;

namespace RestauracionGps.Servicios;

/// <summary>
/// Decide si una tabla ya tiene disponible la data pedida en dbv16_01 y puede saltarse.
///
/// - Si la tabla no existe: hay que restaurarla.
/// - Si existe y NO la creó este servicio, es una tabla histórica original (últimos 6 meses)
///   y está completa: se salta.
/// - Si existe y la creó este servicio (una restauración previa que el cron aún no borra),
///   solo contiene las placas/rangos de esas restauraciones. Se salta solo si cada placa pedida
///   ya fue restaurada con éxito cubriendo el rango pedido; si no, se restaura de nuevo
///   (INSERT IGNORE hace que repetir sea seguro).
///
/// Esto evita el falso positivo de "ya hay filas de alguna placa" cuando una restauración previa
/// trajo otras placas u otro rango de fechas.
/// </summary>
public sealed class Disponibilidad(GpsRepositorio gps, TrabajosRepositorio trabajos)
{
    public async Task<(bool Disponible, string? Motivo)> EvaluarAsync(
        Trabajo trabajo, TablaTrabajo tabla, CancellationToken ct)
    {
        var (existe, creadaEpoch) = await gps.InfoTablaDestinoAsync(tabla.Tabla, ct);
        if (!existe) return (false, null);

        var previas = await trabajos.RestauracionesPreviasAsync(tabla.Tabla, ct);
        var desdeCreacion = previas
            .Where(p => creadaEpoch is null || (p.FinUtc ?? DateTime.MaxValue) >= FromEpoch(creadaEpoch.Value))
            .ToList();

        if (!desdeCreacion.Any(p => p.TablaCreada))
            return (true, "La tabla histórica ya existe en dbv16_01");

        var desde = tabla.FiltroDesde(trabajo);
        var hasta = tabla.FiltroHasta(trabajo);
        var cubiertas = trabajo.Placas.All(placa => desdeCreacion.Any(p =>
            p.Estado == EstadoTabla.Ok &&
            p.TsDesde <= desde && p.TsHasta >= hasta &&
            p.Placas.Contains(placa, StringComparer.OrdinalIgnoreCase)));

        return cubiertas
            ? (true, "Ya restaurada previamente para estas placas y fechas")
            : (false, null);
    }

    private static DateTime FromEpoch(long epoch) => DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime;
}
