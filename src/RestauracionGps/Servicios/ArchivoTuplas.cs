using System.Text;

namespace RestauracionGps.Servicios;

/// <summary>
/// Archivo temporal con las tuplas aceptadas (longitud + bytes). Permite validar el dump completo
/// antes de insertar nada, sin acumular en memoria las tuplas cuando se piden muchas placas.
/// </summary>
public sealed class ArchivoTuplas : IDisposable
{
    private readonly FileStream _fs;
    private readonly BinaryWriter _w;

    public ArchivoTuplas(string ruta)
    {
        _fs = new FileStream(ruta, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        _w = new BinaryWriter(_fs);
    }

    public void Agregar(ReadOnlySpan<byte> tupla)
    {
        _w.Write(tupla.Length);
        _w.Write(tupla);
    }

    public void Dispose()
    {
        _w.Dispose();
        _fs.Dispose();
    }

    /// <summary>Lee las tuplas en el mismo orden y las decodifica con el charset del dump.</summary>
    public static IEnumerable<string> Leer(string ruta, Encoding codificacion, string charset)
    {
        using var fs = new FileStream(ruta, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        using var r = new BinaryReader(fs);
        var n = 0L;
        while (fs.Position < fs.Length)
        {
            var bytes = r.ReadBytes(r.ReadInt32());
            n++;
            string tupla;
            try
            {
                tupla = codificacion.GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                throw new DumpInvalidoException(
                    $"La tupla aceptada #{n} tiene bytes inválidos para el charset {charset}. No se insertó nada.");
            }
            yield return tupla;
        }
    }
}
