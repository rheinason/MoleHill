using System.Collections;
using System.Reflection;
using MoleHill.Core.Reporting;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace MoleHill.Rhino.Tests;

/// <summary>Invoke inside an owned Rhino against the exact loaded RHP.</summary>
public static class HostedScatterTrancheTests
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    public static void Run(RhinoDoc doc, string csvPath)
    {
        Assembly plugin = global::Rhino.PlugIns.PlugIn.Find(new Guid("0c0b9e83-4959-437e-a935-4addf0d3f886")).GetType().Assembly;
        object controller = plugin.GetType("MoleHill.Rhino.Services.TerrainController")!.GetProperty("Instance", Flags)!.GetValue(null)!;
        doc.ModelUnitSystem = UnitSystem.Meters;
        using var mesh = new Mesh();
        mesh.Vertices.Add(-20, -20, 0); mesh.Vertices.Add(20, -20, 0);
        mesh.Vertices.Add(20, 20, 0); mesh.Vertices.Add(-20, 20, 0);
        mesh.Faces.AddFace(0, 1, 2); mesh.Faces.AddFace(0, 2, 3);
        object terrain = Call(controller, "CreateTerrainFromTinMeshId", doc, doc.Objects.AddMesh(mesh), "Scatter tranche")!;
        Set(terrain, "LiveUpdateEnabled", false);
        Guid terrainId = (Guid)Get(terrain, "TerrainId");
        object New(string name) => Activator.CreateInstance(plugin.GetType("MoleHill.Rhino.Model." + name)!)!;
        Guid Boundary(double minX, double minY, double maxX, double maxY)
        {
            using var curve = new PolylineCurve(new[] { new Point3d(minX,minY,0), new Point3d(maxX,minY,0), new Point3d(maxX,maxY,0), new Point3d(minX,maxY,0), new Point3d(minX,minY,0) });
            return doc.Objects.AddCurve(curve);
        }
        object Zone(string name, Guid boundary)
        {
            object zone = New("CollageZoneDefinition"); Set(zone,"Name",name); Set(zone,"UseInputElevationForPriority",false);
            ((IList)Get(Get(zone,"Boundaries"),"ObjectIds")).Add(boundary);
            ((IList)Get(terrain,"Zones")).Add(zone); return zone;
        }
        object plantedZone = Zone("Planting", Boundary(-10,-10,10,10));
        Zone("Higher priority island", Boundary(-2,-2,2,2));
        Call(controller,"AddObjectDefinition",doc,terrainId,"scatter");
        object scatter = ((IList)Get(terrain,"Objects"))[0]!;
        Guid scatterId = (Guid)Get(scatter,"Id");
        Set(scatter,"Count",2000d); Set(scatter,"UseTerrainZones",true);
        ((IList)Get(scatter,"ZoneIds")).Add(Get(plantedZone,"ZoneId"));
        Set(scatter,"BoundarySetback",1d); Set(scatter,"BoundaryFeather",2d); Set(scatter,"ExclusionClearance",0.5d);
        ((IList)Get(Get(scatter,"Exclusions"),"ObjectIds")).Add(Boundary(4,-10,6,10));
        using var blockMesh = Mesh.CreateFromBox(new BoundingBox(new Point3d(-0.2,-0.2,0),new Point3d(0.2,0.2,1)),1,1,1);
        doc.InstanceDefinitions.Add("Tranche Oak","",Point3d.Origin,new GeometryBase[]{blockMesh},new[]{new ObjectAttributes()});
        doc.InstanceDefinitions.Add("Tranche Birch","",Point3d.Origin,new GeometryBase[]{blockMesh},new[]{new ObjectAttributes()});
        foreach (string name in new[]{"Tranche Oak","Tranche Birch"})
        {
            object entry=New("ScatterBlockEntry");Set(entry,"BlockDefinitionName",name);
            if(name.EndsWith("Birch")) Set(entry,"IsEnabled",false);
            ((IList)Get(scatter,"Blocks")).Add(entry);
        }
        object annotation=New("ReportTableAnnotationDefinition");
        foreach(string property in new[]{"IncludeOverview","IncludeZones","IncludeEarthworks","IncludePonding","IncludeCatchments","IncludeGradientCompliance","IncludeLandforms"}) Set(annotation,property,false);
        ((IList)Get(terrain,"Annotations")).Add(annotation);
        object mode=Enum.Parse(plugin.GetType("MoleHill.Rhino.Services.TerrainBuildMode")!,"Final");
        object Display()=>Get(Call(controller,"GetRuntimeCache",doc.RuntimeSerialNumber,terrainId)!,"DisplayState");
        void Build()=>Require((bool)Call(controller,"BuildTerrainSynchronously",doc,Call(controller,"GetState",doc),terrain,mode,null)!,"Build failed");
        Build();
        object summary=((IList)Get(Display(),"ScatterSummaries"))[0]!;
        int count=(int)Get(summary,"Placed"); Require(count>0 && count<2000,"Filters failed");
        Require((int)Get(summary,"Feathered")>0 && (int)Get(summary,"Setback")>0 && (int)Get(summary,"Excluded")>0,"Missing rejection counts");
        int removed=new[]{"OutsideRegion","Excluded","Setback","Feathered","TerrainMisses","SlopeRejected","ElevationRejected","FootprintRejected"}.Sum(key=>(int)Get(summary,key));
        Require(count+removed==(int)Get(summary,"Sampled"),"Counts do not reconcile");
        object[] placements=((IList)Get(Display(),"ScatterObjects")).Cast<object>().ToArray();
        foreach(object output in placements)
        {
            Require((string)Get(output,"InstanceDefinitionName")=="Tranche Oak","Disabled block was placed");
            Transform pose=(Transform)Get(output,"InstanceTransform");double x=pose.M03,y=pose.M13;
            Require(Math.Abs(x)<=9.0001 && Math.Abs(y)<=9.0001,"Boundary setback");
            Require(Math.Sqrt(Math.Pow(Math.Max(0, Math.Abs(x)-2),2)+Math.Pow(Math.Max(0, Math.Abs(y)-2),2))>=0.9999,"Higher-priority island setback");
            Require(x<=3.5001 || x>=6.4999,"Exclusion clearance");
        }
        Transform[] poses=placements.Select(item=>(Transform)Get(item,"InstanceTransform")).ToArray();
        Build();
        Require(poses.SequenceEqual(((IList)Get(Display(),"ScatterObjects")).Cast<object>().Select(item=>(Transform)Get(item,"InstanceTransform"))),"Cached scatter changed poses");
        Require((int)Get(((IList)Get(Display(),"ScatterSummaries"))[0]!,"Placed")==count,"Cached summary lost");
        Require(((IList)Get(Display(),"AuxiliaryObjects")).Count>0,"Planting report did not draw");
        object snapshot=plugin.GetType("MoleHill.Rhino.Services.TerrainBuildSnapshotBuilder")!.GetMethod("Create",Flags)!.Invoke(null,new object?[]{doc,terrain,null})!;
        object sections=Enum.Parse(plugin.GetType("MoleHill.Rhino.Services.TerrainReportSections")!,"Planting");
        var report=(ReportDocument)plugin.GetType("MoleHill.Rhino.Services.TerrainReportBuilder")!.GetMethod("Build",Flags)!.Invoke(null,new[]{terrain,Get(Display(),"ZoneAnalysisResults"),Get(Display(),"AnalysisResults"),Get(snapshot,"ResolvedUnitContext"),Get(annotation,"Unit"),DateTime.Now,sections,Get(Display(),"ScatterSummaries")})!;
        Require(report.Tables.Count==1 && report.Tables[0].Rows.Last()[1]==count.ToString(),"Report counts differ");
        File.WriteAllText(csvPath,CsvWriter.Write(report));
        Set(terrain,"ShowTerrainMesh",true);
        Call(controller,"BakeTerrain",doc,terrainId);
        Guid[] full=((IList)Get(terrain,"BakedObjectIds")).Cast<Guid>().ToArray();
        Guid[] other=full.Where(id=>doc.Objects.FindId(id) is not InstanceObject).ToArray();
        Require(other.Length>0,"Full bake fixture missing other outputs");
        Call(controller,"BakeScatter",doc,terrainId,scatterId);
        Require(other.All(id=>doc.Objects.FindId(id)!=null),"Card bake replaced other output");
        var tracked=((IList)Get(terrain,"BakedObjectIds")).Cast<Guid>().Select(id=>doc.Objects.FindId(id)).OfType<InstanceObject>().ToArray();
        Require(tracked.Length==count,"Card bake appended duplicates");
        Require(full.Where(id=>!other.Contains(id)).All(id=>doc.Objects.FindId(id)==null),"Old card bake survived replacement");
        Console.WriteLine($"PASS scatter tranche: zone priority/hole; exclusion/clearance; setback/feather; disabled blocks; reconciled and cached counts; drawn/CSV report; card bake preserves other outputs and replaces {count} instances. RHP {plugin.ManifestModule.ModuleVersionId}");
        object island=((IList)Get(terrain,"Zones"))[1]!;
        ((IList)Get(scatter,"ZoneIds")).Add(Get(island,"ZoneId"));
        Build();
        Require(((IList)Get(Display(),"ScatterObjects")).Cast<object>().Any(item=>
        {
            Transform pose=(Transform)Get(item,"InstanceTransform");
            return Math.Abs(pose.M03)<1 && Math.Abs(pose.M13)<1;
        }),"Shared zone edge incorrectly created a setback");
        object duplicate=Call(controller,"DuplicateTerrain",doc,terrainId)!;
        object duplicateScatter=((IList)Get(duplicate,"Objects"))[0]!;
        var newZoneIds=((IList)Get(duplicate,"Zones")).Cast<object>().Select(zone=>(Guid)Get(zone,"ZoneId")).ToHashSet();
        Require(((IList)Get(duplicateScatter,"ZoneIds")).Cast<Guid>().All(newZoneIds.Contains),"Duplicate scatter retained original zone IDs");
        Call(controller,"DeleteTerrain",doc,(Guid)Get(duplicate,"TerrainId"));
        Console.WriteLine("PASS selected-zone union removes internal setbacks; duplicate remaps zone references.");
        // Retain this disposable fixture for panel inspection. The owning router closes it unsaved.
    }

    private static object Get(object value,string name)=>value.GetType().GetProperty(name,Flags)!.GetValue(value)!;
    private static void Set(object value,string name,object data)=>value.GetType().GetProperty(name,Flags)!.SetValue(value,data);
    private static object? Call(object value,string name,params object?[] args)=>value.GetType().GetMethods(Flags).Single(method=>method.Name==name&&method.GetParameters().Length==args.Length).Invoke(value,args);
    private static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
}
