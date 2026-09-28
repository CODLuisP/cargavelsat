using System.Diagnostics;
using Microsoft.Extensions.Options;
using RestauracionGps.Configuracion;
using RestauracionGps.Datos;
using RestauracionGps.Dominio;

namespace RestauracionGps.Servicios;

/// <summary>Restaura UNA tabla: descarga, importa en scratch, filtra a dbv16_01 y limpia.</summary>
public sealed class Restaurador(
    R2Descargador r2,
    ImportadorMysql importador,
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
        var scratch = _opt.BaseScratch;
        Directory.CreateDirectory(_opt.DirectorioTemporal);
        var archivo = Path.Combine(_opt.DirectorioTemporal, $"{trabajo.Id}_{tabla}.sql.gz");

        try
        {
            // a. Descargar
            VerificarEspacio();
            await trabajos.ActualizarEstadoTablaAsync(trabajo.Id, t.Orden, EstadoTabla.Descargando, ct);
            var sw = Stopwatch.StartNew();
            var bytes = await r2.DescargarAsync($"{tabla}.sql.gz", archivo, ct);
            log.LogInformation("[{Job}] {Tabla}: descargado {Mb:F1} MB en {Seg:F0}s",
                trabajo.Id, tabla, bytes / 1048576.0, sw.Elapsed.TotalSeconds);

            // b. Importar en la base scratch (sin tocar el SQL del dump)
            await trabajos.ActualizarEstadoTablaAsync(trabajo.Id, t.Orden, EstadoTabla.Importando, ct);
            await gps.PrepararTablaScratchAsync(scratch, tabla, ct);
            sw.Restart();
            await importador.ImportarAsync(archivo, scratch, ct);
            log.LogInformation("[{Job}] {Tabla}: importado en {Base} en {Seg:F0}s",
                trabajo.Id, tabla, scratch, sw.Elapsed.TotalSeconds);

            // El archivo ya no hace falta: liberar disco cuanto antes.
            BorrarArchivo(archivo);

            if (!await gps.ExisteTablaAsync(scratch, tabla, ct))
                throw new InvalidOperationException(
                    $"El dump no creó la tabla {scratch}.{tabla} (¿el dump usa otro nombre de tabla?)");

            // c. Crear destino si no existe
            await trabajos.ActualizarEstadoTablaAsync(trabajo.Id, t.Orden, EstadoTabla.Filtrando, ct);
            if (await gps.CrearTablaDestinoAsync(tabla, ct))
            {
                await trabajos.MarcarTablaCreadaAsync(trabajo.Id, t.Orden, ct);
                log.LogInformation("[{Job}] {Tabla}: creada en {Base} (LIKE gts.eventdata)", trabajo.Id, tabla, gps.BaseDestino);
            }

            // d. Insertar solo lo pedido
            var columnas = await ColumnasComunesAsync(trabajo.Id, scratch, tabla, ct);
            sw.Restart();
            var filas = await gps.InsertarFiltradoAsync(scratch, tabla, columnas, trabajo.Placas,
                trabajo.TsDesde, trabajo.TsHasta, _opt.TimeoutInsertSegundos, ct);
            log.LogInformation("[{Job}] {Tabla}: {Filas} filas insertadas en {Seg:F0}s",
                trabajo.Id, tabla, filas, sw.Elapsed.TotalSeconds);
            return filas;
        }
        finally
        {
            // e. Limpiar SIEMPRE, aunque algo falle o se cancele.
            BorrarArchivo(archivo);
            try
            {
                await gps.EliminarTablaScratchAsync(scratch, tabla, CancellationToken.None);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "[{Job}] No se pudo eliminar {Base}.{Tabla}", trabajo.Id, scratch, tabla);
            }
        }
    }

    private async Task<List<string>> ColumnasComunesAsync(string jobId, string scratch, string tabla, CancellationToken ct)
    {
        var origen = await gps.ColumnasAsync(scratch, tabla, ct);
        var destino = await gps.ColumnasAsync(gps.BaseDestino, tabla, ct);
        var enOrigen = new HashSet<string>(origen, StringComparer.OrdinalIgnoreCase);
        var comunes = destino.Where(enOrigen.Contains).ToList();

        if (comunes.Count != origen.Count || comunes.Count != destino.Count)
            log.LogWarning("[{Job}] {Tabla}: estructura distinta a eventdata. Solo en dump: [{SoloOrigen}]. Solo en destino: [{SoloDestino}]",
                jobId, tabla,
                string.Join(",", origen.Except(destino, StringComparer.OrdinalIgnoreCase)),
                string.Join(",", destino.Except(origen, StringComparer.OrdinalIgnoreCase)));

        if (!comunes.Contains("deviceID", StringComparer.OrdinalIgnoreCase) ||
            !comunes.Contains("timestamp", StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"La tabla {scratch}.{tabla} no tiene columnas deviceID/timestamp");
        return comunes;
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
