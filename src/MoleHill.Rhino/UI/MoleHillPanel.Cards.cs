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
    private static Label CreateCollapseChevron(bool collapsed) => new()
    {
        Text = collapsed ? "▶" : "▼",
        VerticalAlignment = VerticalAlignment.Center,
        Width = 14
    };

    /// <summary>
    /// The two-line title block used by every stack card: when collapsed, a bold read-only name over
    /// a summary line; when expanded, the editable <paramref name="nameBox"/> over a subtitle line.
    /// </summary>
    private static StackLayout CreateCardTitleBlock(
        bool collapsed,
        string collapsedName,
        string collapsedSummary,
        TextBox nameBox,
        string expandedSubtitle)
    {
        if (collapsed)
        {
            return new StackLayout
            {
                Orientation = Orientation.Vertical,
                Spacing = 1,
                Items =
                {
                    new Label
                    {
                        Text = collapsedName,
                        Font = new Font(SystemFont.Bold),
                        VerticalAlignment = VerticalAlignment.Center
                    },
                    CreateCardMetaLabel(collapsedSummary)
                }
            };
        }

        return new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = 2,
            Items =
            {
                nameBox,
                CreateCardMetaLabel(expandedSubtitle)
            }
        };
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
        return new Label
        {
            Text = text,
            TextColor = textColor ?? UiTheme.MutedText,
            VerticalAlignment = VerticalAlignment.Center,
            Wrap = WrapMode.Word
        };
    }

    /// <summary>A muted subtitle/summary label used as the second line of a card title block.</summary>
    private static Label CreateCardMetaLabel(string text)
    {
        return new Label
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
            TextColor = UiTheme.MutedText,
            Wrap = WrapMode.Word
        };
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
            ? new ImageView { Image = image, Size = new Size(16, 16) }
            : new Label { Text = glyphFallback, VerticalAlignment = VerticalAlignment.Center };
    }

    /// <summary>A tinted square plate holding a card's type icon, coloured by the card's accent.</summary>
    private static Panel CreateIconPlate(Color accent, Control content)
    {
        return new Panel
        {
            BackgroundColor = new Color(accent.R, accent.G, accent.B, 0.20f),
            Padding = new Padding(6, 4),
            Content = content
        };
    }

    /// <summary>Wraps a finished card in its left accent strip and outer padding/background.</summary>
    private static Panel WrapCardControl(Control card, Panel accentStrip, Color backgroundColor)
    {
        return new Panel
        {
            Padding = new Padding(4, 2),
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
        bool wrapActions = UseWrappedModifierActions();
        bool compactHeader = UseCompactCardHeaders();

        var header = new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = compactHeader || wrapActions ? 4 : 0,
            Padding = new Padding(8, 6, 8, 6),
            BackgroundColor = options.HeaderBackground
        };

        if (compactHeader)
        {
            var compactMetaRow = new StackLayout
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalContentAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Items =
                {
                    options.Handle,
                    options.CollapseControl,
                    options.IconPlate,
                    options.EnabledControl,
                    new StackLayoutItem(new Panel(), expand: true)
                }
            };
            header.Items.Add(new StackLayoutItem(compactMetaRow, HorizontalAlignment.Stretch));
            header.Items.Add(new StackLayoutItem(options.TitleBlock, HorizontalAlignment.Stretch));
            if (options.StatusControls.Count > 0)
                header.Items.Add(new StackLayoutItem(CreateCardStatusRow(options.StatusControls), HorizontalAlignment.Stretch));
            if (options.ActionControls.Count > 0)
                header.Items.Add(new StackLayoutItem(CreateCardActionRow(options.ActionControls), HorizontalAlignment.Stretch));
        }
        else
        {
            var headerTopRow = new StackLayout
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
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
            foreach (var statusControl in options.StatusControls)
                headerTopRow.Items.Add(new StackLayoutItem(statusControl));

            if (options.ActionControls.Count > 0 && !wrapActions)
            {
                foreach (var actionControl in options.ActionControls)
                    headerTopRow.Items.Add(new StackLayoutItem(actionControl));
            }

            header.Items.Add(new StackLayoutItem(headerTopRow, HorizontalAlignment.Stretch));
            if (options.ActionControls.Count > 0 && wrapActions)
                header.Items.Add(new StackLayoutItem(CreateCardActionRow(options.ActionControls), HorizontalAlignment.Stretch));
        }

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

    /// <summary>The status row (first control stretches, the rest sit at the right) used in compact headers.</summary>
    private static StackLayout CreateCardStatusRow(IReadOnlyList<Control> controls)
    {
        var row = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };

        if (controls.Count == 0)
            return row;

        row.Items.Add(new StackLayoutItem(controls[0], expand: true));
        for (int index = 1; index < controls.Count; index++)
            row.Items.Add(new StackLayoutItem(controls[index]));

        return row;
    }

    /// <summary>The right-aligned action-button row used when actions wrap onto their own line.</summary>
    private static StackLayout CreateCardActionRow(IReadOnlyList<Control> actions)
    {
        var row = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                new StackLayoutItem(new Panel(), expand: true)
            }
        };

        foreach (var action in actions)
            row.Items.Add(new StackLayoutItem(action));

        return row;
    }
}
