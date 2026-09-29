using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace RestauracionGps.Servicios;

/// <summary>El dump no se pudo leer de forma confiable. Nunca se inserta nada de un dump inválido.</summary>
public sealed class DumpInvalidoException(string mensaje) : Exception(mensaje);

/// <summary>Recibe el texto exacto de una tupla aceptada, de "(" a ")" inclusive, tal como viene en el dump.</summary>
public delegate void TuplaAceptada(ReadOnlySpan<byte> tupla);

public sealed record ResultadoLectura(
    long BytesDescomprimidos,
    long SentenciasInsert,
    long TuplasTotales,
    long TuplasFiltradas,
    long TuplasOtrasTablas,
    Encoding Codificacion,
    string Charset,
    string? ZonaHoraria);

/// <summary>
/// Lee un dump de mysqldump (.sql.gz) en streaming y se queda solo con las tuplas de las placas y el
/// rango pedidos, sin cargar el archivo en memoria ni ejecutarlo.
///
/// Trabaja sobre bytes, no sobre caracteres: los delimitadores ( ) , ' \ ; son ASCII y nunca aparecen
/// dentro de una secuencia multibyte UTF-8, así que no hace falta decodificar los GB del dump y el
/// texto de cada tupla aceptada se conserva byte a byte.
///
/// Se alimenta por bloques (<see cref="Procesar"/>) y guarda todo su estado entre bloques, así que da
/// igual dónde corte cada bloque (en medio de una comilla, de un escape, etc.).
///
/// Verificación integrada: cada tupla debe empezar con "(" y terminar con ")" seguida de "," o ";",
/// tener exactamente la cantidad de campos de la tabla destino, y el archivo debe terminar fuera de
/// cualquier sentencia y comilla. Si algo no cuadra se lanza <see cref="DumpInvalidoException"/>.
/// </summary>
public sealed partial class LectorDumpSql
{
    private enum Estado
    {
        Linea,           // al inicio o dentro de una línea que todavía no se sabe si es un INSERT
        SaltarLinea,     // línea que no interesa (comentarios, CREATE TABLE, SET, LOCK...)
        EsperaTupla,     // después de "VALUES" o de "),": se espera "("
        EnTupla,         // dentro de una tupla, fuera de comillas
        EnComilla,       // dentro de una cadena '...'
        Escape,          // después de una barra invertida dentro de una cadena
        CierrePendiente, // se vio ' dentro de una cadena: o cierra, o es '' (apóstrofe literal)
        TrasTupla        // después de ")": se espera "," o ";"
    }

    private const int MaxCabecera = 8192;

    private readonly string _tabla;
    private readonly HashSet<string> _placas;
    private readonly long _desde;
    private readonly long _hasta;
    private readonly int _columnasEsperadas;
    private readonly TuplaAceptada _alAceptar;

    private Estado _estado = Estado.Linea;
    private readonly byte[] _linea = new byte[MaxCabecera];
    private int _lineaLen;

    // Sentencia actual
    private bool _esObjetivo;

    // Tupla actual
    private byte[] _tupla = new byte[4096];
    private int _tuplaLen;
    private bool _grabando;
    private bool _decidida;
    private bool _aceptada;
    private int _campo;
    private int _inicioCampo;
    private int _devIni, _devFin;

    // Cabecera del dump
    private string? _charset;
    private string? _zonaHoraria;
    private Encoding? _codificacion;

    // Contadores
    private long _bytes;
    private long _sentencias;
    private long _tuplas;
    private long _filtradas;
    private long _otras;

    public LectorDumpSql(string tabla, IEnumerable<string> placas, long desde, long hasta,
        int columnasEsperadas, TuplaAceptada alAceptar)
    {
        if (columnasEsperadas < 3)
            throw new ArgumentOutOfRangeException(nameof(columnasEsperadas), "La tabla debe tener al menos 3 columnas");
        _tabla = tabla;
        _placas = new HashSet<string>(placas, StringComparer.OrdinalIgnoreCase);
        _desde = desde;
        _hasta = hasta;
        _columnasEsperadas = columnasEsperadas;
        _alAceptar = alAceptar;
    }

    public long BytesProcesados => _bytes;
    public long TuplasTotales => _tuplas;
    public long TuplasFiltradas => _filtradas;

    /// <summary>Descomprime y procesa un .sql.gz completo.</summary>
    public async Task<ResultadoLectura> LeerGzAsync(Stream gzComprimido, Action<LectorDumpSql>? progreso,
        CancellationToken ct)
    {
        const long pasoProgreso = 1L << 30; // 1 GB descomprimido
        var siguiente = pasoProgreso;
        await using var gz = new GZipStream(gzComprimido, CompressionMode.Decompress, leaveOpen: true);
        var buffer = new byte[1 << 20];
        int n;
        while ((n = await gz.ReadAsync(buffer, ct)) > 0)
        {
            Procesar(buffer.AsSpan(0, n));
            if (_bytes >= siguiente)
            {
                progreso?.Invoke(this);
                siguiente += pasoProgreso;
            }
        }
        return Finalizar();
    }

    public void Procesar(ReadOnlySpan<byte> datos)
    {
        var i = 0;
        while (i < datos.Length)
        {
            var b = datos[i];
            switch (_estado)
            {
                case Estado.Linea:
                    ProcesarLinea(b);
                    break;

                case Estado.SaltarLinea:
                    if (b == (byte)'\n') { _estado = Estado.Linea; _lineaLen = 0; }
                    break;

                case Estado.EsperaTupla:
                    if (b == (byte)'(') IniciarTupla();
                    else if (!EsEspacio(b)) throw Malformada($"se esperaba '(' al inicio de la tupla y se encontró {Mostrar(b)}", i);
                    break;

                case Estado.EnTupla:
                    Grabar(b);
                    if (b == (byte)'\'') _estado = Estado.EnComilla;
                    else if (b == (byte)',') FinCampo(i);
                    else if (b == (byte)')') FinTupla(i);
                    else if (b is (byte)'(' or (byte)';')
                        throw Malformada($"{Mostrar(b)} fuera de comillas dentro de la tupla", i);
                    break;

                case Estado.EnComilla:
                    Grabar(b);
                    if (b == (byte)'\\') _estado = Estado.Escape;
                    else if (b == (byte)'\'') _estado = Estado.CierrePendiente;
                    break;

                case Estado.Escape:
                    // \' y \\ (y cualquier otro escape) no cierran la comilla.
                    Grabar(b);
                    _estado = Estado.EnComilla;
                    break;

                case Estado.CierrePendiente:
                    if (b == (byte)'\'')
                    {
                        // '' = apóstrofe literal: se sigue dentro de la cadena.
                        Grabar(b);
                        _estado = Estado.EnComilla;
                        break;
                    }
                    // La comilla anterior cerró la cadena: este byte se procesa fuera de comillas.
                    _estado = Estado.EnTupla;
                    continue;

                case Estado.TrasTupla:
                    if (b == (byte)',') _estado = Estado.EsperaTupla;
                    else if (b == (byte)';') _estado = Estado.SaltarLinea;
                    else if (!EsEspacio(b))
                        throw Malformada($"se esperaba ',' o ';' después de ')' y se encontró {Mostrar(b)}", i);
                    break;
            }
            i++;
        }
        _bytes += datos.Length;
    }

    /// <summary>Verifica que el archivo terminó en un estado consistente y devuelve los totales.</summary>
    public ResultadoLectura Finalizar()
    {
        switch (_estado)
        {
            case Estado.EnComilla or Estado.Escape:
                throw new DumpInvalidoException(
                    $"El dump de {_tabla} terminó dentro de una comilla (tupla #{_tuplas + 1}): archivo truncado o " +
                    "cadena mal escapada. No se insertó nada.");
            case Estado.EsperaTupla or Estado.EnTupla or Estado.CierrePendiente or Estado.TrasTupla:
            case Estado.Linea when EsLineaInsert():
                throw new DumpInvalidoException(
                    $"El dump de {_tabla} terminó en medio de una sentencia INSERT (tupla #{_tuplas + 1}): " +
                    "archivo truncado. No se insertó nada.");
        }

        if (_tuplas == 0 && _otras > 0)
            throw new DumpInvalidoException(
                $"El dump no contiene INSERT para `{_tabla}` pero sí {_otras} tuplas de otras tablas. No se insertó nada.");

        return new ResultadoLectura(_bytes, _sentencias, _tuplas, _filtradas, _otras,
            _codificacion ?? ResolverCodificacion(), _charset ?? "utf8 (por defecto)", _zonaHoraria);
    }

    // ---------------------------------------------------------------- líneas fuera de INSERT

    private void ProcesarLinea(byte b)
    {
        if (b == (byte)'\n')
        {
            InspeccionarCabecera();
            _lineaLen = 0;
            return;
        }
        if (_lineaLen == MaxCabecera)
        {
            // Línea larga que no es INSERT (por ejemplo, un comentario enorme): no interesa.
            _estado = Estado.SaltarLinea;
            return;
        }
        _linea[_lineaLen++] = b;

        // Un INSERT se reconoce en su primer "(": "INSERT INTO `tabla` VALUES (".
        if (b == (byte)'(' && EsLineaInsert())
            AnalizarCabeceraInsert();
    }

    private void AnalizarCabeceraInsert()
    {
        var texto = Encoding.ASCII.GetString(_linea, 0, _lineaLen);
        var m = RegexInsert().Match(texto);
        if (!m.Success)
        {
            if (RegexInsertConColumnas().IsMatch(texto))
                throw new DumpInvalidoException(
                    $"El dump de {_tabla} usa INSERT con lista de columnas (mysqldump --complete-insert), " +
                    "formato no soportado. No se insertó nada.");
            throw new DumpInvalidoException(
                $"Sentencia INSERT no reconocida en el dump de {_tabla}: '{Recortar(texto)}'. No se insertó nada.");
        }

        _codificacion ??= ResolverCodificacion();
        _sentencias++;
        _esObjetivo = string.Equals(m.Groups[1].Value, _tabla, StringComparison.OrdinalIgnoreCase);
        _lineaLen = 0;
        IniciarTupla(); // el "(" ya se consumió
    }

    /// <summary>Lee de la cabecera del dump el charset (SET NAMES) y la zona horaria (SET TIME_ZONE).</summary>
    private void InspeccionarCabecera()
    {
        if (_codificacion is not null || _lineaLen == 0) return; // solo antes del primer INSERT
        var texto = Encoding.ASCII.GetString(_linea, 0, _lineaLen);
        var names = RegexSetNames().Match(texto);
        if (names.Success) _charset = names.Groups[1].Value.ToLowerInvariant();
        var zona = RegexSetTimeZone().Match(texto);
        if (zona.Success) _zonaHoraria = zona.Groups[1].Value;
    }

    private Encoding ResolverCodificacion()
    {
        switch (_charset)
        {
            case null or "utf8" or "utf8mb3" or "utf8mb4":
                return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            case "latin1":
                // En MySQL "latin1" es en realidad cp1252.
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                return Encoding.GetEncoding(1252);
            case "ascii":
                return Encoding.GetEncoding("us-ascii", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            default:
                throw new DumpInvalidoException(
                    $"El dump de {_tabla} declara SET NAMES {_charset}, charset no soportado. No se insertó nada.");
        }
    }

    // ---------------------------------------------------------------- tuplas

    private void IniciarTupla()
    {
        _tuplaLen = 0;
        _grabando = _esObjetivo;
        _decidida = !_esObjetivo;
        _aceptada = false;
        _campo = 1;
        Grabar((byte)'(');
        _inicioCampo = _tuplaLen;
        _estado = Estado.EnTupla;
    }

    private void Grabar(byte b)
    {
        if (!_grabando) return;
        if (_tuplaLen == _tupla.Length) Array.Resize(ref _tupla, _tupla.Length * 2);
        _tupla[_tuplaLen++] = b;
    }

    /// <summary>Coma fuera de comillas: termina el campo actual.</summary>
    private void FinCampo(int i)
    {
        if (!_decidida)
        {
            var fin = _tuplaLen - 1; // posición de la coma
            if (_campo == 2) { _devIni = _inicioCampo; _devFin = fin; }
            else if (_campo == 3) Decidir(_inicioCampo, fin, i);
        }
        _campo++;
        _inicioCampo = _tuplaLen;
    }

    private void FinTupla(int i)
    {
        if (_esObjetivo)
        {
            if (!_decidida && _campo == 3) Decidir(_inicioCampo, _tuplaLen - 1, i);
            if (_campo != _columnasEsperadas)
                throw Malformada(
                    $"la tupla tiene {_campo} campos y la tabla destino {_columnasEsperadas} columnas " +
                    "(la estructura del dump no coincide con gts.eventdata)", i);
            _tuplas++;
            if (_aceptada)
            {
                _filtradas++;
                _alAceptar(_tupla.AsSpan(0, _tuplaLen));
            }
        }
        else
        {
            _otras++;
        }
        _estado = Estado.TrasTupla;
    }

    /// <summary>Con deviceID (campo 2) y timestamp (campo 3) leídos, decide si la tupla se queda.</summary>
    private void Decidir(int tsIni, int tsFin, int i)
    {
        _decidida = true;
        var device = LeerCadena(_tupla.AsSpan(_devIni, _devFin - _devIni), i);
        var timestamp = LeerEntero(_tupla.AsSpan(tsIni, tsFin - tsIni), i);

        _aceptada = timestamp >= _desde && timestamp <= _hasta && _placas.Contains(device);
        if (!_aceptada)
        {
            // Se descarta: se sigue recorriendo hasta el ")" pero sin copiar bytes.
            _grabando = false;
            _tuplaLen = 0;
        }
    }

    /// <summary>Valor de un campo de texto '...' con los escapes de MySQL resueltos.</summary>
    private string LeerCadena(ReadOnlySpan<byte> campo, int i)
    {
        campo = Recortar(campo);
        if (campo.Length < 2 || campo[0] != (byte)'\'' || campo[^1] != (byte)'\'')
            throw Malformada($"deviceID (campo 2) no es una cadena entre comillas: {MostrarCampo(campo)}", i);

        var interior = campo[1..^1];
        var valor = new byte[interior.Length];
        var n = 0;
        for (var k = 0; k < interior.Length; k++)
        {
            var c = interior[k];
            if (c == (byte)'\\' && k + 1 < interior.Length)
            {
                c = interior[++k] switch
                {
                    (byte)'0' => 0,
                    (byte)'b' => 8,
                    (byte)'n' => (byte)'\n',
                    (byte)'r' => (byte)'\r',
                    (byte)'t' => (byte)'\t',
                    (byte)'Z' => 26,
                    var otro => otro
                };
            }
            else if (c == (byte)'\'' && k + 1 < interior.Length && interior[k + 1] == (byte)'\'')
            {
                k++;
            }
            valor[n++] = c;
        }
        try
        {
            return _codificacion!.GetString(valor, 0, n);
        }
        catch (DecoderFallbackException)
        {
            throw Malformada($"deviceID con bytes inválidos para el charset {_charset ?? "utf8"}", i);
        }
    }

    private long LeerEntero(ReadOnlySpan<byte> campo, int i)
    {
        campo = Recortar(campo);
        if (campo.Length >= 2 && campo[0] == (byte)'\'' && campo[^1] == (byte)'\'') campo = campo[1..^1];
        if (campo.Length is 0 or > 19)
            throw Malformada($"timestamp (campo 3) no es un entero: {MostrarCampo(campo)}", i);
        long v = 0;
        foreach (var c in campo)
        {
            if (c is < (byte)'0' or > (byte)'9')
                throw Malformada($"timestamp (campo 3) no es un entero: {MostrarCampo(campo)}", i);
            v = v * 10 + (c - '0');
        }
        return v;
    }

    // ---------------------------------------------------------------- utilidades

    private DumpInvalidoException Malformada(string detalle, int indiceEnBloque) => new(
        $"Dump de {_tabla} malformado en la tupla #{_tuplas + _otras + 1} (byte {_bytes + indiceEnBloque} del " +
        $"archivo descomprimido): {detalle}. No se insertó nada.");

    private bool EsLineaInsert() => EmpiezaCon("INSERT") || EmpiezaCon("REPLACE");

    private bool EmpiezaCon(string prefijo)
    {
        if (_lineaLen < prefijo.Length) return false;
        for (var k = 0; k < prefijo.Length; k++)
            if ((_linea[k] | 0x20) != (prefijo[k] | 0x20)) return false;
        return true;
    }

    private static bool EsEspacio(byte b) => b is (byte)' ' or (byte)'\n' or (byte)'\r' or (byte)'\t';

    private static ReadOnlySpan<byte> Recortar(ReadOnlySpan<byte> s)
    {
        while (s.Length > 0 && EsEspacio(s[0])) s = s[1..];
        while (s.Length > 0 && EsEspacio(s[^1])) s = s[..^1];
        return s;
    }

    private static string Mostrar(byte b) => b >= 32 && b < 127 ? $"'{(char)b}'" : $"0x{b:X2}";

    private static string MostrarCampo(ReadOnlySpan<byte> campo) =>
        Recortar(Encoding.ASCII.GetString(campo.Length > 60 ? campo[..60] : campo));

    private static string Recortar(string s) => s.Length > 80 ? s[..80] + "..." : s;

    [GeneratedRegex(@"^(?:INSERT(?:\s+IGNORE)?|REPLACE)\s+INTO\s+`?([^`\s(]+)`?\s+VALUES\s*\($", RegexOptions.IgnoreCase)]
    private static partial Regex RegexInsert();

    [GeneratedRegex(@"^(?:INSERT(?:\s+IGNORE)?|REPLACE)\s+INTO\s+`?[^`\s(]+`?\s*\($", RegexOptions.IgnoreCase)]
    private static partial Regex RegexInsertConColumnas();

    [GeneratedRegex(@"SET\s+NAMES\s+'?(\w+)", RegexOptions.IgnoreCase)]
    private static partial Regex RegexSetNames();

    [GeneratedRegex(@"SET\s+TIME_ZONE\s*=\s*'([^']*)'", RegexOptions.IgnoreCase)]
    private static partial Regex RegexSetTimeZone();
}
