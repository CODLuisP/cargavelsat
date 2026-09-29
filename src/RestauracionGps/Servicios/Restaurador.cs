using System.Diagnostics;
using Microsoft.Extensions.Options;
using RestauracionGps.Configuracion;
using RestauracionGps.Datos;
using RestauracionGps.Dominio;

namespace RestauracionGps.Servicios;

/// <summary>
/// Restaura UNA tabla: descarga el dump, lo lee en streaming quedándose solo con las filas de las
/// placas y el rango pedidos, y las inserta directo en dbv16_01. No importa el dump completo.
/// </summary>
public sealed class Restaurador(
    R2Descargador r2,
    GpsRepositorio gps,
    TrabajosRepositorio trabajos,
    IOptions<RestauracionOptions> opt,
    ILogger<Restaurador> log)
{
    private readonly RestauracionOptions _opt = opt.Value;

    /// <summary>Devuelve la cantidad de filas insertadas en dbv16_01.</summary>
    public async Task<long> RestaurarAsync(Trabajo trabajo, TablaTrabajo t, CancellationToken ct)
    {
        var tabla = t.Tabla;
        Directory.CreateDirectory(_opt.DirectorioTemporal);
        var archivo = Path.Combine(_opt.DirectorioTemporal, $"{trabajo.Id}_{tabla}.sql.gz");
        var archivoTuplas = Path.Combine(_opt.DirectorioTemporal, $"{trabajo.Id}_{tabla}.tuplas");

        try
        {
            // 1. Descargar
            VerificarEspacio();
            await trabajos.ActualizarEstadoTablaAsync(trabajo.Id, t.Orden, EstadoTabla.Descargando, ct);
            var sw = Stopwatch.StartNew();
            var bytes = await r2.DescargarAsync($"{tabla}.sql.gz", archivo, ct);
            log.LogInformation("[{Job}] {Tabla}: descargado {Mb:F1} MB en {Seg:F0}s",
                trabajo.Id, tabla, bytes / 1048576.0, sw.Elapsed.TotalSeconds);

            // 2. Crear destino si no existe
            if (await gps.CrearTablaDestinoAsync(tabla, ct))
            {
                await trabajos.MarcarTablaCreadaAsync(trabajo.Id, t.Orden, ct);
                log.LogInformation("[{Job}] {Tabla}: creada en {Base} (LIKE gts.eventdata)", trabajo.Id, tabla, gps.BaseDestino);
            }
            var columnas = await gps.ColumnasDestinoAsync(tabla, ct);
            if (columnas.Count < 3 ||
                !columnas[1].Equals("deviceID", StringComparison.OrdinalIgnoreCase) ||
                !columnas[2].Equals("timestamp", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"{gps.BaseDestino}.{tabla} no tiene deviceID y timestamp como columnas 2 y 3: [{string.Join(",", columnas.Take(4))}...]");

            // 3-5. Leer el dump en streaming y quedarse solo con lo pedido.
            // Las tuplas aceptadas van a un archivo: no se inserta nada hasta validar el dump completo.
            await trabajos.ActualizarEstadoTablaAsync(trabajo.Id, t.Orden, EstadoTabla.Importando, ct);
            sw.Restart();
            ResultadoLectura lectura;
            using (var salida = new ArchivoTuplas(archivoTuplas))
            await using (var gz = new FileStream(archivo, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true))
            {
                var lector = new LectorDumpSql(tabla, trabajo.Placas, trabajo.TsDesde, trabajo.TsHasta,
                    columnas.Count, salida.Agregar);
                lectura = await lector.LeerGzAsync(gz, l => log.LogInformation(
                    "[{Job}] {Tabla}: leídos {Gb:F1} GB, {Tuplas} tuplas, {Filtradas} aceptadas",
                    trabajo.Id, tabla, l.BytesProcesados / 1073741824.0, l.TuplasTotales, l.TuplasFiltradas), ct);
            }
            var segLectura = sw.Elapsed.TotalSeconds;
            log.LogInformation(
                "[{Job}] {Tabla}: dump leído en {Seg:F0}s ({Mb:F0} MB descomprimidos, {Vel:F0} MB/s). " +
                "Sentencias INSERT: {Sentencias}. Tuplas totales: {Tuplas}. Pasaron el filtro: {Filtradas}. " +
                "Charset: {Charset}. Zona: {Zona}",
                trabajo.Id, tabla, segLectura, lectura.BytesDescomprimidos / 1048576.0,
                lectura.BytesDescomprimidos / 1048576.0 / Math.Max(0.001, segLectura),
                lectura.SentenciasInsert, lectura.TuplasTotales, lectura.TuplasFiltradas,
                lectura.Charset, lectura.ZonaHoraria ?? "-");
            if (lectura.TuplasOtrasTablas > 0)
                log.LogWarning("[{Job}] {Tabla}: el dump trae además {N} tuplas de otras tablas (ignoradas)",
                    trabajo.Id, tabla, lectura.TuplasOtrasTablas);
            if (lectura.SentenciasInsert == 0)
                log.LogWarning("[{Job}] {Tabla}: el dump no contiene ninguna sentencia INSERT", trabajo.Id, tabla);

            // El .sql.gz ya no hace falta: liberar disco cuanto antes.
            BorrarArchivo(archivo);

            // 6. Insertar las tuplas aceptadas en dbv16_01
            await trabajos.ActualizarEstadoTablaAsync(trabajo.Id, t.Orden, EstadoTabla.Filtrando, ct);
            sw.Restart();
            var filas = lectura.TuplasFiltradas == 0
                ? 0
                : await gps.InsertarTuplasAsync(tabla,
                    ArchivoTuplas.Leer(archivoTuplas, lectura.Codificacion, lectura.Charset),
                    _opt.TuplasPorLote, lectura.ZonaHoraria, _opt.TimeoutInsertSegundos, ct);
            log.LogInformation(
                "[{Job}] {Tabla}: {Filas} filas insertadas en {Seg:F1}s ({Omitidas} de {Filtradas} ya existían o eran duplicadas)",
                trabajo.Id, tabla, filas, sw.Elapsed.TotalSeconds, lectura.TuplasFiltradas - filas, lectura.TuplasFiltradas);
            return filas;
        }
        finally
        {
            // 7. Limpiar SIEMPRE, aunque algo falle o se cancele.
            BorrarArchivo(archivo);
            BorrarArchivo(archivoTuplas);
        }
    }

    private void VerificarEspacio()
    {
        var libre = EspacioLibreBytes(_opt.DirectorioTemporal);
        var minimo = (long)(_opt.EspacioMinimoGb * 1024 * 1024 * 1024);
        if (libre < minimo)
            throw new IOException(
                $"Espacio insuficiente en {_opt.DirectorioTemporal}: {libre / 1073741824.0:F1} GB libres, se requieren {_opt.EspacioMinimoGb} GB");
    }

    public static long EspacioLibreBytes(string ruta)
    {
        Directory.CreateDirectory(ruta);
        return new DriveInfo(Path.GetFullPath(ruta)).AvailableFreeSpace;
    }

    private void BorrarArchivo(string ruta)
    {
        try
        {
            if (File.Exists(ruta)) File.Delete(ruta);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "No se pudo borrar el temporal {Ruta}", ruta);
        }
    }
}
