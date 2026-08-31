using System;
using System.Linq;
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
        {
            if (parameter.VisibleWhen != null && !parameter.VisibleWhen(modifier))
                continue;
            if (IsBespokePositionedModifierParameter(modifier, parameter))
                continue;

            layout.AddRow(BuildSchemaRow(terrain, modifier, parameter));
        }

        return true;
    }

    private static bool IsBespokePositionedModifierParameter(
        ModifierDefinition modifier,
        ParameterDescriptor parameter) =>
        (modifier is TriangulateModifierDefinition &&
         parameter.Key is "DemSurface" or "ContourMode") ||
        (modifier is GradePathModifierDefinition &&
         parameter.Key is "Paths" or "Width" or "UseVariableWidth" or "WidthEdges" or "MaxEdgeDistance");

    private Control? BuildBespokePositionedModifierRow(
        TerrainDefinition terrain,
        ModifierDefinition modifier,
        string parameterKey)
    {
        ModifierTypeDescriptor? descriptor = TerrainTypeRegistry.ForModifierType(modifier.GetType());
        ParameterDescriptor? parameter = descriptor?.Parameters.FirstOrDefault(
            item => string.Equals(item.Key, parameterKey, StringComparison.Ordinal));
        if (parameter == null || (parameter.VisibleWhen != null && !parameter.VisibleWhen(modifier)))
            return null;
        return BuildSchemaRow(terrain, modifier, parameter);
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

            case ParameterKind.ColorRamp:
                // Modifiers change geometry, not display: nothing they own is colour-mapped, so no
                // modifier declares this kind. The case exists so the shared enum stays exhaustively
                // handled rather than falling into the throw below.
                return new Panel();

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

            case ParameterKind.ColorRamp:
                return CreateColorRampEditor(terrain, analysis);

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

    /// <summary>
    /// Appends a row per declared parameter for <paramref name="annotation"/>'s type. Returns false when the
    /// type has no schema yet, so the caller can fall back to its bespoke rows for that type.
    /// </summary>
    private bool TryBuildSchemaAnnotationBody(DynamicLayout layout, TerrainDefinition terrain, AnnotationDefinition annotation)
    {
        var descriptor = AnnotationTypeRegistry.ForType(annotation.GetType());
        if (descriptor == null || descriptor.Parameters.Count == 0)
            return false;

        foreach (var parameter in descriptor.Parameters)
            layout.AddRow(BuildAnnotationSchemaRow(terrain, annotation, parameter));

        return true;
    }

    private Control BuildAnnotationSchemaRow(TerrainDefinition terrain, AnnotationDefinition annotation, AnnotationParameterDescriptor parameter)
    {
        Guid terrainId = terrain.TerrainId;
        Guid annotationId = annotation.Id;
        string label = parameter.LabelFor?.Invoke(annotation) ?? parameter.Label;

        switch (parameter.Kind)
        {
            case ParameterKind.Sources:
                return CreateSourceEditor(
                    label,
                    parameter.GetSources!(annotation),
                    apply => CommitAnnotationMutation(parameter, terrainId, annotationId, item => apply(parameter.GetSources!(item))),
                    parameter.ObjectFilter,
                    doc => _controller.GetSelectedLayerPaths(doc),
                    parameter.Help);

            case ParameterKind.Number:
                return CreateNumericEditor(
                    label,
                    parameter.GetNumber!(annotation),
                    value => CommitAnnotationMutation(parameter, terrainId, annotationId, item => parameter.SetNumber!(item, value)),
                    parameter.DecimalPlaces,
                    parameter.Help,
                    parameter.Min,
                    parameter.Max,
                    step: parameter.Step);

            case ParameterKind.Bool:
                return CreateCheckEditor(
                    label,
                    parameter.GetBool!(annotation),
                    value => CommitAnnotationMutation(parameter, terrainId, annotationId, item => parameter.SetBool!(item, value)),
                    parameter.Help ?? string.Empty);

            case ParameterKind.Choice:
            {
                var options = parameter.ChoiceOptionsFor?.Invoke(annotation) ?? parameter.ChoiceOptions!;
                return CreateDropDownEditor(
                    label,
                    options,
                    parameter.GetText!(annotation) ?? string.Empty,
                    value => CommitAnnotationMutation(parameter, terrainId, annotationId, item => parameter.SetText!(item, value)),
                    parameter.Help ?? string.Empty);
            }

            case ParameterKind.Color:
                return CreateOptionalColorEditor(
                    label,
                    parameter.GetColor!(annotation),
                    value => CommitAnnotationMutation(parameter, terrainId, annotationId, item => parameter.SetColor!(item, value)),
                    parameter.Help ?? string.Empty,
                    parameter.FallbackColor?.Invoke(terrain, annotation),
                    parameter.ColorDefaultTextFor?.Invoke(terrain, annotation) ?? "(by layer)");

            case ParameterKind.ColorRamp:
                // Annotations draw; they are never colour-mapped, so no annotation declares this kind.
                // The case exists so the shared enum stays exhaustively handled.
                return new Panel();

            case ParameterKind.Text:
                return CreateCommittedTextEditor(
                    label,
                    parameter.GetText!(annotation) ?? string.Empty,
                    value => CommitAnnotationMutation(parameter, terrainId, annotationId, item => parameter.SetText!(item, value)),
                    parameter.Help ?? string.Empty,
                    parameter.TrimText);

            case ParameterKind.ReadOnly:
                return CreateReadOnlyValueRow(
                    label,
                    parameter.GetReadOnly!(annotation),
                    parameter.Help ?? string.Empty);

            default:
                throw new ArgumentOutOfRangeException(nameof(parameter), parameter.Kind, "Unhandled parameter kind.");
        }
    }

    /// <summary>
    /// Commits an annotation-schema row's edit per its descriptor flags: <see cref="AnnotationParameterDescriptor.IncrementalCommit"/>
    /// (contour-style — skip the full rebuild and run the type's own incremental rebuild instead),
    /// <see cref="AnnotationParameterDescriptor.RefreshOnly"/> (cheap preview recolor, no rebuild), or the
    /// default full analysis rebuild.
    /// </summary>
    private void CommitAnnotationMutation(AnnotationParameterDescriptor parameter, Guid terrainId, Guid annotationId, Action<AnnotationDefinition> apply)
    {
        if (parameter.IncrementalCommit)
        {
            AnnotationDefinition? mutated = null;
            MutateAnnotation(terrainId, annotationId, item =>
            {
                apply(item);
                mutated = item;
            }, scheduleRebuild: false);

            var doc = RhinoDoc.ActiveDoc;
            if (doc == null || mutated == null)
                return;

            if (mutated is ContourAnnotationDefinition contour && parameter.Kind == ParameterKind.Color)
                _controller.RefreshContourColor(doc, terrainId, annotationId, contour.ColorArgb);
            else
                _controller.RebuildContourAnalysis(doc, terrainId, annotationId);
            return;
        }

        if (parameter.RefreshOnly)
        {
            MutateAndRefreshAnnotation(terrainId, annotationId, apply);
            return;
        }

        MutateAnnotation(terrainId, annotationId, apply, scheduleRebuild: true);
    }

    private bool TryBuildSchemaObjectBody(DynamicLayout layout, TerrainDefinition terrain, TerrainObjectDefinition definition)
    {
        var descriptor = ObjectTypeRegistry.ForType(definition.GetType());
        if (descriptor == null || descriptor.Parameters.Count == 0)
            return false;

        foreach (var parameter in descriptor.Parameters)
        {
            if (parameter.VisibleWhen != null && !parameter.VisibleWhen(definition))
                continue;

            layout.AddRow(BuildObjectSchemaRow(terrain, definition, parameter));
        }

        return true;
    }

    private Control BuildObjectSchemaRow(
        TerrainDefinition terrain,
        TerrainObjectDefinition definition,
        ObjectParameterDescriptor parameter)
    {
        Guid terrainId = terrain.TerrainId;
        Guid definitionId = definition.Id;
        string label = parameter.LabelFor?.Invoke(definition) ?? parameter.Label;

        void Commit(Action<TerrainObjectDefinition> apply)
        {
            MutateObjectDefinition(
                terrainId,
                definitionId,
                apply,
                deferDocumentSave: parameter.LiveScrub,
                suppressImmediateUiRefresh: parameter.LiveScrub);

            if (parameter.RebuildAfterCommit)
            {
                var doc = RhinoDoc.ActiveDoc;
                if (doc != null)
                    RebuildObjectsLayout(_controller.GetSelectedTerrain(doc));
            }
        }

        switch (parameter.Kind)
        {
            case ObjectParameterKind.Sources:
                return CreateSourceEditor(
                    label,
                    parameter.GetSources!(definition),
                    apply => Commit(item => apply(parameter.GetSources!(item))),
                    parameter.ObjectFilter,
                    doc => _controller.GetSelectedLayerPaths(doc),
                    parameter.Help);

            case ObjectParameterKind.Number:
                return CreateNumericEditor(
                    label,
                    parameter.GetNumber!(definition),
                    value => Commit(item => parameter.SetNumber!(item, value)),
                    decimalPlaces: parameter.DecimalPlaces,
                    help: parameter.Help,
                    minValue: parameter.Min,
                    maxValue: parameter.Max,
                    liveEdit: parameter.LiveEdit,
                    step: parameter.Step);

            case ObjectParameterKind.Slider:
                return CreateSliderNumericEditor(
                    label,
                    parameter.GetNumber!(definition),
                    value => Commit(item => parameter.SetNumber!(item, value)),
                    parameter.SoftMin,
                    parameter.SoftMax,
                    parameter.DecimalPlaces,
                    parameter.Min,
                    parameter.Max,
                    parameter.Help);

            case ObjectParameterKind.Bool:
                return CreateCheckEditor(
                    label,
                    parameter.GetBool!(definition),
                    value => Commit(item => parameter.SetBool!(item, value)),
                    parameter.Help ?? string.Empty);

            case ObjectParameterKind.Choice:
                return CreateDropDownEditor(
                    label,
                    parameter.ChoiceOptionsFor?.Invoke(definition) ?? parameter.ChoiceOptions!,
                    parameter.GetText!(definition) ?? string.Empty,
                    value => Commit(item => parameter.SetText!(item, value)),
                    parameter.Help ?? string.Empty);

            case ObjectParameterKind.ReadOnly:
                return CreateReadOnlyValueRow(
                    label,
                    parameter.GetReadOnly!(definition),
                    parameter.Help ?? string.Empty);

            case ObjectParameterKind.BlockMix:
                return definition is ScatterObjectDefinition scatter
                    ? CreateScatterBlockMixEditor(terrain, scatter)
                    : new Panel();

            default:
                throw new ArgumentOutOfRangeException(nameof(parameter), parameter.Kind, "Unhandled object parameter kind.");
        }
    }
}
