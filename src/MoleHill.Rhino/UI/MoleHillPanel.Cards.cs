using System;
using System.Collections.Generic;
using Eto.Drawing;
using Eto.Forms;
using MoleHill.Rhino.Model;
using Rhino;

namespace MoleHill.Rhino.UI;

// Reusable building blocks for the collapsible "card" UI shared by the modifier, zone, and object
// stacks. Each stack card has the same anatomy — a collapse chevron, a drag handle, an enable
// checkbox, a title block (name + subtitle when expanded, name + summary when collapsed), and a
// ctrl-collapses-all toggle — differing only in which model collection and collapsed-id set it is
// bound to. These helpers capture that shared anatomy so a new card type is a few lines of binding
// rather than a copy of the whole header.
public sealed partial class MoleHillPanel
{
    /// <summary>
    /// Configuration for <see cref="CreateSharedCardShell"/>: the header controls (drag handle,
    /// chevron, icon plate, enable checkbox, title block), the status/action control rows, the body
    /// shown when expanded, the collapsed flag, the collapse toggle, and the header/card colours.
    /// </summary>
    private sealed class SharedCardShellOptions
    {
        public required Control Handle { get; init; }
        public required Control CollapseControl { get; init; }
        public required Control IconPlate { get; init; }
        public required Control EnabledControl { get; init; }
        public required Control TitleBlock { get; init; }
        public required Action<bool> ToggleCollapsed { get; init; }
        public required bool Collapsed { get; init; }
        public Color CardBackground { get; init; } = UiTheme.CardBackground;
        public Color HeaderBackground { get; init; } = UiTheme.HeaderBackground;
        public IReadOnlyList<Control> StatusControls { get; init; } = Array.Empty<Control>();
        public IReadOnlyList<Control> ActionControls { get; init; } = Array.Empty<Control>();
        public Control? Body { get; init; }
    }

    /// <summary>The expand/collapse chevron shown at the left of every stack card header.</summary>
    private static Label CreateCollapseChevron(bool collapsed)
    {
        var label = UiControls.Label(collapsed ? "▶" : "▼");
        label.Width = UiMetrics.Chs(2);
        return label;
    }

    /// <summary>
    /// A heading for a group of rows that does not collapse: a small caption and a rule across the rest of
    /// the width. The panel's alternative was a GroupBox, whose border boxed in content that was already
    /// visually grouped by the card around it — and a chevron here would promise a collapse that is not
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
    /// A collapsible section header outside the card stack — Terrain Settings, Status, and the modifier
    /// sub-sections.
    ///
    /// These used a boxed icon button to collapse while every card used a bare chevron glyph, so the panel
    /// had two visual languages for the same gesture at two different heights. This is the card's: the same
    /// <see cref="CreateCollapseChevron"/> glyph, the same header fill and padding, and the whole strip is
    /// the hit target rather than a 28px button.
    /// </summary>
    private sealed class SectionHeader : Panel
    {
        private readonly Label _chevron;

        public SectionHeader(string title, Control? trailing, bool expanded, Action<bool> onToggle)
        {
            _chevron = CreateCollapseChevron(!expanded);

            var layout = new StackLayout
            {
                Orientation = Orientation.Horizontal,
                Spacing = UiMetrics.SpaceMedium,
                Padding = new Padding(UiMetrics.CardHorizontalPadding, UiMetrics.CardVerticalPadding),
                VerticalContentAlignment = VerticalAlignment.Center,
                Items = { _chevron, UiControls.Label(title, UiLabelRole.Section) }
            };

            if (trailing != null)
                layout.Items.Add(new StackLayoutItem(trailing, expand: true));

            BackgroundColor = UiTheme.HeaderBackground;
            Content = layout;
            Cursor = Cursors.Pointer;
            Expanded = expanded;

            MouseDown += (_, e) =>
            {
                if (e.Buttons != MouseButtons.Primary)
                    return;

                Expanded = !Expanded;
                _chevron.Text = Expanded ? "▼" : "▶";
                onToggle(Expanded);
            };
        }

        public bool Expanded { get; private set; }
    }

    /// <summary>
    /// The two-line title block used by every stack card: when collapsed, a bold read-only name over
    /// a summary line; when expanded, the editable <paramref name="nameBox"/> over a subtitle line.
    /// </summary>
    private static Control CreateCardTitleBlock(
        bool collapsed,
        string collapsedName,
        string collapsedSummary,
        TextBox nameBox,
        string expandedSubtitle)
    {
        if (collapsed)
        {
            var title = UiControls.Label(collapsedName);
            title.Font = new Font(SystemFont.Bold);
            title.ToolTip = collapsedName;
            return new ResponsiveCardTitle(title, CreateCardMetaLabel(collapsedSummary));
        }

        return new ResponsiveCardTitle(nameBox, CreateCardMetaLabel(expandedSubtitle));
    }

    /// <summary>Card metadata is useful context at normal widths, but the title and actions take
    /// priority in a narrow panel. Hiding it also prevents compact cards becoming needlessly tall.</summary>
    private sealed class ResponsiveCardTitle : Panel
    {
        private readonly Control _secondary;

        public ResponsiveCardTitle(Control primary, Control secondary)
        {
            _secondary = secondary;
            Content = new StackLayout
            {
                Orientation = Orientation.Vertical,
                Spacing = UiMetrics.SpaceXSmall,
                Items = { primary, secondary }
            };
            SizeChanged += (_, _) => UpdateDensity();
        }

        private void UpdateDensity()
        {
            if (Width > 0)
                _secondary.Visible = Width >= UiMetrics.Chs(18);
        }
    }

    /// <summary>
    /// A drag handle wired to start a reorder drag carrying <paramref name="id"/> under the given
    /// <paramref name="dragKey"/> data format (the format the matching drop target listens for).
    /// </summary>
    private Drawable CreateReorderHandle(Guid id, string dragKey, string help)
    {
        var handle = CreateDragHandle();
        ApplyHelp(handle, help);
        handle.MouseDown += (_, e) =>
        {
            if (e.Buttons != MouseButtons.Primary)
                return;

            var data = new DataObject();
            data.SetString(id.ToString(), dragKey);
            handle.DoDragDrop(data, DragEffects.Move);
        };
        return handle;
    }

    /// <summary>A non-reorderable drag handle placeholder (greyed out) with an explanatory tooltip.</summary>
    private Drawable CreateDisabledReorderHandle(string help)
    {
        var handle = CreateDragHandle();
        handle.Enabled = false;
        handle.Cursor = Cursors.Default;
        ApplyHelp(handle, help);
        return handle;
    }

    /// <summary>
    /// Toggles the collapsed state of a single stack card, or — when <paramref name="ctrlHeld"/> is
    /// true — collapses every sibling card (or expands them all when the clicked card was the last
    /// collapsed one). Centralises the ctrl-collapse-all behaviour that all three stacks share.
    /// </summary>
    private void ToggleCardCollapsed(
        HashSet<Guid> collapsedIds,
        Guid id,
        bool ctrlHeld,
        Func<TerrainDefinition, IEnumerable<Guid>> siblingIds,
        Action<TerrainDefinition?> rebuild)
    {
        bool nowCollapsed = !collapsedIds.Contains(id);
        var doc = RhinoDoc.ActiveDoc;
        var terrain = doc == null ? null : _controller.GetSelectedTerrain(doc);

        if (ctrlHeld)
        {
            if (terrain != null)
            {
                if (nowCollapsed)
                {
                    foreach (var siblingId in siblingIds(terrain))
                        collapsedIds.Add(siblingId);
                }
                else
                {
                    collapsedIds.Clear();
                }
            }
        }
        else if (nowCollapsed)
        {
            collapsedIds.Add(id);
        }
        else
        {
            collapsedIds.Remove(id);
        }

        rebuild(terrain);
    }

    /// <summary>A muted secondary status pill used in card headers (type label, "Enabled", etc.).</summary>
    private static Label CreateCardStatusLabel(string text, Color? textColor = null)
    {
        var label = UiControls.Label(text, UiLabelRole.Meta);
        label.TextColor = textColor ?? UiTheme.MutedText;
        label.Width = UiMetrics.Chs(7);
        label.ToolTip = text;
        return label;
    }

    /// <summary>A muted subtitle/summary label used as the second line of a card title block.</summary>
    private static Label CreateCardMetaLabel(string text)
    {
        var label = UiControls.Label(text, UiLabelRole.Meta);
        label.ToolTip = text;
        return label;
    }

    /// <summary>
    /// Builds a card's type-icon control: the 16×16 PNG named <paramref name="iconName"/> when it
    /// resolves, otherwise a text-glyph label (<paramref name="glyphFallback"/>). Mirrors the modifier
    /// card's icon handling so Object/Analysis/Annotation cards can carry real icons too.
    /// </summary>
    private static Control CreateCardIconControl(string? iconName, string glyphFallback)
    {
        var image = string.IsNullOrEmpty(iconName) ? null : PanelIcons.Load(iconName);
        return image != null
            ? new ImageView { Image = image, Size = new Size(UiMetrics.IconSize, UiMetrics.IconSize) }
            : new Label
            {
                Text = glyphFallback,
                VerticalAlignment = VerticalAlignment.Center,
                Width = UiMetrics.Chs(2),
                Wrap = WrapMode.None,
                ToolTip = glyphFallback
            };
    }

    /// <summary>A tinted square plate holding a card's type icon, coloured by the card's accent.</summary>
    private static Panel CreateIconPlate(Color accent, Control content)
    {
        return new Panel
        {
            BackgroundColor = new Color(accent.R, accent.G, accent.B, 0.20f),
            Padding = new Padding(UiMetrics.SpaceSmall, UiMetrics.PropertyRowVerticalPadding),
            Content = content
        };
    }

    /// <summary>Wraps a finished card in its left accent strip and outer padding/background.</summary>
    private static Panel WrapCardControl(Control card, Panel accentStrip, Color backgroundColor)
    {
        return new Panel
        {
            Padding = new Padding(UiMetrics.SpaceSmall, UiMetrics.SpaceXSmall),
            BackgroundColor = backgroundColor,
            Content = new StackLayout
            {
                Orientation = Orientation.Horizontal,
                Spacing = 0,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Items =
                {
                    accentStrip,
                    new StackLayoutItem(card, expand: true)
                }
            }
        };
    }

    /// <summary>
    /// Assembles a stack card from <paramref name="options"/>: a header (handle, chevron, icon,
    /// enable check, title, and status/action controls) over an optional body. The header layout
    /// adapts to the panel width — compact mode stacks the metadata, and narrow widths wrap the
    /// action buttons onto their own row. Clicking the header toggles collapse (ctrl = all).
    /// </summary>
    private Panel CreateSharedCardShell(SharedCardShellOptions options)
    {
        var header = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = UiMetrics.SpaceMedium,
            Padding = new Padding(
                UiMetrics.CardHorizontalPadding,
                UiMetrics.CardVerticalPadding,
                UiMetrics.CardHorizontalPadding,
                UiMetrics.CardVerticalPadding),
            BackgroundColor = options.HeaderBackground,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                options.Handle,
                options.CollapseControl,
                options.IconPlate,
                options.EnabledControl,
                new StackLayoutItem(options.TitleBlock, expand: true)
            }
        };
        var statusHosts = new List<Panel>();
        foreach (var statusControl in options.StatusControls)
        {
            var host = new Panel { Content = statusControl };
            statusHosts.Add(host);
            header.Items.Add(new StackLayoutItem(host));
        }
        foreach (var actionControl in options.ActionControls)
            header.Items.Add(new StackLayoutItem(actionControl));

        void UpdateStatusVisibility()
        {
            bool showStatus = header.Width <= 0 || header.Width >= UiMetrics.HeaderStatusBreak;
            foreach (var host in statusHosts)
                host.Visible = showStatus;
        }

        header.SizeChanged += (_, _) => UpdateStatusVisibility();
        UpdateStatusVisibility();

        header.MouseDown += (_, e) => options.ToggleCollapsed(e.Modifiers.HasFlag(Keys.Control));

        var card = new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = 0
        };
        card.Items.Add(new StackLayoutItem(header, HorizontalAlignment.Stretch));
        if (!options.Collapsed && options.Body != null)
            card.Items.Add(new StackLayoutItem(options.Body, HorizontalAlignment.Stretch));

        return new Panel
        {
            BackgroundColor = options.CardBackground,
            Content = card
        };
    }

    /// <summary>Stacked-squares "duplicate this card" icon button.</summary>
    private static Button MakeDuplicateIconButton(Action action, string help) =>
        MakeIconButton(PanelButtonIcon.Duplicate, (_, _) => action(), help);

    /// <summary>Outline-bin "delete this card" icon button.</summary>
    private static Button MakeDeleteIconButton(Action action, string help) =>
        MakeIconButton(PanelButtonIcon.Delete, (_, _) => action(), help);
}
