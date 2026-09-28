using System.Text.RegularExpressions;
using Dapper;
using RestauracionGps.Dominio;

namespace RestauracionGps.Datos;

/// <summary>Consultas sobre gts.historicos, las tablas gps_* de dbv16_01 y la base scratch.</summary>
public sealed partial class GpsRepositorio(Conexiones conexiones)
{
    /// <summary>
    /// Los nombres de tabla se interpolan en SQL (no pueden ir como parámetro) y en la
    /// línea de comandos, así que solo se aceptan nombres con el formato esperado.
    /// </summary>
    [GeneratedRegex(@"^gps_\d{8}$")]
    private static partial Regex RegexTabla();

    public static bool NombreTablaValido(string tabla) => RegexTabla().IsMatch(tabla);

    /// <summary>Base destino (dbv16_01), tomada de ConnectionStrings:Dbv16.</summary>
    public string BaseDestino => conexiones.DatosDbv16().Database;

    private static string Validar(string tabla) =>
        NombreTablaValido(tabla) ? tabla : throw new InvalidOperationException($"Nombre de tabla inválido: '{tabla}'");

    private static string ValidarBase(string db) =>
        Regex.IsMatch(db, @"^[A-Za-z0-9_]+$") ? db : throw new InvalidOperationException($"Nombre de base inválido: '{db}'");

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

    public async Task<bool> ExisteTablaAsync(string baseDatos, string tabla, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirDbv16Async(ct);
        return await cn.ExecuteScalarAsync<long>(new CommandDefinition("""
            SELECT COUNT(*) FROM information_schema.tables
             WHERE table_schema = @baseDatos AND table_name = @tabla
            """, new { baseDatos, tabla }, cancellationToken: ct)) > 0;
    }

    public async Task<bool> ExisteBaseAsync(string baseDatos, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirDbv16Async(ct);
        return await cn.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM information_schema.schemata WHERE schema_name = @baseDatos",
            new { baseDatos }, cancellationToken: ct)) > 0;
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

    public async Task<List<string>> ColumnasAsync(string baseDatos, string tabla, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirDbv16Async(ct);
        var cols = await cn.QueryAsync<string>(new CommandDefinition("""
            SELECT COLUMN_NAME FROM information_schema.columns
             WHERE table_schema = @baseDatos AND table_name = @tabla
             ORDER BY ORDINAL_POSITION
            """, new { baseDatos, tabla }, cancellationToken: ct));
        return cols.ToList();
    }

    /// <summary>
    /// INSERT IGNORE INTO dbv16_01.gps_X (cols) SELECT cols FROM scratch.gps_X WHERE placa/rango.
    /// Se usa la lista explícita de columnas comunes por si algún dump antiguo difiere de eventdata.
    /// </summary>
    public async Task<long> InsertarFiltradoAsync(string baseScratch, string tabla, IReadOnlyList<string> columnas,
        IReadOnlyList<string> placas, long desde, long hasta, int timeoutSegundos, CancellationToken ct)
    {
        Validar(tabla);
        ValidarBase(baseScratch);
        var lista = string.Join(", ", columnas.Select(c => $"`{c.Replace("`", "``")}`"));
        var sql = $"""
            INSERT IGNORE INTO `{tabla}` ({lista})
            SELECT {lista} FROM `{baseScratch}`.`{tabla}`
             WHERE deviceID IN @placas
               AND `timestamp` BETWEEN @desde AND @hasta
            """;

        await using var cn = await conexiones.AbrirDbv16Async(ct);
        return await cn.ExecuteAsync(new CommandDefinition(sql, new { placas, desde, hasta },
            commandTimeout: timeoutSegundos, cancellationToken: ct));
    }

    public async Task EliminarTablaScratchAsync(string baseScratch, string tabla, CancellationToken ct)
    {
        Validar(tabla);
        ValidarBase(baseScratch);
        await using var cn = await conexiones.AbrirDbv16Async(ct);
        await cn.ExecuteAsync(new CommandDefinition(
            $"DROP TABLE IF EXISTS `{baseScratch}`.`{tabla}`", cancellationToken: ct));
    }

    /// <summary>
    /// Deja lista en la base scratch una tabla vacía con la estructura de gts.eventdata, pero sin
    /// llave primaria ni índices. Algunos dumps (los generados desde MySQL 5.6) no traen CREATE TABLE
    /// y contienen filas duplicadas; así se importan igual. Si el dump sí trae su CREATE TABLE,
    /// reemplaza esta tabla. Los duplicados los descarta después el INSERT IGNORE hacia el destino.
    /// </summary>
    public async Task PrepararTablaScratchAsync(string baseScratch, string tabla, CancellationToken ct)
    {
        Validar(tabla);
        ValidarBase(baseScratch);
        await using var cn = await conexiones.AbrirDbv16Async(ct);
        await cn.ExecuteAsync(new CommandDefinition(
            $"DROP TABLE IF EXISTS `{baseScratch}`.`{tabla}`", cancellationToken: ct));
        await cn.ExecuteAsync(new CommandDefinition(
            $"CREATE TABLE `{baseScratch}`.`{tabla}` LIKE gts.eventdata", cancellationToken: ct));
        await cn.ExecuteAsync(new CommandDefinition($"""
            ALTER TABLE `{baseScratch}`.`{tabla}`
              DROP PRIMARY KEY,
              DROP INDEX idx_timestamp_account_device
            """, cancellationToken: ct));
        // MyISAM acelera la carga (sin transacciones ni redo log). Se cambia con la tabla vacía,
        // donde es instantáneo; la tabla es de un solo uso y el destino en dbv16_01 sigue en InnoDB.
        await cn.ExecuteAsync(new CommandDefinition(
            $"ALTER TABLE `{baseScratch}`.`{tabla}` ENGINE = MyISAM", cancellationToken: ct));
    }

    /// <summary>Tablas gps_* que quedaron en la base scratch (por ejemplo tras un reinicio).</summary>
    public async Task<List<string>> TablasScratchAsync(string baseScratch, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirDbv16Async(ct);
        var tablas = await cn.QueryAsync<string>(new CommandDefinition("""
            SELECT TABLE_NAME FROM information_schema.tables
             WHERE table_schema = @baseScratch AND TABLE_NAME LIKE 'gps\_%'
            """, new { baseScratch }, cancellationToken: ct));
        return tablas.Where(NombreTablaValido).ToList();
    }

    public async Task PingAsync(CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirGtsAsync(ct);
        await cn.ExecuteScalarAsync<int>(new CommandDefinition("SELECT 1", cancellationToken: ct));
    }
}
