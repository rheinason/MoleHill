using MoleHill.Core.Analysis;
using MoleHill.Rhino.Model;
using Rhino;
using Rhino.Geometry;
using RhinoMesh = Rhino.Geometry.Mesh;

namespace MoleHill.Rhino.Services;

// The section family: terrain, cross-station and longitudinal sections, cut/fill reference slices and
// comparison hatches.
internal static partial class TerrainAnalysisAnnotationBuilder
{
    private sealed record SectionTerrainProfile(
        Guid TerrainId,
        string TerrainName,
        int ColorArgb,
        TerrainSectionResult Slice,
        bool IsOwner);

    private readonly record struct SectionEmissionStats(int OutputCount, int CutRegions, int FillRegions);

    public static TerrainAnalysisSummary BuildTerrainSectionSummary(
        TerrainBuildSnapshot snapshot,
        RhinoMesh mesh,
        TerrainSectionAnnotationDefinition analysis,
        TerrainBuildResult build,
        Func<bool>? shouldCancel,
        LayerRoleTable? layerRoles = null,
        RhinoMesh? baseMesh = null)
    {
        var objects = TerrainBuildSnapshotResolver.ResolveObjects(snapshot, analysis.Sources);
        var insertionPlane = ResolveInsertionPlane(analysis, mesh);
        double tolerance = snapshot.ModelAbsoluteTolerance;
        int sourceCount = 0;
        int outputCount = 0;
        AddMissingSectionTerrainDiagnostics(snapshot, analysis, build);
        var referenceMesh = new CutFillReferenceMesh();

        var sectionProfiles = new List<List<SectionTerrainProfile>>();
        var sectionCuts = new List<SectionCutGeometry>();
        double maxStation = 0.0;
        double maxRange = 0.0;
        int availableTerrainCount = 1;

        foreach (var entry in objects)
        {
            ThrowIfCancellationRequested(shouldCancel);
            if (entry.Geometry is not Curve curve)
                continue;

            sourceCount++;
            var cutVertices = ApproximateCurveAsPolyline(curve, tolerance);
            if (cutVertices.Count < 2)
                continue;

            List<SectionTerrainProfile> profiles = SliceTerrainsAlongPolyline(
                snapshot, mesh, analysis, cutVertices, tolerance);
            if (profiles.Count == 0 || profiles[0].Slice.IsEmpty)
                continue;

            sectionProfiles.Add(profiles);
            sectionCuts.Add(SectionCutGeometry.AlongPolyline(cutVertices));
            availableTerrainCount = Math.Max(availableTerrainCount, profiles.Count);
            double minimum = profiles.Min(profile => profile.Slice.MinimumElevation);
            double maximum = profiles.Max(profile => profile.Slice.MaximumElevation);
            maxStation = Math.Max(maxStation, profiles.Max(profile => profile.Slice.TotalStationLength));
            double range = maximum - minimum;
            if (range > maxRange)
                maxRange = range;
        }

        double cellWidth = maxStation + Math.Max(maxStation * 0.15, ResolveTextHeight(snapshot, analysis) * 8.0);
        double cellHeight = Math.Max(maxRange * 1.4, ResolveTextHeight(snapshot, analysis) * 6.0);

        int cutRegions = 0;
        int fillRegions = 0;
        for (int i = 0; i < sectionProfiles.Count; i++)
        {
            ThrowIfCancellationRequested(shouldCancel);
            List<SectionTerrainProfile> profiles = sectionProfiles[i];
            Plane cellPlane = OffsetCellPlane(insertionPlane, i, columns: Math.Max(sectionProfiles.Count, 1), cellWidth, cellHeight);

            SectionEmissionStats emitted = EmitCombinedProfileObjects(
                snapshot,
                sectionCuts[i],
                analysis,
                build,
                profiles,
                cellPlane,
                horizontalScale: 1.0,
                verticalScale: ResolveVerticalExaggeration(analysis),
                baseElevation: profiles.Min(profile => profile.Slice.MinimumElevation),
                comparisonTolerance: tolerance,
                showBaseline: true,
                showElevationGrid: analysis.ShowElevationGrid,
                elevationGridInterval: analysis.ElevationGridInterval,
                showStationTicks: analysis.ShowStationTicks,
                stationTickInterval: analysis.StationTickInterval,
                showStationLabels: analysis.ShowStationLabels,
                stationLabelInterval: analysis.StationTickInterval,
                textHeight: ResolveTextHeight(snapshot, analysis),
                layerRoles: layerRoles,
                sectionLabel: $"{analysis.Label} {i + 1}",
                hatchPatterns: snapshot.HatchPatterns,
                    baseMesh: baseMesh,
                    referenceMesh: referenceMesh);
            outputCount += emitted.OutputCount;
            cutRegions += emitted.CutRegions;
            fillRegions += emitted.FillRegions;
        }

        return new TerrainAnalysisSummary
        {
            AnalysisId = analysis.Id,
            SampleSourceCount = sourceCount,
            GeneratedOutputCount = outputCount,
            SectionTerrainCount = availableTerrainCount,
            SectionCutRegionCount = cutRegions,
            SectionFillRegionCount = fillRegions
        };
    }

    public static TerrainAnalysisSummary BuildCrossSectionStationSummary(
        TerrainBuildSnapshot snapshot,
        RhinoMesh mesh,
        CrossSectionStationAnnotationDefinition analysis,
        TerrainBuildResult build,
        Func<bool>? shouldCancel,
        LayerRoleTable? layerRoles = null,
        RhinoMesh? baseMesh = null)
    {
        var objects = TerrainBuildSnapshotResolver.ResolveObjects(snapshot, analysis.Sources);
        var insertionPlane = ResolveInsertionPlane(analysis, mesh);
        double tolerance = snapshot.ModelAbsoluteTolerance;
        double stationInterval = Math.Max(analysis.StationInterval, tolerance * 100.0);
        double halfWidth = Math.Max(analysis.CrossSectionWidth * 0.5, tolerance * 10.0);
        int gridColumns = Math.Max(analysis.GridColumns, 1);
        double verticalScale = ResolveVerticalExaggeration(analysis);
        int sourceCount = 0;
        int outputCount = 0;
        AddMissingSectionTerrainDiagnostics(snapshot, analysis, build);
        var referenceMesh = new CutFillReferenceMesh();
        int globalIndex = 0;
        int availableTerrainCount = 1;
        int cutRegions = 0;
        int fillRegions = 0;

        foreach (var entry in objects)
        {
            ThrowIfCancellationRequested(shouldCancel);
            if (entry.Geometry is not Curve alignment)
                continue;

            sourceCount++;
            var stations = GetCurveDivisionSamples(alignment, stationInterval);
            if (stations.Count == 0)
                continue;

            var slices = new List<(double Station, List<SectionTerrainProfile> Profiles, SectionCutGeometry Cut)>(stations.Count);
            double maxStation = 0.0;
            double maxRange = 0.0;

            foreach (var station in stations)
            {
                ThrowIfCancellationRequested(shouldCancel);
                Vector3d tangent = alignment.TangentAt(station.Parameter);
                tangent.Z = 0.0;
                if (!tangent.Unitize())
                    continue;

                var perpendicular = new Vector3d(-tangent.Y, tangent.X, 0.0);
                Point3d a = station.Point - (perpendicular * halfWidth);
                Point3d b = station.Point + (perpendicular * halfWidth);
                a.Z = 0.0;
                b.Z = 0.0;

                var cut = new[] { a, b };
                List<SectionTerrainProfile> profiles = SliceTerrainsAlongPolyline(
                    snapshot, mesh, analysis, cut, tolerance);
                if (profiles.Count == 0 || profiles[0].Slice.IsEmpty)
                    continue;

                slices.Add((
                    alignment.GetLength(new Interval(alignment.Domain.T0, station.Parameter)),
                    profiles,
                    SectionCutGeometry.AlongPolyline(cut)));
                availableTerrainCount = Math.Max(availableTerrainCount, profiles.Count);
                maxStation = Math.Max(maxStation, profiles.Max(profile => profile.Slice.TotalStationLength));
                double range = profiles.Max(profile => profile.Slice.MaximumElevation) -
                               profiles.Min(profile => profile.Slice.MinimumElevation);
                if (range > maxRange)
                    maxRange = range;
            }

            double cellWidth = analysis.GridCellWidth > 0.0 ? analysis.GridCellWidth : (analysis.CrossSectionWidth + Math.Max(maxStation, analysis.CrossSectionWidth) * 0.1);
            double cellHeight = analysis.GridCellHeight > 0.0 ? analysis.GridCellHeight : Math.Max(maxRange * verticalScale * 1.4, analysis.CrossSectionWidth * 0.3);

            for (int i = 0; i < slices.Count; i++)
            {
                ThrowIfCancellationRequested(shouldCancel);
                var (alignmentStation, profiles, _) = slices[i];
                Plane cellPlane = OffsetCellPlane(insertionPlane, globalIndex, gridColumns, cellWidth, cellHeight);
                globalIndex++;

                if (analysis.ShowCutLinesOnTerrain)
                {
                    foreach (SectionTerrainProfile profile in profiles)
                    {
                        foreach (TerrainSectionSegment segment in profile.Slice.Segments)
                        {
                            var poly = new Polyline(segment.Vertices.Count);
                            for (int v = 0; v < segment.Vertices.Count; v++)
                                poly.Add(segment.Vertices[v].World);
                            build.AuxiliaryObjects.Add(BuildPolylineObject(
                                analysis,
                                poly,
                                layerRoles,
                                $"{analysis.Label} {profile.TerrainName} cut {globalIndex}",
                                LayerRole.SectionsCuts,
                                profile.ColorArgb));
                            outputCount++;
                        }
                    }
                }

                SectionEmissionStats emitted = EmitCombinedProfileObjects(
                    snapshot,
                    slices[i].Cut,
                    analysis,
                    build,
                    profiles,
                    cellPlane,
                    horizontalScale: 1.0,
                    verticalScale: verticalScale,
                    baseElevation: profiles.Min(profile => profile.Slice.MinimumElevation),
                    comparisonTolerance: tolerance,
                    showBaseline: true,
                    showElevationGrid: analysis.ShowElevationGrid,
                    elevationGridInterval: analysis.ElevationGridInterval,
                    showStationTicks: false,
                    stationTickInterval: 0.0,
                    showStationLabels: analysis.LabelStations,
                    stationLabelInterval: 0.0,
                    textHeight: ResolveTextHeight(snapshot, analysis),
                    layerRoles: layerRoles,
                    sectionLabel: $"Sta {alignmentStation:F2}",
                    hatchPatterns: snapshot.HatchPatterns,
                    baseMesh: baseMesh,
                    referenceMesh: referenceMesh);
                outputCount += emitted.OutputCount;
                cutRegions += emitted.CutRegions;
                fillRegions += emitted.FillRegions;
            }
        }

        return new TerrainAnalysisSummary
        {
            AnalysisId = analysis.Id,
            SampleSourceCount = sourceCount,
            GeneratedOutputCount = outputCount,
            SectionTerrainCount = availableTerrainCount,
            SectionCutRegionCount = cutRegions,
            SectionFillRegionCount = fillRegions
        };
    }

    public static TerrainAnalysisSummary BuildLongitudinalSectionSummary(
        TerrainBuildSnapshot snapshot,
        RhinoMesh mesh,
        LongitudinalSectionAnnotationDefinition analysis,
        TerrainBuildResult build,
        Func<bool>? shouldCancel,
        LayerRoleTable? layerRoles = null,
        RhinoMesh? baseMesh = null)
    {
        var objects = TerrainBuildSnapshotResolver.ResolveObjects(snapshot, analysis.Sources);
        var insertionPlane = ResolveInsertionPlane(analysis, mesh);
        double tolerance = snapshot.ModelAbsoluteTolerance;
        double sampleInterval = Math.Max(analysis.SampleInterval, tolerance * 10.0);
        double verticalScale = ResolveVerticalExaggeration(analysis);
        int sourceCount = 0;
        int outputCount = 0;
        int sectionIndex = 0;
        int availableTerrainCount = 1;
        int cutRegions = 0;
        int fillRegions = 0;
        AddMissingSectionTerrainDiagnostics(snapshot, analysis, build);
        var referenceMesh = new CutFillReferenceMesh();

        foreach (var entry in objects)
        {
            ThrowIfCancellationRequested(shouldCancel);
            if (entry.Geometry is not Curve curve)
                continue;

            sourceCount++;
            List<SectionTerrainProfile> profiles = SampleTerrainsAlongCurve(
                snapshot, mesh, analysis, curve, sampleInterval, tolerance);
            if (profiles.Count == 0 || profiles[0].Slice.IsEmpty)
                continue;

            sectionIndex++;
            Plane cellPlane = OffsetCellPlane(insertionPlane, sectionIndex - 1, columns: 1, cellWidth: 0.0, cellHeight: 0.0);

            availableTerrainCount = Math.Max(availableTerrainCount, profiles.Count);
            SectionEmissionStats emitted = EmitCombinedProfileObjects(
                snapshot,
                SectionCutGeometry.AlongCurve(curve, sampleInterval),
                analysis,
                build,
                profiles,
                cellPlane,
                horizontalScale: 1.0,
                verticalScale: verticalScale,
                baseElevation: profiles.Min(profile => profile.Slice.MinimumElevation),
                comparisonTolerance: tolerance,
                showBaseline: analysis.ShowBaseline,
                showElevationGrid: analysis.ShowElevationGrid,
                elevationGridInterval: analysis.ElevationGridInterval,
                showStationTicks: analysis.ShowStationLabels,
                stationTickInterval: analysis.StationLabelInterval,
                showStationLabels: analysis.ShowStationLabels,
                stationLabelInterval: analysis.StationLabelInterval,
                textHeight: ResolveTextHeight(snapshot, analysis),
                layerRoles: layerRoles,
                sectionLabel: $"{analysis.Label} {sectionIndex}",
                hatchPatterns: snapshot.HatchPatterns,
                    baseMesh: baseMesh,
                    referenceMesh: referenceMesh);
            outputCount += emitted.OutputCount;
            cutRegions += emitted.CutRegions;
            fillRegions += emitted.FillRegions;
        }

        return new TerrainAnalysisSummary
        {
            AnalysisId = analysis.Id,
            SampleSourceCount = sourceCount,
            GeneratedOutputCount = outputCount,
            SectionTerrainCount = availableTerrainCount,
            SectionCutRegionCount = cutRegions,
            SectionFillRegionCount = fillRegions
        };
    }

    private static SectionEmissionStats EmitCombinedProfileObjects(
        TerrainBuildSnapshot snapshot,
        SectionCutGeometry cutGeometry,
        TerrainSectionAnnotationDefinitionBase analysis,
        TerrainBuildResult build,
        IReadOnlyList<SectionTerrainProfile> profiles,
        Plane cellPlane,
        double horizontalScale,
        double verticalScale,
        double baseElevation,
        double comparisonTolerance,
        bool showBaseline,
        bool showElevationGrid,
        double elevationGridInterval,
        bool showStationTicks,
        double stationTickInterval,
        bool showStationLabels,
        double stationLabelInterval,
        double textHeight,
        LayerRoleTable? layerRoles,
        string sectionLabel,
        HatchPatternSnapshot hatchPatterns,
        RhinoMesh? baseMesh,
        CutFillReferenceMesh referenceMesh)
    {
        if (!analysis.IsEnabled)
            return default;

        int emitted = 0;
        int cutRegions = 0;
        int fillRegions = 0;
        TerrainSectionResult ownerSlice = profiles[0].Slice;
        double totalStation = profiles.Max(profile => profile.Slice.TotalStationLength);
        double minimumElevation = profiles.Min(profile => profile.Slice.MinimumElevation);
        double maximumElevation = profiles.Max(profile => profile.Slice.MaximumElevation);
        double effectiveElevationGridInterval = SectionLayoutHelper.ResolveElevationGridSpacing(
            minimumElevation,
            maximumElevation,
            elevationGridInterval);

        TerrainSectionResult? referenceSliceForProfile = null;
        if (analysis.ShowCutFillRegions)
        {
            TerrainSectionResult? referenceSlice = ResolveCutFillReferenceSlice(
                snapshot, cutGeometry, analysis, profiles, comparisonTolerance, build, baseMesh, referenceMesh);
            referenceSliceForProfile = referenceSlice;
            if (referenceSlice != null)
            {
                IReadOnlyList<SectionComparisonRegion> regions = SectionProfileComparison.Compare(
                    ownerSlice,
                    referenceSlice,
                    Math.Max(comparisonTolerance, totalStation * 1e-10));
                foreach (SectionComparisonRegion region in regions)
                {
                    bool isCut = region.IsCut;
                    LayerRole regionRole = isCut ? LayerRole.SectionsCutFillCut : LayerRole.SectionsCutFillFill;
                    LayerAppearance regionAppearance = Roles(layerRoles).Appearance(regionRole);
                    string regionLayerPath = Roles(layerRoles).Path(regionRole);

                    // Pattern, scale and rotation come from the role, so every section in a document
                    // fills the same way and the office controls it from one place. The analysis's own
                    // fields are only a fallback for a document whose template predates them.
                    string? patternName = regionAppearance.HatchPatternName
                        ?? (isCut ? analysis.CutHatchPatternName : analysis.FillHatchPatternName);
                    string defaultPatternName = isCut
                        ? HatchPatternService.DefaultCutPatternName
                        : HatchPatternService.DefaultFillPatternName;

                    // A hatch, not a transparent mesh: a shaded mesh is a rendering artefact that does not
                    // print and ignores the document hatch scale.
                    IReadOnlyList<Hatch> regionHatches = BuildComparisonRegionHatch(
                        region,
                        cellPlane,
                        horizontalScale,
                        verticalScale,
                        baseElevation,
                        hatchPatterns.ResolveIndex(patternName, defaultPatternName),
                        hatchPatterns.ResolveScale(
                            patternName,
                            defaultPatternName,
                            regionAppearance.HatchScale,
                            textHeight),
                        regionAppearance.HatchRotationDegrees,
                        comparisonTolerance);
                    if (regionHatches.Count == 0)
                        continue;
                    foreach (Hatch regionHatch in regionHatches)
                    {
                        build.AuxiliaryObjects.Add(new GeneratedRhinoObject
                        {
                            Role = regionRole,
                            Geometry = regionHatch,
                            Name = $"{sectionLabel} {(isCut ? "cut" : "fill")}",
                            AnalysisId = analysis.Id,
                            AppearanceSource = GeneratedAppearanceSource.Layer,
                            LayerPath = regionLayerPath,
                            DisplayOrder = SectionDisplayOrder.Fill
                        });
                        emitted++;
                    }

                    // Region counts stay per comparison region: one region may need several hatches.
                    if (isCut)
                        cutRegions++;
                    else
                        fillRegions++;
                }
            }
        }

        // Existing ground, drawn from whatever the cut/fill comparison measured against. It is context:
        // a light line the proposed profile is read against. Without it the original ground is only ever
        // implied by the far edge of a hatch, so it vanishes wherever nothing was cut or filled.
        if (referenceSliceForProfile != null)
        {
            foreach (Polyline existing in SectionLayoutHelper.LayoutFlatAll(
                         referenceSliceForProfile, cellPlane, horizontalScale, verticalScale, baseElevation))
            {
                if (existing.Count < 2)
                    continue;

                build.AuxiliaryObjects.Add(BuildPolylineObject(
                    analysis,
                    existing,
                    layerRoles,
                    $"{sectionLabel} existing ground",
                    LayerRole.SectionsExisting,
                    colorArgbOverride: null));
                emitted++;
            }
        }

        for (int profileIndex = 0; profileIndex < profiles.Count; profileIndex++)
        {
            SectionTerrainProfile profile = profiles[profileIndex];

            // The sectioned terrain itself — the finished modifier stack — is the subject of the drawing,
            // so it is the heaviest line on it and takes its appearance from the layer: black, by
            // convention, rather than the terrain's preview tint, which is a screen colour and prints as
            // whatever pastel it happens to be. Additional comparison terrains keep their own colours,
            // which is the only thing telling them apart.
            bool isOwnerProfile = profileIndex == 0;

            // A comparison terrain that is also the cut/fill reference has already been drawn above as
            // existing ground, from this same slice; drawing it again stacks a second, coloured line on it.
            if (!isOwnerProfile && ReferenceEquals(profile.Slice, referenceSliceForProfile))
                continue;

            foreach (Polyline poly in SectionLayoutHelper.LayoutFlatAll(
                         profile.Slice, cellPlane, horizontalScale, verticalScale, baseElevation))
            {
                if (poly.Count < 2)
                    continue;

                build.AuxiliaryObjects.Add(BuildPolylineObject(
                    analysis,
                    poly,
                    layerRoles,
                    $"{sectionLabel} {profile.TerrainName}",
                    LayerRole.Sections,
                    isOwnerProfile ? null : profile.ColorArgb));
                emitted++;
            }
        }

        if (showBaseline && totalStation > 0.0)
        {
            var baseline = SectionLayoutHelper.BuildBaselineAxis(cellPlane, totalStation, horizontalScale, verticalScale, minimumElevation, baseElevation);
            build.AuxiliaryObjects.Add(BuildLineObject(analysis, baseline, layerRoles, $"{sectionLabel} baseline", LayerRole.SectionsGrid));
            emitted++;
        }

        if (showElevationGrid && effectiveElevationGridInterval > 0.0 && totalStation > 0.0)
        {
            var grid = SectionLayoutHelper.BuildElevationGridLines(cellPlane, totalStation, minimumElevation, maximumElevation, baseElevation, effectiveElevationGridInterval, horizontalScale, verticalScale);
            foreach (var line in grid)
            {
                build.AuxiliaryObjects.Add(BuildLineObject(analysis, line, layerRoles, $"{sectionLabel} grid", LayerRole.SectionsGrid));
                emitted++;
            }
        }

        if (showStationTicks && stationTickInterval > 0.0 && totalStation > 0.0)
        {
            var stations = BuildStationList(totalStation, stationTickInterval);
            double tickHalf = Math.Max(textHeight, double.Epsilon);
            var ticks = SectionLayoutHelper.BuildStationTicks(cellPlane, stations, tickHalf, horizontalScale, verticalScale, baseElevation, minimumElevation);
            foreach (var line in ticks)
            {
                build.AuxiliaryObjects.Add(BuildLineObject(analysis, line, layerRoles, $"{sectionLabel} tick", LayerRole.SectionsTicks));
                emitted++;
            }
        }

        if (showStationLabels)
        {
            double labelInterval = stationLabelInterval > 0.0 ? stationLabelInterval : totalStation * 0.25;
            var stations = BuildStationList(totalStation, labelInterval);
            double labelOffset = Math.Max(textHeight, double.Epsilon) * 1.5;
            foreach (double station in stations)
            {
                var label = SectionLayoutHelper.BuildLabel(
                    cellPlane,
                    station,
                    minimumElevation - labelOffset,
                    horizontalScale,
                    verticalScale,
                    baseElevation,
                    station.ToString("F1"),
                    Math.Max(textHeight, double.Epsilon));
                build.AuxiliaryObjects.Add(BuildTextObject(analysis, label, layerRoles, $"{sectionLabel} {station:F1}", LayerRole.SectionsLabels));
                emitted++;
            }
        }

        return new SectionEmissionStats(emitted, cutRegions, fillRegions);
    }

    /// <summary>
    /// How the terrain was cut for one section cell, so an arbitrary reference mesh can be cut the same
    /// way. A polyline cut and a sampled-along-curve cut produce different station parametrizations, and
    /// comparing profiles built two different ways would misreport every depth.
    /// </summary>
    private readonly record struct SectionCutGeometry(
        IReadOnlyList<Point3d>? CutVertices,
        Curve? SampledCurve,
        double SampleInterval)
    {
        public static SectionCutGeometry AlongPolyline(IReadOnlyList<Point3d> cutVertices) =>
            new(cutVertices, null, 0.0);

        public static SectionCutGeometry AlongCurve(Curve curve, double sampleInterval) =>
            new(null, curve, sampleInterval);

        /// <summary>Cuts a mesh exactly as the terrain was cut. Null when the mesh misses the cut.</summary>
        public TerrainSectionResult? Slice(RhinoMesh mesh, double tolerance)
        {
            TerrainSectionResult slice = SampledCurve != null
                ? TerrainSectionSlicer.SampleAlongCurve(mesh, SampledCurve, SampleInterval, tolerance)
                : TerrainSectionSlicer.SliceAlongPolyline(mesh, CutVertices!, tolerance);
            return slice.IsEmpty ? null : slice;
        }
    }

    /// <summary>
    /// The existing-ground profile to shade cut and fill against: explicitly referenced Rhino geometry
    /// first, then another MoleHill terrain. Returns null — with a diagnostic saying why — when cut/fill
    /// is switched on but nothing usable is configured, which used to fail silently and read as "the hatch
    /// does not work".
    /// </summary>
    private static TerrainSectionResult? ResolveCutFillReferenceSlice(
        TerrainBuildSnapshot snapshot,
        SectionCutGeometry cutGeometry,
        TerrainSectionAnnotationDefinitionBase analysis,
        IReadOnlyList<SectionTerrainProfile> profiles,
        double tolerance,
        TerrainBuildResult build,
        RhinoMesh? baseMesh,
        CutFillReferenceMesh referenceMesh)
    {
        if (analysis.CutFillReference.HasReferences)
        {
            RhinoMesh? combined = referenceMesh.Get(snapshot, analysis);
            if (combined == null)
            {
                build.Diagnostics.Add(
                    $"{analysis.Label}: cut/fill reference resolved no mesh geometry; no cut or fill was shaded.");
                return null;
            }

            TerrainSectionResult? slice = cutGeometry.Slice(combined, tolerance);
            if (slice == null)
            {
                build.Diagnostics.Add(
                    $"{analysis.Label}: the cut/fill reference does not reach this section line; no cut or fill was shaded.");
            }

            return slice;
        }

        if (analysis.CutFillReferenceTerrainId.HasValue)
        {
            SectionTerrainProfile? referenceProfile = profiles.FirstOrDefault(
                profile => profile.TerrainId == analysis.CutFillReferenceTerrainId.Value);
            if (referenceProfile != null)
                return referenceProfile.Slice;

            build.Diagnostics.Add(
                $"{analysis.Label}: the reference terrain has no profile on this section line; no cut or fill was shaded.");
            return null;
        }

        // No explicit reference: compare against this terrain's own initial triangulation — the ground as
        // it was before any modifier moved it. That is what "how much cut and fill did my grading do"
        // means, and it is the overwhelmingly common question; requiring a second terrain to ask it made
        // the feature unreachable for the case it exists to serve. An explicit reference still wins, for
        // comparing against surveyed ground that is not this terrain's own starting point.
        if (baseMesh == null)
        {
            build.Diagnostics.Add(
                $"{analysis.Label}: cut/fill shading is on but this terrain has no base triangulation to " +
                "compare against, and no reference is set.");
            return null;
        }

        TerrainSectionResult? baseSlice = cutGeometry.Slice(baseMesh, tolerance);
        if (baseSlice == null)
        {
            build.Diagnostics.Add(
                $"{analysis.Label}: the terrain's initial triangulation does not reach this section line; " +
                "no cut or fill was shaded.");
        }

        return baseSlice;
    }

    /// <summary>
    /// An annotation's explicit cut/fill reference as one mesh, resolved on first use and then shared by
    /// every section cell. Resolving meshes a Brep reference and welds a multi-object one, and neither
    /// result depends on the cell, so doing it per cell repeated the whole cost for every section drawn.
    /// </summary>
    private sealed class CutFillReferenceMesh
    {
        private bool _resolved;
        private RhinoMesh? _mesh;

        /// <summary>The combined reference, or null when it resolves to no mesh geometry.</summary>
        public RhinoMesh? Get(TerrainBuildSnapshot snapshot, TerrainSectionAnnotationDefinitionBase analysis)
        {
            if (_resolved)
                return _mesh;

            _resolved = true;
            var meshes = TerrainBuildSnapshotResolver.ResolveMeshes(snapshot, analysis.CutFillReference);
            _mesh = meshes.Count switch
            {
                0 => null,
                1 => meshes[0],
                _ => CombineMeshes(meshes),
            };
            return _mesh;
        }

        private static RhinoMesh CombineMeshes(IReadOnlyList<RhinoMesh> meshes)
        {
            var combined = new RhinoMesh();
            foreach (RhinoMesh mesh in meshes)
                combined.Append(mesh);
            RhinoGeometryConversions.NormalizeMeshInPlace(combined);
            return combined;
        }
    }

    private static List<SectionTerrainProfile> SliceTerrainsAlongPolyline(
        TerrainBuildSnapshot snapshot,
        RhinoMesh ownerMesh,
        TerrainSectionAnnotationDefinitionBase analysis,
        IReadOnlyList<Point3d> cutVertices,
        double tolerance)
    {
        var profiles = new List<SectionTerrainProfile>();
        TerrainSectionResult ownerSlice = TerrainSectionSlicer.SliceAlongPolyline(ownerMesh, cutVertices, tolerance);
        if (!ownerSlice.IsEmpty)
        {
            profiles.Add(new SectionTerrainProfile(
                snapshot.Terrain.TerrainId,
                snapshot.Terrain.Name,
                analysis.ColorArgb ?? snapshot.Terrain.TerrainColorArgb,
                ownerSlice,
                IsOwner: true));
        }

        foreach (Guid terrainId in analysis.ComparisonTerrainIds)
        {
            if (!snapshot.SectionTerrains.TryGetValue(terrainId, out TerrainSectionReferenceSnapshot? terrain))
                continue;
            TerrainSectionResult slice = TerrainSectionSlicer.SliceAlongPolyline(terrain.Mesh, cutVertices, tolerance);
            if (slice.IsEmpty)
                continue;
            profiles.Add(new SectionTerrainProfile(
                terrain.TerrainId,
                terrain.Name,
                terrain.ColorArgb,
                slice,
                IsOwner: false));
        }

        return profiles;
    }

    private static List<SectionTerrainProfile> SampleTerrainsAlongCurve(
        TerrainBuildSnapshot snapshot,
        RhinoMesh ownerMesh,
        TerrainSectionAnnotationDefinitionBase analysis,
        Curve curve,
        double sampleInterval,
        double tolerance)
    {
        var profiles = new List<SectionTerrainProfile>();
        TerrainSectionResult ownerSlice = TerrainSectionSlicer.SampleAlongCurve(
            ownerMesh, curve, sampleInterval, tolerance);
        if (!ownerSlice.IsEmpty)
        {
            profiles.Add(new SectionTerrainProfile(
                snapshot.Terrain.TerrainId,
                snapshot.Terrain.Name,
                analysis.ColorArgb ?? snapshot.Terrain.TerrainColorArgb,
                ownerSlice,
                IsOwner: true));
        }

        foreach (Guid terrainId in analysis.ComparisonTerrainIds)
        {
            if (!snapshot.SectionTerrains.TryGetValue(terrainId, out TerrainSectionReferenceSnapshot? terrain))
                continue;
            TerrainSectionResult slice = TerrainSectionSlicer.SampleAlongCurve(
                terrain.Mesh, curve, sampleInterval, tolerance);
            if (slice.IsEmpty)
                continue;
            profiles.Add(new SectionTerrainProfile(
                terrain.TerrainId,
                terrain.Name,
                terrain.ColorArgb,
                slice,
                IsOwner: false));
        }

        return profiles;
    }

    private static void AddMissingSectionTerrainDiagnostics(
        TerrainBuildSnapshot snapshot,
        TerrainSectionAnnotationDefinitionBase analysis,
        TerrainBuildResult build)
    {
        foreach (Guid terrainId in analysis.ComparisonTerrainIds.Distinct())
        {
            if (snapshot.SectionTerrains.ContainsKey(terrainId))
                continue;
            build.Diagnostics.Add(
                $"{analysis.Label}: comparison terrain {terrainId} has no completed final mesh; its profile was skipped.");
        }
    }

    /// <summary>
    /// Builds the closed boundary of a cut/fill region and returns it as a hatch. The region is a ribbon
    /// between the proposed and reference profiles, so its outline is the proposed elevations forward then
    /// the reference elevations back. Regions are already split at profile crossings by
    /// <see cref="SectionProfileComparison"/>, so the loop does not self-intersect.
    /// </summary>
    private static IReadOnlyList<Hatch> BuildComparisonRegionHatch(
        SectionComparisonRegion region,
        Plane cellPlane,
        double horizontalScale,
        double verticalScale,
        double baseElevation,
        int hatchPatternIndex,
        double hatchScale,
        double hatchRotationDegrees,
        double tolerance)
    {
        if (region.Vertices.Count < 2)
            return Array.Empty<Hatch>();

        var loop = new List<Point3d>(region.Vertices.Count * 2 + 1);
        for (int i = 0; i < region.Vertices.Count; i++)
        {
            SectionComparisonVertex vertex = region.Vertices[i];
            AppendDistinct(loop, SectionLayoutHelper.ProjectToInsertionPlane(
                cellPlane, vertex.Station, vertex.ProposedElevation, horizontalScale, verticalScale, baseElevation), tolerance);
        }

        for (int i = region.Vertices.Count - 1; i >= 0; i--)
        {
            SectionComparisonVertex vertex = region.Vertices[i];
            AppendDistinct(loop, SectionLayoutHelper.ProjectToInsertionPlane(
                cellPlane, vertex.Station, vertex.ReferenceElevation, horizontalScale, verticalScale, baseElevation), tolerance);
        }

        // A hatch boundary needs three distinct corners; anything less encloses no area.
        if (loop.Count < 3)
            return Array.Empty<Hatch>();

        loop.Add(loop[0]);
        var boundary = new PolylineCurve(loop);
        Hatch[]? hatches = Hatch.Create(
            boundary,
            Math.Max(hatchPatternIndex, 0),
            RhinoMath.ToRadians(hatchRotationDegrees),
            hatchScale > 0.0 ? hatchScale : 1.0,
            Math.Max(tolerance, RhinoMath.ZeroTolerance));

        // Hatch.Create can split one boundary into several hatches; keeping only the first would silently
        // drop part of the filled region.
        return hatches is { Length: > 0 }
            ? hatches.Where(hatch => hatch != null).ToList()
            : (IReadOnlyList<Hatch>)Array.Empty<Hatch>();
    }

    private static void AppendDistinct(List<Point3d> points, Point3d candidate, double tolerance)
    {
        double thresholdSquared = Math.Max(tolerance, RhinoMath.ZeroTolerance);
        thresholdSquared *= thresholdSquared;
        if (points.Count > 0 && points[^1].DistanceToSquared(candidate) <= thresholdSquared)
            return;

        points.Add(candidate);
    }
}
