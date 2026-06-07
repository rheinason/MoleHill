namespace MoleHill.Core.Grading;

public static partial class PathGrader
{
    private static double ComputeConstraintSegmentLength(PathDefinition path, double shoulderDistance)
    {
        double baseSpacing = shoulderDistance > 1e-6 ? shoulderDistance * 0.5 : path.Width;
        double minSpacing = Math.Max(path.Width * 0.5, 1.0);
        double maxSpacing = Math.Max(minSpacing, path.Width * 2.0);
        return Math.Clamp(baseSpacing, minSpacing, maxSpacing);
    }

    private static ConstraintPath MakeGeometryConstraintPath(double[] xy, double[] z, int count)
        => new ConstraintPath(xy, z, count, Array.Empty<double>(), Array.Empty<double>());

    private static ConstraintPath BuildConstraintPolyline(PathDefinition path, double maxSegmentLength, double dedupTol)
    {
        if (path.VertexCount < 2 || maxSegmentLength <= dedupTol)
        {
            double[] xyOut = (double[])path.XyVertices.Clone();
            int n = path.VertexCount;
            ComputeSmoothedTangents(xyOut, n, out double[] tx, out double[] ty);
            return new ConstraintPath(xyOut, (double[])path.ZValues.Clone(), n, tx, ty);
        }

        var xy = new List<double>(path.VertexCount * 4);
        var z = new List<double>(path.VertexCount * 2);
        for (int segmentIndex = 0; segmentIndex < path.VertexCount - 1; segmentIndex++)
        {
            double ax = path.XyVertices[segmentIndex * 2];
            double ay = path.XyVertices[segmentIndex * 2 + 1];
            double bx = path.XyVertices[(segmentIndex + 1) * 2];
            double by = path.XyVertices[(segmentIndex + 1) * 2 + 1];
            double az = path.ZValues[segmentIndex];
            double bz = path.ZValues[segmentIndex + 1];

            double segLen = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
            int divisions = Math.Max(1, (int)Math.Ceiling(segLen / maxSegmentLength));

            for (int step = 0; step < divisions; step++)
            {
                double t = (double)step / divisions;
                AddConstraintSample(xy, z, ax + (bx - ax) * t, ay + (by - ay) * t, az + ((bz - az) * t), dedupTol);
            }
        }

        AddConstraintSample(
            xy,
            z,
            path.XyVertices[(path.VertexCount - 1) * 2],
            path.XyVertices[(path.VertexCount - 1) * 2 + 1],
            path.ZValues[path.VertexCount - 1],
            dedupTol);

        double[] xyArr = xy.ToArray();
        int count = xy.Count / 2;
        ComputeSmoothedTangents(xyArr, count, out double[] tangentX, out double[] tangentY);
        return new ConstraintPath(xyArr, z.ToArray(), count, tangentX, tangentY);
    }

    private static ConstraintPath ResampleConstraintPath(ConstraintPath path, double maxSegmentLength, double dedupTol)
    {
        if (path.VertexCount < 3 || maxSegmentLength <= dedupTol)
            return path;

        var cumulativeLengths = BuildConstraintCumulativeLengths(path.XyVertices, path.VertexCount);
        double totalLength = cumulativeLengths[^1];
        if (totalLength <= maxSegmentLength + dedupTol)
            return path;

        var xy = new List<double>(path.VertexCount * 2);
        var z = new List<double>(path.VertexCount);
        int sampleCount = Math.Max(2, (int)Math.Ceiling(totalLength / maxSegmentLength) + 1);
        for (int sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
        {
            double targetDistance = sampleIndex == sampleCount - 1
                ? totalLength
                : Math.Min(sampleIndex * maxSegmentLength, totalLength);
            SampleConstraintPathAtDistance(path, cumulativeLengths, targetDistance, out double x, out double y, out double elevation);
            AddConstraintSample(xy, z, x, y, elevation, dedupTol);
        }

        double[] xyArr = xy.ToArray();
        int count = xyArr.Length / 2;
        ComputeSmoothedTangents(xyArr, count, out double[] tangentX, out double[] tangentY);
        return new ConstraintPath(xyArr, z.ToArray(), count, tangentX, tangentY);
    }

    private static double[] ResampleConstraintRow(
        double[] rowXy,
        double[] referenceXy,
        double maxSegmentLength,
        double dedupTol)
    {
        int vertexCount = Math.Min(rowXy.Length, referenceXy.Length) / 2;
        if (vertexCount < 3 || maxSegmentLength <= dedupTol)
            return rowXy;

        double[] cumulativeLengths = BuildConstraintCumulativeLengths(referenceXy, vertexCount);
        double totalLength = cumulativeLengths[^1];
        if (totalLength <= maxSegmentLength + dedupTol)
            return rowXy;

        var resampled = new List<double>(rowXy.Length);
        int sampleCount = Math.Max(2, (int)Math.Ceiling(totalLength / maxSegmentLength) + 1);
        for (int sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
        {
            double targetDistance = sampleIndex == sampleCount - 1
                ? totalLength
                : Math.Min(sampleIndex * maxSegmentLength, totalLength);
            SampleConstraintRowAtDistance(rowXy, cumulativeLengths, vertexCount, targetDistance, out double x, out double y);
            if (resampled.Count >= 2)
            {
                double dx = x - resampled[^2];
                double dy = y - resampled[^1];
                if ((dx * dx) + (dy * dy) <= dedupTol * dedupTol)
                    continue;
            }

            resampled.Add(x);
            resampled.Add(y);
        }

        return resampled.ToArray();
    }

    private static double[] BuildConstraintCumulativeLengths(double[] xyVertices, int vertexCount)
    {
        var cumulativeLengths = new double[vertexCount];
        for (int i = 1; i < vertexCount; i++)
        {
            double dx = xyVertices[i * 2] - xyVertices[(i - 1) * 2];
            double dy = xyVertices[i * 2 + 1] - xyVertices[(i - 1) * 2 + 1];
            cumulativeLengths[i] = cumulativeLengths[i - 1] + Math.Sqrt((dx * dx) + (dy * dy));
        }

        return cumulativeLengths;
    }

    private static void SampleConstraintPathAtDistance(
        ConstraintPath path,
        double[] cumulativeLengths,
        double targetDistance,
        out double x,
        out double y,
        out double elevation)
    {
        int segmentIndex = FindConstraintDistanceSegment(cumulativeLengths, targetDistance);
        double segmentStartDistance = cumulativeLengths[segmentIndex - 1];
        double segmentEndDistance = cumulativeLengths[segmentIndex];
        double blend = segmentEndDistance <= segmentStartDistance + 1e-12
            ? 0.0
            : (targetDistance - segmentStartDistance) / (segmentEndDistance - segmentStartDistance);

        double ax = path.XyVertices[(segmentIndex - 1) * 2];
        double ay = path.XyVertices[(segmentIndex - 1) * 2 + 1];
        double bx = path.XyVertices[segmentIndex * 2];
        double by = path.XyVertices[segmentIndex * 2 + 1];
        double az = path.ZValues[segmentIndex - 1];
        double bz = path.ZValues[segmentIndex];
        x = ax + ((bx - ax) * blend);
        y = ay + ((by - ay) * blend);
        elevation = az + ((bz - az) * blend);
    }

    private static void SampleConstraintRowAtDistance(
        double[] rowXy,
        double[] cumulativeLengths,
        int vertexCount,
        double targetDistance,
        out double x,
        out double y)
    {
        int segmentIndex = FindConstraintDistanceSegment(cumulativeLengths, targetDistance);
        double segmentStartDistance = cumulativeLengths[segmentIndex - 1];
        double segmentEndDistance = cumulativeLengths[segmentIndex];
        double blend = segmentEndDistance <= segmentStartDistance + 1e-12
            ? 0.0
            : (targetDistance - segmentStartDistance) / (segmentEndDistance - segmentStartDistance);

        double ax = rowXy[(segmentIndex - 1) * 2];
        double ay = rowXy[(segmentIndex - 1) * 2 + 1];
        double bx = rowXy[segmentIndex * 2];
        double by = rowXy[segmentIndex * 2 + 1];
        x = ax + ((bx - ax) * blend);
        y = ay + ((by - ay) * blend);
    }

    private static int FindConstraintDistanceSegment(double[] cumulativeLengths, double targetDistance)
    {
        if (cumulativeLengths.Length < 2)
            return 1;

        int segmentIndex = 1;
        while (segmentIndex < cumulativeLengths.Length && cumulativeLengths[segmentIndex] < targetDistance)
            segmentIndex++;

        return Math.Min(segmentIndex, cumulativeLengths.Length - 1);
    }

    private static void ComputeSmoothedTangents(
        double[] xyVertices,
        int vertexCount,
        out double[] tangentX,
        out double[] tangentY)
    {
        tangentX = new double[vertexCount];
        tangentY = new double[vertexCount];

        if (vertexCount < 2)
            return;

        for (int i = 0; i < vertexCount; i++)
        {
            double sumX = 0.0;
            double sumY = 0.0;
            double prevDx = 0.0;
            double prevDy = 0.0;
            double prevLen = 0.0;
            if (i > 0)
            {
                prevDx = xyVertices[i * 2] - xyVertices[(i - 1) * 2];
                prevDy = xyVertices[i * 2 + 1] - xyVertices[(i - 1) * 2 + 1];
                prevLen = Math.Sqrt((prevDx * prevDx) + (prevDy * prevDy));
                if (prevLen < 1e-9)
                    prevLen = 0.0;
            }

            double nextDx = 0.0;
            double nextDy = 0.0;
            double nextLen = 0.0;
            if (i < vertexCount - 1)
            {
                nextDx = xyVertices[(i + 1) * 2] - xyVertices[i * 2];
                nextDy = xyVertices[(i + 1) * 2 + 1] - xyVertices[i * 2 + 1];
                nextLen = Math.Sqrt((nextDx * nextDx) + (nextDy * nextDy));
                if (nextLen < 1e-9)
                    nextLen = 0.0;
            }

            bool hasPrev = prevLen > 1e-9;
            bool hasNext = nextLen > 1e-9;
            if (hasPrev && hasNext)
            {
                double pnx = prevDx / prevLen;
                double pny = prevDy / prevLen;
                double nnx = nextDx / nextLen;
                double nny = nextDy / nextLen;
                double dot = (pnx * nnx) + (pny * nny);
                if (dot < -0.7)
                {
                    if (prevLen >= nextLen)
                    {
                        sumX = pnx * prevLen;
                        sumY = pny * prevLen;
                    }
                    else
                    {
                        sumX = nnx * nextLen;
                        sumY = nny * nextLen;
                    }
                }
                else
                {
                    sumX = (pnx * prevLen) + (nnx * nextLen);
                    sumY = (pny * prevLen) + (nny * nextLen);
                }
            }
            else if (hasPrev)
            {
                sumX = prevDx;
                sumY = prevDy;
            }
            else if (hasNext)
            {
                sumX = nextDx;
                sumY = nextDy;
            }

            double len = Math.Sqrt((sumX * sumX) + (sumY * sumY));
            if (len < 1e-9)
            {
                if (prevLen >= nextLen && prevLen > 1e-9)
                {
                    sumX = prevDx;
                    sumY = prevDy;
                }
                else if (nextLen > 1e-9)
                {
                    sumX = nextDx;
                    sumY = nextDy;
                }
                else
                {
                    tangentX[i] = 1.0;
                    tangentY[i] = 0.0;
                    continue;
                }

                len = Math.Sqrt((sumX * sumX) + (sumY * sumY));
            }

            tangentX[i] = sumX / len;
            tangentY[i] = sumY / len;
        }
    }
}
