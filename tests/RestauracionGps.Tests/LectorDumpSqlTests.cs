using System.IO.Compression;
using System.Text;
using RestauracionGps.Servicios;
using Xunit;

namespace RestauracionGps.Tests;

public class LectorDumpSqlTests
{
    private const string Tabla = "gps_20250605";
    private const int Columnas = 6; // accountID, deviceID, timestamp, statusCode, address, speedKPH

    private const string Cabecera = """
        -- MySQL dump 10.13  Distrib 8.0.36, for Linux (x86_64)
        --
        -- Host: localhost    Database: dbv16_01
        /*!40101 SET @OLD_CHARACTER_SET_CLIENT=@@CHARACTER_SET_CLIENT */;
        /*!40101 SET NAMES utf8 */;
        /*!50503 SET NAMES utf8mb4 */;
        /*!40103 SET @OLD_TIME_ZONE=@@TIME_ZONE */;
        /*!40103 SET TIME_ZONE='+00:00' */;
        DROP TABLE IF EXISTS `gps_20250605`;
        CREATE TABLE `gps_20250605` (
          `accountID` varchar(32) NOT NULL COMMENT 'cuenta (principal); ver ''docs''',
          `deviceID` varchar(32) NOT NULL,
          PRIMARY KEY (`accountID`,`deviceID`)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
        LOCK TABLES `gps_20250605` WRITE;
        /*!40000 ALTER TABLE `gps_20250605` DISABLE KEYS */;

        """;

    private const string Pie = """

        /*!40000 ALTER TABLE `gps_20250605` ENABLE KEYS */;
        UNLOCK TABLES;
        /*!40103 SET TIME_ZONE=@OLD_TIME_ZONE */;
        -- Dump completed on 2025-06-06  2:00:01

        """;

    private static string T(string dev, long ts, string address = "'Av. Arequipa 123'", string acc = "'velsat'") =>
        $"({acc},'{dev}',{ts},61472,{address},35.5)";

    private static string Insert(params string[] tuplas) => $"INSERT INTO `{Tabla}` VALUES {string.Join(",", tuplas)};";

    private sealed record Salida(ResultadoLectura R, List<string> Aceptadas);

    private static Salida Leer(string dump, string[] placas, long desde, long hasta, int trozo = 0,
        int columnas = Columnas, Encoding? codificacionArchivo = null)
    {
        var bytes = (codificacionArchivo ?? new UTF8Encoding(false)).GetBytes(dump);
        var aceptadas = new List<byte[]>();
        var lector = new LectorDumpSql(Tabla, placas, desde, hasta, columnas, t => aceptadas.Add(t.ToArray()));
        if (trozo <= 0) lector.Procesar(bytes);
        else
            for (var i = 0; i < bytes.Length; i += trozo)
                lector.Procesar(bytes.AsSpan(i, Math.Min(trozo, bytes.Length - i)));
        var r = lector.Finalizar();
        return new Salida(r, aceptadas.Select(a => r.Codificacion.GetString(a)).ToList());
    }

    [Fact]
    public void Filtra_por_placa_y_rango_y_cuenta_todas_las_tuplas()
    {
        var dump = Cabecera
                   + Insert(T("bam-754", 100), T("c4b-849", 150), T("bam-754", 200), T("xyz-999", 150)) + "\n"
                   + Insert(T("bam-754", 99), T("bam-754", 201), T("BAM-754", 180)) + "\n"
                   + Pie;

        var s = Leer(dump, ["bam-754"], 100, 200);

        Assert.Equal(7, s.R.TuplasTotales);
        Assert.Equal(2, s.R.SentenciasInsert);
        Assert.Equal(3, s.R.TuplasFiltradas); // límites inclusivos y placa sin distinguir mayúsculas
        Assert.Equal([T("bam-754", 100), T("bam-754", 200), T("BAM-754", 180)], s.Aceptadas);
        Assert.Equal("+00:00", s.R.ZonaHoraria);
        Assert.Equal("utf8mb4", s.R.Charset);
    }

    public static TheoryData<string> DireccionesDificiles => new()
    {
        "'Carretera Interocéanica Norte, Salas, Perú'",
        "'Av. Los Incas (Km 12), Ate'",
        "'),(\\'bam-754\\',150,1,\\'x\\',1),('",
        "'O\\'Higgins 123'",
        "'O''Higgins 123'",
        "'C:\\\\'",
        "'barra y comilla \\\\\\''",
        "''",
        "''''",
        "'línea\\nnueva\\r\\ty tab \\0 \\Z'",
        "'punto y coma; ) ( , fin'",
        "NULL",
        "'emoji 🚚 y ñ'",
    };

    [Theory]
    [MemberData(nameof(DireccionesDificiles))]
    public void Direcciones_con_comillas_escapes_y_parentesis_no_rompen_el_parser(string address)
    {
        var tuplas = new[] { T("bam-754", 150, address), T("otra-111", 150, address), T("bam-754", 160, address) };
        var dump = Cabecera + Insert(tuplas) + "\n" + Pie;

        foreach (var trozo in new[] { 0, 1, 2, 3, 7 })
        {
            var s = Leer(dump, ["bam-754"], 0, 1000, trozo);
            Assert.Equal(3, s.R.TuplasTotales);
            Assert.Equal([tuplas[0], tuplas[2]], s.Aceptadas);
        }
    }

    [Fact]
    public void DeviceID_con_escape_se_compara_por_su_valor()
    {
        var dump = Cabecera + Insert("('velsat','o\\'neil',150,1,'x',1)", "('velsat','o''neil',150,1,'x',1)") + Pie;
        var s = Leer(dump, ["o'neil"], 0, 1000);
        Assert.Equal(2, s.R.TuplasFiltradas);
    }

    [Fact]
    public void Acepta_INSERT_IGNORE_REPLACE_y_finales_de_linea_CRLF()
    {
        var dump = (Cabecera + $"INSERT IGNORE INTO `{Tabla}` VALUES {T("a", 1)};\n"
                             + $"REPLACE INTO `{Tabla}` VALUES {T("a", 2)},\n{T("a", 3)};\n" + Pie)
            .Replace("\n", "\r\n");
        var s = Leer(dump, ["a"], 0, 10);
        Assert.Equal(3, s.R.TuplasFiltradas);
    }

    [Fact]
    public void Dump_sin_CREATE_TABLE_como_los_de_MySQL_56()
    {
        var dump = "LOCK TABLES `gps_20250605` WRITE;\n" + Insert(T("a", 1), T("a", 1)) + "\nUNLOCK TABLES;\n";
        var s = Leer(dump, ["a"], 0, 10);
        Assert.Equal(2, s.R.TuplasFiltradas); // los duplicados los descarta después el INSERT IGNORE
        Assert.Null(s.R.ZonaHoraria);
    }

    [Fact]
    public void Ignora_otras_tablas_pero_falla_si_solo_hay_otras()
    {
        var otra = $"INSERT INTO `gps_20250529` VALUES {T("a", 1)},{T("a", 2)};\n";
        var s = Leer(Cabecera + otra + Insert(T("a", 3)) + "\n", ["a"], 0, 10);
        Assert.Equal(1, s.R.TuplasTotales);
        Assert.Equal(2, s.R.TuplasOtrasTablas);

        var ex = Assert.Throws<DumpInvalidoException>(() => Leer(Cabecera + otra, ["a"], 0, 10));
        Assert.Contains("no contiene INSERT para `gps_20250605`", ex.Message);
    }

    [Fact]
    public void Latin1_se_decodifica_como_cp1252()
    {
        var dump = "/*!40101 SET NAMES latin1 */;\n" + Insert(T("a", 1, "'Perú, Año'")) + "\n";
        var s = Leer(dump, ["a"], 0, 10, codificacionArchivo: Encoding.Latin1);
        Assert.Equal(T("a", 1, "'Perú, Año'"), Assert.Single(s.Aceptadas));
    }

    [Fact]
    public void Dump_sin_INSERT_devuelve_cero()
    {
        var s = Leer(Cabecera + Pie, ["a"], 0, 10);
        Assert.Equal(0, s.R.TuplasTotales);
        Assert.Equal(0, s.R.SentenciasInsert);
    }

    public static TheoryData<string, string> DumpsInvalidos => new()
    {
        { Cabecera + $"INSERT INTO `{Tabla}` VALUES ('velsat','a',1,1,'sin cerrar", "terminó dentro de una comilla" },
        { Cabecera + $"INSERT INTO `{Tabla}` VALUES ('velsat','a',1,1,'x\\", "terminó dentro de una comilla" },
        { Cabecera + $"INSERT INTO `{Tabla}` VALUES {T("a", 1)},", "terminó en medio de una sentencia INSERT" },
        { Cabecera + $"INSERT INTO `{Tabla}` VALUES {T("a", 1)}", "terminó en medio de una sentencia INSERT" },
        { Cabecera + $"INSERT INTO `{Tabla}` VAL", "terminó en medio de una sentencia INSERT" },
        { Cabecera + $"INSERT INTO `{Tabla}` VALUES {T("a", 1)}{T("a", 2)};\n", "se esperaba ',' o ';'" },
        { Cabecera + $"INSERT INTO `{Tabla}` VALUES {T("a", 1)},x{T("a", 2)};\n", "se esperaba '('" },
        { Cabecera + $"INSERT INTO `{Tabla}` VALUES ('velsat','a',1,1,'x');\n", "5 campos" },
        { Cabecera + $"INSERT INTO `{Tabla}` VALUES ('velsat','a',1,1,'x',1,2);\n", "7 campos" },
        { Cabecera + $"INSERT INTO `{Tabla}` VALUES ('velsat','a',1,1,('x'),1);\n", "fuera de comillas" },
        { Cabecera + $"INSERT INTO `{Tabla}` VALUES ('velsat',a,1,1,'x',1);\n", "deviceID (campo 2) no es una cadena" },
        { Cabecera + $"INSERT INTO `{Tabla}` VALUES ('velsat','a',NULL,1,'x',1);\n", "timestamp (campo 3) no es un entero" },
        { Cabecera + $"INSERT INTO `{Tabla}` (`accountID`,`deviceID`) VALUES ('velsat','a');\n", "lista de columnas" },
        { "/*!40101 SET NAMES big5 */;\n" + Insert(T("a", 1)), "charset no soportado" },
    };

    [Theory]
    [MemberData(nameof(DumpsInvalidos))]
    public void Dumps_invalidos_abortan_con_error_claro(string dump, string mensaje)
    {
        foreach (var trozo in new[] { 0, 1, 5 })
        {
            var ex = Assert.Throws<DumpInvalidoException>(() => Leer(dump, ["a"], 0, 10, trozo));
            Assert.Contains(mensaje, ex.Message);
            Assert.Contains("No se insertó nada", ex.Message);
        }
    }

    [Fact]
    public void La_estructura_distinta_se_detecta_aunque_ninguna_tupla_pase_el_filtro()
    {
        var dump = Cabecera + Insert("('velsat','zzz',1,1,'x')") + "\n";
        var ex = Assert.Throws<DumpInvalidoException>(() => Leer(dump, ["a"], 0, 10));
        Assert.Contains("la estructura del dump no coincide con gts.eventdata", ex.Message);
    }

    /// <summary>
    /// Dump sintético grande con 104 columnas y direcciones aleatorias con todos los caracteres
    /// conflictivos, escapado como lo hace mysqldump. El resultado debe coincidir exactamente con
    /// lo esperado, con bloques de cualquier tamaño y comprimido con gzip.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dump_sintetico_grande_coincide_exactamente(bool unaSolaLinea)
    {
        const int columnas = 104;
        var rnd = new Random(12345);
        var placas = new[] { "bam-754", "c4b-849", "aar-555", "h1k-422", "otra-001", "otra-002", "otra-003" };
        var piezas = new[] { "Av. Los Incas (Km 12)", ", Ate", "O'Higgins", "\\", "),(", "'", "''", ";", "\n", "\r",
            "\0", "\u001a", "Perú", "Interocéanica", "🚚", "\"comillas dobles\"", " ", "(", ")", "," };

        var esperadas = new List<string>();
        var sentencias = new List<List<string>>();
        var actual = new List<string>();
        const int total = 30_000;
        for (var n = 0; n < total; n++)
        {
            var dev = placas[rnd.Next(placas.Length)];
            var ts = 1_751_000_000L + rnd.Next(0, 400_000);
            var direccion = string.Concat(Enumerable.Range(0, rnd.Next(0, 8)).Select(_ => piezas[rnd.Next(piezas.Length)]));
            var valores = new List<string> { "'velsat'", Cadena(dev), ts.ToString(), rnd.Next(0, 70000).ToString(), Cadena(direccion) };
            while (valores.Count < columnas)
                valores.Add(rnd.Next(4) switch
                {
                    0 => "NULL",
                    1 => (rnd.NextDouble() * 1000 - 500).ToString("0.######", System.Globalization.CultureInfo.InvariantCulture),
                    2 => Cadena(piezas[rnd.Next(piezas.Length)]),
                    _ => rnd.Next().ToString()
                });
            var tupla = "(" + string.Join(",", valores) + ")";

            actual.Add(tupla);
            if (actual.Count == 250 && !unaSolaLinea) { sentencias.Add(actual); actual = new(); }

            if (dev is "bam-754" or "h1k-422" && ts is >= 1_751_100_000 and <= 1_751_200_000)
                esperadas.Add(tupla);
        }
        if (actual.Count > 0) sentencias.Add(actual);

        var dump = Cabecera + string.Join("\n", sentencias.Select(s => Insert(s.ToArray()))) + "\n" + Pie;
        var gz = new MemoryStream();
        using (var z = new GZipStream(gz, CompressionLevel.Fastest, leaveOpen: true))
            z.Write(new UTF8Encoding(false).GetBytes(dump));

        var aceptadas = new List<string>();
        gz.Position = 0;
        var lector = new LectorDumpSql(Tabla, ["BAM-754", "h1k-422"], 1_751_100_000, 1_751_200_000, columnas,
            t => aceptadas.Add(Encoding.UTF8.GetString(t)));
        var r = await lector.LeerGzAsync(gz, null, CancellationToken.None);

        Assert.Equal(total, r.TuplasTotales);
        Assert.Equal(sentencias.Count, r.SentenciasInsert);
        Assert.Equal(esperadas.Count, r.TuplasFiltradas);
        Assert.Equal(esperadas, aceptadas);

        // Y con bloques de tamaño irregular, sin gzip.
        foreach (var trozo in new[] { 1, 13, 4096 })
        {
            var s = Leer(dump, ["BAM-754", "h1k-422"], 1_751_100_000, 1_751_200_000, trozo, columnas);
            Assert.Equal(esperadas, s.Aceptadas);
        }
    }

    /// <summary>Escapa como mysqldump (mysql_real_escape_string).</summary>
    private static string Cadena(string s)
    {
        var sb = new StringBuilder("'");
        foreach (var c in s)
            sb.Append(c switch
            {
                '\\' => "\\\\",
                '\'' => "\\'",
                '"' => "\\\"",
                '\n' => "\\n",
                '\r' => "\\r",
                '\0' => "\\0",
                '\u001a' => "\\Z",
                _ => c.ToString()
            });
        return sb.Append('\'').ToString();
    }
}
