using System.Collections.ObjectModel;
using Eto.Drawing;
using Eto.Forms;
using MoleHill.Core.Interop;
using MoleHill.Rhino.Services;
using Rhino;
using Rhino.UI;
using OpenFileDialog = Eto.Forms.OpenFileDialog;   // Rhino.UI declares its own; this dialog is Eto throughout.
using SaveFileDialog = Eto.Forms.SaveFileDialog;

namespace MoleHill.Rhino.UI;

/// <summary>
/// Editor for the per-user field code table: one grid of code rules, plus the marker spellings the
/// office's crews actually type.
///
/// The marker fields are the part that is easy to leave out and expensive to omit. Hard-coding "ST" and
/// "END" would work for one office and fail silently for the next — the codes would still parse and the
/// runs would simply never close, which reads as a convention nobody used rather than as a setting
/// nobody changed.
///
/// Edits are made against a clone and only written on Save, so Cancel is genuinely a cancel.
/// </summary>
internal sealed class FieldCodeTableEditorDialog : Dialog<bool>
{
    private readonly FieldCodeTableStore _store;
    private FieldCodeTable _table;

    private readonly GridView _grid = new() { ShowHeader = true, Height = 300 };
    private readonly ObservableCollection<RuleRow> _rows = new();

    private readonly TextBox _startTokens = new();
    private readonly TextBox _endTokens = new();
    private readonly TextBox _arcTokens = new();
    private readonly TextBox _closeTokens = new();
    private readonly TextBox _continuation = new() { Width = UiMetrics.Chs(8) };
    private readonly TextBox _unmatchedLayer = new();
    private readonly Label _status = new() { TextColor = UiTheme.MutedText, Wrap = WrapMode.Word };

    /// <summary>Guards the handlers while the controls are being filled from the model.</summary>
    private bool _isRefreshing;

    private FieldCodeTableEditorDialog(FieldCodeTableStore store)
    {
        _store = store;
        _table = store.Load().Clone();

        Title = "MoleHill Field Codes";
        Resizable = true;
        Padding = 12;
        MinimumSize = new Size(720, 560);
        this.UseRhinoStyle();

        Content = BuildLayout();
        Refresh();
    }

    public static bool ShowDialog(RhinoDoc doc, FieldCodeTableStore store)
    {
        var dialog = new FieldCodeTableEditorDialog(store);
        return dialog.ShowModal(RhinoEtoApp.MainWindowForDocument(doc));
    }

    // ── Layout ───────────────────────────────────────────────────────────────

    private Control BuildLayout()
    {
        BuildGrid();

        var addButton = new Button { Text = "Add" };
        addButton.Click += (_, _) => AddRule();

        var removeButton = new Button { Text = "Remove" };
        removeButton.Click += (_, _) => RemoveSelectedRule();

        var defaultsButton = new Button { Text = "Restore Defaults" };
        defaultsButton.Click += (_, _) => RestoreDefaults();

        var importButton = new Button { Text = "Import…" };
        importButton.Click += (_, _) => ImportTable();

        var exportButton = new Button { Text = "Export…" };
        exportButton.Click += (_, _) => ExportTable();

        var saveButton = new Button { Text = "Save" };
        saveButton.Click += (_, _) => Commit();

        var cancelButton = new Button { Text = "Cancel" };
        cancelButton.Click += (_, _) => Close(false);

        DefaultButton = saveButton;
        AbortButton = cancelButton;

        var layout = new DynamicLayout { Spacing = new Size(8, 8) };

        layout.AddRow(new Label
        {
            Text = "A rule says what a surveyed code means. Leave Layer blank to follow the role's own "
                + "input layer, which is what lets one layer assignment pick up the whole survey.",
            Wrap = WrapMode.Word
        });

        layout.Add(_grid, yscale: true);

        layout.AddRow(new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Items = { addButton, removeButton, null, defaultsButton, importButton, exportButton }
        });

        layout.AddRow(new Label { Text = "Markers", Font = SystemFonts.Bold() });
        layout.AddRow(new Label
        {
            Text = "How this office marks a run. Several spellings per box, separated by commas.",
            TextColor = UiTheme.MutedText,
            Wrap = WrapMode.Word
        });

        layout.AddRow(MarkerRow("Start", _startTokens, "End", _endTokens));
        layout.AddRow(MarkerRow("Arc", _arcTokens, "Close", _closeTokens));

        layout.AddRow(new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalContentAlignment = VerticalAlignment.Center,
            Items =
            {
                new Label { Text = "Continues a run", Width = UiMetrics.LabelColumn },
                _continuation,
                new Label { Text = "Unmatched codes go to", Width = UiMetrics.Chs(20) },
                new StackLayoutItem(_unmatchedLayer, expand: true)
            }
        });

        layout.AddRow(_status);

        layout.AddRow(new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Items = { null, saveButton, cancelButton }
        });

        return layout;
    }

    private static Control MarkerRow(string firstLabel, TextBox first, string secondLabel, TextBox second) =>
        new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalContentAlignment = VerticalAlignment.Center,
            Items =
            {
                new Label { Text = firstLabel, Width = UiMetrics.LabelColumn },
                new StackLayoutItem(first, expand: true),
                new Label { Text = secondLabel, Width = UiMetrics.ShortLabel },
                new StackLayoutItem(second, expand: true)
            }
        };

    private void BuildGrid()
    {
        _grid.DataStore = _rows;

        _grid.Columns.Add(new GridColumn
        {
            HeaderText = "Code",
            Editable = true,
            Width = UiMetrics.Chs(10),
            DataCell = new TextBoxCell
            {
                Binding = Binding.Delegate<RuleRow, string>(r => r.Code, (r, v) => r.Code = v ?? string.Empty)
            }
        });

        _grid.Columns.Add(new GridColumn
        {
            HeaderText = "Role",
            Editable = true,
            Width = UiMetrics.Chs(14),
            DataCell = new ComboBoxCell
            {
                DataStore = RuleRow.RoleNames,
                Binding = Binding.Delegate<RuleRow, object>(r => r.RoleName, (r, v) => r.RoleName = v as string ?? r.RoleName)
            }
        });

        _grid.Columns.Add(new GridColumn
        {
            HeaderText = "Layer",
            Editable = true,
            Expand = true,
            DataCell = new TextBoxCell
            {
                Binding = Binding.Delegate<RuleRow, string>(r => r.Layer, (r, v) => r.Layer = v ?? string.Empty)
            }
        });

        _grid.Columns.Add(new GridColumn
        {
            HeaderText = "Closed",
            Editable = true,
            DataCell = new CheckBoxCell
            {
                Binding = Binding.Delegate<RuleRow, bool?>(r => r.Closed, (r, v) => r.Closed = v ?? false)
            }
        });

        _grid.Columns.Add(new GridColumn
        {
            HeaderText = "Note",
            Editable = true,
            Expand = true,
            DataCell = new TextBoxCell
            {
                Binding = Binding.Delegate<RuleRow, string>(r => r.Note, (r, v) => r.Note = v ?? string.Empty)
            }
        });
    }

    // ── State ────────────────────────────────────────────────────────────────

    /// <summary>The single place the controls are filled from <see cref="_table"/>.</summary>
    private void Refresh()
    {
        _isRefreshing = true;
        try
        {
            _rows.Clear();
            foreach (FieldCodeRule rule in _table.Rules)
                _rows.Add(RuleRow.From(rule));

            _startTokens.Text = string.Join(", ", _table.StartTokens);
            _endTokens.Text = string.Join(", ", _table.EndTokens);
            _arcTokens.Text = string.Join(", ", _table.ArcTokens);
            _closeTokens.Text = string.Join(", ", _table.CloseTokens);
            _continuation.Text = _table.ContinuationSuffix;
            _unmatchedLayer.Text = _table.UnmatchedLayer;
            _status.Text = $"{_rows.Count} codes. Saved to {_store.GetStorePath()}";
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    /// <summary>Reads every control back into a table. Normalization is the store's job, not the grid's.</summary>
    private FieldCodeTable Collect()
    {
        var table = new FieldCodeTable
        {
            StartTokens = SplitTokens(_startTokens.Text),
            EndTokens = SplitTokens(_endTokens.Text),
            ArcTokens = SplitTokens(_arcTokens.Text),
            CloseTokens = SplitTokens(_closeTokens.Text),
            ContinuationSuffix = _continuation.Text?.Trim() ?? string.Empty,
            UnmatchedLayer = _unmatchedLayer.Text?.Trim() ?? string.Empty,
            Rules = _rows.Select(static row => row.ToRule()).ToList()
        };

        return FieldCodeTableStore.Normalize(table);
    }

    private void AddRule()
    {
        _rows.Add(new RuleRow { Code = "NEW", RoleName = FieldCodeRole.Breakline.ToString() });
        _grid.SelectRow(_rows.Count - 1);
        _status.Text = "Added a code. Give it the spelling the crew types.";
    }

    private void RemoveSelectedRule()
    {
        int index = _grid.SelectedRow;
        if (index < 0 || index >= _rows.Count)
        {
            _status.Text = "Select a code to remove.";
            return;
        }

        string code = _rows[index].Code;
        _rows.RemoveAt(index);
        _status.Text = $"Removed '{code}'.";
    }

    private void RestoreDefaults()
    {
        if (!Confirm("Replace every code with the shipped defaults?"))
            return;

        _table = _store.GetDefaultTable();
        Refresh();
        _status.Text = "Restored the shipped codes.";
    }

    private void ImportTable()
    {
        var dialog = new OpenFileDialog { Title = "Import Field Codes" };
        dialog.Filters.Add(new FileFilter("Field Codes", ".json"));
        if (dialog.ShowDialog(this) != DialogResult.Ok || string.IsNullOrWhiteSpace(dialog.FileName))
            return;

        if (!FieldCodeTableStore.Import(dialog.FileName, out FieldCodeTable? imported) || imported == null)
        {
            _status.Text = "That file is not a field code table.";
            return;
        }

        _table = imported;
        Refresh();
        _status.Text = $"Imported {_rows.Count} codes from {Path.GetFileName(dialog.FileName)}.";
    }

    private void ExportTable()
    {
        var dialog = new SaveFileDialog { Title = "Export Field Codes", FileName = "field-codes.json" };
        dialog.Filters.Add(new FileFilter("Field Codes", ".json"));
        if (dialog.ShowDialog(this) != DialogResult.Ok || string.IsNullOrWhiteSpace(dialog.FileName))
            return;

        try
        {
            FieldCodeTableStore.Export(dialog.FileName, Collect());
            _status.Text = $"Exported to {Path.GetFileName(dialog.FileName)}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _status.Text = $"Could not write that file: {ex.Message}";
        }
    }

    private void Commit()
    {
        FieldCodeTable collected = Collect();
        if (collected.Rules.Count == 0 && !Confirm("Save a table with no codes? Every point will be reported as unmatched."))
            return;

        try
        {
            _store.Save(collected);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _status.Text = $"Could not save: {ex.Message}";
            return;
        }

        Close(true);
    }

    private bool Confirm(string question) =>
        MessageBox.Show(this, question, "MoleHill Field Codes", MessageBoxButtons.YesNo, MessageBoxType.Question)
            == DialogResult.Yes;

    private static List<string> SplitTokens(string? text) =>
        (text ?? string.Empty)
            .Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(static token => token.Trim())
            .Where(static token => token.Length > 0)
            .ToList();

    /// <summary>
    /// Grid-facing view of a rule. The role is carried as its name because a grid combo edits strings;
    /// the conversion back to the enum is one place, in <see cref="ToRule"/>.
    /// </summary>
    private sealed class RuleRow
    {
        public static readonly List<object> RoleNames =
            Enum.GetNames<FieldCodeRole>().Cast<object>().ToList();

        public string Code { get; set; } = string.Empty;

        public string RoleName { get; set; } = FieldCodeRole.Breakline.ToString();

        public string Layer { get; set; } = string.Empty;

        public bool Closed { get; set; }

        public string Note { get; set; } = string.Empty;

        public static RuleRow From(FieldCodeRule rule) => new()
        {
            Code = rule.Code,
            RoleName = rule.Role.ToString(),
            Layer = rule.Layer,
            Closed = rule.ClosedByDefault,
            Note = rule.Description
        };

        public FieldCodeRule ToRule() => new()
        {
            Code = Code,
            Role = Enum.TryParse(RoleName, out FieldCodeRole role) ? role : FieldCodeRole.Breakline,
            Layer = Layer,
            ClosedByDefault = Closed,
            Description = Note
        };
    }
}
