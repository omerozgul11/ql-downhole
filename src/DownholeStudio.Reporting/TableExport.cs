using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;
using DownholeStudio.Domain.Units;

namespace DownholeStudio.Reporting;

/// <summary>CSV (RFC 4180, UTF-8 with BOM for spreadsheet compatibility, invariant decimal point).</summary>
public static class CsvExporter
{
    public static string Write(ResultTable table, UnitPreferences units)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", table.Columns.Select(c => Escape(c.HeaderWithUnit(units)))));
        for (var r = 0; r < table.Rows.Count; r++)
        {
            var cells = new string[table.Columns.Count];
            for (var c = 0; c < table.Columns.Count; c++)
            {
                cells[c] = table.DisplayValue(r, c, units) switch
                {
                    null => "",
                    double d when !double.IsFinite(d) => "",
                    double d => d.ToString("R", CultureInfo.InvariantCulture),
                    bool b => b ? "yes" : "no",
                    DateTimeOffset t => t.ToString("O", CultureInfo.InvariantCulture),
                    var v => Escape(v.ToString() ?? ""),
                };
            }

            sb.AppendLine(string.Join(",", cells));
        }

        return sb.ToString();
    }

    public static byte[] WriteBytes(ResultTable table, UnitPreferences units) => [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(Write(table, units))];

    private static string Escape(string s)
    {
        // Prevent spreadsheet formula injection from free-text fields.
        if (s.Length > 0 && "=+-@\t\r".Contains(s[0]) && !double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            s = "'" + s;
        }

        return s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }
}

/// <summary>
/// Minimal Office Open XML (XLSX) workbook writer: one sheet per table, bold frozen header row,
/// numbers stored at full double precision with a display format (never rounded values).
/// </summary>
public static class XlsxWriter
{
    public static byte[] Write(IReadOnlyList<ResultTable> tables, UnitPreferences units, string? title = null)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var names = UniqueSheetNames(tables.Select(t => t.Name)).ToList();
            Entry(zip, "[Content_Types].xml", ContentTypes(tables.Count));
            Entry(zip, "_rels/.rels", """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties" Target="docProps/core.xml"/></Relationships>""");
            Entry(zip, "docProps/core.xml", $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><cp:coreProperties xmlns:cp="http://schemas.openxmlformats.org/package/2006/metadata/core-properties" xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:dcterms="http://purl.org/dc/terms/" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"><dc:title>{X(title ?? "Downhole Studio export")}</dc:title><dc:creator>Downhole Studio</dc:creator></cp:coreProperties>""");
            var sheets = new StringBuilder();
            var rels = new StringBuilder();
            for (var i = 0; i < tables.Count; i++)
            {
                sheets.Append($"""<sheet name="{X(names[i])}" sheetId="{i + 1}" r:id="rId{i + 1}"/>""");
                rels.Append($"""<Relationship Id="rId{i + 1}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet{i + 1}.xml"/>""");
                Entry(zip, $"xl/worksheets/sheet{i + 1}.xml", Sheet(tables[i], units));
            }

            rels.Append($"""<Relationship Id="rId{tables.Count + 1}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>""");
            Entry(zip, "xl/workbook.xml", $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets>{sheets}</sheets></workbook>""");
            Entry(zip, "xl/_rels/workbook.xml.rels", $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">{rels}</Relationships>""");
            Entry(zip, "xl/styles.xml", Styles);
        }

        return ms.ToArray();
    }

    // Style indexes: 0 default, 1 bold header, 2..7 numbers with 0..5 decimals, 8 date-time.
    private const string Styles = """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><numFmts count="7"><numFmt numFmtId="164" formatCode="0"/><numFmt numFmtId="165" formatCode="0.0"/><numFmt numFmtId="166" formatCode="0.00"/><numFmt numFmtId="167" formatCode="0.000"/><numFmt numFmtId="168" formatCode="0.0000"/><numFmt numFmtId="169" formatCode="0.00000"/><numFmt numFmtId="170" formatCode="yyyy-mm-dd hh:mm:ss"/></numFmts><fonts count="2"><font><sz val="10"/><name val="Calibri"/></font><font><b/><sz val="10"/><name val="Calibri"/></font></fonts><fills count="3"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill><fill><patternFill patternType="solid"><fgColor rgb="FFF1F3F6"/><bgColor indexed="64"/></patternFill></fill></fills><borders count="2"><border/><border><bottom style="thin"><color rgb="FFA3AAB8"/></bottom></border></borders><cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs><cellXfs count="9"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/><xf numFmtId="0" fontId="1" fillId="2" borderId="1" xfId="0" applyFont="1" applyFill="1" applyBorder="1"/><xf numFmtId="164" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/><xf numFmtId="165" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/><xf numFmtId="166" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/><xf numFmtId="167" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/><xf numFmtId="168" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/><xf numFmtId="169" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/><xf numFmtId="170" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/></cellXfs></styleSheet>""";

    private static string Sheet(ResultTable t, UnitPreferences units)
    {
        var sb = new StringBuilder();
        sb.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">""");
        sb.Append("""<sheetViews><sheetView workbookViewId="0"><pane ySplit="1" topLeftCell="A2" activePane="bottomLeft" state="frozen"/></sheetView></sheetViews><cols>""");
        for (var c = 0; c < t.Columns.Count; c++)
        {
            var width = Math.Clamp(Math.Max(t.Columns[c].HeaderWithUnit(units).Length + 2, t.Columns[c].Numeric ? 12 : 24), 8, 60);
            sb.Append($"""<col min="{c + 1}" max="{c + 1}" width="{width}" customWidth="1"/>""");
        }

        sb.Append("</cols><sheetData>");
        sb.Append("""<row r="1">""");
        for (var c = 0; c < t.Columns.Count; c++)
        {
            sb.Append($"""<c r="{Col(c)}1" t="inlineStr" s="1"><is><t>{X(t.Columns[c].HeaderWithUnit(units))}</t></is></c>""");
        }

        sb.Append("</row>");
        for (var r = 0; r < t.Rows.Count; r++)
        {
            var rowNo = r + 2;
            sb.Append($"""<row r="{rowNo}">""");
            for (var c = 0; c < t.Columns.Count; c++)
            {
                var cell = $"{Col(c)}{rowNo}";
                switch (t.DisplayValue(r, c, units))
                {
                    case null:
                        break;
                    case double d when double.IsFinite(d):
                        sb.Append($"""<c r="{cell}" s="{2 + Math.Clamp(t.Columns[c].Decimals, 0, 5)}"><v>{d.ToString("R", CultureInfo.InvariantCulture)}</v></c>""");
                        break;
                    case double:
                        break;
                    case bool b:
                        sb.Append($"""<c r="{cell}" t="inlineStr"><is><t>{(b ? "yes" : "no")}</t></is></c>""");
                        break;
                    case DateTimeOffset dto:
                        sb.Append($"""<c r="{cell}" s="8"><v>{dto.UtcDateTime.ToOADate().ToString("R", CultureInfo.InvariantCulture)}</v></c>""");
                        break;
                    case var v:
                        sb.Append($"""<c r="{cell}" t="inlineStr"><is><t xml:space="preserve">{X(v.ToString() ?? "")}</t></is></c>""");
                        break;
                }
            }

            sb.Append("</row>");
        }

        sb.Append("</sheetData></worksheet>");
        return sb.ToString();
    }

    private static string ContentTypes(int sheets)
    {
        var sb = new StringBuilder("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/><Override PartName="/docProps/core.xml" ContentType="application/vnd.openxmlformats-package.core-properties+xml"/>""");
        for (var i = 1; i <= sheets; i++)
        {
            sb.Append($"""<Override PartName="/xl/worksheets/sheet{i}.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>""");
        }

        return sb.Append("</Types>").ToString();
    }

    private static IEnumerable<string> UniqueSheetNames(IEnumerable<string> names)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in names)
        {
            var clean = new string(n.Where(ch => !"[]:*?/\\".Contains(ch)).ToArray()).Trim();
            clean = clean.Length == 0 ? "Sheet" : clean[..Math.Min(31, clean.Length)];
            var candidate = clean;
            for (var i = 2; !used.Add(candidate); i++)
            {
                var suffix = $" ({i})";
                candidate = clean[..Math.Min(31 - suffix.Length, clean.Length)] + suffix;
            }

            yield return candidate;
        }
    }

    private static string Col(int index)
    {
        var s = "";
        index++;
        while (index > 0)
        {
            var m = (index - 1) % 26;
            s = (char)('A' + m) + s;
            index = (index - 1) / 26;
        }

        return s;
    }

    private static string X(string s) => SecurityElement.Escape(new string(s.Where(ch => ch is '\t' or '\n' or '\r' || ch >= ' ').ToArray())) ?? "";

    private static void Entry(ZipArchive zip, string name, string content)
    {
        var e = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
        w.Write(content);
    }
}
