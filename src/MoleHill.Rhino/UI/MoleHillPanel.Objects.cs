using Eto.Drawing;
using Eto.Forms;
using MoleHill.Core.Analysis;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Registry;
using MoleHill.Rhino.Services;
using Rhino;
using InstanceDefinition = Rhino.DocObjects.InstanceDefinition;
using RhinoObjectType = Rhino.DocObjects.ObjectType;

namespace MoleHill.Rhino.UI;

// Objects tab: add buttons, object/scatter card + body builders, scatter block selection.
public sealed partial class MoleHillPanel
{
    private Control BuildObjectAddButtons(TerrainDefinition terrain)
    {
        var addButton = MakeToolbarButton("Add Object", (_, _) => { }, "Add a terrain object definition.");
        var menu = new ContextMenu();

        foreach (var descriptor in ObjectTypeRegistry.Objects)
        {
            string kind = descriptor.Kind;
            var item = new ButtonMenuItem { Text = descriptor.DisplayName, ToolTip = descriptor.Subtitle };
            item.Click += (_, _) =>
            {
                var doc = RhinoDoc.ActiveDoc;
                if (doc == null)
                    return;

                _controller.AddObjectDefinition(doc, terrain.TerrainId, kind);
                RebuildObjectsLayout(_controller.GetSelectedTerrain(doc));
            };
            menu.Items.Add(item);
        }

        addButton.Click += (_, _) => menu.Show(addButton);

        return CreateSectionToolbar(
            "TERRAIN OBJECTS",
            addButton);
    }

    private Panel CreateObjectCard(TerrainDefinition terrain, TerrainObjectDefinition definition)
    {
        Guid terrainId = terrain.TerrainId, id = definition.Id;

        return CreateCard(new CardSpec
        {
            Stack = _objectCards,
            Id = id,
            Name = definition.Name,
            NameHelp = "Object definition label. Press Enter or click away to rename.",
            Rename = text => MutateObjectDefinition(terrainId, id, item => item.Name = text, scheduleRebuild: false),
            IsEnabled = definition.IsEnabled,
            EnableHelp = "Enable or disable this object definition without deleting it.",
            SetEnabled = value => MutateObjectDefinition(terrainId, id, item => item.IsEnabled = value),
            TypeName = GetTerrainObjectTypeLabel(definition),
            TypeIcon = CreateCardIconControl(GetTerrainObjectIconName(definition), GetTerrainObjectIconLabel(definition)),
            Summary = () => AppendRuntimeDiagnosticSummary(
                GetTerrainObjectCollapsedSummary(definition),
                terrain,
                new RuntimeOverlayOwner(RuntimeOverlayOwnerKind.Object, id)),
            Description = GetTerrainObjectSubtitle(definition),
            HandleHelp = "Object definition cards use the modifier card layout. Reordering is not enabled yet.",
            Reorderable = false,
            Duplicate = (() =>
            {
                if (RhinoDoc.ActiveDoc is { } doc)
                    _controller.DuplicateObjectDefinition(doc, terrainId, id);
            }, "Duplicate this object definition."),
            Delete = (() =>
            {
                if (RhinoDoc.ActiveDoc is { } doc)
                    _controller.RemoveObjectDefinition(doc, terrainId, id);
            }, "Delete this object definition and restore any currently placed objects."),
            Body = () => CreateObjectBody(terrain, definition)
        });
    }

    private Control CreateObjectBody(TerrainDefinition terrain, TerrainObjectDefinition definition)
    {
        // Every object type declares its rows (ParameterSchemaGuardTests.EveryCardType_DeclaresItsRows).
        var layout = UiLayouts.CardBody();
        BuildSchemaObjectBody(layout, terrain, definition);
        Control? diagnosticsRow = CreateRuntimeDiagnosticsRow(
            terrain,
            new RuntimeOverlayOwner(RuntimeOverlayOwnerKind.Object, definition.Id),
            definition is ScatterObjectDefinition ? "Show rejected samples" : null);
        if (diagnosticsRow != null)
            layout.AddRow(diagnosticsRow);
        return layout;
    }

    private Control CreateScatterBlockMixEditor(TerrainDefinition terrain, ScatterObjectDefinition scatter)
    {
        void Mutate(Action<ScatterObjectDefinition> apply, bool live = false)
        {
            MutateObjectDefinition(terrain.TerrainId, scatter.Id, item =>
            {
                if (item is ScatterObjectDefinition target)
                    apply(target);
            }, deferDocumentSave: live, suppressImmediateUiRefresh: live);
        }

        var layout = new DynamicLayout { DefaultSpacing = new Size(UiMetrics.SpaceMedium, UiMetrics.SpaceSmall) };
        layout.AddRow(CreateScatterBlockMixHeader(
            scatter.Blocks.Count,
            (_, _) => AddScatterBlocksFromSelector(terrain, scatter.Id)));

        var doc = RhinoDoc.ActiveDoc;
        ScatterBuildSummary? result = doc == null ? null : _controller.GetScatterSummaries(doc, terrain.TerrainId)
            .FirstOrDefault(item => item.DefinitionId == scatter.Id);
        double totalWeight = scatter.Blocks.Where(entry => entry.IsEnabled).Sum(entry => Math.Max(0, entry.Weight) *
            (!string.IsNullOrWhiteSpace(entry.BlockDefinitionName) ? 1 : doc == null ? 0 :
                RhinoSourceResolver.ResolveObjects(doc, entry.Source).OfType<global::Rhino.DocObjects.InstanceObject>().Count()));

        // Along a curve in Sequence the mix is an ordered cycle (design review Step 61): rows show their place
        // in it and drag to reorder; weight and share mean nothing there and are hidden.
        bool sequence = scatter.CyclesInSequence && scatter.Blocks.Count > 1;
        string dragKey = $"scatter-block:{scatter.Id}";
        void MoveBlock(int from, int before) => Mutate(s => StackReorder.MoveToIndex(s.Blocks, from, before));
        int position = 0;

        for (int blockIndex = 0; blockIndex < scatter.Blocks.Count; blockIndex++)
        {
            int index = blockIndex;
            ScatterBlockEntry entry = scatter.Blocks[index];
            int? place = entry.IsEnabled ? ++position : null;
            if (sequence && !string.IsNullOrWhiteSpace(entry.BlockDefinitionName))
            {
                layout.AddRow(CreateScatterSequenceRow(
                    entry.BlockDefinitionName!,
                    place,
                    index,
                    dragKey,
                    MoveBlock,
                    () => Mutate(s => { if (index < s.Blocks.Count) s.Blocks.RemoveAt(index); }),
                    entry.IsEnabled,
                    enabled => Mutate(s => { if (index < s.Blocks.Count) s.Blocks[index].IsEnabled = enabled; }),
                    result == null ? "count pending" : $"{(result.BlockCounts.TryGetValue(entry.BlockDefinitionName!, out int placed) ? placed : 0):N0} placed"));
            }
            else if (!string.IsNullOrWhiteSpace(entry.BlockDefinitionName))
            {
                layout.AddRow(CreateScatterBlockRow(
                    entry.BlockDefinitionName!,
                    entry.Weight,
                    value => Mutate(s => { if (index < s.Blocks.Count) s.Blocks[index].Weight = value; }, live: true),
                    () => Mutate(s => { if (index < s.Blocks.Count) s.Blocks.RemoveAt(index); }),
                    entry.IsEnabled,
                    enabled => Mutate(s => { if (index < s.Blocks.Count) s.Blocks[index].IsEnabled = enabled; }),
                    $"{(entry.IsEnabled && totalWeight > 0 ? 100 * Math.Max(0, entry.Weight) / totalWeight : 0):0.#}% mix · " +
                        (result == null ? "count pending" : $"{(result.BlockCounts.TryGetValue(entry.BlockDefinitionName!, out int count) ? count : 0):N0} placed")));

            }
            else
            {
                var sourceEditor = CreateSourceEditor(
                    $"Block {index + 1}",
                    entry.Source,
                    apply => Mutate(s => { if (index < s.Blocks.Count) apply(s.Blocks[index].Source); }),
                    RhinoObjectType.InstanceReference,
                    doc => _controller.GetSelectedLayerPaths(doc),
                    "Block instance(s) to scatter for this entry.");
                var enabledCheck = new CheckBox { Text = "Include this source entry", Checked = entry.IsEnabled };
                enabledCheck.CheckedChanged += (_, _) => Mutate(s => { if (index < s.Blocks.Count) s.Blocks[index].IsEnabled = enabledCheck.Checked == true; });
                layout.AddRow(enabledCheck);
                layout.AddRow(CreateLegacyScatterBlockRow(
                    sourceEditor,
                    entry.Weight,
                    value => Mutate(s => { if (index < s.Blocks.Count) s.Blocks[index].Weight = value; }, live: true),
                    () => Mutate(s => { if (index < s.Blocks.Count) s.Blocks.RemoveAt(index); }),
                    showWeight: !sequence));
            }
        }

        if (sequence)
            layout.AddRow(CreateScatterSequenceTail(scatter.Blocks.Count, dragKey, MoveBlock));

        if (scatter.Blocks.Count == 0)
            layout.AddRow(CreateScatterBlockEmptyState());

        return layout;
    }

    private Control CreateScatterBlockRow(
        string blockName,
        double weight,
        Action<double> onWeightChanged,
        Action onRemove,
        bool enabled,
        Action<bool> onEnabledChanged,
        string quantities)
    {
        const string weightHelp = "Relative likelihood this block is chosen per instance.";
        const string removeHelp = "Remove this block from the scatter mix.";

        var nameLabel = new Label
        {
            Text = TruncateMiddle(blockName, 22),
            TextColor = UiTheme.InputText,
            VerticalAlignment = VerticalAlignment.Center,
            Wrap = WrapMode.None
        };
        ApplyHelp(nameLabel, blockName);

        var weightEditor = CreateCompactScatterWeightEditor(weight, onWeightChanged, weightHelp);
        var removeButton = MakeIconButton(PanelButtonIcon.Clear, (_, _) => onRemove(), removeHelp);

        weightEditor.Enabled = enabled;
        var include = new CheckBox { Checked = enabled };
        ApplyHelp(include, "Include this block without removing it from the mix.");
        include.CheckedChanged += (_, _) => onEnabledChanged(include.Checked == true);
        var thumbnail = new ImageView { Size = new Size(28, 28) };
        thumbnail.Shown += (_, _) =>
        {
            if (RhinoDoc.ActiveDoc is { } doc && doc.InstanceDefinitions.Find(blockName) is { } block)
                thumbnail.Image = BlockThumbnailRenderer.Get(doc, block, 28);
        };
        return new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = UiMetrics.SpaceSmall,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                new StackLayout { Orientation = Orientation.Horizontal, Spacing = UiMetrics.SpaceMedium,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Items = { include, thumbnail, new StackLayoutItem(nameLabel, expand: true), removeButton } },
                new Label { Text = quantities, TextColor = UiTheme.MutedText, Wrap = WrapMode.Word },
                weightEditor
            }
        };
    }

    /// <summary>
    /// A block's row in a sequence: drag grip, inclusion checkbox, its place in the cycle ("–" when left
    /// out), thumbnail, name and remove, with its placed count below. Dropping another row on it puts that
    /// block just before this one.
    /// </summary>
    private Control CreateScatterSequenceRow(
        string blockName,
        int? place,
        int index,
        string dragKey,
        Action<int, int> move,
        Action onRemove,
        bool enabled,
        Action<bool> onEnabledChanged,
        string quantities)
    {
        var grip = CardStack.CreateGrip();
        grip.ToolTip = "Drag to move this block in the sequence.";
        grip.MouseDown += (_, e) =>
        {
            if (e.Buttons != MouseButtons.Primary)
                return;
            var data = new DataObject();
            data.SetString(index.ToString(System.Globalization.CultureInfo.InvariantCulture), dragKey);
            grip.DoDragDrop(data, DragEffects.Move);
        };

        var include = new CheckBox { Checked = enabled };
        ApplyHelp(include, "Include this block in the sequence. Unticked, it keeps its place in the list but is skipped.");
        include.CheckedChanged += (_, _) => onEnabledChanged(include.Checked == true);

        var placeLabel = UiControls.Label(place?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "–", UiLabelRole.Meta);
        placeLabel.Width = UiMetrics.Chs(2);
        placeLabel.TextAlignment = TextAlignment.Right;
        ApplyHelp(placeLabel, place == null ? "Left out of the sequence." : $"Block {place} of the repeating sequence.");

        var nameLabel = new Label
        {
            Text = TruncateMiddle(blockName, 22),
            TextColor = UiTheme.InputText,
            VerticalAlignment = VerticalAlignment.Center,
            Wrap = WrapMode.None
        };
        ApplyHelp(nameLabel, blockName);

        var thumbnail = new ImageView { Size = new Size(28, 28) };
        thumbnail.Shown += (_, _) =>
        {
            if (RhinoDoc.ActiveDoc is { } doc && doc.InstanceDefinitions.Find(blockName) is { } block)
                thumbnail.Image = BlockThumbnailRenderer.Get(doc, block, 28);
        };
        var removeButton = MakeIconButton(PanelButtonIcon.Clear, (_, _) => onRemove(), "Remove this block from the scatter mix.");

        var row = new Panel
        {
            AllowDrop = true,
            Content = new StackLayout
            {
                Orientation = Orientation.Vertical,
                Spacing = UiMetrics.SpaceXSmall,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Items =
                {
                    new StackLayout
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = UiMetrics.SpaceMedium,
                        VerticalContentAlignment = VerticalAlignment.Center,
                        Items = { grip, include, placeLabel, thumbnail, new StackLayoutItem(nameLabel, expand: true), removeButton }
                    },
                    UiControls.Label(quantities, UiLabelRole.Meta)
                }
            }
        };
        WireSequenceDrop(row, index, dragKey, move);
        return row;
    }

    /// <summary>The drop target after the last row, which moves a block to the end of the sequence.</summary>
    private static Control CreateScatterSequenceTail(int count, string dragKey, Action<int, int> move)
    {
        var tail = new Panel { Height = UiMetrics.StackGap, AllowDrop = true, ToolTip = "Drop here to move a block to the end of the sequence." };
        WireSequenceDrop(tail, count, dragKey, move);
        return tail;
    }

    private static void WireSequenceDrop(Panel target, int before, string dragKey, Action<int, int> move)
    {
        target.DragEnter += (_, e) =>
        {
            if (!e.Data.Contains(dragKey))
                return;
            e.Effects = DragEffects.Move;
            target.BackgroundColor = UiTheme.DragHighlight;
        };
        target.DragLeave += (_, _) => target.BackgroundColor = Colors.Transparent;
        target.DragDrop += (_, e) =>
        {
            target.BackgroundColor = Colors.Transparent;
            if (e.Data.Contains(dragKey) &&
                int.TryParse(e.Data.GetString(dragKey), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int from))
            {
                move(from, before);
            }
        };
    }

    private Control CreateScatterBlockMixHeader(int blockCount, EventHandler<EventArgs> addBlocks)
    {
        string countText = blockCount == 1 ? "1 block" : $"{blockCount} blocks";
        var label = new Label
        {
            Text = "Blocks",
            TextColor = UiTheme.MutedText,
            VerticalAlignment = VerticalAlignment.Center
        };
        var count = new Label
        {
            Text = countText,
            TextColor = UiTheme.MutedText,
            VerticalAlignment = VerticalAlignment.Center
        };
        var addButton = MakeInlineButton("Add", addBlocks, "Pick block definitions to add to the weighted mix.");

        return new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = UiMetrics.SpaceMedium,
            Padding = new Padding(0, UiMetrics.SpaceMedium, 0, UiMetrics.SpaceHairline),
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                label,
                new StackLayoutItem(new Panel(), expand: true),
                count,
                addButton
            }
        };
    }

    private static Control CreateScatterBlockEmptyState()
    {
        return new Panel
        {
            BackgroundColor = UiTheme.InputBackground,
            Padding = new Padding(UiMetrics.SpaceLarge, UiMetrics.SpaceMedium),
            Content = new Label
            {
                Text = "No blocks selected",
                TextColor = UiTheme.MutedText,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
    }

    private Control CreateLegacyScatterBlockRow(
        Control sourceEditor,
        double weight,
        Action<double> onWeightChanged,
        Action onRemove,
        bool showWeight = true)
    {
        var weightEditor = CreateCompactScatterWeightEditor(
            weight,
            onWeightChanged,
            "Relative likelihood this legacy source block is chosen per instance.");
        var removeButton = MakeIconButton(PanelButtonIcon.Clear, (_, _) => onRemove(), "Remove this block from the scatter mix.");

        var controls = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = UiMetrics.SpaceMedium,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                new StackLayoutItem(showWeight ? weightEditor : null, expand: true),
                removeButton
            }
        };

        return new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = UiMetrics.SpaceSmall,
            Padding = new Padding(0, UiMetrics.SpaceSmall),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                new StackLayoutItem(sourceEditor, HorizontalAlignment.Stretch),
                new StackLayoutItem(controls, HorizontalAlignment.Stretch)
            }
        };
    }

    private Control CreateCompactScatterWeightEditor(double value, Action<double> onChanged, string help)
    {
        double currentMin = 0.0;
        double currentMax = 10.0;
        ExpandSliderRange(value, ref currentMin, ref currentMax);

        var slider = new Slider
        {
            MinValue = 0,
            MaxValue = 1000,
            Width = UiMetrics.SliderMin
        };

        var valueText = new TextBox
        {
            Width = UiMetrics.ShortLabel,
            TextColor = UiTheme.MutedText,
            TextAlignment = TextAlignment.Right
        };

        ApplyHelp(slider, help);
        ApplyHelp(valueText, help);
        StyleTextBox(valueText);

        double committedValue = ClampSliderValue(value, 0.0, null);
        double pendingValue = committedValue;
        bool syncing = false;
        bool sliderEditActive = false;
        bool textEditActive = false;
        bool unloaded = false;
        bool timerDisposed = false;
        var timer = new UITimer { Interval = UiTiming.SliderCommitSeconds };

        void BeginSliderEdit()
        {
            if (sliderEditActive || unloaded || timerDisposed)
                return;

            sliderEditActive = true;
            BeginControllerRefreshDeferral();
        }

        void EndSliderEdit()
        {
            if (!sliderEditActive)
                return;

            sliderEditActive = false;
            EndControllerRefreshDeferral();
        }

        void SyncControls(double numericValue)
        {
            numericValue = ClampSliderValue(numericValue, 0.0, null);
            syncing = true;
            ExpandSliderRange(numericValue, ref currentMin, ref currentMax);
            slider.Value = ToSliderValue(numericValue, currentMin, currentMax, slider.MaxValue);
            if (!textEditActive)
                valueText.Text = FormatSliderValue(numericValue, 2);
            syncing = false;
        }

        void Commit(double numericValue)
        {
            if (unloaded || timerDisposed)
                return;
            timer.Stop();
            numericValue = ClampSliderValue(numericValue, 0.0, null);
            pendingValue = numericValue;
            SyncControls(numericValue);
            if (!TerrainCommitGuard.HasMeaningfulNumericChange(committedValue, numericValue))
                return;

            committedValue = numericValue;
            onChanged(numericValue);
        }

        void OnTimerElapsed(object? sender, EventArgs e) => Commit(pendingValue);
        timer.Elapsed += OnTimerElapsed;
        valueText.GotFocus += (_, _) =>
        {
            if (unloaded || timerDisposed)
                return;
            timer.Stop();
            textEditActive = true;
            BeginSliderEdit();
        };
        void CommitText(bool cancel = false)
        {
            if (unloaded || timerDisposed)
                return;
            double number = committedValue;
            if (!cancel && (!UserNumber.TryParseLeading(valueText.Text, out number) || !double.IsFinite(number)))
                number = committedValue;
            textEditActive = false;
            Commit(number);
            EndSliderEdit();
        }
        valueText.LostFocus += (_, _) => CommitText();
        valueText.KeyDown += (_, e) =>
        {
            if (e.Key is Keys.Enter or Keys.Escape)
            {
                CommitText(e.Key == Keys.Escape);
                e.Handled = true;
            }
        };
        slider.MouseDown += (_, e) =>
        {
            if (e.Buttons == MouseButtons.Primary)
                BeginSliderEdit();
        };
        slider.MouseUp += (_, e) =>
        {
            if (e.Buttons == MouseButtons.Primary)
            {
                Commit(pendingValue);
                EndSliderEdit();
            }
        };
        slider.LostFocus += (_, _) =>
        {
            Commit(pendingValue);
            EndSliderEdit();
        };
        slider.ValueChanged += (_, _) =>
        {
            if (_isRefreshing || syncing || unloaded || timerDisposed)
                return;

            BeginSliderEdit();
            pendingValue = FromSliderValue(slider.Value, currentMin, currentMax, slider.MaxValue);
            SyncControls(pendingValue);
            timer.Stop();
            timer.Start();
        };
        slider.UnLoad += (_, _) =>
        {
            // UnLoad can repeat during card refresh, and a tab can load again.
            unloaded = true;
            if (!timerDisposed)
            {
                timerDisposed = true;
                timer.Stop();
                timer.Elapsed -= OnTimerElapsed;
                timer.Dispose();
            }
            EndSliderEdit();
        };
        slider.LoadComplete += (_, _) =>
        {
            if (timerDisposed)
            {
                timer = new UITimer { Interval = UiTiming.SliderCommitSeconds };
                timer.Elapsed += OnTimerElapsed;
                timerDisposed = false;
            }
            unloaded = false;
        };

        SyncControls(committedValue);
        return new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = UiMetrics.SpaceMedium,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                new StackLayoutItem(slider, expand: true),
                valueText
            }
        };
    }

    private static string TruncateMiddle(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength || maxLength < 5)
            return value;

        int left = (maxLength - 3) / 2;
        int right = maxLength - 3 - left;
        return string.Concat(value.AsSpan(0, left), "...", value.AsSpan(value.Length - right, right));
    }

    private void AddScatterBlocksFromSelector(TerrainDefinition terrain, Guid scatterId)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null)
            return;

        var selected = BlockSelectorDialog.Show(doc, CollectBlockDefinitions(doc));
        if (selected == null || selected.Count == 0)
            return;

        MutateObjectDefinition(terrain.TerrainId, scatterId, item =>
        {
            if (item is not ScatterObjectDefinition scatter)
                return;

            var existing = new HashSet<string>(
                scatter.Blocks
                    .Where(entry => !string.IsNullOrWhiteSpace(entry.BlockDefinitionName))
                    .Select(entry => entry.BlockDefinitionName!),
                StringComparer.OrdinalIgnoreCase);

            foreach (string name in selected)
            {
                if (existing.Add(name))
                    scatter.Blocks.Add(new ScatterBlockEntry { BlockDefinitionName = name, Weight = 1.0 });
            }
        });
    }

    private static string GetTerrainObjectTypeLabel(TerrainObjectDefinition definition) =>
        ObjectTypeRegistry.ForType(definition.GetType())?.DisplayName ?? "Objects";

    private static string GetTerrainObjectKind(TerrainObjectDefinition definition) =>
        ObjectTypeRegistry.ForType(definition.GetType())?.Kind ?? string.Empty;

    private static Color TerrainObjectTypeColor(string kind) =>
        UiTheme.TypeAccent(ObjectTypeRegistry.ForKind(kind)?.AccentArgb);

    private static string GetTerrainObjectIconLabel(TerrainObjectDefinition definition) =>
        ObjectTypeRegistry.ForType(definition.GetType())?.IconLabel ?? "O";

    private static string? GetTerrainObjectIconName(TerrainObjectDefinition definition) =>
        ObjectTypeRegistry.ForType(definition.GetType())?.IconName;

    private static string GetTerrainObjectSubtitle(TerrainObjectDefinition definition) =>
        ObjectTypeRegistry.ForType(definition.GetType())?.Subtitle ?? "Terrain objects";

    private static string GetTerrainObjectCollapsedSummary(TerrainObjectDefinition definition)
    {
        var parts = new List<string>
        {
            SummaryText.Sources(definition.Sources)
        };

        if (definition.RandomRotationMinDegrees > 1e-6 ||
            definition.RandomRotationMaxDegrees > definition.RandomRotationMinDegrees + 1e-6)
        {
            parts.Add($"Rot {definition.RandomRotationMinDegrees:G4}-{definition.RandomRotationMaxDegrees:G4}");
        }

        if (Math.Abs(definition.RandomScaleMin - 1.0) > 1e-6 ||
            Math.Abs(definition.RandomScaleMax - 1.0) > 1e-6)
        {
            parts.Add($"Scale {definition.RandomScaleMin:G4}-{definition.RandomScaleMax:G4}");
        }

        if (Math.Abs(definition.ZOffset) > 1e-6)
            parts.Add($"Z {definition.ZOffset:G4}");

        return string.Join(" · ", parts);
    }
}
