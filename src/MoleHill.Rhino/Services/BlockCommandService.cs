using System.Globalization;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;
using Rhino.Render;

namespace MoleHill.Rhino.Services;

internal static class BlockCommandService
{
    public static Result RunExternalizeBlock(RhinoDoc doc)
    {
        var getObject = new GetObject();
        getObject.SetCommandPrompt("Select block to link");
        getObject.GeometryFilter = ObjectType.InstanceReference;
        getObject.EnablePreSelect(true, true);
        if (getObject.Get() != global::Rhino.Input.GetResult.Object)
            return getObject.CommandResult();

        if (getObject.Object(0)?.Object() is not InstanceObject instanceObject)
            return Result.Nothing;

        InstanceDefinition? definition = instanceObject.InstanceDefinition;
        if (definition == null)
            return Result.Failure;

        if (definition.UpdateType != InstanceDefinitionUpdateType.Static)
        {
            RhinoApp.WriteLine("Block is already linked.");
            return Result.Nothing;
        }

        var folderDialog = new Eto.Forms.SelectFolderDialog { Title = "Directory for linked files" };
        if (folderDialog.ShowDialog(global::Rhino.UI.RhinoEtoApp.MainWindowForDocument(doc)) != Eto.Forms.DialogResult.Ok ||
            string.IsNullOrWhiteSpace(folderDialog.Directory))
            return Result.Cancel;
        string folder = folderDialog.Directory;

        string blockName = definition.Name;
        string filepath = Path.Combine(folder, $"{blockName}.3dm");
        string exportCommand = string.Create(CultureInfo.InvariantCulture,
            $"_-BlockManager _Export \"{blockName}\" \"{filepath}\" _Enter");
        if (!RhinoApp.RunScript(exportCommand, false))
            return Result.Failure;

        string updateCommand = string.Create(CultureInfo.InvariantCulture,
            $"_-BlockManager _Properties \"{blockName}\" _UpdateType=Linked \"{filepath}\" _UpdateType=Linked _Enter _Enter");
        if (!RhinoApp.RunScript(updateCommand, false))
            return Result.Failure;

        definition = doc.InstanceDefinitions.Find(blockName);
        if (definition != null &&
            definition.UpdateType == InstanceDefinitionUpdateType.Linked &&
            definition.LayerStyle == InstanceDefinitionLayerStyle.Reference)
        {
            definition.LayerStyle = InstanceDefinitionLayerStyle.Active;
        }

        RhinoApp.WriteLine($"Block successfully linked to: {filepath}");
        return Result.Success;
    }

    public static Result RunUpdateAllLinkedBlocks()
    {
        bool ran = RhinoApp.RunScript("_UpdateAllLinkedBlocks _Enter", false);
        return ran ? Result.Success : Result.Failure;
    }

    public static Result RunSetSunNorth(RhinoDoc doc)
    {
        RhinoApp.WriteLine("Pick two points to define north direction.");
        Result lineResult = RhinoGet.GetLine(out Line line);
        if (lineResult != Result.Success)
            return lineResult;

        var direction = line.Direction;
        double angle = Math.Atan2(direction.Y, direction.X) * 180.0 / Math.PI;
        angle = Math.Round((angle + 360.0) % 360.0, 1);
        if (Math.Abs(angle - 360.0) < RhinoMath.ZeroTolerance)
            angle = 0.0;

        Sun rhinoSun = doc.Lights.Sun;
        bool changed = Math.Abs(DocumentNorth.AzimuthDegrees(doc) - angle) > RhinoMath.ZeroTolerance;
        rhinoSun.North = angle;
        RhinoApp.WriteLine(angle.ToString("G", CultureInfo.InvariantCulture));

        // Aspect colours and bearings are measured from north, and the build fingerprint includes it, but
        // nothing about this command edits a terrain — so without asking, they kept the old north until
        // some unrelated edit happened to rebuild them.
        if (changed)
        {
            foreach (Guid terrainId in DocumentNorth.TerrainsToRefreshAfterChange(TerrainController.Instance.GetTerrains(doc)))
                TerrainController.Instance.RebuildTerrain(doc, terrainId);
        }

        return Result.Success;
    }

    public static Result RunMutateMarkerParentheses(RhinoDoc doc, bool add)
    {
        var getObject = new GetObject();
        getObject.SetCommandPrompt(add
            ? "Select marker blocks to add parentheses"
            : "Select marker blocks to remove parentheses");
        getObject.GeometryFilter = ObjectType.InstanceReference;
        getObject.EnablePreSelect(true, true);
        getObject.GetMultiple(1, 0);
        if (getObject.CommandResult() != Result.Success)
            return getObject.CommandResult();

        bool changed = false;
        for (int i = 0; i < getObject.ObjectCount; i++)
        {
            if (getObject.Object(i)?.Object() is not InstanceObject instanceObject)
                continue;

            ObjectAttributes attributes = instanceObject.Attributes.Duplicate();
            string prefixKey = instanceObject.Attributes.GetUserString(GeneratedBlockCatalog.PrefixToken) != null
                ? GeneratedBlockCatalog.PrefixToken
                : "Prefix";
            string suffixKey = instanceObject.Attributes.GetUserString(GeneratedBlockCatalog.SuffixToken) != null
                ? GeneratedBlockCatalog.SuffixToken
                : "Suffix";

            string currentPrefix = attributes.GetUserString(prefixKey) ?? string.Empty;
            string currentSuffix = attributes.GetUserString(suffixKey) ?? string.Empty;
            string nextPrefix = add
                ? (currentPrefix.Contains('(') ? currentPrefix : $"{currentPrefix}(")
                : RemoveFirst(currentPrefix, '(');
            string nextSuffix = add
                ? (currentSuffix.Contains(')') ? currentSuffix : $"){currentSuffix}")
                : RemoveFirst(currentSuffix, ')');

            attributes.SetUserString(prefixKey, nextPrefix);
            attributes.SetUserString(suffixKey, nextSuffix);
            changed |= doc.Objects.ModifyAttributes(instanceObject.Id, attributes, quiet: true);
        }

        if (changed)
            doc.Views.Redraw();
        return changed ? Result.Success : Result.Nothing;
    }

    private static string RemoveFirst(string value, char token)
    {
        int index = value.IndexOf(token);
        if (index < 0)
            return value;

        return value.Remove(index, 1);
    }
}
