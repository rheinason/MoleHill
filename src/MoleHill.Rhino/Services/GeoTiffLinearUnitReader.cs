namespace MoleHill.Rhino.Services;

internal readonly record struct GeoTiffLinearUnit(string Name, double MetersPerUnit);

internal static class GeoTiffLinearUnitReader
{
    private const ushort ProjLinearUnitsGeoKey = 3076;

    public static bool TryRead(
        IReadOnlyList<ushort> directory,
        out GeoTiffLinearUnit linearUnit)
    {
        linearUnit = default;
        if (directory.Count < 4)
            return false;

        int keyCount = directory[3];
        for (int index = 0; index < keyCount; index++)
        {
            int offset = 4 + (index * 4);
            if (offset + 3 >= directory.Count)
                break;

            ushort keyId = directory[offset];
            ushort tagLocation = directory[offset + 1];
            ushort count = directory[offset + 2];
            ushort value = directory[offset + 3];
            // Only projected-coordinate units describe the raster model-space X/Y values.
            // GeogLinearUnitsGeoKey (2052) describes linear parts of a geographic CRS while
            // its raster coordinates are ordinarily angular, so applying it as an XY scale
            // would silently misplace latitude/longitude imagery.
            if (keyId != ProjLinearUnitsGeoKey || tagLocation != 0 || count != 1)
            {
                continue;
            }

            return TryGetEpsgLinearUnit(value, out linearUnit);
        }

        return false;
    }

    private static bool TryGetEpsgLinearUnit(ushort code, out GeoTiffLinearUnit linearUnit)
    {
        linearUnit = code switch
        {
            9001 => new GeoTiffLinearUnit("metres", 1.0),
            9002 => new GeoTiffLinearUnit("international feet", 0.3048),
            9003 => new GeoTiffLinearUnit("US survey feet", 1200.0 / 3937.0),
            9005 => new GeoTiffLinearUnit("Clarke's feet", 0.3047972654),
            9014 => new GeoTiffLinearUnit("fathoms", 1.8288),
            9030 => new GeoTiffLinearUnit("nautical miles", 1852.0),
            _ => default
        };
        return linearUnit.MetersPerUnit > 0.0;
    }
}
