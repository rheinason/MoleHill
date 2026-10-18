namespace MoleHill.Core.Scan;

/// <summary>
/// Heights on a regular plan lattice: node (column, row) sits at
/// (<see cref="OriginX"/> + column x <see cref="CellSize"/>, <see cref="OriginY"/> + row x <see cref="CellSize"/>),
/// row-major, NaN where there is no value. The shape every scan stage reads and writes.
/// </summary>
public sealed class ScanGrid
{
    public ScanGrid(double originX, double originY, double cellSize, int columns, int rows, double[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (columns < 0 || rows < 0 || (long)columns * rows != values.Length)
            throw new ArgumentException($"{columns} x {rows} nodes need {(long)columns * rows:N0} values; got {values.Length:N0}.", nameof(values));
        if (!(cellSize > 0.0) || !double.IsFinite(cellSize))
            throw new ArgumentOutOfRangeException(nameof(cellSize), "Cell size must be positive.");

        OriginX = originX;
        OriginY = originY;
        CellSize = cellSize;
        Columns = columns;
        Rows = rows;
        Values = values;
    }

    public double OriginX { get; }

    public double OriginY { get; }

    public double CellSize { get; }

    public int Columns { get; }

    public int Rows { get; }

    /// <summary>Row-major heights, NaN where there is none.</summary>
    public double[] Values { get; }

    public int NodeCount => Values.Length;

    public double this[int column, int row] => Values[(row * Columns) + column];

    public double NodeX(int column) => OriginX + (column * CellSize);

    public double NodeY(int row) => OriginY + (row * CellSize);

    /// <summary>The plan area one node stands for.</summary>
    public double NodeArea => CellSize * CellSize;

    public ScanGrid WithValues(double[] values) => new(OriginX, OriginY, CellSize, Columns, Rows, values);
}
