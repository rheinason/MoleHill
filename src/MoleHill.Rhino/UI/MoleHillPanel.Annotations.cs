using Eto.Drawing;
using Eto.Forms;
using MoleHill.Core.Analysis;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Registry;
using MoleHill.Rhino.Services;
using MoleHill.Shared;
using Rhino;
using Rhino.UI;
using RhinoGetPoint = Rhino.Input.Custom.GetPoint;
using RhinoGetResult = Rhino.Input.GetResult;
using RhinoObjectType = Rhino.DocObjects.ObjectType;
using RhinoPoint3d = Rhino.Geometry.Point3d;

namespace MoleHill.Rhino.UI;

// Annotation cards: the Annotations tab's counterpart to MoleHillPanel.Analysis.cs. Split from it when
// annotations became their own content family — the two tabs already had separate stacks, toolbars and
// card maps, and now they have separate card builders over separate types too.
public sealed partial class MoleHillPanel
{
    private Control CreateAnnotationBody(TerrainDefinition terrain, AnnotationDefinition annotation)
    {
        var layout = UiLayouts.CardBody();
        TerrainAnalysisSummary? summary = GetAnalysisSummary(terrain, annotation.Id);

        // Above the controls, not below them: if this annotation cannot draw anything yet, that is the
        // first thing to say, and it names which control to reach for.
        var descriptor = AnnotationTypeRegistry.ForType(annotation.GetType());
        string? blocker = descriptor?.DescribeBlocker(terrain, annotation);
        if (blocker != null)
            layout.AddRow(CreateWarningRow(blocker));
        else if (descriptor?.DescribeBasis(terrain, annotation) is { } basis)
            layout.AddRow(CreateNoteRow(basis));

        BuildSchemaAnnotationBody(layout, terrain, annotation);
        // A card that declares where its Text Size goes has it there already (AnnotationParameterCatalog.TextSize).
        if (descriptor?.Parameters.Any(parameter => parameter.Key == AnnotationParameterCatalog.TextSizeKey) != true)
            AppendAnnotationTextSizeRows(layout, terrain, annotation);
        AddResultRows(
            layout,
            GetAnnotationKind(annotation),
            () => descriptor?.DescribeResult(annotation, summary, ResultFormats));
        Control? diagnosticsRow = CreateRuntimeDiagnosticsRow(
            terrain,
            new RuntimeOverlayOwner(RuntimeOverlayOwnerKind.Analysis, annotation.Id));
        if (diagnosticsRow != null)
            layout.AddRow(diagnosticsRow);

        return layout;
    }

    /// <summary>The unit-aware formatters result rows need, bound to the active document and the user's slope unit.</summary>
    private static readonly ResultFormatter ResultFormats = new(FormatArea, FormatVolume, FormatZoneLength, AnalysisFormatting.FormatSlopeDegrees);

    /// <summary>
    /// What the last build computed, under one Results section on every card that reports anything
    /// (design review Step 71: "Results" for computed values). Open by default and remembered per card
    /// type like every section; summaries stay selectable so their figures can be copied.
    /// </summary>
    private void AddResultRows(DynamicLayout layout, string cardKind, Func<IReadOnlyList<ResultRow>?> produce, bool expandedByDefault = true)
    {
        IReadOnlyList<ResultRow>? rows = SummaryText.Invariant(produce);
        if (rows == null || rows.Count == 0)
            return;

        var body = UiLayouts.CardBody();
        body.Padding = new Padding(0);
        foreach (ResultRow row in rows)
        {
            body.AddRow(row.Kind == ResultRowKind.SelectableSummary
                ? CreateSelectableSummaryEditor(row.Label, row.Value, row.Help, row.MinHeight)
                : CreateReadOnlyValueRow(row.Label, row.Value, row.Help));
        }

        layout.AddRow(new CardSection(cardKind, "results", "Results", expandedByDefault, null, body));
    }

    /// <summary>Above this many terrains the profile list gets a filter box.</summary>
    private const int SectionProfileFilterThreshold = 8;

    /// <summary>
    /// The "Profiles" block: every other terrain in the document, ticked to draw its profile on this section.
    ///
    /// It used to be shown only when a terrain was left over after the Compare To Terrain choice, so in the
    /// common two-terrain file it vanished the moment that choice was made and read as a lost feature. Now
    /// it lists every other terrain whenever there is one, with the compared terrain shown ticked and fixed
    /// (the dropdown owns it), so the list never changes shape under the user. Each profile's colour is
    /// this card's to choose: click the swatch.
    /// </summary>
    private void AddSectionProfileRows(
        DynamicLayout layout,
        TerrainDefinition owner,
        TerrainSectionAnnotationDefinitionBase annotation)
    {
        RhinoDoc? doc = RhinoDoc.ActiveDoc;
        IReadOnlyList<TerrainDefinition> terrains = doc == null
            ? Array.Empty<TerrainDefinition>()
            : _controller.GetTerrains(doc);
        var others = terrains.Where(item => item.TerrainId != owner.TerrainId).ToList();
        Guid? compareId = annotation.CutFillReferenceTerrainId;
        var drawn = annotation.ComparisonTerrainIds.Distinct().ToHashSet();
        if (others.Count == 0)
            return;

        // The compared terrain is drawn as existing ground, in that layer's colour, only while cut/fill is
        // on and no Compare To geometry has taken over; otherwise it is an ordinary profile in its own colour.
        bool comparedIsExistingGround = compareId.HasValue
            && annotation.ShowCutFillRegions
            && !annotation.CutFillReference.HasReferences;
        int existingGroundArgb = AnalysisFormatting.ResolveLayerColorArgb(
            AnalysisFormatting.GetRoleLayerPath(owner, LayerRole.SectionsExisting)) ?? unchecked((int)0xFF9E9E9E);

        const string help = "Terrains drawn on this section, each as its own profile. The one chosen in Compare " +
            "To Terrain is always drawn; tick others to add them. Click a swatch to choose the colour that " +
            "profile is drawn in on this card; right-click it to go back to the terrain's own colour.";
        layout.AddRow(CreateSectionRule("Profiles"));

        var list = new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = UiMetrics.SpaceXSmall,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        var rows = new List<(Control Row, string Name, bool Pinned)>();
        foreach (TerrainDefinition other in others)
        {
            Guid otherId = other.TerrainId;
            bool isCompared = otherId == compareId;
            bool isDrawn = isCompared || drawn.Contains(otherId);
            var check = new CheckBox
            {
                Text = isCompared ? $"{other.Name} (compared)" : other.Name,
                Checked = isDrawn,
                Enabled = !isCompared
            };
            ApplyHelp(check, isCompared
                ? "Drawn because it is the terrain this section is compared to. Change that in Compare To Terrain above."
                : help);
            check.CheckedChanged += (_, _) =>
            {
                if (_isRefreshing)
                    return;

                bool selected = check.Checked == true;
                MutateAnnotation(owner.TerrainId, annotation.Id, item =>
                {
                    if (item is not TerrainSectionAnnotationDefinitionBase section)
                        return;

                    section.ComparisonTerrainIds.RemoveAll(id => id == otherId);
                    if (selected)
                        section.ComparisonTerrainIds.Add(otherId);
                }, scheduleRebuild: true);
                RefreshUi();
            };

            Control swatch = isCompared && comparedIsExistingGround
                ? CreateFixedProfileSwatch(existingGroundArgb,
                    "Drawn as existing ground, in the colour of its layer role (Sections ▸ Existing).")
                : CreateProfileColorSwatch(owner, annotation, other);
            var row = new StackLayout
            {
                Orientation = Orientation.Horizontal,
                Spacing = UiMetrics.SpaceSmall,
                VerticalContentAlignment = VerticalAlignment.Center,
                Items = { swatch, check }
            };
            list.Items.Add(row);
            rows.Add((row, other.Name, isDrawn));
        }

        Control editor = list;
        if (others.Count > SectionProfileFilterThreshold)
        {
            // Filtering only hides rows; ticked terrains stay visible so what is drawn is always in view.
            var filter = new TextBox { PlaceholderText = "Filter terrains…" };
            ApplyHelp(filter, "Show only terrains whose name contains this text. Ticked terrains are always shown.");
            filter.TextChanged += (_, _) =>
            {
                string text = filter.Text?.Trim() ?? string.Empty;
                foreach (var (row, name, pinned) in rows)
                    row.Visible = pinned || text.Length == 0 || name.Contains(text, StringComparison.OrdinalIgnoreCase);
            };
            editor = new StackLayout
            {
                Orientation = Orientation.Vertical,
                Spacing = UiMetrics.SpaceSmall,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Items = { filter, list }
            };
        }

        layout.AddRow(new PropertyRow(CreateHelpLabel("Also Draw", help, 0), editor, expandWidget: true));
        foreach (Guid drawnId in drawn.Where(id => id != compareId))
        {
            if (CreateCompareTerrainStatusRow(owner, drawnId, "its profile is left off this section") is { } status)
                layout.AddRow(status);
        }
    }

    /// <summary>A swatch that shows a colour the card does not own.</summary>
    private Control CreateFixedProfileSwatch(int argb, string help)
    {
        var swatch = new Panel
        {
            Size = new Size(12, 12),
            BackgroundColor = ToEtoColor(System.Drawing.Color.FromArgb(argb))
        };
        ApplyHelp(swatch, help);
        return swatch;
    }

    /// <summary>
    /// The colour of one terrain's profile on this card. Click to choose; right-click to clear back to the
    /// terrain's own preview colour.
    /// </summary>
    private Control CreateProfileColorSwatch(
        TerrainDefinition owner,
        TerrainSectionAnnotationDefinitionBase annotation,
        TerrainDefinition terrain)
    {
        Guid terrainId = terrain.TerrainId;
        bool isCustom = annotation.ProfileColorArgbs.ContainsKey(terrainId);
        int argb = annotation.ResolveProfileColorArgb(terrainId, terrain.TerrainColorArgb);
        var swatch = new Panel
        {
            Size = new Size(12, 12),
            BackgroundColor = ToEtoColor(System.Drawing.Color.FromArgb(argb)),
            Cursor = Cursors.Pointer
        };
        ApplyHelp(swatch, isCustom
            ? $"Drawn in {DescribeSolidColor(argb)} on this section. Click to change; right-click to use {terrain.Name}'s own colour."
            : $"Drawn in {terrain.Name}'s own colour. Click to choose a colour for this section.");

        void Commit(int? chosen) =>
            MutateAnnotation(owner.TerrainId, annotation.Id, item =>
            {
                if (item is not TerrainSectionAnnotationDefinitionBase section)
                    return;

                if (chosen is { } value)
                    section.ProfileColorArgbs[terrainId] = value;
                else
                    section.ProfileColorArgbs.Remove(terrainId);
            }, scheduleRebuild: true);

        swatch.MouseDown += (_, e) =>
        {
            if (RhinoDoc.ActiveDoc is not { } doc)
                return;

            if (e.Buttons == MouseButtons.Alternate)
            {
                if (isCustom)
                {
                    Commit(null);
                    RefreshUi();
                }

                return;
            }

            if (e.Buttons != MouseButtons.Primary)
                return;

            var dialog = new ColorDialog { Color = ToEtoColor(System.Drawing.Color.FromArgb(argb)), AllowAlpha = false };
            if (dialog.ShowDialog(RhinoEtoApp.MainWindowForDocument(doc)) != DialogResult.Ok)
                return;

            Commit(ToArgb(dialog.Color));
            RefreshUi();
        };
        return swatch;
    }

    /// <summary>
    /// A point picked in the viewport, in the source picker's pattern (design review Steps 26 and 53): a
    /// Pick Point pill while nothing is picked; a Picked pill (click to pick again, coordinates in its
    /// tooltip) with an x that clears it. Cancelling a pick keeps the point there was. The Rhino prompt
    /// also takes typed coordinates.
    /// </summary>
    private Control CreatePointPickRow(
        string label,
        bool picked,
        string coordinates,
        string help,
        string prompt,
        Action<RhinoPoint3d> onPick,
        Action onClear,
        string clearHelp,
        string? pickHelp = null)
    {
        pickHelp ??= help;
        var pill = MakePillButton(
            picked ? "Picked" : "Pick Point",
            picked ? $"Picked at {coordinates}. Click to pick again. {pickHelp}" : pickHelp);
        pill.Click += (_, _) =>
        {
            if (RhinoDoc.ActiveDoc == null)
                return;

            var gp = new RhinoGetPoint();
            gp.SetCommandPrompt(prompt);
            if (gp.Get() != RhinoGetResult.Point)
                return;

            onPick(gp.Point());
            RefreshUi();
        };

        var fields = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 0,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items = { new StackLayoutItem(pill, expand: true) }
        };
        if (picked)
        {
            fields.Items.Add(MakeIconButton(PanelButtonIcon.Clear, (_, _) =>
            {
                onClear();
                RefreshUi();
            }, clearHelp));
        }

        return new PropertyRow(CreateHelpLabel(label, help, 0), fields, expandWidget: true);
    }

    // ---- Annotation-family twins of the analysis card infrastructure ----

    private void MutateAnnotation(
        Guid terrainId,
        Guid annotationId,
        Action<AnnotationDefinition> mutator,
        bool scheduleRebuild = false,
        bool deferDocumentSave = false,
        bool suppressImmediateUiRefresh = false)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null)
            return;

        _controller.MutateTerrain(doc, terrainId, terrain =>
        {
            var annotation = terrain.Annotations.FirstOrDefault(item => item.Id == annotationId);
            if (annotation != null)
                mutator(annotation);
        }, scheduleRebuild, deferDocumentSave, suppressImmediateUiRefresh);
    }

    private void MutateAndRefreshAnnotation(Guid terrainId, Guid annotationId, Action<AnnotationDefinition> mutator)
    {
        MutateAnnotation(terrainId, annotationId, mutator, scheduleRebuild: false);
        RefreshTerrainPreview(terrainId);
    }

    private void AddAnnotation(string kind)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null)
            return;

        MutateSelectedTerrain(terrain =>
        {
            AnnotationDefinition? annotation = AnnotationTypeRegistry.Create(
                kind,
                ModelUnitContext.FromDocument(doc));
            if (annotation != null)
                terrain.Annotations.Insert(0, annotation);
        }, scheduleRebuild: false);

        var terrain = _controller.GetSelectedTerrain(doc);
        if (terrain != null)
        {
            RefreshUi();
            RefreshTerrainPreview(terrain.TerrainId);
        }
    }

    private void RemoveAnnotation(Guid terrainId, Guid annotationId)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null)
            return;

        _controller.MutateTerrain(doc, terrainId, terrain =>
        {
            terrain.Annotations.RemoveAll(item => item.Id == annotationId);
        }, scheduleRebuild: false);
        RefreshUi();
    }

    private void DuplicateAnnotation(Guid terrainId, Guid annotationId)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc != null)
        {
            _controller.DuplicateAnnotation(doc, terrainId, annotationId);
            RefreshUi();
        }
    }

    private static string GetAnnotationKind(AnnotationDefinition annotation) =>
        AnnotationTypeRegistry.ForType(annotation.GetType())?.Kind ?? string.Empty;

    private static string GetAnnotationIconLabel(AnnotationDefinition annotation) =>
        AnnotationTypeRegistry.ForType(annotation.GetType())?.IconLabel ?? "?";

    private static string? GetAnnotationIconName(AnnotationDefinition annotation) =>
        AnnotationTypeRegistry.ForType(annotation.GetType())?.IconName;

    private static string GetAnnotationTypeLabel(AnnotationDefinition annotation) =>
        AnnotationTypeRegistry.ForType(annotation.GetType())?.TypeLabel ?? "Annotation";

    private static string GetAnnotationSubtitle(AnnotationDefinition annotation) =>
        AnnotationTypeRegistry.ForType(annotation.GetType())?.Subtitle ?? string.Empty;

    private static Color AnnotationTypeColor(string kind) =>
        UiTheme.TypeAccent(AnnotationTypeRegistry.ForKind(kind)?.AccentArgb);

    private Panel CreateAnnotationCard(TerrainDefinition terrain, AnnotationDefinition annotation)
    {
        Guid terrainId = terrain.TerrainId, id = annotation.Id;

        return CreateCard(new CardSpec
        {
            Stack = _annotationCards,
            Id = id,
            Name = annotation.Label,
            NameHelp = "Friendly annotation name shown in the panel.",
            Rename = text => MutateAnnotation(terrainId, id, item => item.Label = text, scheduleRebuild: false),
            IsEnabled = annotation.IsEnabled,
            EnableHelp = "Enable or disable this annotation card without deleting it.",
            SetEnabled = value =>
            {
                MutateAnnotation(terrainId, id, item => item.IsEnabled = value, scheduleRebuild: false);
                RefreshTerrainPreview(terrainId);
            },
            TypeName = GetAnnotationTypeLabel(annotation),
            TypeIcon = CreateCardIconControl(GetAnnotationIconName(annotation), GetAnnotationIconLabel(annotation)),
            Summary = () => AppendRuntimeDiagnosticSummary(
                GetAnnotationCollapsedSummary(terrain, annotation),
                terrain,
                new RuntimeOverlayOwner(RuntimeOverlayOwnerKind.Analysis, id)),
            Description = GetAnnotationSubtitle(annotation),
            HandleHelp = "Drag to reorder this annotation.",
            Status = CreateCardStatusLabel(annotation.IsEnabled ? "Output" : "Disabled", UiTheme.MutedText),
            Duplicate = (() =>
            {
                DuplicateAnnotation(terrainId, id);
                RefreshTerrainPreview(terrainId);
            }, "Duplicate this annotation card."),
            Delete = (() =>
            {
                RemoveAnnotation(terrainId, id);
                RefreshTerrainPreview(terrainId);
            }, "Delete this annotation card."),
            Body = () => CreateAnnotationBody(terrain, annotation)
        });
    }

    private static string GetAnnotationCollapsedSummary(TerrainDefinition terrain, AnnotationDefinition annotation) =>
        AnnotationTypeRegistry.ForType(annotation.GetType())?.Summarize(terrain, annotation, GetAnalysisSummary(terrain, annotation.Id), ResultFormats)
            ?? string.Empty;
}
