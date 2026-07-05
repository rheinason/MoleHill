using System;
using Eto.Forms;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Registry;
using Rhino;

namespace MoleHill.Rhino.UI;

// Schema-driven card bodies. A modifier type declares its inputs as an ordered ParameterDescriptor list
// (Registry side, Eto-free) and this builder turns each into the matching reusable editor primitive from
// MoleHillPanel.Editors.cs. Adding/changing a modifier's inputs is then data, not UI plumbing — and the
// same schema is the contract that will generate the Grasshopper component.
public sealed partial class MoleHillPanel
{
    /// <summary>
    /// Appends a row per declared parameter for <paramref name="modifier"/>'s type. Returns false when the
    /// type has no schema yet, so the caller can fall back to its hand-written card body.
    /// </summary>
    private bool TryBuildSchemaModifierBody(DynamicLayout layout, TerrainDefinition terrain, ModifierDefinition modifier)
    {
        var descriptor = TerrainTypeRegistry.ForModifierType(modifier.GetType());
        if (descriptor == null || descriptor.Parameters.Count == 0)
            return false;

        foreach (var parameter in descriptor.Parameters)
            layout.AddRow(BuildSchemaRow(terrain, modifier, parameter));

        return true;
    }

    private Control BuildSchemaRow(TerrainDefinition terrain, ModifierDefinition modifier, ParameterDescriptor parameter)
    {
        Guid terrainId = terrain.TerrainId;
        Guid modifierId = modifier.Id;

        switch (parameter.Kind)
        {
            case ParameterKind.Sources:
                return CreateSourceEditor(
                    parameter.Label,
                    parameter.GetSources!(modifier),
                    apply => MutateModifier(terrainId, modifierId, item => apply(parameter.GetSources!(item))),
                    parameter.ObjectFilter,
                    doc => _controller.GetSelectedLayerPaths(doc),
                    parameter.Help);

            case ParameterKind.Number:
                return CreateNumericEditor(
                    parameter.Label,
                    parameter.GetNumber!(modifier),
                    value => MutateModifier(terrainId, modifierId, item => parameter.SetNumber!(item, value)),
                    parameter.DecimalPlaces,
                    parameter.Help,
                    parameter.Min,
                    parameter.Max,
                    step: parameter.Step);

            case ParameterKind.OptionalNumber:
                return CreateOptionalNumericEditor(
                    parameter.Label,
                    parameter.GetNumber!(modifier),
                    parameter.InheritedValue!(modifier),
                    value => MutateModifier(terrainId, modifierId, item => parameter.SetNumber!(item, value)),
                    parameter.DecimalPlaces,
                    parameter.Help);

            case ParameterKind.Slider:
                return CreateSliderNumericEditor(
                    parameter.Label,
                    parameter.GetNumber!(modifier),
                    value => MutateModifier(
                        terrainId,
                        modifierId,
                        item => parameter.SetNumber!(item, value),
                        deferDocumentSave: parameter.LiveScrub,
                        suppressImmediateUiRefresh: parameter.LiveScrub),
                    parameter.SoftMin,
                    parameter.SoftMax,
                    parameter.DecimalPlaces,
                    parameter.Min,
                    parameter.Max,
                    parameter.Help);

            case ParameterKind.Bool:
                return CreateCheckEditor(
                    parameter.Label,
                    parameter.GetBool!(modifier),
                    value => MutateModifier(terrainId, modifierId, item => parameter.SetBool!(item, value)),
                    parameter.Help ?? string.Empty);

            case ParameterKind.Layer:
                return CreateLayerAssignmentEditor(
                    parameter.Label,
                    parameter.GetText!(modifier),
                    path => MutateModifier(terrainId, modifierId, item => parameter.SetText!(item, path)),
                    parameter.Help ?? string.Empty);

            case ParameterKind.ReadOnly:
                return CreateReadOnlyValueRow(
                    parameter.Label,
                    parameter.GetReadOnly!(modifier),
                    parameter.Help ?? string.Empty);

            case ParameterKind.Choice:
                return CreateDropDownEditor(
                    parameter.Label,
                    parameter.ChoiceOptions!,
                    parameter.GetText!(modifier) ?? string.Empty,
                    value => MutateModifier(terrainId, modifierId, item => parameter.SetText!(item, value)),
                    parameter.Help ?? string.Empty);

            case ParameterKind.Color:
                return CreateOptionalColorEditor(
                    parameter.Label,
                    parameter.GetColor!(modifier),
                    value => MutateModifier(terrainId, modifierId, item => parameter.SetColor!(item, value)),
                    parameter.Help ?? string.Empty,
                    parameter.FallbackColor?.Invoke(modifier),
                    parameter.ColorDefaultText);

            case ParameterKind.Text:
                return CreateCommittedTextEditor(
                    parameter.Label,
                    parameter.GetText!(modifier) ?? string.Empty,
                    value => MutateModifier(terrainId, modifierId, item => parameter.SetText!(item, value)),
                    parameter.Help ?? string.Empty,
                    parameter.TrimText);

            default:
                throw new ArgumentOutOfRangeException(nameof(parameter), parameter.Kind, "Unhandled parameter kind.");
        }
    }

    /// <summary>
    /// Appends a row per declared parameter for <paramref name="analysis"/>'s type. Returns false when the
    /// type has no schema yet, so the caller can fall back to its bespoke rows for that type.
    /// </summary>
    private bool TryBuildSchemaAnalysisBody(DynamicLayout layout, TerrainDefinition terrain, AnalysisDefinition analysis)
    {
        var descriptor = AnalysisTypeRegistry.ForType(analysis.GetType());
        if (descriptor == null || descriptor.Parameters.Count == 0)
            return false;

        foreach (var parameter in descriptor.Parameters)
            layout.AddRow(BuildAnalysisSchemaRow(terrain, analysis, parameter));

        return true;
    }

    private Control BuildAnalysisSchemaRow(TerrainDefinition terrain, AnalysisDefinition analysis, AnalysisParameterDescriptor parameter)
    {
        Guid terrainId = terrain.TerrainId;
        Guid analysisId = analysis.Id;
        string label = parameter.LabelFor?.Invoke(analysis) ?? parameter.Label;

        switch (parameter.Kind)
        {
            case ParameterKind.Sources:
                return CreateSourceEditor(
                    label,
                    parameter.GetSources!(analysis),
                    apply => CommitAnalysisMutation(parameter, terrainId, analysisId, item => apply(parameter.GetSources!(item))),
                    parameter.ObjectFilter,
                    doc => _controller.GetSelectedLayerPaths(doc),
                    parameter.Help);

            case ParameterKind.Number:
                return CreateNumericEditor(
                    label,
                    parameter.GetNumber!(analysis),
                    value => CommitAnalysisMutation(parameter, terrainId, analysisId, item => parameter.SetNumber!(item, value)),
                    parameter.DecimalPlaces,
                    parameter.Help,
                    parameter.Min,
                    parameter.Max,
                    step: parameter.Step);

            case ParameterKind.Bool:
                return CreateCheckEditor(
                    label,
                    parameter.GetBool!(analysis),
                    value => CommitAnalysisMutation(parameter, terrainId, analysisId, item => parameter.SetBool!(item, value)),
                    parameter.Help ?? string.Empty);

            case ParameterKind.Layer:
                return CreateLayerAssignmentEditor(
                    label,
                    parameter.GetText!(analysis),
                    path => CommitAnalysisMutation(parameter, terrainId, analysisId, item => parameter.SetText!(item, path)),
                    parameter.Help ?? string.Empty);

            case ParameterKind.Choice:
            {
                var options = parameter.ChoiceOptionsFor?.Invoke(analysis) ?? parameter.ChoiceOptions!;
                return CreateDropDownEditor(
                    label,
                    options,
                    parameter.GetText!(analysis) ?? string.Empty,
                    value => CommitAnalysisMutation(parameter, terrainId, analysisId, item => parameter.SetText!(item, value)),
                    parameter.Help ?? string.Empty);
            }

            case ParameterKind.Color:
                return CreateOptionalColorEditor(
                    label,
                    parameter.GetColor!(analysis),
                    value => CommitAnalysisMutation(parameter, terrainId, analysisId, item => parameter.SetColor!(item, value)),
                    parameter.Help ?? string.Empty,
                    parameter.FallbackColor?.Invoke(terrain, analysis),
                    parameter.ColorDefaultTextFor?.Invoke(terrain, analysis) ?? "(by layer)");

            case ParameterKind.Text:
                return CreateCommittedTextEditor(
                    label,
                    parameter.GetText!(analysis) ?? string.Empty,
                    value => CommitAnalysisMutation(parameter, terrainId, analysisId, item => parameter.SetText!(item, value)),
                    parameter.Help ?? string.Empty,
                    parameter.TrimText);

            case ParameterKind.ReadOnly:
                return CreateReadOnlyValueRow(
                    label,
                    parameter.GetReadOnly!(analysis),
                    parameter.Help ?? string.Empty);

            default:
                throw new ArgumentOutOfRangeException(nameof(parameter), parameter.Kind, "Unhandled parameter kind.");
        }
    }

    /// <summary>
    /// Commits an analysis-schema row's edit per its descriptor flags: <see cref="AnalysisParameterDescriptor.IncrementalCommit"/>
    /// (contour-style — skip the full rebuild and run the type's own incremental rebuild instead),
    /// <see cref="AnalysisParameterDescriptor.RefreshOnly"/> (cheap preview recolor, no rebuild), or the
    /// default full analysis rebuild.
    /// </summary>
    private void CommitAnalysisMutation(AnalysisParameterDescriptor parameter, Guid terrainId, Guid analysisId, Action<AnalysisDefinition> apply)
    {
        if (parameter.IncrementalCommit)
        {
            AnalysisDefinition? mutated = null;
            MutateAnalysis(terrainId, analysisId, item =>
            {
                apply(item);
                mutated = item;
            }, scheduleRebuild: false);

            var doc = RhinoDoc.ActiveDoc;
            if (doc == null || mutated == null)
                return;

            if (mutated is ContourAnalysisDefinition contour && parameter.Kind == ParameterKind.Color)
                _controller.RefreshContourColor(doc, terrainId, analysisId, contour.ColorArgb);
            else
                _controller.RebuildContourAnalysis(doc, terrainId, analysisId);
            return;
        }

        if (parameter.RefreshOnly)
        {
            MutateAndRefreshAnalysis(terrainId, analysisId, apply);
            return;
        }

        MutateAnalysis(terrainId, analysisId, apply, scheduleRebuild: true);
    }
}
