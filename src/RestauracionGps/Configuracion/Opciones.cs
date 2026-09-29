namespace RestauracionGps.Configuracion;

public sealed class R2Options
{
    public const string Seccion = "R2";

    public string ServiceUrl { get; set; } = "";
    public string AccessKeyId { get; set; } = "";
    public string SecretAccessKey { get; set; } = "";
    public string Bucket { get; set; } = "data-semanal";
    public string Region { get; set; } = "auto";

    /// <summary>Intentos totales de descarga por archivo (incluye el primero).</summary>
    public int IntentosDescarga { get; set; } = 4;
}

public sealed class RestauracionOptions
{
    public const string Seccion = "Restauracion";

    /// <summary>Valor esperado en el header X-Api-Key.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>Carpeta donde se descargan los .sql.gz (montar como volumen).</summary>
    public string DirectorioTemporal { get; set; } = "/data/tmp";

    /// <summary>Espacio libre mínimo en disco antes de descargar cada archivo.</summary>
    public double EspacioMinimoGb { get; set; } = 5;

    /// <summary>Rango máximo permitido entre "desde" y "hasta" (inclusive).</summary>
    public int RangoMaximoDias { get; set; } = 62;

    /// <summary>Máximo de placas por solicitud.</summary>
    public int MaximoPlacas { get; set; } = 200;

    /// <summary>Minutos estimados por tabla (solo para el campo estimadoMinutos).</summary>
    public int MinutosPorTabla { get; set; } = 4;

    /// <summary>Tiempo máximo de cada lote INSERT hacia dbv16_01.</summary>
    public int TimeoutInsertSegundos { get; set; } = 1800;

    /// <summary>Tuplas por sentencia INSERT IGNORE al insertar lo filtrado.</summary>
    public int TuplasPorLote { get; set; } = 500;

    /// <summary>Cantidad por defecto de trabajos en GET /api/restauracion.</summary>
    public int ListadoPorDefecto { get; set; } = 20;
}
