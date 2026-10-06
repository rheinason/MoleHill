# Reporting

The assembled form of a terrain report, and the CSV renderer over it. Pure text and numbers: nothing
here knows about Rhino, terrains, or how a figure was measured.

| File | What it is |
|------|------------|
| `ReportColumn.cs` | A column: heading, unit, alignment. The unit lives on the column so cells stay plain numbers. |
| `ReportTable.cs` | A titled block: columns and already-formatted rows. |
| `ReportDocument.cs` | The whole report: a title and its tables, in reading order. |
| `CsvWriter.cs` | RFC 4180 rendering of a `ReportDocument`, invariant-culture numbers. |

**Why cells are strings.** Rounding is a presentation decision, and it is the same decision in a
spreadsheet as on a drawing. Settling it once where the report is assembled
(`MoleHill.Rhino/Services/Annotation/TerrainReportBuilder.cs`) is what keeps the exported CSV and the drawn
summary table from reporting the same quantity differently.

**Why units are on the column.** A cell reading `1250.00 m²` cannot be summed by a spreadsheet; a bare
`1250` says nothing. `Plan Area (m²)` as a heading solves both.
