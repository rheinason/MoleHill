using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

internal static class GradingInputValidator
{
    public static bool ValidateFiniteValues(
        double[]? values,
        int requiredValueCount,
        string emptyOrShortMessage,
        string nonFiniteMessage,
        out string? errorMessage)
    {
        errorMessage = null;
        if (values == null)
        {
            errorMessage = emptyOrShortMessage;
            return false;
        }

        if (requiredValueCount < 0 ||
            requiredValueCount > values.Length)
        {
            errorMessage = emptyOrShortMessage;
            return false;
        }

        for (int i = 0; i < requiredValueCount; i++)
        {
            if (!double.IsFinite(values[i]))
            {
                errorMessage = nonFiniteMessage;
                return false;
            }
        }

        return true;
    }

    public static bool ValidateTerrainMesh(
        double[]? vertices,
        int vertexCount,
        int[]? faces,
        int faceCount,
        out string? errorMessage)
    {
        errorMessage = null;

        if (vertices == null)
        {
            errorMessage = "Terrain vertices are required.";
            return false;
        }

        if (faces == null)
        {
            errorMessage = "Terrain faces are required.";
            return false;
        }

        if (vertexCount < 3)
        {
            errorMessage = "Terrain mesh must contain at least 3 vertices.";
            return false;
        }

        if (faceCount < 1)
        {
            errorMessage = "Terrain mesh must contain at least 1 face.";
            return false;
        }

        long requiredVertexValues = (long)vertexCount * 3;
        long requiredFaceValues = (long)faceCount * 3;

        if (requiredVertexValues > int.MaxValue ||
            requiredFaceValues > int.MaxValue)
        {
            errorMessage = "Terrain mesh is too large to validate safely.";
            return false;
        }

        if (!ValidateFiniteValues(
                vertices,
                (int)requiredVertexValues,
                "Terrain vertex array is shorter than vertexCount requires.",
                "Terrain vertices must contain only finite coordinates.",
                out errorMessage))
        {
            return false;
        }

        if (faces.Length < requiredFaceValues)
        {
            errorMessage = "Terrain face array is shorter than faceCount requires.";
            return false;
        }

        for (int faceIndex = 0; faceIndex < faceCount; faceIndex++)
        {
            int a = faces[faceIndex * 3];
            int b = faces[faceIndex * 3 + 1];
            int c = faces[faceIndex * 3 + 2];
            if ((uint)a >= (uint)vertexCount ||
                (uint)b >= (uint)vertexCount ||
                (uint)c >= (uint)vertexCount)
            {
                errorMessage = $"Terrain face {faceIndex} references a vertex outside the terrain vertex range.";
                return false;
            }

            if (a == b || b == c || c == a)
            {
                errorMessage = $"Terrain face {faceIndex} is degenerate.";
                return false;
            }
        }

        return true;
    }

    public static bool ValidateVertexArray(
        double[]? vertices,
        int vertexCount,
        string label,
        out string? errorMessage)
    {
        errorMessage = null;

        if (vertices == null)
        {
            errorMessage = $"{label} vertices are required.";
            return false;
        }

        if (vertexCount < 0)
        {
            errorMessage = $"{label} vertexCount cannot be negative.";
            return false;
        }

        long requiredValues = (long)vertexCount * 3;
        if (requiredValues > int.MaxValue)
        {
            errorMessage = $"{label} vertex array is too large to validate safely.";
            return false;
        }

        return ValidateFiniteValues(
            vertices,
            (int)requiredValues,
            $"{label} vertex array is shorter than vertexCount requires.",
            $"{label} vertices must contain only finite coordinates.",
            out errorMessage);
    }

    public static bool ValidateConstraintPolylines(
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline>? constraints,
        string label,
        out string? errorMessage)
    {
        errorMessage = null;
        if (constraints == null)
            return true;

        for (int i = 0; i < constraints.Count; i++)
        {
            SurfaceRemesher.ConstraintPolyline constraint = constraints[i];
            if (constraint.PointCount < 0)
            {
                errorMessage = $"{label} constraint {i} has an invalid point count.";
                return false;
            }

            long requiredValues = (long)constraint.PointCount * 3;
            if (requiredValues > int.MaxValue ||
                !ValidateFiniteValues(
                    constraint.Points,
                    (int)requiredValues,
                    $"{label} constraint {i} point array is shorter than PointCount requires.",
                    $"{label} constraint {i} points must contain only finite coordinates.",
                    out errorMessage))
            {
                return false;
            }
        }

        return true;
    }

    public static bool ValidateLockCurves(
        IReadOnlyList<PadGrader.LockCurve>? lockCurves,
        out string? errorMessage)
    {
        errorMessage = null;
        if (lockCurves == null)
            return true;

        for (int i = 0; i < lockCurves.Count; i++)
        {
            PadGrader.LockCurve lockCurve = lockCurves[i];
            if (lockCurve == null ||
                lockCurve.VertexCount < 2)
            {
                errorMessage = $"Lock curve {i} must contain at least 2 vertices.";
                return false;
            }

            long requiredValues = (long)lockCurve.VertexCount * 2;
            if (requiredValues > int.MaxValue ||
                !ValidateFiniteValues(
                    lockCurve.XyVertices,
                    (int)requiredValues,
                    $"Lock curve {i} vertex array is shorter than VertexCount requires.",
                    $"Lock curve {i} coordinates must contain only finite values.",
                    out errorMessage))
            {
                return false;
            }
        }

        return true;
    }

    public static bool ValidatePathDefinitions(
        IReadOnlyList<PathGrader.PathDefinition>? paths,
        out string? errorMessage,
        bool requireAny = true)
    {
        errorMessage = null;

        if (paths == null)
        {
            errorMessage = "No path definitions provided.";
            return false;
        }

        if (requireAny && paths.Count == 0)
        {
            errorMessage = "No path definitions provided.";
            return false;
        }

        for (int i = 0; i < paths.Count; i++)
        {
            PathGrader.PathDefinition path = paths[i];
            if (path == null)
            {
                errorMessage = "Each path must have valid XY and Z vertices.";
                return false;
            }

            if (path.VertexCount < 2)
            {
                errorMessage = "Each path must have at least 2 vertices.";
                return false;
            }

            long requiredPathXyValues = (long)path.VertexCount * 2;
            if (requiredPathXyValues > int.MaxValue ||
                !ValidateFiniteValues(
                    path.XyVertices,
                    (int)requiredPathXyValues,
                    "Each path must have valid XY and Z vertices.",
                    "Path coordinates must contain only finite values.",
                    out errorMessage) ||
                !ValidateFiniteValues(
                    path.ZValues,
                    path.VertexCount,
                    "Each path must have valid XY and Z vertices.",
                    "Path elevations must contain only finite values.",
                    out errorMessage))
            {
                return false;
            }

            if (!double.IsFinite(path.Width) || path.Width <= 0)
            {
                errorMessage = "Path width must be positive.";
                return false;
            }

            if (!double.IsFinite(path.SlopeAngleDeg) ||
                !double.IsFinite(path.MaxDistance) ||
                path.MaxDistance < 0.0)
            {
                errorMessage = "Each path must define finite grading parameters.";
                return false;
            }
        }

        return true;
    }

    public static bool ValidatePadBoundaries(
        IReadOnlyList<PadGrader.PadBoundary>? pads,
        out string? errorMessage,
        bool requireAny = true)
    {
        errorMessage = null;

        if (pads == null)
        {
            errorMessage = "No pad boundaries provided.";
            return false;
        }

        if (requireAny && pads.Count == 0)
        {
            errorMessage = "No pad boundaries provided.";
            return false;
        }

        for (int i = 0; i < pads.Count; i++)
        {
            PadGrader.PadBoundary pad = pads[i];
            if (pad == null ||
                pad.VertexCount < 3)
            {
                errorMessage = "Each pad must have at least 3 valid vertices.";
                return false;
            }

            long requiredPadXyValues = (long)pad.VertexCount * 2;
            long requiredPadBoundaryValues = (long)pad.VertexCount * 3;
            if (requiredPadXyValues > int.MaxValue ||
                requiredPadBoundaryValues > int.MaxValue ||
                !ValidateFiniteValues(
                    pad.XyVertices,
                    (int)requiredPadXyValues,
                    "Each pad must have at least 3 valid vertices.",
                    "Pad coordinates must contain only finite values.",
                    out errorMessage) ||
                !ValidateFiniteValues(
                    pad.BoundaryVertices,
                    (int)requiredPadBoundaryValues,
                    "Each pad must have at least 3 valid vertices.",
                    "Pad boundary vertices must contain only finite values.",
                    out errorMessage))
            {
                return false;
            }

            if (!double.IsFinite(pad.PlaneXCoeff) ||
                !double.IsFinite(pad.PlaneYCoeff) ||
                !double.IsFinite(pad.PlaneConstant) ||
                !double.IsFinite(pad.SlopeAngleDeg) ||
                !double.IsFinite(pad.MaxDistance) ||
                !double.IsFinite(pad.StitchApronDistance) ||
                pad.MaxDistance < 0.0 ||
                pad.StitchApronDistance < 0.0)
            {
                errorMessage = "Each pad must define valid finite grading parameters.";
                return false;
            }
        }

        return true;
    }

    public static bool ValidateSurfaceDefinitions(
        IReadOnlyList<SurfaceStripGrader.SurfaceDefinition>? surfaces,
        out string? errorMessage,
        bool requireAny = true)
    {
        errorMessage = null;

        if (surfaces == null)
        {
            errorMessage = "At least one graded surface is required.";
            return false;
        }

        if (requireAny && surfaces.Count == 0)
        {
            errorMessage = "At least one graded surface is required.";
            return false;
        }

        for (int i = 0; i < surfaces.Count; i++)
        {
            SurfaceStripGrader.SurfaceDefinition surface = surfaces[i];
            if (surface == null)
            {
                errorMessage = "Each graded surface must be valid.";
                return false;
            }

            if (surface.FootprintVertexCount < 3 ||
                surface.FootprintXy == null)
            {
                errorMessage = "The graded surface footprint must contain at least 3 vertices.";
                return false;
            }

            if (surface.BoundaryVertexCount < 2 ||
                surface.BoundaryVertices == null)
            {
                errorMessage = "The graded surface boundary must contain at least 2 vertices.";
                return false;
            }

            long requiredFootprintValues = (long)surface.FootprintVertexCount * 2;
            if (requiredFootprintValues > int.MaxValue ||
                !ValidateFiniteValues(
                    surface.FootprintXy,
                    (int)requiredFootprintValues,
                    "The graded surface footprint must contain at least 3 vertices.",
                    "The graded surface footprint must contain only finite coordinates.",
                    out errorMessage))
            {
                return false;
            }

            long requiredBoundaryValues = (long)surface.BoundaryVertexCount * 3;
            if (requiredBoundaryValues > int.MaxValue ||
                !ValidateFiniteValues(
                    surface.BoundaryVertices,
                    (int)requiredBoundaryValues,
                    "The graded surface boundary must contain at least 2 vertices.",
                    "The graded surface boundary must contain only finite coordinates.",
                    out errorMessage))
            {
                return false;
            }

            if (!double.IsFinite(surface.PlaneXCoeff) ||
                !double.IsFinite(surface.PlaneYCoeff) ||
                !double.IsFinite(surface.PlaneConstant) ||
                !double.IsFinite(surface.SlopeAngleDeg) ||
                !double.IsFinite(surface.MaxDistance) ||
                surface.MaxDistance < 0.0)
            {
                errorMessage = "Each graded surface must define valid finite grading parameters.";
                return false;
            }
        }

        return true;
    }
}
