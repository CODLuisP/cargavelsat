using System.Diagnostics;
using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using RestauracionGps.Configuracion;

namespace RestauracionGps.Servicios;

/// <summary>El objeto no existe en el bucket: no tiene sentido reintentar.</summary>
public sealed class ArchivoNoEncontradoException(string mensaje) : Exception(mensaje);

/// <summary>Descarga los dumps gps_XXXX.sql.gz desde Cloudflare R2 (API S3) con reintentos.</summary>
public sealed class R2Descargador : IDisposable
{
    private readonly IAmazonS3 _s3;
    private readonly R2Options _opt;
    private readonly ILogger<R2Descargador> _log;

    public R2Descargador(IOptions<R2Options> opt, ILogger<R2Descargador> log)
    {
        _opt = opt.Value;
        _log = log;
        var config = new AmazonS3Config
        {
            ServiceURL = _opt.ServiceUrl,
            ForcePathStyle = true,
            AuthenticationRegion = _opt.Region,
            // R2 no soporta los checksums CRC que los SDK recientes calculan por defecto.
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
            Timeout = TimeSpan.FromMinutes(30),
            MaxErrorRetry = 2
        };
        _s3 = new AmazonS3Client(new BasicAWSCredentials(_opt.AccessKeyId, _opt.SecretAccessKey), config);
    }

    /// <summary>Descarga el objeto a <paramref name="destino"/>. Devuelve el tamaño en bytes.</summary>
    public async Task<long> DescargarAsync(string clave, string destino, CancellationToken ct)
    {
        var intentos = Math.Max(1, _opt.IntentosDescarga);
        for (var intento = 1; ; intento++)
        {
            try
            {
                return await DescargarUnaVezAsync(clave, destino, ct);
            }
            catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound || ex.ErrorCode == "NoSuchKey")
            {
                BorrarSilencioso(destino);
                throw new ArchivoNoEncontradoException($"No existe el objeto '{clave}' en el bucket '{_opt.Bucket}'");
            }
            catch (AmazonS3Exception ex) when (ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            {
                BorrarSilencioso(destino);
                throw new InvalidOperationException($"R2 rechazó las credenciales ({(int)ex.StatusCode} {ex.ErrorCode})", ex);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                BorrarSilencioso(destino);
                if (intento >= intentos) throw;

                var espera = TimeSpan.FromSeconds(5 * Math.Pow(3, intento - 1)); // 5s, 15s, 45s...
                _log.LogWarning(ex, "Descarga de {Clave} falló (intento {Intento}/{Total}). Reintentando en {Espera}s",
                    clave, intento, intentos, espera.TotalSeconds);
                await Task.Delay(espera, ct);
            }
        }
    }

    private async Task<long> DescargarUnaVezAsync(string clave, string destino, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        using var resp = await _s3.GetObjectAsync(new GetObjectRequest { BucketName = _opt.Bucket, Key = clave }, ct);
        var esperado = resp.ContentLength;
        _log.LogInformation("Descargando {Clave} ({Mb:F1} MB) -> {Destino}", clave, esperado / 1048576.0, destino);

        long escritos = 0, siguienteLog = 100L * 1024 * 1024;
        var buffer = new byte[1024 * 1024];
        await using (var origen = resp.ResponseStream)
        await using (var archivo = new FileStream(destino, FileMode.Create, FileAccess.Write, FileShare.None,
                         bufferSize: 1024 * 1024, useAsync: true))
        {
            int leidos;
            while ((leidos = await origen.ReadAsync(buffer, ct)) > 0)
            {
                await archivo.WriteAsync(buffer.AsMemory(0, leidos), ct);
                escritos += leidos;
                if (escritos >= siguienteLog)
                {
                    _log.LogInformation("  {Clave}: {Mb:F0}/{Total:F0} MB", clave, escritos / 1048576.0, esperado / 1048576.0);
                    siguienteLog += 100L * 1024 * 1024;
                }
            }
        }

        if (esperado > 0 && escritos != esperado)
            throw new IOException($"Descarga incompleta de {clave}: {escritos} de {esperado} bytes");

        _log.LogInformation("Descarga de {Clave} completa: {Mb:F1} MB en {Seg:F0}s ({Vel:F1} MB/s)",
            clave, escritos / 1048576.0, sw.Elapsed.TotalSeconds, escritos / 1048576.0 / Math.Max(1, sw.Elapsed.TotalSeconds));
        return escritos;
    }

    private static void BorrarSilencioso(string ruta)
    {
        try { if (File.Exists(ruta)) File.Delete(ruta); } catch { /* se reintenta en la limpieza general */ }
    }

    public void Dispose() => _s3.Dispose();
}
