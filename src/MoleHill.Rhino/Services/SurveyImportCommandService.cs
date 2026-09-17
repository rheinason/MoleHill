using MoleHill.Rhino.UI;
using Rhino;
using Rhino.Commands;

namespace MoleHill.Rhino.Services;

/// <summary>
/// Command entry points for survey point import and its field code table.
///
/// The import itself lands in a later phase; this holds the editor route so <c>mhEditFieldCodes</c> is
/// usable — and testable — before the importer exists. A user can write the office's codes first and
/// import second, which is also the order the work actually happens in.
/// </summary>
internal static class SurveyImportCommandService
{
    public static Result RunEditFieldCodes(RhinoDoc doc)
    {
        bool saved = FieldCodeTableEditorDialog.ShowDialog(doc, MoleHillRhinoPlugin.Instance.FieldCodeTableStore);
        return saved ? Result.Success : Result.Cancel;
    }
}
