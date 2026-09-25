# Interop

Pure, host-independent exchange algorithms and formats.

- `TinSurfaceData.cs` and `LandXmlCodec.cs` provide unit-aware LandXML TIN import/export data and XML
  encoding. Coordinate-system reprojection remains outside Core.
- `SurveyPoint.cs`, `SurveyColumnMap.cs`, `SurveyReadOptions.cs`, `SurveyReadDiagnostic.cs`,
  `SurveyPointFile.cs` and `SurveyPointFileReader.cs` read delimited survey point files (PNEZD / PENZD /
  ENZ / NEZ / XYZ, or a hand-set column map) into coordinates plus an untouched description string.
  **`SurveyColumnMap` names its columns `EastingColumn` / `NorthingColumn`, never X / Y, on purpose:**
  PNEZD puts northing before easting, so a map read as XYZ produces a terrain transposed about the 45°
  line that parses cleanly and that nothing downstream can detect. Unit scaling and the per-delivery
  vertical-datum offset are applied here; the description is left whole for the field-code layer to
  split. Coordinates parse invariant-culture **without thousands separators**, so a comma-decimal
  `512345,67` fails the row with a diagnostic instead of reading as `51234567`. See `docs/survey-field-codes-plan.md`.
- `FieldCodeRole.cs`, `FieldCodeRule.cs`, `FieldCodeTable.cs`, `ParsedFieldCode.cs`, `FieldCodeParser.cs`,
  `SurveyFigure.cs`, `SurveyFigureBuilder.cs` and `SurveyImportResult.cs` turn those descriptions into
  linework. `FieldCodeParser` splits a description into code, figure number and markers; marker spellings
  (`ST`/`END`/`AR`/`CL`) live on the table as **editable data**, since hard-coding them would work for one
  office and fail silently for the next. A full stop separates markers (`EP.ST`) but not inside a decimal,
  so `TOE 1.5` is not figure 1. `SurveyFigureBuilder` keeps one open run per code and closes it
  on a marker, a reopening start, or end of file; the continuation suffix (`EP-`) reopens the most
  recently closed run of its key — **walking the points in file order, never sorted by
  point number**, because a merged file renumbers and sorting would zigzag a kerb line. Nothing is
  dropped: an unrecognised code is counted in `SurveyImportResult.UnmatchedCodes` and its points kept, a
  one-point run degrades to a spot level (on the Spot role's layer; each Spot rule's points go to that
  rule's own layer, in `SurveyImportResult.SpotLayers`), and `FieldCodeRole.Ignore` is a decision distinct from an
  absent rule. Figures hold indices into the point list plus parallel arc flags; fitting the arcs is the
  Rhino host's job.
- `ToposolidPointReducer.cs` performs deterministic, adaptive Toposolid elevation-point reduction. It
  preserves the caller's boundary/breakline-critical samples, seeds the approximation with spatial cell
  elevation extrema, triangulates in Core, measures source-vertex vertical error, and iteratively inserts
  the largest errors up to the requested point budget.

Revit API calls do not belong here. The Grasshopper host validates/converts Rhino curves and meshes around
this pure reducer; optional Python adapters under `examples/RhinoInside.Revit/` own the small Revit
transaction boundary.
