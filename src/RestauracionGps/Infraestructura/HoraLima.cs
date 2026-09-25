namespace RestauracionGps.Infraestructura;

/// <summary>
/// Conversiones de fecha en zona America/Lima (UTC-5, sin horario de verano),
/// que es la zona en la que están calculados los epoch de gts.historicos.
/// </summary>
public static class HoraLima
{
    public static readonly TimeZoneInfo Zona = Resolver();

    private static TimeZoneInfo Resolver()
    {
        foreach (var id in new[] { "America/Lima", "SA Pacific Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        // Lima no tiene horario de verano: un offset fijo es equivalente.
        return TimeZoneInfo.CreateCustomTimeZone("America/Lima", TimeSpan.FromHours(-5), "America/Lima", "PET");
    }

    /// <summary>Epoch de las 00:00:00 del día, hora Lima.</summary>
    public static long InicioDelDia(DateOnly dia) => AEpoch(dia.ToDateTime(TimeOnly.MinValue));

    /// <summary>Epoch de las 23:59:59 del día, hora Lima.</summary>
    public static long FinDelDia(DateOnly dia) => AEpoch(dia.ToDateTime(new TimeOnly(23, 59, 59)));

    private static long AEpoch(DateTime local)
    {
        var offset = Zona.GetUtcOffset(local);
        return new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), offset).ToUnixTimeSeconds();
    }

    /// <summary>Formatea un instante UTC como ISO-8601 con offset de Lima (ej. 2026-09-23T22:10:00-05:00).</summary>
    public static string? Formatear(DateTime? utc)
    {
        if (utc is null) return null;
        var dto = new DateTimeOffset(DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc));
        return TimeZoneInfo.ConvertTime(dto, Zona).ToString("yyyy-MM-dd'T'HH:mm:sszzz");
    }
}
