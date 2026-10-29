using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using DocumentFormat.OpenXml.Packaging;
using UglyToad.PdfPig;
using A = DocumentFormat.OpenXml.Drawing;
using S = DocumentFormat.OpenXml.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Thokolosi.Core.Tools;

/// <summary>Extracts plain text from PDF, Word, Excel, PowerPoint and plain-text documents.</summary>
public class DocumentTool : ITool
{
    public string Name => "read_document";

    public JsonObject Definition { get; } = Json.Def("read_document",
        "Extract text from a document: .pdf, .docx, .xlsx (rows tab-separated, per sheet), .pptx (per slide), .csv/.txt/.md/.json. " +
        "Use 'range' to limit pages (pdf), slides (pptx) or sheets (xlsx, by number or name).",
        """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Absolute path. Environment variables are expanded." },
            "range": { "type": "string", "description": "Optional. Pages/slides like '1-3,7', or sheet numbers/names for xlsx like '1' or 'Invoices'." },
            "max_chars": { "type": "integer", "description": "Maximum characters to return (default 40000, max 150000)." }
          },
          "required": ["path"]
        }
        """);

    public bool NeedsApproval(JsonObject input) => false;
    public string Describe(JsonObject input) => "Reading document " + input.Str("path");

    public Task<ToolResult> RunAsync(JsonObject input, ToolContext ctx, CancellationToken ct) => Task.Run(() =>
    {
        var path = Environment.ExpandEnvironmentVariables(input.Str("path"));
        if (!File.Exists(path)) return ToolResult.Error($"File not found: {path}");
        int max = Math.Clamp(input.Int("max_chars", 40000), 500, 150000);
        var range = input.Str("range").Trim();
        try
        {
            var text = Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".pdf" => Pdf(path, range, max, ct),
                ".docx" or ".docm" => Docx(path),
                ".xlsx" or ".xlsm" => Xlsx(path, range, max, ct),
                ".pptx" or ".pptm" => Pptx(path, range),
                ".csv" or ".txt" or ".md" or ".json" or ".tsv" or ".log" or ".xml" or ".yaml" or ".yml" => File.ReadAllText(path),
                var ext => throw new NotSupportedException($"Unsupported file type '{ext}'. Supported: pdf, docx, xlsx, pptx, csv, txt, md, json.")
            };
            return ToolResult.Text(Json.Truncate($"{path}\n{text.TrimEnd()}", max));
        }
        catch (NotSupportedException ex) { return ToolResult.Error(ex.Message); }
        catch (IOException ex) { return ToolResult.Error($"Could not open the file (is it open in another app?): {ex.Message}"); }
        catch (Exception ex) { return ToolResult.Error($"Could not read {Path.GetFileName(path)}: {ex.Message}"); }
    }, ct);

    /// <summary>Parses '1-3,7' into a predicate over 1-based numbers. Empty = everything.</summary>
    static Func<int, bool> NumberFilter(string range)
    {
        if (string.IsNullOrWhiteSpace(range)) return _ => true;
        var spans = new List<(int From, int To)>();
        foreach (var part in range.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var bits = part.Split('-', 2, StringSplitOptions.TrimEntries);
            if (!int.TryParse(bits[0], out var a)) continue;
            int b = bits.Length == 2 ? (int.TryParse(bits[1], out var x) ? x : int.MaxValue) : a;
            spans.Add((a, b));
        }
        return spans.Count == 0 ? _ => true : n => spans.Any(s => n >= s.From && n <= s.To);
    }

    static string Pdf(string path, string range, int max, CancellationToken ct)
    {
        using var doc = PdfDocument.Open(path);
        var want = NumberFilter(range);
        var sb = new StringBuilder();
        sb.AppendLine($"PDF, {doc.NumberOfPages} pages");
        int withText = 0, read = 0;
        for (int i = 1; i <= doc.NumberOfPages && sb.Length < max; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (!want(i)) continue;
            read++;
            var page = doc.GetPage(i);
            string t;
            try { t = UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor.ContentOrderTextExtractor.GetText(page); }
            catch { t = page.Text; }
            if (!string.IsNullOrWhiteSpace(t)) withText++;
            sb.AppendLine($"\n--- page {i} ---");
            sb.AppendLine(string.IsNullOrWhiteSpace(t) ? "(no text on this page)" : t.Trim());
        }
        if (read > 0 && withText == 0)
            return $"PDF, {doc.NumberOfPages} pages: no text layer found. This looks like a scanned/image-only PDF; it needs OCR " +
                   "(e.g. open it as images with a screenshot/vision step).";
        return sb.ToString();
    }

    static string Docx(string path)
    {
        using var doc = WordprocessingDocument.Open(path, false);
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body == null) return "(empty document)";
        var sb = new StringBuilder();
        foreach (var el in body.Elements())
        {
            switch (el)
            {
                case W.Paragraph p:
                    var text = ParagraphText(p);
                    var style = p.ParagraphProperties?.ParagraphStyleId?.Val?.Value ?? "";
                    if (style.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) && int.TryParse(style[7..], out var lvl) && text.Length > 0)
                        sb.AppendLine(new string('#', Math.Clamp(lvl, 1, 6)) + " " + text);
                    else if (p.ParagraphProperties?.NumberingProperties != null && text.Length > 0)
                        sb.AppendLine("- " + text);
                    else
                        sb.AppendLine(text);
                    break;
                case W.Table t:
                    foreach (var row in t.Elements<W.TableRow>())
                        sb.AppendLine(string.Join("\t", row.Elements<W.TableCell>().Select(c =>
                            string.Join(" ", c.Elements<W.Paragraph>().Select(ParagraphText)).Trim())));
                    sb.AppendLine();
                    break;
            }
        }
        return sb.ToString();
    }

    static string ParagraphText(W.Paragraph p)
    {
        var sb = new StringBuilder();
        foreach (var node in p.Descendants())
        {
            switch (node)
            {
                case W.Text t: sb.Append(t.Text); break;
                case W.TabChar: sb.Append('\t'); break;
                case W.Break: sb.Append('\n'); break;
            }
        }
        return sb.ToString();
    }

    static string Xlsx(string path, string range, int max, CancellationToken ct)
    {
        using var doc = SpreadsheetDocument.Open(path, false);
        var wb = doc.WorkbookPart ?? throw new InvalidDataException("No workbook in file.");
        var shared = wb.SharedStringTablePart?.SharedStringTable?.Elements<S.SharedStringItem>().Select(s => s.InnerText).ToArray() ?? Array.Empty<string>();
        var sheets = wb.Workbook.Sheets?.Elements<S.Sheet>().ToList() ?? new();

        // Range parts are either sheet numbers ('1', '2-3') or sheet names ('Invoices').
        var parts = range.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var names = parts.Where(p => sheets.Any(s => string.Equals(s.Name?.Value, p, StringComparison.OrdinalIgnoreCase)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var numericParts = parts.Where(p => !names.Contains(p) && p.All(ch => char.IsDigit(ch) || ch == '-')).ToList();
        var numbers = numericParts.Count > 0 ? NumberFilter(string.Join(",", numericParts)) : _ => false;

        var sb = new StringBuilder();
        sb.AppendLine($"Workbook with {sheets.Count} sheets: {string.Join(", ", sheets.Select(s => s.Name?.Value))}");
        for (int i = 0; i < sheets.Count && sb.Length < max; i++)
        {
            var sheet = sheets[i];
            var name = sheet.Name?.Value ?? $"Sheet{i + 1}";
            bool selected = parts.Length == 0 || names.Contains(name) || numbers(i + 1);
            if (!selected) continue;
            if (sheet.Id?.Value is not string rid || wb.GetPartById(rid) is not WorksheetPart wsp) continue;

            sb.AppendLine($"\n=== sheet {i + 1}: {name} ===");
            int rows = 0;
            foreach (var row in wsp.Worksheet.Descendants<S.Row>())
            {
                ct.ThrowIfCancellationRequested();
                var cells = new List<string>();
                foreach (var c in row.Elements<S.Cell>())
                {
                    // Keep column positions: pad for skipped (empty) columns.
                    int col = ColumnIndex(c.CellReference?.Value);
                    while (col > 0 && cells.Count < col) cells.Add("");
                    cells.Add(CellText(c, shared));
                }
                while (cells.Count > 0 && cells[^1].Length == 0) cells.RemoveAt(cells.Count - 1);
                if (cells.Count == 0) continue;
                sb.Append(row.RowIndex?.Value ?? 0).Append(":\t").AppendLine(string.Join("\t", cells));
                rows++;
                if (sb.Length >= max) { sb.AppendLine("... (more rows)"); break; }
            }
            if (rows == 0) sb.AppendLine("(empty)");
        }
        return sb.ToString();
    }

    static int ColumnIndex(string? cellRef)
    {
        if (string.IsNullOrEmpty(cellRef)) return -1;
        int n = 0;
        foreach (var ch in cellRef)
        {
            if (!char.IsLetter(ch)) break;
            n = n * 26 + (char.ToUpperInvariant(ch) - 'A' + 1);
        }
        return n - 1;
    }

    static string CellText(S.Cell c, string[] shared)
    {
        var raw = c.CellValue?.Text ?? c.InlineString?.InnerText ?? "";
        var type = c.DataType?.Value;
        if (type == S.CellValues.SharedString && int.TryParse(raw, out var idx) && idx >= 0 && idx < shared.Length) return Clean(shared[idx]);
        if (type == S.CellValues.Boolean) return raw == "1" ? "TRUE" : "FALSE";
        return Clean(raw);
    }

    static string Clean(string s) => s.Replace('\t', ' ').Replace("\r", "").Replace('\n', ' ');

    static string Pptx(string path, string range)
    {
        using var doc = PresentationDocument.Open(path, false);
        var pres = doc.PresentationPart ?? throw new InvalidDataException("No presentation in file.");
        var ids = pres.Presentation?.SlideIdList?.Elements<DocumentFormat.OpenXml.Presentation.SlideId>().ToList() ?? new();
        var want = NumberFilter(range);
        var sb = new StringBuilder();
        sb.AppendLine($"Presentation, {ids.Count} slides");
        for (int i = 0; i < ids.Count; i++)
        {
            if (!want(i + 1)) continue;
            if (ids[i].RelationshipId?.Value is not string rid || pres.GetPartById(rid) is not SlidePart slide) continue;
            sb.AppendLine($"\n--- slide {i + 1} ---");
            foreach (var para in slide.Slide.Descendants<A.Paragraph>())
            {
                var t = string.Concat(para.Descendants<A.Text>().Select(x => x.Text)).Trim();
                if (t.Length > 0) sb.AppendLine(t);
            }
            var notes = slide.NotesSlidePart?.NotesSlide?.Descendants<A.Paragraph>()
                .Select(p => string.Concat(p.Descendants<A.Text>().Select(x => x.Text)).Trim())
                .Where(t => t.Length > 0 && !int.TryParse(t, out _)).ToList();
            if (notes is { Count: > 0 }) sb.AppendLine("[notes] " + string.Join(" ", notes));
        }
        return sb.ToString();
    }
}
