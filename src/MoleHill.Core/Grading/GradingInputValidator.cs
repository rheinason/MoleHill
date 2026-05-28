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
}
