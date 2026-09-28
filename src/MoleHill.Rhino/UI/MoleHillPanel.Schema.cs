using Eto.Forms;
using MoleHill.Core.Analysis;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Registry;
using MoleHill.Rhino.Services;
using Rhino;

namespace MoleHill.Rhino.UI;

// Schema-driven card bodies. A terrain type declares its inputs as an ordered
// ParameterDescriptor<TDefinition> list (Registry side, Eto-free) and one generic builder turns each into
// the matching reusable editor primitive from MoleHillPanel.Editors.cs. Adding or changing a type's
// inputs is then data, not UI plumbing — and the same schema is the contract that will generate the
// Grasshopper component.
//
// All four families (modifiers, analyses, annotations, objects) share BuildSchemaRow. What stays
// per-family is the commit closure each one passes in: a family's own save/rebuild/refresh semantics live
// there, and the descriptor's commit-hint flags are inert data that closure interprets. Kinds needing a
// whole bespoke control (ColorRamp, BlockMix) come in through the optional bespoke hook.
public sealed partial class MoleHillPanel
{
    /// <summary>
    /// Builds one editor row for <paramref name="parameter"/> against <paramref name="definition"/>.
    ///
    /// <paramref name="commit"/> is the family's mutation wrapper — it receives the mutation to apply and
    /// is responsible for honouring whichever of the descriptor's commit hints that family observes.
    /// <paramref name="bespoke"/> renders the kinds that are not expressible as a shared primitive; when
    /// it is null for such a kind the row is an empty panel, so a family simply never declaring that kind
    /// costs nothing.
    /// </summary>
    private Control BuildSchemaRow<TDef>(
        TerrainDefinition terrain,
        TDef definition,
        ParameterDescriptor<TDef> parameter,
        Action<Action<TDef>> commit,
        Func<ParameterDescriptor<TDef>, TDef, Control>? bespoke = null)
        where TDef : class
    {
        string label = parameter.LabelFor?.Invoke(definition) ?? parameter.Label;

        switch (parameter.Kind)
        {
            case ParameterKind.Sources:
                return CreateSourceEditor(
                    label,
                    parameter.GetSources!(definition),
                    apply => commit(item => apply(parameter.GetSources!(item))),
                    parameter.ObjectFilter,
                    doc => _controller.GetSelectedLayerPaths(doc),
                    parameter.Help);

            case ParameterKind.Number:
                // A slope is stored in degrees but neither shown nor typed that way, so it gets the
                // unit-aware field instead of a stepper. Everything else keeps the stepper and gains
                // only the trailing unit label.
                if (parameter.Unit == ParameterUnit.Slope)
                {
                    return CreateSlopeEditor(
                        label,
                        parameter.GetNumber!(definition),
                        value => commit(item => parameter.SetNumber!(item, value)),
                        parameter.Help);
                }

                return CreateNumericEditor(
                    label,
                    parameter.GetNumber!(definition),
                    value => commit(item => parameter.SetNumber!(item, value)),
                    parameter.DecimalPlaces,
                    parameter.Help,
                    parameter.Min,
                    parameter.Max,
                    liveEdit: parameter.LiveEdit,
                    step: parameter.Step,
                    unitSuffix: ResolveUnitSuffix(parameter.Unit));

            case ParameterKind.OptionalNumber:
                if (parameter.Unit == ParameterUnit.Slope)
                {
                    return CreateSlopeEditor(
                        label,
                        parameter.GetNumber!(definition),
                        value => commit(item => parameter.SetNumber!(item, value)),
                        parameter.Help,
                        inheritedDegrees: parameter.InheritedValue!(definition));
                }

                return CreateOptionalNumericEditor(
                    label,
                    parameter.GetNumber!(definition),
                    parameter.InheritedValue!(definition),
                    value => commit(item => parameter.SetNumber!(item, value)),
                    parameter.DecimalPlaces,
                    parameter.Help,
                    unitSuffix: ResolveUnitSuffix(parameter.Unit));

            case ParameterKind.Slider:
                // The slider track stays in the stored unit (degrees for a slope — the only bounded
                // slope axis to drag along); only the value box speaks the user's unit.
                bool slopeSlider = parameter.Unit == ParameterUnit.Slope;
                return CreateSliderNumericEditor(
                    label,
                    parameter.GetNumber!(definition),
                    value => commit(item => parameter.SetNumber!(item, value)),
                    parameter.SoftMin,
                    parameter.SoftMax,
                    parameter.DecimalPlaces,
                    parameter.Min,
                    parameter.Max,
                    parameter.Help,
                    unitSuffix: ResolveUnitSuffix(parameter.Unit),
                    formatValue: slopeSlider
                        ? degrees => SlopeInput.FormatDegreesAsUnit(degrees, SlopeUnitPreference.Current)
                        : null,
                    parseValue: slopeSlider
                        ? text => SlopeInput.TryParseToDegrees(text, SlopeUnitPreference.Current, out double degrees)
                            ? degrees
                            : null
                        : null);

            case ParameterKind.Bool:
                return CreateCheckEditor(
                    label,
                    parameter.GetBool!(definition),
                    value => commit(item => parameter.SetBool!(item, value)),
                    parameter.Help ?? string.Empty);

            case ParameterKind.ReadOnly:
                return CreateReadOnlyValueRow(
                    label,
                    parameter.GetReadOnly!(definition),
                    parameter.Help ?? string.Empty);

            case ParameterKind.Choice:
                return CreateDropDownEditor(
                    label,
                    parameter.ChoiceOptionsFor?.Invoke(definition) ?? parameter.ChoiceOptions!,
                    parameter.GetText!(definition) ?? string.Empty,
                    value => commit(item => parameter.SetText!(item, value)),
                    parameter.Help ?? string.Empty);

            case ParameterKind.Color:
                return CreateOptionalColorEditor(
                    label,
                    parameter.GetColor!(definition),
                    value => commit(item => parameter.SetColor!(item, value)),
                    parameter.Help ?? string.Empty,
                    parameter.FallbackColor?.Invoke(terrain, definition),
                    parameter.ColorDefaultTextFor?.Invoke(terrain, definition) ?? "(by layer)");

            case ParameterKind.Text:
                return CreateCommittedTextEditor(
                    label,
                    parameter.GetText!(definition) ?? string.Empty,
                    value => commit(item => parameter.SetText!(item, value)),
                    parameter.Help ?? string.Empty,
                    parameter.TrimText);

            // Whole-control kinds. Only the family that owns one passes a bespoke renderer for it:
            // ColorRamp is analyses (they are colour-mapped; modifiers change geometry and annotations
            // draw), BlockMix is Scatter alone. Anyone else declaring one gets an empty row rather than a
            // throw, which keeps the shared enum exhaustively handled.
            case ParameterKind.ColorRamp:
            case ParameterKind.BlockMix:
            case ParameterKind.GoingTable:
                return bespoke?.Invoke(parameter, definition) ?? new Panel();

            default:
                throw new ArgumentOutOfRangeException(nameof(parameter), parameter.Kind, "Unhandled parameter kind.");
        }
    }

    /// <summary>
    /// The trailing unit label for a numeric row. Model lengths resolve against the active document so a
    /// terrain in feet does not claim millimetres; the slope unit comes from the user's preference, which
    /// is why every slope row relabels the moment that preference changes.
    /// </summary>
    private static string? ResolveUnitSuffix(ParameterUnit unit) => unit switch
    {
        ParameterUnit.ModelLength => RhinoDoc.ActiveDoc is { } doc
            ? ModelUnits.Abbreviation(doc.ModelUnitSystem)
            : null,
        ParameterUnit.Degrees => "°",
        ParameterUnit.Slope => SlopeInput.Suffix(SlopeUnitPreference.Current),
        ParameterUnit.Percent => "%",
        _ => null
    };

    // ---- Modifiers ----

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

            layout.AddRow(BuildModifierSchemaRow(terrain, modifier, parameter));
        }

        return true;
    }

    private static bool IsBespokePositionedModifierParameter(
        ModifierDefinition modifier,
        ParameterDescriptor<ModifierDefinition> parameter) =>
        (modifier is TriangulateModifierDefinition &&
         parameter.Key is "DemSurface" or "ContourMode" or "OuterBoundaries" or "HideBoundaries" or "ShowBoundaries" or "DataClipBoundaries") ||
        (modifier is GradePathModifierDefinition &&
         parameter.Key is "Paths" or "Width" or "UseVariableWidth" or "WidthEdges" or "MaxEdgeDistance") ||
        (modifier is ProjectToModifierDefinition && parameter.Key == "TargetMesh") ||
        // Declared by every geometry-input modifier, but drawn inside the panel's "Peel Border" group.
        (modifier is GeometryInputModifierDefinition &&
         GeometryInputParameterCatalog.BoundaryPeelKeys.Contains(parameter.Key, StringComparer.Ordinal));

    private Control? BuildBespokePositionedModifierRow(
        TerrainDefinition terrain,
        ModifierDefinition modifier,
        string parameterKey)
    {
        ModifierTypeDescriptor? descriptor = TerrainTypeRegistry.ForModifierType(modifier.GetType());
        ParameterDescriptor<ModifierDefinition>? parameter = descriptor?.Parameters.FirstOrDefault(
            item => string.Equals(item.Key, parameterKey, StringComparison.Ordinal));
        if (parameter == null || (parameter.VisibleWhen != null && !parameter.VisibleWhen(modifier)))
            return null;
        return BuildModifierSchemaRow(terrain, modifier, parameter);
    }

    /// <summary>
    /// Modifier rows commit straight through <see cref="MutateModifier"/>. Only sliders declare
    /// <c>LiveScrub</c>, so every other kind takes the plain save-and-rebuild path it had before.
    /// </summary>
    private Control BuildModifierSchemaRow(
        TerrainDefinition terrain,
        ModifierDefinition modifier,
        ParameterDescriptor<ModifierDefinition> parameter)
    {
        Guid terrainId = terrain.TerrainId;
        Guid modifierId = modifier.Id;

        return BuildSchemaRow(
            terrain,
            modifier,
            parameter,
            apply => MutateModifier(
                terrainId,
                modifierId,
                apply,
                deferDocumentSave: parameter.LiveScrub,
                suppressImmediateUiRefresh: parameter.LiveScrub));
    }

    // ---- Analyses ----

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
        {
            if (parameter.VisibleWhen != null && !parameter.VisibleWhen(analysis))
                continue;

            layout.AddRow(BuildAnalysisSchemaRow(terrain, analysis, parameter));
        }

        return true;
    }

    private Control BuildAnalysisSchemaRow(
        TerrainDefinition terrain,
        AnalysisDefinition analysis,
        ParameterDescriptor<AnalysisDefinition> parameter)
    {
        Guid terrainId = terrain.TerrainId;
        Guid analysisId = analysis.Id;

        return BuildSchemaRow(
            terrain,
            analysis,
            parameter,
            apply => CommitAnalysisMutation(parameter, terrainId, analysisId, apply),
            (declared, item) => declared.Kind switch
            {
                ParameterKind.ColorRamp => CreateColorRampEditor(terrain, item),
                ParameterKind.GoingTable when item is GradientComplianceAnalysisDefinition compliance =>
                    CreateGoingTableEditor(compliance, declared, apply => CommitAnalysisMutation(declared, terrainId, analysisId, apply)),
                _ => new Panel(),
            });
    }

    /// <summary>
    /// Commits an analysis-schema row's edit per its descriptor flags: <c>IncrementalCommit</c>
    /// (contour-style — skip the full rebuild and run the type's own incremental rebuild instead),
    /// <c>RefreshOnly</c> (cheap preview recolor, no rebuild), or the default full analysis rebuild.
    /// </summary>
    private void CommitAnalysisMutation(
        ParameterDescriptor<AnalysisDefinition> parameter,
        Guid terrainId,
        Guid analysisId,
        Action<AnalysisDefinition> apply)
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

    // ---- Annotations ----

    /// <summary>
    /// Appends a row per declared parameter for <paramref name="annotation"/>'s type. Returns false when
    /// the type has no schema yet, so the caller can fall back to its bespoke rows for that type.
    /// </summary>
    private bool TryBuildSchemaAnnotationBody(DynamicLayout layout, TerrainDefinition terrain, AnnotationDefinition annotation)
    {
        var descriptor = AnnotationTypeRegistry.ForType(annotation.GetType());
        if (descriptor == null || descriptor.Parameters.Count == 0)
            return false;

        foreach (var parameter in descriptor.Parameters)
        {
            if (parameter.VisibleWhen != null && !parameter.VisibleWhen(annotation))
                continue;

            layout.AddRow(BuildAnnotationSchemaRow(terrain, annotation, parameter));
        }

        return true;
    }

    private Control BuildAnnotationSchemaRow(
        TerrainDefinition terrain,
        AnnotationDefinition annotation,
        ParameterDescriptor<AnnotationDefinition> parameter)
    {
        Guid terrainId = terrain.TerrainId;
        Guid annotationId = annotation.Id;

        return BuildSchemaRow(
            terrain,
            annotation,
            parameter,
            apply => CommitAnnotationMutation(parameter, terrainId, annotationId, apply));
    }

    /// <summary>
    /// Commits an annotation-schema row's edit per its descriptor flags: <c>IncrementalCommit</c>
    /// (contour-style — skip the full rebuild and run the type's own incremental rebuild instead),
    /// <c>RefreshOnly</c> (cheap preview recolor, no rebuild), or the default full rebuild.
    /// </summary>
    private void CommitAnnotationMutation(
        ParameterDescriptor<AnnotationDefinition> parameter,
        Guid terrainId,
        Guid annotationId,
        Action<AnnotationDefinition> apply)
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

    // ---- Terrain objects ----

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
        ParameterDescriptor<TerrainObjectDefinition> parameter)
    {
        Guid terrainId = terrain.TerrainId;
        Guid definitionId = definition.Id;

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

        return BuildSchemaRow(
            terrain,
            definition,
            parameter,
            Commit,
            (_, item) => item is ScatterObjectDefinition scatter
                ? CreateScatterBlockMixEditor(terrain, scatter)
                : new Panel());
    }
}
