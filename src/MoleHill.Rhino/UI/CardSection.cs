using Eto.Drawing;
using Eto.Forms;
using MoleHill.Rhino.Services;

namespace MoleHill.Rhino.UI;

/// <summary>
/// A collapsible group of rows inside a card body: Settings, Boundaries, Variation. Every card section is
/// one of these, whether the rows were declared (a schema <c>Section</c>) or placed by hand (Triangulate).
///
/// The heading is as legible as a field label, bold, with the shared <see cref="DisclosureArrow"/> and a
/// rule across the rest of the row (design review: "make section headers as legible as field labels, with
/// stronger weight and disclosure arrows"). An optional summary after the title says what is set inside a
/// closed section. Open/closed is remembered per user and card type (<see cref="SectionExpansionPreference"/>);
/// toggling shows or hides the rows in place, so it never rebuilds the card or touches the document.
/// </summary>
internal sealed class CardSection : Panel
{
    private readonly DisclosureArrow _arrow;
    private readonly Control _content;

    public CardSection(string cardKind, string sectionKey, string title, bool expandedByDefault, string? summary, Control content)
    {
        bool expanded = SectionExpansionPreference.IsExpanded(cardKind, sectionKey, expandedByDefault);
        _arrow = new DisclosureArrow(expanded);
        _content = content;
        _content.Visible = expanded;

        var titleLabel = UiControls.Label(title);
        titleLabel.Font = new Font(SystemFont.Bold);

        var heading = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = UiMetrics.SpaceMedium,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Padding(0, UiMetrics.SpaceSmall, 0, 0),
            Items = { _arrow, titleLabel }
        };
        if (!string.IsNullOrWhiteSpace(summary))
            heading.Items.Add(UiControls.Label(summary, UiLabelRole.Meta));
        heading.Items.Add(new StackLayoutItem(
            new Panel { Height = UiMetrics.SpaceHairline, BackgroundColor = UiTheme.RampDivider },
            expand: true));

        var hit = new Panel { Content = heading, Cursor = Cursors.Pointer, ToolTip = $"Show or hide {title}." };
        _arrow.ToolTip = hit.ToolTip;

        void Toggle()
        {
            bool open = !_content.Visible;
            _content.Visible = open;
            _arrow.Expanded = open;
            SectionExpansionPreference.SetExpanded(cardKind, sectionKey, open);
        }

        hit.MouseDown += (_, e) =>
        {
            if (e.Buttons != MouseButtons.Primary)
                return;

            e.Handled = true;
            Toggle();
        };
        _arrow.Activated += (_, _) => Toggle();

        Content = new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = 0,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items = { hit, _content }
        };
    }
}
