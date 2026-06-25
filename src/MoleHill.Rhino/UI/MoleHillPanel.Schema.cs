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
                    parameter.Max);

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

            default:
                throw new ArgumentOutOfRangeException(nameof(parameter), parameter.Kind, "Unhandled parameter kind.");
        }
    }
}
