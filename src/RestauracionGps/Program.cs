using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using RestauracionGps.Api;
using RestauracionGps.Configuracion;
using RestauracionGps.Datos;
using RestauracionGps.Servicios;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
    o.IncludeScopes = false;
});

builder.Services.AddOptions<R2Options>()
    .Bind(builder.Configuration.GetSection(R2Options.Seccion))
    .Validate(o => !string.IsNullOrWhiteSpace(o.ServiceUrl), "Falta R2:ServiceUrl")
    .Validate(o => !string.IsNullOrWhiteSpace(o.AccessKeyId), "Falta R2:AccessKeyId")
    .Validate(o => !string.IsNullOrWhiteSpace(o.SecretAccessKey), "Falta R2:SecretAccessKey")
    .Validate(o => !string.IsNullOrWhiteSpace(o.Bucket), "Falta R2:Bucket")
    .ValidateOnStart();

builder.Services.AddOptions<RestauracionOptions>()
    .Bind(builder.Configuration.GetSection(RestauracionOptions.Seccion))
    .Validate(o => o.ApiKey.Length >= 16, "Restauracion:ApiKey es obligatoria (mínimo 16 caracteres)")
    .Validate(o => o.RangoMaximoDias > 0, "Restauracion:RangoMaximoDias debe ser > 0")
    .Validate(o => o.TuplasPorLote > 0, "Restauracion:TuplasPorLote debe ser > 0")
    .ValidateOnStart();

builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull);

builder.Services.AddSingleton<Conexiones>();
builder.Services.AddSingleton<GpsRepositorio>();
builder.Services.AddSingleton<TrabajosRepositorio>();
builder.Services.AddSingleton<R2Descargador>();
builder.Services.AddSingleton<Disponibilidad>();
builder.Services.AddSingleton<Restaurador>();
builder.Services.AddSingleton<ColaTrabajos>();
builder.Services.AddSingleton<EstadoServicio>();
builder.Services.AddHostedService<TrabajadorRestauracion>();

// Un trabajo en curso puede tardar en cancelarse (lectura del dump, INSERT largo).
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(30));

var app = builder.Build();

app.MapearEndpoints();

var opt = app.Services.GetRequiredService<IOptions<RestauracionOptions>>().Value;
app.Logger.LogInformation("Restauración GPS iniciando. Temporales={Dir} RangoMax={Dias} días TuplasPorLote={Lote}",
    opt.DirectorioTemporal, opt.RangoMaximoDias, opt.TuplasPorLote);

app.Run();
