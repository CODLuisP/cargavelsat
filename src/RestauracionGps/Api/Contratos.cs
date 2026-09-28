using RestauracionGps.Dominio;
using RestauracionGps.Infraestructura;

namespace RestauracionGps.Api;

public sealed record SolicitudRestauracion(
    List<string>? Placas,
    string? Desde,
    string? Hasta,
    string? Solicitante);

public sealed record RespuestaEncolado(
    string JobId,
    string Estado,
    List<string> TablasAProcesar,
    int EstimadoMinutos,
    int TrabajosEnColaAntes);

public sealed record Progreso(int Completadas, int Total);

public sealed record DetalleTablaDto(
    string Tabla,
    string Estado,
    long? FilasInsertadas,
    int? Segundos,
    string? Mensaje);

public sealed record TrabajoDto(
    string JobId,
    string Estado,
    string? Solicitante,
    List<string> Placas,
    string Desde,
    string Hasta,
    string? TablaActual,
    Progreso Progreso,
    long FilasInsertadas,
    string? Creado,
    string? Inicio,
    string? Fin,
    string? Error,
    List<DetalleTablaDto>? Detalle)
{
    public static TrabajoDto Crear(Trabajo t, bool incluirDetalle = true) => new(
        t.Id,
        t.Estado,
        t.Solicitante,
        t.Placas,
        t.Desde,
        t.Hasta,
        t.TablaActual,
        new Progreso(t.Detalle.Count(d => EstadoTabla.EsFinal(d.Estado)), t.Detalle.Count),
        t.Detalle.Sum(d => d.FilasInsertadas ?? 0),
        HoraLima.Formatear(t.Creado),
        HoraLima.Formatear(t.Inicio),
        HoraLima.Formatear(t.Fin),
        t.Error,
        incluirDetalle
            ? t.Detalle.Select(d => new DetalleTablaDto(d.Tabla, d.Estado, d.FilasInsertadas, d.Segundos, d.Mensaje)).ToList()
            : null);
}
