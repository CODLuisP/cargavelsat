using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Options;
using MySqlConnector;
using RestauracionGps.Configuracion;
using RestauracionGps.Datos;

namespace RestauracionGps.Servicios;

/// <summary>
/// Importa un dump .sql.gz en la base scratch ejecutando
/// <c>gzip -dc archivo | mysql ... restore_tmp</c> como proceso externo (con
/// "SET SESSION sql_log_bin = 0" antepuesto si el usuario tiene permiso).
/// El SQL del dump no se modifica.
/// </summary>
public sealed class ImportadorMysql(
    Conexiones conexiones,
    IOptions<RestauracionOptions> opt,
    ILogger<ImportadorMysql> log)
{
    private readonly RestauracionOptions _opt = opt.Value;

    public async Task ImportarAsync(string archivoGz, string baseScratch, CancellationToken ct)
    {
        VerificarQueNoCambiaDeBase(archivoGz);
        var sinBinlog = await PuedeDesactivarBinlogAsync(ct);

        var cs = conexiones.DatosDbv16();
        var psi = new ProcessStartInfo("bash")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        // Los valores van como argumentos posicionales ($0..$5) para no interpolarlos en el script.
        // Con permiso, se antepone "SET SESSION sql_log_bin = 0" al dump: solo afecta a esta sesión.
        var extra = string.IsNullOrWhiteSpace(_opt.MysqlArgsExtra) ? "" : " " + _opt.MysqlArgsExtra.Trim();
        var entrada = sinBinlog
            ? "{ printf 'SET SESSION sql_log_bin = 0;\\n'; gzip -dc -- \"$0\"; }"
            : "gzip -dc -- \"$0\"";
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(
            $"set -o pipefail; {entrada} | \"$1\" --host=\"$2\" --port=\"$3\" --user=\"$4\" " +
            $"--default-character-set=utf8mb4 --max-allowed-packet=1073741824{extra} \"$5\"");
        psi.ArgumentList.Add(archivoGz);
        psi.ArgumentList.Add(_opt.MysqlCliente);
        psi.ArgumentList.Add(cs.Server);
        psi.ArgumentList.Add(cs.Port.ToString());
        psi.ArgumentList.Add(cs.UserID);
        psi.ArgumentList.Add(baseScratch);
        // La contraseña va por variable de entorno para que no aparezca en `ps`.
        psi.Environment["MYSQL_PWD"] = cs.Password;

        var sw = Stopwatch.StartNew();
        using var proceso = new Process { StartInfo = psi };
        var stderr = new ColaAcotada(50);
        proceso.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.Agregar(e.Data); };
        proceso.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) log.LogDebug("mysql: {Linea}", e.Data); };

        if (!proceso.Start())
            throw new InvalidOperationException("No se pudo iniciar bash para importar el dump");
        proceso.BeginErrorReadLine();
        proceso.BeginOutputReadLine();

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(_opt.TimeoutImportacionMinutos));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            await proceso.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            try { proceso.Kill(entireProcessTree: true); } catch { /* ya terminó */ }
            if (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
                throw new TimeoutException($"La importación superó {_opt.TimeoutImportacionMinutos} minutos");
            throw;
        }

        if (proceso.ExitCode != 0)
        {
            var salida = stderr.Texto();
            if (salida.Contains("ERROR 1136", StringComparison.Ordinal))
            {
                log.LogDebug("Salida de mysql: {Salida}", salida);
                throw new InvalidOperationException(
                    $"La estructura del dump {Path.GetFileName(archivoGz)} no coincide con gts.eventdata " +
                    "(la cantidad de columnas de los INSERT es distinta)");
            }
            throw new InvalidOperationException(
                $"La importación terminó con código {proceso.ExitCode}: {salida}");
        }

        var avisos = stderr.Texto();
        if (avisos.Length > 0) log.LogWarning("Salida de error de mysql (código 0): {Salida}", avisos);
        log.LogInformation("Importación de {Archivo} en {Base} terminada en {Seg:F0}s (binlog {Binlog})",
            Path.GetFileName(archivoGz), baseScratch, sw.Elapsed.TotalSeconds, sinBinlog ? "desactivado" : "activo");
    }

    /// <summary>
    /// Prueba "SET SESSION sql_log_bin = 0" en una conexión aparte. Si el cliente mysql la
    /// ejecutara sin permiso, abortaría todo el dump; por eso solo se antepone cuando se sabe
    /// que funciona. Requiere SESSION_VARIABLES_ADMIN (o SUPER); sin él se importa con binlog.
    /// </summary>
    private async Task<bool> PuedeDesactivarBinlogAsync(CancellationToken ct)
    {
        try
        {
            await using var cn = await conexiones.AbrirDbv16Async(ct);
            await using var cmd = cn.CreateCommand();
            cmd.CommandText = "SET SESSION sql_log_bin = 0; SET SESSION sql_log_bin = 1;";
            await cmd.ExecuteNonQueryAsync(ct);
            return true;
        }
        catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.SpecifiedAccessDeniedError)
        {
            log.LogWarning("Sin permiso para SET SESSION sql_log_bin = 0 ({Mensaje}); se importa con binlog. " +
                           "Ver README: GRANT SESSION_VARIABLES_ADMIN", ex.Message);
            return false;
        }
        catch (MySqlException ex)
        {
            log.LogWarning(ex, "No se pudo probar SET SESSION sql_log_bin = 0; se importa con binlog");
            return false;
        }
    }

    /// <summary>
    /// Un dump hecho con --databases trae "USE dbv16_01;" y escribiría fuera de la base scratch.
    /// Se revisa el encabezado (primeros ~256 KB descomprimidos) y se rechaza si cambia de base.
    /// </summary>
    private static void VerificarQueNoCambiaDeBase(string archivoGz)
    {
        using var fs = File.OpenRead(archivoGz);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var lector = new StreamReader(gz, Encoding.UTF8);
        long leidos = 0;
        string? linea;
        while (leidos < 256 * 1024 && (linea = lector.ReadLine()) is not null)
        {
            leidos += linea.Length + 1;
            var l = linea.TrimStart();
            if (l.StartsWith("USE ", StringComparison.OrdinalIgnoreCase) ||
                l.StartsWith("CREATE DATABASE", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"El dump contiene '{Recortar(l)}': cambiaría de base de datos. No se importa.");
            if (l.StartsWith("INSERT INTO", StringComparison.OrdinalIgnoreCase)) break; // ya pasó el encabezado
        }
    }

    private static string Recortar(string s) => s.Length > 80 ? s[..80] + "..." : s;

    /// <summary>Guarda solo las últimas N líneas de stderr.</summary>
    private sealed class ColaAcotada(int maximo)
    {
        private readonly Queue<string> _lineas = new();
        public void Agregar(string linea)
        {
            lock (_lineas)
            {
                _lineas.Enqueue(linea);
                while (_lineas.Count > maximo) _lineas.Dequeue();
            }
        }
        public string Texto()
        {
            lock (_lineas) return string.Join(" | ", _lineas).Trim();
        }
    }
}
