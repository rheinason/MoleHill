// Resolves Grasshopper inputs (Rhino.Inside.Revit goo, raw elements, ids or numbers) to Revit values.
using System.Globalization;
using System.Reflection;
using Autodesk.Revit.DB;
using Grasshopper.Kernel.Types;

namespace MoleHill.Revit.RevitHost;

internal static class RevitInputs
{
    /// <summary>
    /// Unwraps an input to the Revit element it names, or to a bare id. Rhino.Inside.Revit's goo hands its
    /// element over through <see cref="IGH_Goo.ScriptVariable"/>, which is how its own Python components
    /// see it; that keeps this assembly free of any Rhino.Inside.Revit reference.
    /// </summary>
    public static object? Unwrap(object? value)
    {
        for (int depth = 0; depth < 4 && value is IGH_Goo goo; depth++)
            value = goo.ScriptVariable();
        return value;
    }

    public static Element? AsElement(object? value, Document? document)
    {
        value = Unwrap(value);
        if (value is Element element)
            return element;
        ElementId id = AsElementId(value);
        return document != null && id != ElementId.InvalidElementId ? document.GetElement(id) : null;
    }

    public static ElementId AsElementId(object? value)
    {
        value = Unwrap(value);
        switch (value)
        {
            case null:
                return ElementId.InvalidElementId;
            case ElementId id:
                return id;
            case Element element:
                return element.Id;
            case int number:
                return new ElementId((long)number);
            case long number:
                return new ElementId(number);
            case double number when Math.Abs(number - Math.Round(number)) < 1e-9:
                return new ElementId((long)Math.Round(number));
            case string text when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed):
                return new ElementId(parsed);
        }

        return value.GetType().GetProperty("Id")?.GetValue(value) as ElementId ?? ElementId.InvalidElementId;
    }

    /// <summary>
    /// The Revit document Rhino.Inside.Revit is working in, read by reflection
    /// (<c>RhinoInside.Revit.Revit.ActiveDBDocument</c>, the same property its Python samples use).
    /// </summary>
    public static Document? ActiveDocument()
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (!string.Equals(assembly.GetName().Name, "RhinoInside.Revit", StringComparison.Ordinal))
                continue;
            PropertyInfo? property = assembly.GetType("RhinoInside.Revit.Revit")?
                .GetProperty("ActiveDBDocument", BindingFlags.Public | BindingFlags.Static);
            if (property?.GetValue(null) is Document document)
                return document;
        }

        return null;
    }
}
