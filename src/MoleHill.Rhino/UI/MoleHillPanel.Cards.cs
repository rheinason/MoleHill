using Eto.Drawing;
using Eto.Forms;
using MoleHill.Rhino.Registry;

namespace MoleHill.Rhino.UI;

// The stack card. Modifier, object, zone, analysis and annotation cards differ only in what they bind —
// a name, an enabled flag, a type, a summary, a body — so each family fills in a CardSpec and CreateCard
// builds the same header for all of them. A header change is made here once, never per family.
public sealed partial class MoleHillPanel
{
    /// <summary>What one stack card shows and what its header controls do.</summary>
    private sealed class CardSpec
    {
        public required CardStack Stack { get; init; }
        public required Guid Id { get; init; }

        /// <summary>The user's name for the card; editable when expanded.</summary>
        public required string Name { get; init; }
        public required string NameHelp { get; init; }
        public required Action<string> Rename { get; init; }

        public required bool IsEnabled { get; init; }
        public string EnableHelp { get; init; } = "Enable or disable this card without deleting it.";
        public required Action<bool> SetEnabled { get; init; }

        /// <summary>The card's type ("Retaining Wall"), which leads the second line in both states.</summary>
        public required string TypeName { get; init; }
        public required Control TypeIcon { get; init; }

        /// <summary>The second line, after the type, in both states: counts and key values. Produced
        /// through <see cref="SummaryText.Invariant{T}"/>, so its numbers read with a point.</summary>
        public required Func<string> Summary { get; init; }

        /// <summary>What the card does; the second line's tooltip.</summary>
        public required string Description { get; init; }

        public required string HandleHelp { get; init; }
        public bool Reorderable { get; init; } = true;

        /// <summary>A short state word beside the actions ("Preview", "Pinned"); dropped first when narrow.</summary>
        public Control? Status { get; init; }

        public (Action Run, string Help)? Duplicate { get; init; }
        public (Action Run, string Help)? Delete { get; init; }

        public required Func<Control> Body { get; init; }
        public Color CardBackground { get; init; } = UiTheme.CardBackground;
        public Color HeaderBackground { get; init; } = UiTheme.HeaderBackground;
    }

    /// <summary>
    /// Builds a stack card: a fixed two-line header over the body when expanded. The first line (design
    /// review Steps 14 and 20) is grip, disclosure arrow, enable, bare type icon, name, then status and
    /// actions, all centred on it. The second line, "Type · summary" in both states (Step 79), runs the full
    /// width of the header from its left edge. Both lines ellipsize rather than wrap, so every header is the
    /// same height at every width. Clicking the header toggles the card; Ctrl-click toggles the whole stack.
    /// </summary>
    private Panel CreateCard(CardSpec spec)
    {
        bool collapsed = spec.Stack.IsCollapsed(spec.Id);

        var enabled = new CheckBox { Checked = spec.IsEnabled };
        ApplyHelp(enabled, spec.EnableHelp);
        enabled.CheckedChanged += (_, _) => spec.SetEnabled(enabled.Checked == true);

        Control nameLine;
        if (collapsed)
        {
            nameLine = new TextLine(spec.Name, new Font(SystemFont.Bold), UiTheme.PrimaryText);
        }
        else
        {
            var nameBox = new TextBox { Text = spec.Name };
            StyleTextBox(nameBox);
            ApplyHelp(nameBox, spec.NameHelp);
            BindCommittedText(nameBox, () => spec.Name, spec.Rename);
            nameLine = nameBox;
        }

        string meta = JoinMeta(spec.TypeName, SummaryText.Invariant(spec.Summary));
        var metaLine = new TextLine(meta, SystemFonts.Default(), UiTheme.MutedText)
        {
            ToolTip = string.IsNullOrWhiteSpace(spec.Description) ? meta : $"{meta}{Environment.NewLine}{spec.Description}"
        };

        var arrow = new DisclosureArrow(!collapsed)
        {
            ToolTip = collapsed
                ? "Expand this card (Enter or Space when focused). Ctrl-click expands every card."
                : "Collapse this card (Enter or Space when focused). Ctrl-click collapses every card."
        };
        arrow.Activated += (_, _) => spec.Stack.ToggleCollapsed(spec.Id, false);

        var firstLine = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = UiMetrics.SpaceMedium,
            Height = UiMetrics.ControlHeight,
            VerticalContentAlignment = VerticalAlignment.Center,
            Items =
            {
                spec.Stack.CreateHandle(spec.Id, spec.HandleHelp, spec.Reorderable),
                arrow,
                enabled,
                spec.TypeIcon,
                new StackLayoutItem(nameLine, expand: true)
            }
        };

        Control? statusHost = spec.Status;
        if (statusHost != null)
            firstLine.Items.Add(statusHost);
        if (spec.Duplicate is { } duplicate)
            firstLine.Items.Add(MakeDuplicateIconButton(duplicate.Run, duplicate.Help));
        if (spec.Delete is { } delete)
            firstLine.Items.Add(MakeDeleteIconButton(delete.Run, delete.Help));

        var header = new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = UiMetrics.CardHeaderLineGap,
            Padding = new Padding(UiMetrics.CardHorizontalPadding, UiMetrics.CardVerticalPadding),
            BackgroundColor = spec.HeaderBackground,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items = { firstLine, metaLine }
        };

        // The status word is the one optional thing on the header: it goes before the name is squeezed.
        if (statusHost != null)
        {
            header.SizeChanged += (_, _) =>
                statusHost.Visible = header.Width <= 0 || header.Width >= UiMetrics.HeaderStatusBreak;
        }

        header.MouseDown += (_, e) => spec.Stack.ToggleCollapsed(spec.Id, e.Modifiers.HasFlag(Keys.Control));

        var card = new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = 0,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items = { header }
        };
        if (!collapsed)
            card.Items.Add(spec.Body());

        return new Panel { BackgroundColor = spec.CardBackground, Content = card };
    }

    /// <summary>"Type · detail", or just the type when the detail is empty or repeats it.</summary>
    private static string JoinMeta(string typeName, string detail) =>
        string.IsNullOrWhiteSpace(detail) || string.Equals(detail, typeName, StringComparison.OrdinalIgnoreCase)
            ? typeName
            : SummaryText.Join(typeName, detail);

    /// <summary>
    /// A heading for a group of rows that does not collapse: a small caption and a rule across the rest of
    /// the width. The panel's alternative was a GroupBox, whose border boxed in content that was already
    /// visually grouped by the card around it — and an arrow here would promise a collapse that is not
    /// on offer.
    /// </summary>
    private static Control CreateSectionRule(string title) => new StackLayout
    {
        Orientation = Orientation.Horizontal,
        Spacing = UiMetrics.SpaceLarge,
        Padding = new Padding(0, UiMetrics.SpaceSmall, 0, 0),
        VerticalContentAlignment = VerticalAlignment.Center,
        Items =
        {
            UiControls.Label(title, UiLabelRole.Section),
            new StackLayoutItem(
                new Panel { Height = UiMetrics.SpaceHairline, BackgroundColor = UiTheme.RampDivider },
                expand: true)
        }
    };

    /// <summary>
    /// A collapsible section header outside the card stack — Terrain Settings and Status. It uses the
    /// card's disclosure arrow, header fill and padding, and the whole strip is the hit target, so the same
    /// gesture looks the same everywhere.
    /// </summary>
    private sealed class SectionHeader : Panel
    {
        private readonly DisclosureArrow _arrow;

        public SectionHeader(string title, Control? trailing, bool expanded, Action<bool> onToggle)
        {
            _arrow = new DisclosureArrow(expanded);

            var layout = new StackLayout
            {
                Orientation = Orientation.Horizontal,
                Spacing = UiMetrics.SpaceMedium,
                Padding = new Padding(UiMetrics.CardHorizontalPadding, UiMetrics.CardVerticalPadding),
                VerticalContentAlignment = VerticalAlignment.Center,
                Items = { _arrow, UiControls.Label(title, UiLabelRole.Section) }
            };

            if (trailing != null)
                layout.Items.Add(new StackLayoutItem(trailing, expand: true));

            BackgroundColor = UiTheme.HeaderBackground;
            Content = layout;
            Cursor = Cursors.Pointer;
            Expanded = expanded;

            void Toggle()
            {
                Expanded = !Expanded;
                _arrow.Expanded = Expanded;
                onToggle(Expanded);
            }

            ToolTip = $"Show or hide {title}.";
            _arrow.ToolTip = ToolTip;
            MouseDown += (_, e) =>
            {
                if (e.Buttons != MouseButtons.Primary)
                    return;

                Toggle();
            };
            _arrow.Activated += (_, _) => Toggle();
        }

        public bool Expanded { get; private set; }
    }

    /// <summary>A short state word on a card header ("Preview", "Pinned", "Disabled").</summary>
    private static Label CreateCardStatusLabel(string text, Color? textColor = null)
    {
        var label = UiControls.Label(text, UiLabelRole.Meta);
        label.TextColor = textColor ?? UiTheme.MutedText;
        label.Width = UiMetrics.Chs(7);
        label.ToolTip = text;
        return label;
    }

    /// <summary>
    /// A card's bare type glyph: the PNG named <paramref name="iconName"/> when it resolves, otherwise the
    /// <paramref name="glyphFallback"/> text. No plate behind it (Step 19); the strip carries the colour.
    /// </summary>
    private static Control CreateCardIconControl(string? iconName, string glyphFallback)
    {
        var image = string.IsNullOrEmpty(iconName) ? null : PanelIcons.Load(iconName);
        return image != null
            ? new ImageView { Image = image, Size = new Size(UiMetrics.CardTypeIconSize, UiMetrics.CardTypeIconSize) }
            : new Label
            {
                Text = glyphFallback,
                VerticalAlignment = VerticalAlignment.Center,
                Width = UiMetrics.Chs(2),
                Wrap = WrapMode.None,
                ToolTip = glyphFallback
            };
    }

    /// <summary>Stacked-squares "duplicate this card" icon button.</summary>
    private static IconButton MakeDuplicateIconButton(Action action, string help) =>
        MakeIconButton(PanelButtonIcon.Duplicate, (_, _) => action(), help);

    /// <summary>Outline-bin "delete this card" icon button.</summary>
    private static IconButton MakeDeleteIconButton(Action action, string help) =>
        MakeIconButton(PanelButtonIcon.Delete, (_, _) => action(), help);
}
