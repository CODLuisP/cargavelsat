using MySqlConnector;

namespace RestauracionGps.Datos;

/// <summary>Fábrica de conexiones a las dos bases (mismo servidor mysql-gts).</summary>
public sealed class Conexiones
{
    public string Gts { get; }
    public string Dbv16 { get; }

    public Conexiones(IConfiguration config)
    {
        Gts = config.GetConnectionString("Gts")
              ?? throw new InvalidOperationException("Falta ConnectionStrings:Gts");
        Dbv16 = config.GetConnectionString("Dbv16")
                ?? throw new InvalidOperationException("Falta ConnectionStrings:Dbv16");
    }

    public async Task<MySqlConnection> AbrirGtsAsync(CancellationToken ct = default)
    {
        var cn = new MySqlConnection(Gts);
        await cn.OpenAsync(ct);
        return cn;
    }

    public async Task<MySqlConnection> AbrirDbv16Async(CancellationToken ct = default)
    {
        var cn = new MySqlConnection(Dbv16);
        await cn.OpenAsync(ct);
        return cn;
    }

    /// <summary>Datos de conexión para el cliente de línea de comandos mysql.</summary>
    public MySqlConnectionStringBuilder DatosDbv16() => new(Dbv16);
}
