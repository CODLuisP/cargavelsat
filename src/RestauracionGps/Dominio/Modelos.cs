namespace RestauracionGps.Dominio;

/// <summary>Estados de un trabajo completo.</summary>
public static class EstadoTrabajo
{
    public const string EnCola = "EN_COLA";
    public const string Procesando = "PROCESANDO";
    public const string Ok = "OK";
    /// <summary>Terminó, pero al menos una tabla falló y al menos una salió bien.</summary>
    public const string Parcial = "PARCIAL";
    public const string Error = "ERROR";

    public static bool EsFinal(string estado) => estado is Ok or Parcial or Error;
}

/// <summary>Estados de cada tabla dentro de un trabajo.</summary>
public static class EstadoTabla
{
    public const string Pendiente = "PENDIENTE";
    public const string Descargando = "DESCARGANDO";
    public const string Importando = "IMPORTANDO";
    public const string Filtrando = "FILTRANDO";
    public const string Ok = "OK";
    /// <summary>No hizo falta restaurar: la data ya estaba disponible en dbv16_01.</summary>
    public const string Omitida = "OMITIDA";
    public const string Error = "ERROR";

    public static bool EsFinal(string estado) => estado is Ok or Omitida or Error;
}

/// <summary>Periodo de gts.historicos.</summary>
public sealed record Periodo(string Tabla, long Ini, long Fin);

public sealed class Trabajo
{
    public string Id { get; set; } = "";
    public List<string> Placas { get; set; } = new();
    public DateOnly Desde { get; set; }
    public DateOnly Hasta { get; set; }
    public long TsDesde { get; set; }
    public long TsHasta { get; set; }
    public string? Solicitante { get; set; }
    public string Estado { get; set; } = EstadoTrabajo.EnCola;
    public string? TablaActual { get; set; }
    public string? Error { get; set; }
    public DateTime Creado { get; set; }
    public DateTime? Inicio { get; set; }
    public DateTime? Fin { get; set; }
    public List<TablaTrabajo> Detalle { get; set; } = new();
}

public sealed class TablaTrabajo
{
    public int Orden { get; set; }
    public string Tabla { get; set; } = "";
    public long PeriodoIni { get; set; }
    public long PeriodoFin { get; set; }
    public string Estado { get; set; } = EstadoTabla.Pendiente;
    public long? FilasInsertadas { get; set; }
    public int? Segundos { get; set; }
    public bool TablaCreada { get; set; }
    public string? Mensaje { get; set; }
    public DateTime? Inicio { get; set; }
    public DateTime? Fin { get; set; }

    /// <summary>Inicio efectivo del filtro dentro de esta tabla (intersección con el periodo).</summary>
    public long FiltroDesde(Trabajo t) => Math.Max(t.TsDesde, PeriodoIni);

    /// <summary>Fin efectivo del filtro dentro de esta tabla (intersección con el periodo).</summary>
    public long FiltroHasta(Trabajo t) => Math.Min(t.TsHasta, PeriodoFin);
}
