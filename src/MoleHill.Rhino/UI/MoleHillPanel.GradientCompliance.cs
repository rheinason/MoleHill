using Eto.Forms;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Registry;

namespace MoleHill.Rhino.UI;

// Gradient compliance card: the ramp going table, edited in place as rows on the card.
// There is deliberately no separate editor dialog: every other setting in the product is edited where it
// applies, and a standard should be no different. Rows reuse the shared slope and numeric editors, so a
// going row reads and types exactly like every other slope and length on the card.
public sealed partial class MoleHillPanel
{
    private Control CreateGoingTableEditor(
        GradientComplianceAnalysisDefinition analysis,
        ParameterDescriptor<AnalysisDefinition> parameter,
        Action<Action<AnalysisDefinition>> commit)
    {
        GradientRuleSet rules = analysis.Rules;

        // Every edit goes through the card's commit and re-derives the "(modified)" mark, as the
        // single-value rule rows do.
        void Edit(Action<GradientRuleSet> change) => commit(item =>
        {
            var rulesToEdit = ((GradientComplianceAnalysisDefinition)item).Rules;
            change(rulesToEdit);
            GradientRulePresets.RefreshModified(rulesToEdit);
        });

        var layout = new DynamicLayout { Spacing = new Eto.Drawing.Size(UiMetrics.SpaceSmall, UiMetrics.SpaceSmall) };

        var addButton = MakeIconButton(
            PanelButtonIcon.Add,
            (_, _) => Edit(r => r.AddGoingLimit()),
            "Add a gradient row. It starts as a copy of the steepest row.");
        var header = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = UiMetrics.SpaceSmall,
            VerticalContentAlignment = VerticalAlignment.Center,
            Items =
            {
                new StackLayoutItem(UiControls.Label(parameter.Label), expand: true),
                addButton,
            },
        };
        ApplyHelp(header, parameter.Help ?? string.Empty);
        layout.AddRow(header);

        if (rules.RampGoingLimits.Count == 0)
            layout.AddRow(CreateReadOnlyValueRow("Goings", "No limit", parameter.Help ?? string.Empty));

        // Shown gentlest first, as the standards list them. Edits address the stored row, not its
        // position on screen, so reordering for display cannot edit the wrong limit.
        var ordered = rules.RampGoingLimits
            .Select((limit, index) => (limit, index))
            .OrderBy(entry => entry.limit.SlopeDegrees)
            .ToList();
        int row = 1;
        foreach (var (limit, index) in ordered)
        {
            int storedIndex = index;
            layout.AddRow(CreateSlopeEditor(
                $"Gradient {row}",
                limit.SlopeDegrees,
                value => Edit(r => r.RampGoingLimits[storedIndex].SlopeDegrees = value),
                "A ramp at this gradient, or gentler down to the next row, may run up to the going below."));

            var goingRow = new StackLayout
            {
                Orientation = Orientation.Horizontal,
                Spacing = UiMetrics.SpaceSmall,
                VerticalContentAlignment = VerticalAlignment.Center,
                Items =
                {
                    new StackLayoutItem(CreateNumericEditor(
                        $"Up To {row}",
                        limit.MaxGoing,
                        value => Edit(r => r.RampGoingLimits[storedIndex].MaxGoing = Math.Max(0.0, value)),
                        decimalPlaces: 3,
                        help: "The longest run allowed at this gradient, between landings.",
                        minValue: 0.0,
                        unitSuffix: ResolveUnitSuffix(ParameterUnit.ModelLength)), expand: true),
                    MakeDeleteIconButton(
                        () => Edit(r => r.RemoveGoingLimitAt(storedIndex)),
                        "Remove this gradient row."),
                },
            };
            layout.AddRow(goingRow);
            row++;
        }

        if (rules.RampGoingLimits.Count > 1)
        {
            layout.AddRow(CreateCheckEditor(
                "Interpolate",
                rules.InterpolateGoing,
                value => Edit(r => r.InterpolateGoing = value),
                "Between two rows, allow a going between theirs (Approved Document M allows this). Off, the " +
                "steeper row's going applies."));
        }

        return layout;
    }
}
