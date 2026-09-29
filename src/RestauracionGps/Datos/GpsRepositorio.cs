using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using MySqlConnector;
using RestauracionGps.Dominio;

namespace RestauracionGps.Datos;

/// <summary>Consultas sobre gts.historicos y las tablas gps_* de dbv16_01.</summary>
public sealed partial class GpsRepositorio(Conexiones conexiones)
{
    /// <summary>
    /// Los nombres de tabla se interpolan en SQL (no pueden ir como parámetro),
    /// así que solo se aceptan nombres con el formato esperado.
    /// </summary>
    [GeneratedRegex(@"^gps_\d{8}$")]
    private static partial Regex RegexTabla();

    public static bool NombreTablaValido(string tabla) => RegexTabla().IsMatch(tabla);

    /// <summary>Base destino (dbv16_01), tomada de ConnectionStrings:Dbv16.</summary>
    public string BaseDestino => conexiones.DatosDbv16().Database;

    private static string Validar(string tabla) =>
        NombreTablaValido(tabla) ? tabla : throw new InvalidOperationException($"Nombre de tabla inválido: '{tabla}'");

    /// <summary>Periodos de gts.historicos que se cruzan con el rango (epoch, hora Lima).</summary>
    public async Task<List<Periodo>> PeriodosAsync(long desde, long hasta, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirGtsAsync(ct);
        var filas = await cn.QueryAsync<Periodo>(new CommandDefinition("""
            SELECT tabla AS Tabla, CAST(timeini AS SIGNED) AS Ini, CAST(timefin AS SIGNED) AS Fin
              FROM gts.historicos
             WHERE CAST(timefin AS UNSIGNED) >= @desde
               AND CAST(timeini AS UNSIGNED) <= @hasta
             ORDER BY CAST(timeini AS UNSIGNED)
            """, new { desde, hasta }, cancellationToken: ct));
        return filas.ToList();
    }

    /// <summary>Epoch de creación de la tabla en dbv16_01, o null si no existe.</summary>
    public async Task<(bool Existe, long? CreadaEpoch)> InfoTablaDestinoAsync(string tabla, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirDbv16Async(ct);
        var fila = await cn.QuerySingleOrDefaultAsync<(string Nombre, long? Creada)>(new CommandDefinition("""
            SELECT TABLE_NAME, CAST(UNIX_TIMESTAMP(CREATE_TIME) AS SIGNED)
              FROM information_schema.tables
             WHERE table_schema = DATABASE() AND table_name = @tabla
            """, new { tabla }, cancellationToken: ct));
        return fila.Nombre is null ? (false, null) : (true, fila.Creada);
    }

    /// <summary>CREATE TABLE IF NOT EXISTS dbv16_01.gps_X LIKE gts.eventdata. Devuelve true si la creó.</summary>
    public async Task<bool> CrearTablaDestinoAsync(string tabla, CancellationToken ct)
    {
        Validar(tabla);
        var (existe, _) = await InfoTablaDestinoAsync(tabla, ct);
        if (existe) return false;

        await using var cn = await conexiones.AbrirDbv16Async(ct);
        await cn.ExecuteAsync(new CommandDefinition(
            $"CREATE TABLE IF NOT EXISTS `{tabla}` LIKE gts.eventdata", cancellationToken: ct));
        return true;
    }

    /// <summary>Columnas de dbv16_01.gps_X en orden.</summary>
    public async Task<List<string>> ColumnasDestinoAsync(string tabla, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirDbv16Async(ct);
        var cols = await cn.QueryAsync<string>(new CommandDefinition("""
            SELECT COLUMN_NAME FROM information_schema.columns
             WHERE table_schema = DATABASE() AND table_name = @tabla
             ORDER BY ORDINAL_POSITION
            """, new { tabla }, cancellationToken: ct));
        return cols.ToList();
    }

    /// <summary>
    /// INSERT IGNORE INTO dbv16_01.gps_X VALUES (...),(...) en lotes, reusando el texto de cada tupla
    /// tal como viene del dump. Todo va en una transacción: o entran todos los lotes o ninguno.
    /// La PK descarta los duplicados (INSERT IGNORE). Devuelve las filas efectivamente insertadas.
    /// </summary>
    public async Task<long> InsertarTuplasAsync(string tabla, IEnumerable<string> tuplas, int tuplasPorLote,
        string? zonaHoraria, int timeoutSegundos, CancellationToken ct)
    {
        Validar(tabla);
        const int maxCaracteresLote = 2_000_000; // lejos de max_allowed_packet aunque las tuplas sean grandes
        var prefijo = $"INSERT IGNORE INTO `{tabla}` VALUES ";

        await using var cn = await conexiones.AbrirDbv16Async(ct);
        if (zonaHoraria is not null)
        {
            // Igual que al importar el dump: sus valores TIMESTAMP están en esa zona (SET TIME_ZONE del dump).
            await cn.ExecuteAsync(new CommandDefinition("SET time_zone = @zonaHoraria", new { zonaHoraria },
                cancellationToken: ct));
        }

        await using var tx = await cn.BeginTransactionAsync(ct);
        var sql = new StringBuilder(maxCaracteresLote + 64 * 1024);
        var enLote = 0;
        long insertadas = 0;

        async Task EjecutarLoteAsync()
        {
            await using var cmd = new MySqlCommand(sql.ToString(), cn, tx) { CommandTimeout = timeoutSegundos };
            insertadas += await cmd.ExecuteNonQueryAsync(ct);
            sql.Clear();
            enLote = 0;
        }

        foreach (var tupla in tuplas)
        {
            sql.Append(enLote == 0 ? prefijo : ",").Append(tupla);
            if (++enLote >= tuplasPorLote || sql.Length >= maxCaracteresLote)
                await EjecutarLoteAsync();
        }
        if (enLote > 0) await EjecutarLoteAsync();

        await tx.CommitAsync(ct);
        return insertadas;
    }

    public async Task PingAsync(CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirGtsAsync(ct);
        await cn.ExecuteScalarAsync<int>(new CommandDefinition("SELECT 1", cancellationToken: ct));
    }
}
