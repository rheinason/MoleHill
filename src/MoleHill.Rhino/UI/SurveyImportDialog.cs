using System.Collections.ObjectModel;
using System.Globalization;
using Eto.Drawing;
using Eto.Forms;
using MoleHill.Core.Interop;
using MoleHill.Shared;
using Rhino;
using Rhino.UI;

namespace MoleHill.Rhino.UI;

/// <summary>
/// Collects how to read a survey point file, and shows what that reading actually produces.
///
/// <b>The preview grid is the point of this dialog, not decoration.</b> PNEZD writes northing before
/// easting, so a file read as XYZ parses perfectly and yields a terrain transposed about the 45° line —
/// and no later stage, and no automated test, can tell the two apart. A format dropdown alone would
/// leave the user guessing; a grid showing resolved X, Y, Z and code for the first rows lets them see
/// easting in the X column before committing.
/// </summary>
internal sealed class SurveyImportDialog : Dialog<bool>
{
    private const int PreviewRows = 20;

    private readonly string _path;
    private readonly string _content;
    private readonly ModelUnitContext _units;

    private readonly DropDown _preset = new();
    private readonly DropDown _delimiter = new();
    private readonly DropDown _fileUnit = new();
    private readonly NumericStepper _headerRows = new() { MinValue = 0, MaxValue = 50, DecimalPlaces = 0, Width = UiMetrics.Chs(7) };
    private readonly NumericStepper _verticalOffset = new() { DecimalPlaces = 3, MinValue = -100000, MaxValue = 100000, Width = UiMetrics.Chs(11) };

    private readonly GridView _previewGrid = new() { ShowHeader = true, Height = 260 };
    private readonly ObservableCollection<PreviewRow> _previewRows = new();
    private readonly Label _summary = new() { TextColor = UiTheme.MutedText, Wrap = WrapMode.Word };

    private bool _isRefreshing;

    /// <summary>The options the user settled on. Only meaningful when the dialog returned true.</summary>
    public SurveyReadOptions Options { get; private set; } = new();

    private SurveyImportDialog(string path, string content, ModelUnitContext units)
    {
        _path = path;
        _content = content;
        _units = units;

        Title = "Import Survey Points";
        Resizable = true;
        Padding = 12;
        MinimumSize = new Size(760, 600);
        this.UseRhinoStyle();

        Content = BuildLayout();
        LoadChoices();
        Refresh();
    }

    public static bool ShowDialog(
        RhinoDoc doc,
        string path,
        string content,
        ModelUnitContext units,
        out SurveyReadOptions options)
    {
        var dialog = new SurveyImportDialog(path, content, units);
        bool accepted = dialog.ShowModal(RhinoEtoApp.MainWindowForDocument(doc));
        options = dialog.Options;
        return accepted;
    }

    // ── Layout ───────────────────────────────────────────────────────────────

    private Control BuildLayout()
    {
        BuildPreviewGrid();

        var importButton = new Button { Text = "Import" };
        importButton.Click += (_, _) => Commit();

        var cancelButton = new Button { Text = "Cancel" };
        cancelButton.Click += (_, _) => Close(false);

        DefaultButton = importButton;
        AbortButton = cancelButton;

        var layout = new DynamicLayout { Spacing = new Size(8, 8) };

        layout.AddRow(new Label { Text = Path.GetFileName(_path), Font = SystemFonts.Bold() });

        layout.AddRow(new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalContentAlignment = VerticalAlignment.Center,
            Items =
            {
                new Label { Text = "Format" },
                _preset,
                new Label { Text = "Separator" },
                _delimiter,
                new Label { Text = "Header rows" },
                _headerRows
            }
        });

        layout.AddRow(new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalContentAlignment = VerticalAlignment.Center,
            Items =
            {
                new Label { Text = "File units" },
                _fileUnit,
                new Label { Text = $"Vertical offset ({_units.Abbreviation})" },
                _verticalOffset
            }
        });

        layout.AddRow(new Label
        {
            Text = "The vertical offset reconciles a delivered datum with this document's. It belongs to "
                + "this file, not to the site, so it is not saved — two surveys on two datums can land in "
                + "one document.",
            TextColor = UiTheme.MutedText,
            Wrap = WrapMode.Word
        });

        layout.AddRow(new Label { Text = "Preview", Font = SystemFonts.Bold() });
        layout.AddRow(new Label
        {
            Text = "Check that easting landed in X and northing in Y. PNEZD writes them the other way "
                + "round, and a file read the wrong way still parses.",
            TextColor = UiTheme.MutedText,
            Wrap = WrapMode.Word
        });

        layout.Add(_previewGrid, yscale: true);
        layout.AddRow(_summary);

        layout.AddRow(new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Items = { null, importButton, cancelButton }
        });

        return layout;
    }

    private void BuildPreviewGrid()
    {
        _previewGrid.DataStore = _previewRows;
        AddColumn("Line", static r => r.Line);
        AddColumn("Number", static r => r.Number);
        AddColumn($"X ({_units.Abbreviation})", static r => r.X);
        AddColumn($"Y ({_units.Abbreviation})", static r => r.Y);
        AddColumn($"Z ({_units.Abbreviation})", static r => r.Z);
        AddColumn("Description", static r => r.Description, expand: true);
    }

    private void AddColumn(string heading, Func<PreviewRow, string> read, bool expand = false) =>
        _previewGrid.Columns.Add(new GridColumn
        {
            HeaderText = heading,
            Expand = expand,
            DataCell = new TextBoxCell { Binding = Binding.Delegate(read) }
        });

    private void LoadChoices()
    {
        _isRefreshing = true;
        try
        {
            foreach (string name in SurveyColumnMap.PresetNames)
                _preset.Items.Add(name);
            _preset.SelectedIndex = 0;

            foreach (string name in DelimiterNames)
                _delimiter.Items.Add(name);
            _delimiter.SelectedIndex = 0;

            foreach (string name in FileUnitNames)
                _fileUnit.Items.Add(name);
            _fileUnit.SelectedIndex = 0;
        }
        finally
        {
            _isRefreshing = false;
        }

        _preset.SelectedIndexChanged += (_, _) => Refresh();
        _delimiter.SelectedIndexChanged += (_, _) => Refresh();
        _fileUnit.SelectedIndexChanged += (_, _) => Refresh();
        _headerRows.ValueChanged += (_, _) => Refresh();
        _verticalOffset.ValueChanged += (_, _) => Refresh();
    }

    // ── State ────────────────────────────────────────────────────────────────

    private SurveyReadOptions Collect(int maxPoints)
    {
        SurveyColumnMap map =
            SurveyColumnMap.FromPreset(_preset.SelectedKey ?? "PNEZD") ?? SurveyColumnMap.FromPreset("PNEZD")!;

        return new SurveyReadOptions
        {
            ColumnMap = map,
            Delimiter = SelectedDelimiter(),
            HeaderRowCount = (int)_headerRows.Value,
            UnitScale = SelectedUnitScale(),
            VerticalOffset = _verticalOffset.Value,
            MaxPoints = maxPoints
        };
    }

    private void Refresh()
    {
        if (_isRefreshing)
            return;

        _isRefreshing = true;
        try
        {
            SurveyPointFile file = SurveyPointFileReader.Read(_content, Collect(PreviewRows));

            _previewRows.Clear();
            foreach (SurveyPoint point in file.Points)
                _previewRows.Add(PreviewRow.From(point));

            _summary.Text = DescribeRead(file);
        }
        catch (Exception ex)
        {
            _previewRows.Clear();
            _summary.Text = $"Could not read this file: {ex.Message}";
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private string DescribeRead(SurveyPointFile file)
    {
        if (file.Points.Count == 0)
        {
            return file.Diagnostics.Count > 0
                ? $"No rows mapped. First problem: line {file.Diagnostics[0].LineNumber} — {file.Diagnostics[0].Message}"
                : "No rows mapped. Check the format and separator.";
        }

        string text = $"Showing the first {file.Points.Count} rows, separated by {DescribeDelimiter(file.Delimiter)}.";
        if (file.Diagnostics.Count > 0)
            text += $" {file.Diagnostics.Count} of them could not be read — line {file.Diagnostics[0].LineNumber} is the first.";
        return text;
    }

    private void Commit()
    {
        SurveyPointFile check = SurveyPointFileReader.Read(_content, Collect(PreviewRows));
        if (check.Points.Count == 0)
        {
            _summary.Text = "Nothing would be imported with these settings.";
            return;
        }

        // The preview is capped; the import is not.
        Options = Collect(0);
        Close(true);
    }

    // ── Choices ──────────────────────────────────────────────────────────────

    private static readonly string[] DelimiterNames = { "Detect", "Comma", "Tab", "Semicolon", "Space" };

    private char? SelectedDelimiter() => _delimiter.SelectedIndex switch
    {
        1 => ',',
        2 => '\t',
        3 => ';',
        4 => ' ',
        _ => null
    };

    private static string DescribeDelimiter(char delimiter) => delimiter switch
    {
        ',' => "commas",
        '\t' => "tabs",
        ';' => "semicolons",
        ' ' => "spaces",
        _ => $"'{delimiter}'"
    };

    private static readonly string[] FileUnitNames =
    {
        "Same as document", "Metres", "Feet (international)", "Feet (US survey)"
    };

    /// <summary>
    /// Multiplier from file units into model units.
    ///
    /// The US survey foot differs from the international foot in the twelfth significant figure, which
    /// is nothing on one point and about 30 mm across a 10 km state-plane easting — so the two are
    /// offered separately rather than rounded together.
    /// </summary>
    private double SelectedUnitScale() => _fileUnit.SelectedIndex switch
    {
        1 => _units.FromMeters(1.0),
        2 => _units.FromMeters(0.3048),
        3 => _units.FromMeters(1200.0 / 3937.0),
        _ => 1.0
    };

    private sealed class PreviewRow
    {
        public string Line { get; init; } = string.Empty;

        public string Number { get; init; } = string.Empty;

        public string X { get; init; } = string.Empty;

        public string Y { get; init; } = string.Empty;

        public string Z { get; init; } = string.Empty;

        public string Description { get; init; } = string.Empty;

        public static PreviewRow From(SurveyPoint point) => new()
        {
            Line = point.LineNumber.ToString(CultureInfo.InvariantCulture),
            Number = point.Number?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            X = point.X.ToString("F3", CultureInfo.InvariantCulture),
            Y = point.Y.ToString("F3", CultureInfo.InvariantCulture),
            Z = point.Z.ToString("F3", CultureInfo.InvariantCulture),
            Description = point.RawDescription
        };
    }
}
