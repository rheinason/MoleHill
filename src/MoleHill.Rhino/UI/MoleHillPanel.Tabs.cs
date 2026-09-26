using Eto.Drawing;
using Eto.Forms;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Registry;
using MoleHill.Rhino.Services;
using Rhino;

namespace MoleHill.Rhino.UI;

// Tab strip and tab layouts: the five tabs, lazy rebuild of only the tab on screen, scroll restore, and
// each tab's add toolbar.
public sealed partial class MoleHillPanel : Panel
{
    /// <summary>The five-tab strip, with eye toggles on Zones and Analysis, above the selected tab's scrollable.</summary>
    private Control BuildTabs()
    {
        // ── Custom tab strip with eye toggles on Zones and Analysis ──────
        var tabScrollables = new[]
        {
            BuildScrollable(_modifierStack),
            BuildScrollable(_objectsStack),
            BuildScrollable(_zonesStack),
            BuildScrollable(_analysisStack),
            BuildScrollable(_annotationStack)
        };

        _tabScrollables = tabScrollables;
        _tabContentPanel = new Panel { Content = tabScrollables[Math.Clamp(_selectedTabIndex, 0, tabScrollables.Length - 1)] };

        void SelectTab(int index)
        {
            _selectedTabIndex = Math.Clamp(index, 0, tabScrollables.Length - 1);
            _tabContentPanel.Content = tabScrollables[_selectedTabIndex];
            RebuildVisibleTabLayout();
        }

        _tabChipMap.Clear();
        _tabChipLabelMap.Clear();

        void UpdateTabSelectionStyles()
        {
            foreach (var (tabIndex, panel) in _tabChipMap)
                panel.BackgroundColor = tabIndex == _selectedTabIndex ? UiTheme.ListSelectionBackground : UiTheme.ToolbarBackground;

            foreach (var (tabIndex, label) in _tabChipLabelMap)
                label.TextColor = tabIndex == _selectedTabIndex ? UiTheme.PrimaryText : UiTheme.MutedText;
        }

        Label MakeTabToggleLabel(string toolTip, Action onClick)
        {
            var label = UiControls.Label(string.Empty);
            label.TextAlignment = TextAlignment.Center;
            label.Width = UiMetrics.Chs(2);
            ApplyHelp(label, toolTip);
            label.MouseDown += (_, e) =>
            {
                if (e.Buttons != MouseButtons.Primary)
                    return;

                onClick();
            };
            return label;
        }

        Control MakeTabHeader(string text, string iconName, int tabIndex, Label? toggleLabel = null)
        {
            var textLabel = UiControls.Label(text);
            var mainRow = new StackLayout
            {
                Orientation = Orientation.Horizontal,
                Spacing = UiMetrics.SpaceSmall,
                VerticalContentAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };

            var icon = PanelIcons.Load(iconName);
            ImageView? iconView = null;
            if (icon != null)
            {
                iconView = new ImageView
                {
                    Image = icon,
                    Width = UiMetrics.IconSize,
                    Height = UiMetrics.IconSize,
                    Size = new Size(UiMetrics.IconSize, UiMetrics.IconSize)
                };
                mainRow.Items.Add(iconView);
            }

            mainRow.Items.Add(textLabel);
            var mainPanel = new Panel
            {
                Padding = toggleLabel == null
                    ? new Padding(UiMetrics.SpaceLarge, 5)
                    : new Padding(UiMetrics.SpaceLarge, 5, UiMetrics.SpaceMedium, 5),
                Content = mainRow
            };

            var row = new StackLayout
            {
                Orientation = Orientation.Horizontal,
                Spacing = 0,
                VerticalContentAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Items =
                {
                    new StackLayoutItem(mainPanel, expand: true)
                }
            };

            if (toggleLabel != null)
            {
                var divider = new Panel
                {
                    Width = 1,
                    BackgroundColor = UiTheme.MutedText
                };
                var togglePanel = new Panel
                {
                    Padding = new Padding(UiMetrics.SpaceMedium, 5, UiMetrics.SpaceLarge, 5),
                    Content = toggleLabel
                };

                row.Items.Add(divider);
                row.Items.Add(togglePanel);
            }

            var inner = new Panel
            {
                Content = row
            };
            var outer = new Panel
            {
                BackgroundColor = UiTheme.HeaderBackground,
                Padding = new Padding(1),
                Content = inner
            };

            void SelectThisTab()
            {
                SelectTab(tabIndex);
                UpdateTabSelectionStyles();
            }

            mainPanel.MouseDown += (_, e) =>
            {
                if (e.Buttons == MouseButtons.Primary)
                    SelectThisTab();
            };
            mainRow.MouseDown += (_, e) =>
            {
                if (e.Buttons == MouseButtons.Primary)
                    SelectThisTab();
            };
            textLabel.MouseDown += (_, e) =>
            {
                if (e.Buttons == MouseButtons.Primary)
                    SelectThisTab();
            };
            if (iconView != null)
            {
                iconView.MouseDown += (_, e) =>
                {
                    if (e.Buttons == MouseButtons.Primary)
                        SelectThisTab();
                };
            }

            _tabChipMap[tabIndex] = inner;
            _tabChipLabelMap[tabIndex] = textLabel;

            return outer;
        }

        Label MakeZonesViewButton()
        {
            var label = MakeTabToggleLabel("Toggle between terrain mesh and zone meshes.", () =>
            {
                var doc = RhinoDoc.ActiveDoc;
                var terrain = doc == null ? null : _controller.GetSelectedTerrain(doc);
                if (doc == null || terrain == null)
                    return;

                bool showZonesOnly = terrain.ShowZoneMeshes && !terrain.ShowTerrainMesh;
                MutateSelectedTerrain(t =>
                {
                    t.ShowTerrainMesh = showZonesOnly;
                    t.ShowZoneMeshes = !showZonesOnly;
                }, scheduleRebuild: false);
                _controller.RefreshTerrainDisplay(doc, terrain.TerrainId);
                RefreshUi();
            });
            return label;
        }

        Label MakeAnalysisVisibilityButton()
        {
            var label = MakeTabToggleLabel("Toggle analysis colors and analysis-owned outputs.", () =>
            {
                var doc = RhinoDoc.ActiveDoc;
                var terrain = doc == null ? null : _controller.GetSelectedTerrain(doc);
                if (doc == null || terrain == null)
                    return;

                bool showAnalysis = !terrain.ShowAnalysisOutputs;
                MutateSelectedTerrain(t => t.ShowAnalysisOutputs = showAnalysis, scheduleRebuild: false);
                RefreshTerrainPreview(terrain.TerrainId);
                RefreshUi();
            });
            return label;
        }

        _zonesEyeButton = MakeZonesViewButton();
        _analysisEyeButton = MakeAnalysisVisibilityButton();

        var tabStrip = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = UiMetrics.SpaceXSmall,
            Padding = new Padding(UiMetrics.SpaceSmall, UiMetrics.SpaceXSmall, UiMetrics.SpaceSmall, 0),
            VerticalContentAlignment = VerticalAlignment.Bottom,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                new StackLayoutItem(MakeTabHeader("Modifiers", "TabModifiers", 0), expand: true),
                new StackLayoutItem(MakeTabHeader("Objects", "TabObjects", 1), expand: true),
                new StackLayoutItem(MakeTabHeader("Zones", "TabZones", 2, _zonesEyeButton), expand: true),
                new StackLayoutItem(MakeTabHeader("Analysis", "TabAnalysis", 3, _analysisEyeButton), expand: true),
                new StackLayoutItem(MakeTabHeader("Annotation", "TabAnnotation", 4), expand: true)
            }
        };
        UpdateTabSelectionStyles();

        // Icons-only once the panel is too narrow for five full labels (Blender-style: shrink to fit,
        // never wrap/clip). Threshold is per-tab share of the strip width against a "short label" budget.
        void UpdateTabTextVisibility()
        {
            int tabCount = _tabChipLabelMap.Count;
            if (tabCount == 0)
                return;

            bool showText = tabStrip.Width / tabCount >= UiMetrics.Chs(9);
            foreach (var label in _tabChipLabelMap.Values)
                label.Visible = showText;
        }

        tabStrip.SizeChanged += (_, _) => UpdateTabTextVisibility();
        tabStrip.Shown += (_, _) => UpdateTabTextVisibility();

        var tabsContainer = new DynamicLayout();
        tabsContainer.Add(tabStrip, yscale: false);
        tabsContainer.Add(_tabContentPanel, yscale: true);

        return tabsContainer;
    }

    private static Scrollable BuildScrollable(Control content)
    {
        var scrollable = new Scrollable
        {
            Content = content,
            Border = BorderType.None,
            ExpandContentWidth = false,
            ExpandContentHeight = false
        };
        UiControls.DisableHorizontalScrolling(scrollable);

        // ExpandContentWidth alone leaves a persistent few-px horizontal scrollbar once the vertical
        // scrollbar appears (the WPF backend measures the expand width before reserving the vertical
        // scrollbar's own width). Pin content width to the scrollable's actual client area instead —
        // ClientSize already excludes a visible vertical scrollbar, so this is the width cards should
        // really lay out against.
        void SyncContentWidth()
        {
            int width = scrollable.ClientSize.Width;
            if (width > 0 && content.Width != width)
                content.Width = width;
        }

        scrollable.SizeChanged += (_, _) => SyncContentWidth();
        scrollable.Shown += (_, _) => SyncContentWidth();

        return scrollable;
    }

    private void CaptureSelectedTabIndex()
    {
        // Tab index is now tracked directly in _selectedTabIndex via the custom tab strip.
    }

    /// <summary>
    /// Builds the visible tab's card stack if it is stale, keeping the scroll position.
    ///
    /// A rebuild replaces the whole stack, and the scrollable then snaps to the top — so editing anything
    /// below the fold threw the user back to the start of the list. The position is restored after the
    /// content is in place, clamped by the scrollable itself if the new content is shorter.
    ///
    /// Timed from the inside rather than at a call site. The first attempt wrapped the tab-selection
    /// caller and reported 0 ms against a 94 ms refresh, which isolated nothing - there are two callers
    /// and the refresh path is the other one.
    /// </summary>
    private void RebuildVisibleTabLayout()
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            RebuildVisibleTabLayoutCore();
        }
        finally
        {
            Services.TerrainUiThreadProbe.RecordTabLayout(
                System.Diagnostics.Stopwatch.GetTimestamp() - start);
        }
    }

    private void RebuildVisibleTabLayoutCore()
    {
        if (_tabScrollables == null)
            return;

        int tab = Math.Clamp(_selectedTabIndex, 0, _tabLayoutDirty.Length - 1);
        if (!_tabLayoutDirty[tab])
            return;

        _tabLayoutDirty[tab] = false;

        Scrollable scrollable = _tabScrollables[tab];
        Point scroll = scrollable.ScrollPosition;

        bool wasRefreshing = _isRefreshing;
        _isRefreshing = true;
        try
        {
            switch (tab)
            {
                case 0: RebuildModifierLayout(_lastRefreshedTerrain); break;
                case 1: RebuildObjectsLayout(_lastRefreshedTerrain); break;
                case 2: RebuildZonesLayout(_lastRefreshedTerrain); break;
                case 3: RebuildAnalysisLayout(_lastRefreshedTerrain); break;
                default: RebuildAnnotationLayout(_lastRefreshedTerrain); break;
            }
        }
        finally
        {
            _isRefreshing = wasRefreshing;
        }

        if (scroll.Y > 0)
            Application.Instance.AsyncInvoke(() => RestoreScroll(scrollable, scroll));
    }

    /// <summary>Restores a scroll offset after layout has settled; the control may have shrunk.</summary>
    private void RestoreScroll(Scrollable scrollable, Point scroll)
    {
        if (IsDisposed || scrollable.Content == null)
            return;

        try
        {
            scrollable.ScrollPosition = new Point(0, scroll.Y);
        }
        catch
        {
            // A shorter stack can reject the offset outright; landing at the top is the correct fallback.
        }
    }

    private void RebuildModifierLayout(TerrainDefinition? terrain)
    {
        _modifierCardMap.Clear();
        _modifierSepMap.Clear();
        _modifierStripMap.Clear();
        _modifierStripColors.Clear();
        _modifierStack.Items.Clear();
        _modifierStack.Items.Add(new StackLayoutItem(BuildAddModifierBar(terrain), HorizontalAlignment.Stretch));

        if (terrain == null)
            return;

        var terrainId = terrain.TerrainId;
        foreach (var modifier in Enumerable.Reverse(terrain.Modifiers))
        {
            var modifierId = modifier.Id;

            var innerSep = new Panel { BackgroundColor = Colors.Transparent };
            var outerSep = new Panel { Height = 8, Padding = new Padding(0, 2), Content = innerSep };
            ApplyHelp(outerSep, "Drop here to reorder modifiers.");
            _modifierSepMap[modifierId] = innerSep;
            WireModifierSepDragDrop(outerSep, innerSep, terrainId, modifierId);
            _modifierStack.Items.Add(new StackLayoutItem(outerSep, HorizontalAlignment.Stretch));

            var box = CreateModifierCard(terrain, modifier);
            _modifierCardMap[modifierId] = box;
            var kind = GetModifierKind(modifier);
            var typeColor = ModifierTypeColor(kind);
            bool isPinnedBaseTriangulate = modifier is TriangulateModifierDefinition &&
                                           terrain.Modifiers.Count > 0 &&
                                           terrain.Modifiers[0].Id == modifierId;
            var strip = new Panel { Width = UiMetrics.CardAccentWidth, BackgroundColor = typeColor };
            _modifierStripMap[modifierId] = strip;
            _modifierStripColors[modifierId] = typeColor;
            var wrapper = WrapCardControl(box, strip, isPinnedBaseTriangulate ? UiTheme.BaseCardBackground : UiTheme.CardBackground);
            WireModifierCardDragDrop(wrapper, terrainId, modifierId);
            _modifierStack.Items.Add(new StackLayoutItem(wrapper, HorizontalAlignment.Stretch));
        }

        var tailInner = new Panel { BackgroundColor = Colors.Transparent };
        var tailOuter = new Panel { Height = 8, Padding = new Padding(0, 2), Content = tailInner };
        ApplyHelp(tailOuter, "Drop here to reorder modifiers.");
        _modifierSepMap[Guid.Empty] = tailInner;
        WireModifierSepDragDrop(tailOuter, tailInner, terrainId, Guid.Empty);
        _modifierStack.Items.Add(new StackLayoutItem(tailOuter, HorizontalAlignment.Stretch));
    }

    private void RebuildObjectsLayout(TerrainDefinition? terrain)
    {
        _objectsStack.Items.Clear();
        if (terrain == null)
            return;

        _objectsStack.Items.Add(new StackLayoutItem(BuildObjectAddButtons(terrain), HorizontalAlignment.Stretch));
        if (terrain.Objects.Count == 0)
        {
            _objectsStack.Items.Add(new StackLayoutItem(new Panel
            {
                Padding = new Padding(8),
                Content = new Label
                {
                    Text = "No object definitions yet. Add one to project or orient Rhino objects onto the terrain while keeping their source layers.",
                    TextColor = UiTheme.MutedText,
                    Wrap = WrapMode.Word
                }
            }, HorizontalAlignment.Stretch));
            return;
        }

        foreach (var definition in terrain.Objects)
        {
            var box = CreateObjectCard(terrain, definition);
            var strip = new Panel { Width = UiMetrics.CardAccentWidth, BackgroundColor = TerrainObjectTypeColor(GetTerrainObjectKind(definition)) };
            var wrapper = WrapCardControl(box, strip, UiTheme.CardBackground);
            _objectsStack.Items.Add(new StackLayoutItem(wrapper, HorizontalAlignment.Stretch));
        }
    }

    private void RebuildAnalysisLayout(TerrainDefinition? terrain)
    {
        _analysisCardMap.Clear();
        _analysisSepMap.Clear();
        _analysisStripMap.Clear();
        _analysisStripColors.Clear();
        _analysisStack.Items.Clear();

        if (terrain == null)
            return;

        _analysisStack.Items.Add(new StackLayoutItem(BuildAnalysisToolbar(terrain), HorizontalAlignment.Stretch));

        var visualAnalyses = terrain.Analyses;
        if (visualAnalyses.Count == 0)
        {
            _analysisStack.Items.Add(new StackLayoutItem(new Panel
            {
                Padding = new Padding(8),
                Content = new Label
                {
                    Text = "No analyses yet. Add one to inspect slope, elevation, cut/fill, or earthworks.",
                    TextColor = UiTheme.MutedText,
                    Wrap = WrapMode.Word
                }
            }, HorizontalAlignment.Stretch));
            return;
        }

        bool hasActiveAnalysis = false;
        var terrainId = terrain.TerrainId;
        foreach (var analysisItem in visualAnalyses)
        {
            bool isActive = !hasActiveAnalysis &&
                            analysisItem.IsEnabled &&
                            TerrainAnalysisPreviewBuilder.SupportsTerrainPreview(analysisItem);
            if (isActive)
                hasActiveAnalysis = true;

            var analysisId = analysisItem.Id;
            var innerSep = new Panel { BackgroundColor = Colors.Transparent };
            var outerSep = new Panel { Height = 8, Padding = new Padding(0, 2), Content = innerSep };
            ApplyHelp(outerSep, "Drop here to reorder analyses.");
            _analysisSepMap[analysisId] = innerSep;
            WireAnalysisSepDragDrop(outerSep, innerSep, terrainId, analysisId);
            _analysisStack.Items.Add(new StackLayoutItem(outerSep, HorizontalAlignment.Stretch));

            var box = CreateAnalysisCard(terrain, analysisItem, isActive);
            _analysisCardMap[analysisId] = box;
            var kind = GetAnalysisKind(analysisItem);
            var typeColor = AnalysisTypeColor(kind);
            var strip = new Panel { Width = UiMetrics.CardAccentWidth, BackgroundColor = typeColor };
            _analysisStripMap[analysisId] = strip;
            _analysisStripColors[analysisId] = typeColor;
            var wrapper = WrapCardControl(box, strip, UiTheme.CardBackground);
            WireAnalysisCardDragDrop(wrapper, terrainId, analysisId);
            _analysisStack.Items.Add(new StackLayoutItem(wrapper, HorizontalAlignment.Stretch));
        }

        var tailInner = new Panel { BackgroundColor = Colors.Transparent };
        var tailOuter = new Panel { Height = 8, Padding = new Padding(0, 2), Content = tailInner };
        ApplyHelp(tailOuter, "Drop here to reorder analyses.");
        _analysisSepMap[Guid.Empty] = tailInner;
        WireAnalysisSepDragDrop(tailOuter, tailInner, terrainId, Guid.Empty);
        _analysisStack.Items.Add(new StackLayoutItem(tailOuter, HorizontalAlignment.Stretch));
    }

    private void RebuildAnnotationLayout(TerrainDefinition? terrain)
    {
        _annotationCardMap.Clear();
        _annotationSepMap.Clear();
        _annotationStripMap.Clear();
        _annotationStripColors.Clear();
        _annotationStack.Items.Clear();

        if (terrain == null)
            return;

        _annotationStack.Items.Add(new StackLayoutItem(BuildAnnotationToolbar(terrain), HorizontalAlignment.Stretch));

        var annotationItems = terrain.Annotations;
        if (annotationItems.Count == 0)
        {
            _annotationStack.Items.Add(new StackLayoutItem(new Panel
            {
                Padding = new Padding(8),
                Content = new Label
                {
                    Text = "No annotations yet. Add contours, elevation labels, or slope labels.",
                    TextColor = UiTheme.MutedText,
                    Wrap = WrapMode.Word
                }
            }, HorizontalAlignment.Stretch));
            return;
        }

        var terrainId = terrain.TerrainId;
        foreach (var analysisItem in annotationItems)
        {
            var analysisId = analysisItem.Id;
            var innerSep = new Panel { BackgroundColor = Colors.Transparent };
            var outerSep = new Panel { Height = 8, Padding = new Padding(0, 2), Content = innerSep };
            ApplyHelp(outerSep, "Drop here to reorder annotations.");
            _annotationSepMap[analysisId] = innerSep;
            WireAnnotationSepDragDrop(outerSep, innerSep, terrainId, analysisId);
            _annotationStack.Items.Add(new StackLayoutItem(outerSep, HorizontalAlignment.Stretch));

            var box = CreateAnnotationCard(terrain, analysisItem);
            _annotationCardMap[analysisId] = box;
            var kind = GetAnnotationKind(analysisItem);
            var typeColor = AnnotationTypeColor(kind);
            var strip = new Panel { Width = UiMetrics.CardAccentWidth, BackgroundColor = typeColor };
            _annotationStripMap[analysisId] = strip;
            _annotationStripColors[analysisId] = typeColor;
            var wrapper = WrapCardControl(box, strip, UiTheme.CardBackground);
            WireAnnotationCardDragDrop(wrapper, terrainId, analysisId);
            _annotationStack.Items.Add(new StackLayoutItem(wrapper, HorizontalAlignment.Stretch));
        }

        var tailInner = new Panel { BackgroundColor = Colors.Transparent };
        var tailOuter = new Panel { Height = 8, Padding = new Padding(0, 2), Content = tailInner };
        ApplyHelp(tailOuter, "Drop here to reorder annotations.");
        _annotationSepMap[Guid.Empty] = tailInner;
        WireAnnotationSepDragDrop(tailOuter, tailInner, terrainId, Guid.Empty);
        _annotationStack.Items.Add(new StackLayoutItem(tailOuter, HorizontalAlignment.Stretch));
    }

    private Control CreateSectionToolbar(string title, Button primaryButton, string? helperText = null, Control? trailingControl = null)
    {
        var section = new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = UiMetrics.SpaceSmall,
            Padding = new Padding(
                UiMetrics.SectionHorizontalPadding,
                UiMetrics.SectionTopPadding,
                UiMetrics.SectionHorizontalPadding,
                UiMetrics.SectionBottomPadding),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                UiControls.Label(title, UiLabelRole.Section)
            }
        };

        bool hasHelperText = !string.IsNullOrWhiteSpace(helperText);
        var row = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = UiMetrics.SpaceLarge,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                primaryButton
            }
        };

        if (hasHelperText)
        {
            row.Items.Add(new StackLayoutItem(UiControls.Label(helperText!, UiLabelRole.Meta, WrapMode.Word), expand: true));
        }
        else
        {
            row.Items.Add(new StackLayoutItem(new Panel(), expand: true));
        }

        if (trailingControl != null)
            row.Items.Add(new StackLayoutItem(trailingControl));

        section.Items.Add(new StackLayoutItem(row, HorizontalAlignment.Stretch));
        return section;
    }

    private Control BuildAddModifierBar(TerrainDefinition? terrain)
    {
        var addButton = MakeToolbarButton("Add Modifier", (_, _) => { }, "Add a modifier above the base geometry");
        if (terrain != null)
        {
            var menu = new ContextMenu();
            foreach (var descriptor in TerrainTypeRegistry.Modifiers
                         .Where(d => d.CanCreateFromMenu)
                         .OrderBy(d => d.SortOrder))
            {
                var item = new ButtonMenuItem
                {
                    Text = descriptor.DisplayName,
                    Image = PanelIcons.Load(descriptor.IconName)
                };
                var capturedKind = descriptor.Kind;
                var capturedTerrainId = terrain.TerrainId;
                item.Click += (_, _) =>
                {
                    var doc = RhinoDoc.ActiveDoc;
                    if (doc == null)
                        return;

                    _controller.AddModifier(doc, capturedTerrainId, capturedKind);
                    RebuildModifierLayout(_controller.GetSelectedTerrain(doc));
                };
                menu.Items.Add(item);
            }

            addButton.Click += (_, _) => menu.Show(addButton);
        }
        else
        {
            addButton.Enabled = false;
        }

        return CreateSectionToolbar(
            "MODIFIER STACK",
            addButton);
    }

    private Control BuildAnalysisToolbar(TerrainDefinition terrain)
    {
        var addButton = MakeToolbarButton("Add Analysis", (_, _) => { }, "Add an analysis card");
        var menu = new ContextMenu();
        foreach (var descriptor in AnalysisTypeRegistry.Analyses.OrderBy(item => item.SortOrder))
        {
            var item = new ButtonMenuItem { Text = descriptor.MenuLabel };
            var capturedKind = descriptor.Kind;
            item.Click += (_, _) => AddAnalysis(capturedKind);
            menu.Items.Add(item);
        }

        addButton.Click += (_, _) => menu.Show(addButton);
        return CreateSectionToolbar("ANALYSIS", addButton);
    }

    private Control BuildAnnotationToolbar(TerrainDefinition terrain)
    {
        var addButton = MakeToolbarButton("Add Annotation", (_, _) => { }, "Add an annotation card");
        var menu = new ContextMenu();
        foreach (var descriptor in AnnotationTypeRegistry.Annotations.OrderBy(item => item.SortOrder))
        {
            var item = new ButtonMenuItem { Text = descriptor.MenuLabel };
            var capturedKind = descriptor.Kind;
            item.Click += (_, _) => AddAnnotation(capturedKind);
            menu.Items.Add(item);
        }
        addButton.Click += (_, _) => menu.Show(addButton);
        return CreateSectionToolbar("ANNOTATION", addButton);
    }

    private static void UpdateZonesTabButton(Label? button, TerrainDefinition? terrain)
    {
        if (button == null)
            return;

        bool showZonesOnly = terrain?.ShowZoneMeshes == true && terrain.ShowTerrainMesh != true;
        button.Text = showZonesOnly ? "\u25A6" : "\u25A0";
        button.TextColor = terrain == null ? UiTheme.MutedText : UiTheme.PrimaryText;
        button.ToolTip = showZonesOnly
            ? "Showing zone meshes. Click to show the terrain mesh."
            : "Showing the terrain mesh. Click to show zone meshes.";
    }

    private static void UpdateAnalysisTabButton(Label? button, TerrainDefinition? terrain)
    {
        if (button == null)
            return;

        bool isVisible = terrain?.ShowAnalysisOutputs != false;
        button.Text = isVisible ? "\u25CF" : "\u25CB";
        button.TextColor = isVisible ? UiTheme.PrimaryText : UiTheme.MutedText;
        button.ToolTip = isVisible
            ? "Analysis preview is visible. Click to hide analysis colors and outputs."
            : "Analysis preview is hidden. Click to show analysis colors and outputs.";
    }
}
