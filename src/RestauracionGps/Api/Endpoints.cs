using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using RestauracionGps.Configuracion;
using RestauracionGps.Datos;
using RestauracionGps.Dominio;
using RestauracionGps.Infraestructura;
using RestauracionGps.Servicios;

namespace RestauracionGps.Api;

public static class Endpoints
{
    public static void MapearEndpoints(this WebApplication app)
    {
        app.MapGet("/health", Health);

        var api = app.MapGroup("/api/restauracion").AddEndpointFilter(ValidarApiKey);
        api.MapPost("/", Crear);
        api.MapGet("/", Listar);
        api.MapGet("/{jobId}", Obtener);
    }

    private static async ValueTask<object?> ValidarApiKey(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var esperada = ctx.HttpContext.RequestServices.GetRequiredService<IOptions<RestauracionOptions>>().Value.ApiKey;
        var recibida = ctx.HttpContext.Request.Headers["X-Api-Key"].ToString();
        var ok = CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(recibida), Encoding.UTF8.GetBytes(esperada));
        return ok ? await next(ctx) : Results.Json(new { error = "X-Api-Key inválida o ausente" }, statusCode: 401);
    }

    private static IResult Error(string mensaje, int status = 400) => Results.Json(new { error = mensaje }, statusCode: status);

    private static async Task<IResult> Crear(
        SolicitudRestauracion? req,
        GpsRepositorio gps,
        TrabajosRepositorio trabajos,
        ColaTrabajos cola,
        IOptions<RestauracionOptions> opciones,
        ILoggerFactory logs,
        CancellationToken ct)
    {
        var opt = opciones.Value;
        var log = logs.CreateLogger("Api");
        if (req is null) return Error("Cuerpo JSON requerido");

        // Placas
        var placas = (req.Placas ?? new())
            .Select(p => p?.Trim() ?? "")
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (placas.Count == 0) return Error("Debe indicar al menos una placa");
        if (placas.Count > opt.MaximoPlacas) return Error($"Máximo {opt.MaximoPlacas} placas por solicitud");
        var invalida = placas.FirstOrDefault(p => p.Length > 32);
        if (invalida is not null) return Error($"Placa inválida (máx. 32 caracteres): '{invalida}'");

        // Fechas
        if (!DateOnly.TryParseExact(req.Desde, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var desde))
            return Error("'desde' debe tener formato yyyy-MM-dd");
        if (!DateOnly.TryParseExact(req.Hasta, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var hasta))
            return Error("'hasta' debe tener formato yyyy-MM-dd");
        if (desde > hasta) return Error("'desde' no puede ser mayor que 'hasta'");
        var dias = hasta.DayNumber - desde.DayNumber + 1;
        if (dias > opt.RangoMaximoDias) return Error($"El rango es de {dias} días; el máximo es {opt.RangoMaximoDias}");

        var tsDesde = HoraLima.InicioDelDia(desde);
        var tsHasta = HoraLima.FinDelDia(hasta);

        // Periodos
        var periodos = await gps.PeriodosAsync(tsDesde, tsHasta, ct);
        var invalidos = periodos.Where(p => !GpsRepositorio.NombreTablaValido(p.Tabla)).ToList();
        if (invalidos.Count > 0)
            log.LogWarning("Periodos con nombre de tabla inesperado en gts.historicos (se ignoran): {Tablas}",
                string.Join(", ", invalidos.Select(p => p.Tabla)));
        periodos = periodos.Where(p => GpsRepositorio.NombreTablaValido(p.Tabla))
            .DistinctBy(p => p.Tabla)
            .ToList();
        if (periodos.Count == 0)
            return Error("No hay periodos en gts.historicos para ese rango de fechas", 422);

        var solicitante = req.Solicitante?.Trim();
        if (string.IsNullOrEmpty(solicitante)) solicitante = null;
        else if (solicitante.Length > 150) solicitante = solicitante[..150];

        var trabajo = new Trabajo
        {
            Placas = placas,
            Desde = desde,
            Hasta = hasta,
            TsDesde = tsDesde,
            TsHasta = tsHasta,
            Solicitante = solicitante,
            Estado = EstadoTrabajo.EnCola,
            Creado = DateTime.UtcNow,
            Detalle = periodos.Select((p, i) => new TablaTrabajo
            {
                Orden = i + 1,
                Tabla = p.Tabla,
                PeriodoIni = p.Ini,
                PeriodoFin = p.Fin,
                Estado = EstadoTabla.Pendiente
            }).ToList()
        };

        await trabajos.CrearAsync(trabajo, ct);
        var antes = cola.Pendientes + (cola.EnProceso is null ? 0 : 1);
        cola.Encolar(trabajo.Id);

        log.LogInformation("[{Job}] Encolado por {Solicitante}: placas=[{Placas}] {Desde}..{Hasta} tablas=[{Tablas}]",
            trabajo.Id, trabajo.Solicitante ?? "-", string.Join(",", placas), desde, hasta,
            string.Join(",", periodos.Select(p => p.Tabla)));

        var respuesta = new RespuestaEncolado(
            trabajo.Id,
            trabajo.Estado,
            periodos.Select(p => p.Tabla).ToList(),
            periodos.Count * opt.MinutosPorTabla,
            antes);
        return Results.Accepted($"/api/restauracion/{trabajo.Id}", respuesta);
    }

    private static async Task<IResult> Obtener(string jobId, TrabajosRepositorio trabajos, CancellationToken ct)
    {
        var t = await trabajos.ObtenerAsync(jobId, ct);
        return t is null ? Error($"No existe el trabajo '{jobId}'", 404) : Results.Ok(TrabajoDto.Crear(t));
    }

    private static async Task<IResult> Listar(int? limite, bool? detalle, TrabajosRepositorio trabajos,
        IOptions<RestauracionOptions> opciones, CancellationToken ct)
    {
        var n = Math.Clamp(limite ?? opciones.Value.ListadoPorDefecto, 1, 200);
        var lista = await trabajos.ListarAsync(n, ct);
        return Results.Ok(lista.Select(t => TrabajoDto.Crear(t, detalle ?? false)));
    }

    private static async Task<IResult> Health(GpsRepositorio gps, ColaTrabajos cola, EstadoServicio estado,
        IOptions<RestauracionOptions> opciones, CancellationToken ct)
    {
        string mysql;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await gps.PingAsync(timeout.Token);
            mysql = "ok";
        }
        catch (Exception ex)
        {
            mysql = "error: " + ex.Message;
        }

        double? discoLibreGb = null;
        try { discoLibreGb = Math.Round(Restaurador.EspacioLibreBytes(opciones.Value.DirectorioTemporal) / 1073741824.0, 1); }
        catch { /* se informa como null */ }

        var sano = mysql == "ok" && estado.Listo;
        return Results.Json(new
        {
            estado = sano ? "ok" : "degradado",
            listo = estado.Listo,
            mysql,
            colaPendientes = cola.Pendientes,
            enProceso = cola.EnProceso,
            discoLibreGb,
            hora = HoraLima.Formatear(DateTime.UtcNow)
        }, statusCode: sano ? 200 : 503);
    }
}
