using System.Globalization;
using System.Linq;
using Eto.Drawing;
using Eto.Forms;
using MoleHill.Core.Analysis;
using MoleHill.Core.Grading;
using MoleHill.Core.Scattering;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Registry;
using MoleHill.Rhino.Services;
using Rhino;
using Rhino.UI;
using RhinoObjectType = Rhino.DocObjects.ObjectType;
using RhinoPoint3d = Rhino.Geometry.Point3d;
using RhinoGetPoint = Rhino.Input.Custom.GetPoint;
using RhinoGetResult = Rhino.Input.GetResult;

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
        bool collapsed = _collapsedObjects.Contains(definition.Id);
        var collapseLabel = CreateCollapseChevron(collapsed);

        var enabledCheck = new CheckBox { Checked = definition.IsEnabled };
        enabledCheck.CheckedChanged += (_, _) =>
            MutateObjectDefinition(terrain.TerrainId, definition.Id, item => item.IsEnabled = enabledCheck.Checked == true);

        var capturedDefinitionId = definition.Id;
        var capturedTerrainId = terrain.TerrainId;
        string kind = GetTerrainObjectKind(definition);
        var nameBox = new TextBox { Text = definition.Name };
        StyleTextBox(nameBox);
        ApplyHelp(nameBox, "Object definition label. Press Enter or click away to rename.");
        BindCommittedText(nameBox, () => definition.Name, text =>
            MutateObjectDefinition(capturedTerrainId, capturedDefinitionId, item => item.Name = text, scheduleRebuild: false));

        var handle = CreateDisabledReorderHandle("Object definition cards use the modifier card layout. Reordering is not enabled yet.");

        var accent = TerrainObjectTypeColor(kind);
        var iconPlate = CreateIconPlate(accent,
            CreateCardIconControl(GetTerrainObjectIconName(definition), GetTerrainObjectIconLabel(definition)));

        Control titleBlock = CreateCardTitleBlock(
            collapsed,
            definition.Name,
            AppendRuntimeDiagnosticSummary(
                GetTerrainObjectCollapsedSummary(definition),
                terrain,
                new RuntimeOverlayOwner(RuntimeOverlayOwnerKind.Object, definition.Id)),
            nameBox,
            GetTerrainObjectSubtitle(definition));

        var duplicateButton = MakeDuplicateIconButton(() =>
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc != null)
                _controller.DuplicateObjectDefinition(doc, capturedTerrainId, capturedDefinitionId);
        }, "Duplicate this object definition.");
        var deleteButton = MakeDeleteIconButton(() =>
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc != null)
                _controller.RemoveObjectDefinition(doc, capturedTerrainId, capturedDefinitionId);
        }, "Delete this object definition and restore any currently placed objects.");

        void ToggleCollapsed(bool ctrlHeld) => ToggleCardCollapsed(
            _collapsedObjects,
            capturedDefinitionId,
            ctrlHeld,
            t => t.Objects.Select(item => item.Id),
            RebuildObjectsLayout);

        return CreateSharedCardShell(new SharedCardShellOptions
        {
            Handle = handle,
            CollapseControl = collapseLabel,
            IconPlate = iconPlate,
            EnabledControl = enabledCheck,
            TitleBlock = titleBlock,
            StatusControls = Array.Empty<Control>(),
            ActionControls = new Control[]
            {
                duplicateButton,
                deleteButton
            },
            ToggleCollapsed = ToggleCollapsed,
            Collapsed = collapsed,
            Body = collapsed ? null : CreateObjectBody(terrain, definition)
        });
    }

    private Control CreateObjectBody(TerrainDefinition terrain, TerrainObjectDefinition definition)
    {
        var layout = UiLayouts.CardBody();
        if (TryBuildSchemaObjectBody(layout, terrain, definition))
        {
            Control? schemaDiagnosticsRow = CreateRuntimeDiagnosticsRow(
                terrain,
                new RuntimeOverlayOwner(RuntimeOverlayOwnerKind.Object, definition.Id));
            if (schemaDiagnosticsRow != null)
                layout.AddRow(schemaDiagnosticsRow);
            return layout;
        }

        layout.AddRow(CreateSourceEditor("Sources", definition.Sources,
            apply => MutateObjectDefinition(terrain.TerrainId, definition.Id, item => apply(item.Sources)),
            0,
            doc => _controller.GetSelectedLayerPaths(doc)));
        layout.AddRow(CreateSliderNumericEditor(
            "Rotate Min",
            definition.RandomRotationMinDegrees,
            value => MutateObjectDefinition(terrain.TerrainId, definition.Id, item => item.RandomRotationMinDegrees = value,
                deferDocumentSave: true, suppressImmediateUiRefresh: true),
            softMin: 0.0,
            softMax: 360.0,
            decimalPlaces: 1,
            hardMin: 0.0,
            hardMax: 360.0,
            help: "Minimum random rotation. Rotation is applied per object around its placement up axis and stays stable between rebuilds.",
            unitSuffix: ResolveUnitSuffix(ParameterUnit.Degrees)));
        layout.AddRow(CreateSliderNumericEditor(
            "Rotate Max",
            definition.RandomRotationMaxDegrees,
            value => MutateObjectDefinition(terrain.TerrainId, definition.Id, item => item.RandomRotationMaxDegrees = value,
                deferDocumentSave: true, suppressImmediateUiRefresh: true),
            softMin: 0.0,
            softMax: 360.0,
            decimalPlaces: 1,
            hardMin: 0.0,
            hardMax: 360.0,
            help: "Maximum random rotation. Set min and max equal to disable rotation variation.",
            unitSuffix: ResolveUnitSuffix(ParameterUnit.Degrees)));
        layout.AddRow(CreateSliderNumericEditor(
            "Scale Min",
            definition.RandomScaleMin,
            value => MutateObjectDefinition(terrain.TerrainId, definition.Id, item => item.RandomScaleMin = value,
                deferDocumentSave: true, suppressImmediateUiRefresh: true),
            softMin: 0.25,
            softMax: 2.0,
            decimalPlaces: 3,
            hardMin: 0.01,
            help: "Minimum random uniform scale. Scaling happens around the placement anchor and stays stable between rebuilds."));
        layout.AddRow(CreateSliderNumericEditor(
            "Scale Max",
            definition.RandomScaleMax,
            value => MutateObjectDefinition(terrain.TerrainId, definition.Id, item => item.RandomScaleMax = value,
                deferDocumentSave: true, suppressImmediateUiRefresh: true),
            softMin: 0.25,
            softMax: 2.0,
            decimalPlaces: 3,
            hardMin: 0.01,
            help: "Maximum random uniform scale. Set min and max to 1.0 for no scale variation."));
        layout.AddRow(CreateNumericEditor(
            "Seed",
            definition.RandomSeed,
            value => MutateObjectDefinition(terrain.TerrainId, definition.Id, item => item.RandomSeed = (int)Math.Round(value),
                deferDocumentSave: true, suppressImmediateUiRefresh: true),
            decimalPlaces: 0,
            help: "Stable random seed for this object card. Change it to reroll all matched objects.",
            liveEdit: true));
        layout.AddRow(CreateNumericEditor(
            "Z Offset",
            definition.ZOffset,
            value => MutateObjectDefinition(terrain.TerrainId, definition.Id, item => item.ZOffset = value,
                deferDocumentSave: true, suppressImmediateUiRefresh: true),
            decimalPlaces: 3,
            help: "Lift or sink placed objects. Project mode offsets in world Z; Surface mode offsets along the terrain normal.",
            minValue: null,
            liveEdit: true,
            unitSuffix: ResolveUnitSuffix(ParameterUnit.ModelLength)));
        layout.AddRow(CreateReadOnlyValueRow(
            "Bindings",
            $"{CountReferences(definition.Sources)} source refs",
            "Explicit picks plus watched layers drive the objects in this definition. Objects keep their original Rhino layers."));
        Control? diagnosticsRow = CreateRuntimeDiagnosticsRow(
            terrain,
            new RuntimeOverlayOwner(RuntimeOverlayOwnerKind.Object, definition.Id));
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

        for (int blockIndex = 0; blockIndex < scatter.Blocks.Count; blockIndex++)
        {
            int index = blockIndex;
            ScatterBlockEntry entry = scatter.Blocks[index];
            if (!string.IsNullOrWhiteSpace(entry.BlockDefinitionName))
            {
                layout.AddRow(CreateScatterBlockRow(
                    entry.BlockDefinitionName!,
                    entry.Weight,
                    value => Mutate(s => { if (index < s.Blocks.Count) s.Blocks[index].Weight = value; }, live: true),
                    () => Mutate(s => { if (index < s.Blocks.Count) s.Blocks.RemoveAt(index); })));
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
                layout.AddRow(CreateLegacyScatterBlockRow(
                    sourceEditor,
                    entry.Weight,
                    value => Mutate(s => { if (index < s.Blocks.Count) s.Blocks[index].Weight = value; }, live: true),
                    () => Mutate(s => { if (index < s.Blocks.Count) s.Blocks.RemoveAt(index); })));
            }
        }

        if (scatter.Blocks.Count == 0)
            layout.AddRow(CreateScatterBlockEmptyState());

        return layout;
    }

    private Control CreateScatterBlockRow(
        string blockName,
        double weight,
        Action<double> onWeightChanged,
        Action onRemove)
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

        var editor = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                new StackLayoutItem(weightEditor, expand: true),
                removeButton
            }
        };
        return new PropertyRow(nameLabel, editor, expandWidget: true);
    }

    private Control CreateScatterBlockMixHeader(int blockCount, EventHandler<EventArgs> addBlocks)
    {
        string countText = blockCount == 1 ? "1 block" : $"{blockCount} blocks";
        var label = new Label
        {
            Text = "Block Mix",
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
            Spacing = 6,
            Padding = new Padding(0, 5, 0, 1),
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
            Padding = new Padding(8, 6),
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
        Action onRemove)
    {
        var weightEditor = CreateCompactScatterWeightEditor(
            weight,
            onWeightChanged,
            "Relative likelihood this legacy source block is chosen per instance.");
        var removeButton = MakeIconButton(PanelButtonIcon.Clear, (_, _) => onRemove(), "Remove this block from the scatter mix.");

        var controls = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                new StackLayoutItem(weightEditor, expand: true),
                removeButton
            }
        };

        return new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = 4,
            Padding = new Padding(0, 3),
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

        var valueLabel = new Label
        {
            Width = UiMetrics.ShortLabel,
            TextColor = UiTheme.MutedText,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Right
        };

        ApplyHelp(slider, help);
        ApplyHelp(valueLabel, help);

        double committedValue = ClampSliderValue(value, 0.0, null);
        double pendingValue = committedValue;
        bool syncing = false;
        bool sliderEditActive = false;
        var timer = new UITimer { Interval = UiTiming.SliderCommitSeconds };

        void BeginSliderEdit()
        {
            if (sliderEditActive)
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
            valueLabel.Text = FormatSliderValue(numericValue, 2);
            syncing = false;
        }

        void Commit(double numericValue)
        {
            timer.Stop();
            numericValue = ClampSliderValue(numericValue, 0.0, null);
            pendingValue = numericValue;
            SyncControls(numericValue);
            if (!TerrainCommitGuard.HasMeaningfulNumericChange(committedValue, numericValue))
                return;

            committedValue = numericValue;
            onChanged(numericValue);
        }

        timer.Elapsed += (_, _) => Commit(pendingValue);
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
            if (_isRefreshing || syncing)
                return;

            BeginSliderEdit();
            pendingValue = FromSliderValue(slider.Value, currentMin, currentMax, slider.MaxValue);
            SyncControls(pendingValue);
            timer.Stop();
            timer.Start();
        };
        slider.UnLoad += (_, _) =>
        {
            timer.Stop();
            EndSliderEdit();
        };

        SyncControls(committedValue);
        return new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                new StackLayoutItem(slider, expand: true),
                valueLabel
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

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var blocks = new List<(string Name, Eto.Drawing.Bitmap? Thumbnail)>();
        foreach (var definition in doc.InstanceDefinitions)
        {
            if (definition == null || definition.IsDeleted || string.IsNullOrWhiteSpace(definition.Name))
                continue;
            if (!seen.Add(definition.Name))
                continue;

            blocks.Add((definition.Name, BlockThumbnailRenderer.Get(definition, 40)));
        }

        blocks.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        var selected = BlockSelectorDialog.Show(doc, blocks);
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

}
