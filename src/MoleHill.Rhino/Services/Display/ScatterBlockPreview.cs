using System.Drawing;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.DocObjects.Tables;
using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

/// <summary>
/// Display-ready copy of a block definition for the scatter "Real (capped)" preview: its surfaces meshed
/// once and joined into one mesh per colour, plus its curves, hatches and text, with nested blocks
/// flattened into definition space. The conduit draws every scatter instance from this under a model
/// transform.
///
/// It exists because <c>DisplayPipeline.DrawInstanceDefinition</c> cannot carry a scatter: measured live
/// on 2,000 instances of a two-Brep block it took ~2.1 s per redraw and grew the process by ~37 MB per
/// frame until the GPU driver killed Rhino. Drawing cached joined meshes under
/// <c>PushModelTransform</c> took ~0.18 s for the same scene with no growth.
///
/// Cached per document and definition id; any instance-definition or layer table change drops that
/// document's entries, so an edited block, or a member coloured by layer, is rebuilt on the next draw.
/// </summary>
internal sealed class ScatterBlockPreview
{
    private const int MaxNestingDepth = 8;

    private readonly record struct CacheKey(uint DocumentSerial, Guid DefinitionId);

    private static readonly object CacheGate = new();
    private static readonly Dictionary<CacheKey, ScatterBlockPreview?> Cache = new();

    static ScatterBlockPreview()
    {
        RhinoDoc.InstanceDefinitionTableEvent += OnInstanceDefinitionTableEvent;
        RhinoDoc.LayerTableEvent += OnLayerTableEvent;
        RhinoDoc.CloseDocument += OnCloseDocument;
    }

    private ScatterBlockPreview()
    {
    }

    /// <summary>
    /// Joined meshes, one per member material. Surfaces shade by material, not display colour, as Rhino's
    /// own Shaded and Rendered modes do: shading by colour drew every By Layer member on the black Default
    /// layer solid black, while the baked block showed white.
    /// </summary>
    public List<(Mesh Mesh, DisplayMaterial Material)> Meshes { get; } = new();

    /// <summary>Wires, hatches and text draw in the member's display colour; null means "by parent".</summary>
    public List<(Curve Curve, Color? Color)> Curves { get; } = new();

    public List<(Hatch Hatch, Color? Color)> Hatches { get; } = new();

    public List<(TextEntity Text, Color? Color)> Texts { get; } = new();

    private bool IsEmpty => Meshes.Count == 0 && Curves.Count == 0 && Hatches.Count == 0 && Texts.Count == 0;

    public static ScatterBlockPreview? Get(RhinoDoc doc, InstanceDefinition definition)
    {
        var key = new CacheKey(doc.RuntimeSerialNumber, definition.Id);
        lock (CacheGate)
        {
            if (Cache.TryGetValue(key, out ScatterBlockPreview? cached))
                return cached;
        }

        ScatterBlockPreview? built = Build(doc, definition);
        lock (CacheGate)
        {
            Cache[key] = built;
        }

        return built;
    }

    private static ScatterBlockPreview? Build(RhinoDoc doc, InstanceDefinition definition)
    {
        var preview = new ScatterBlockPreview();
        var meshesByMaterial = new Dictionary<MaterialKey, (Mesh Mesh, DisplayMaterial Material)>();
        MeshingParameters meshingParameters = doc.GetMeshingParameters(doc.MeshingParameterStyle);

        void AddMesh(Mesh mesh, RhinoObject member)
        {
            if (mesh.Faces.Count == 0)
                return;

            // Resolves object, layer and parent material sources; a member with none gets the default.
            Material material = member.GetMaterial(true) ?? new Material();
            var key = MaterialKey.From(material);
            if (!meshesByMaterial.TryGetValue(key, out var group))
            {
                group = (new Mesh(), new DisplayMaterial(material));
                meshesByMaterial[key] = group;
            }

            group.Mesh.Append(mesh);
        }

        void Collect(InstanceDefinition current, Transform transform, int depth)
        {
            foreach (RhinoObject member in current.GetObjects())
            {
                if (member?.Geometry == null)
                    continue;

                Color? color = member.Attributes.ColorSource == ObjectColorSource.ColorFromParent
                    ? null
                    : member.Attributes.DrawColor(doc);

                if (member is InstanceObject nested)
                {
                    if (depth < MaxNestingDepth && nested.InstanceDefinition != null)
                        Collect(nested.InstanceDefinition, transform * nested.InstanceXform, depth + 1);
                    continue;
                }

                switch (member.Geometry)
                {
                    case Curve curve:
                    {
                        Curve copy = curve.DuplicateCurve();
                        if (copy.Transform(transform))
                            preview.Curves.Add((copy, color));
                        break;
                    }
                    case Hatch hatch:
                    {
                        var copy = (Hatch)hatch.Duplicate();
                        if (copy.Transform(transform))
                            preview.Hatches.Add((copy, color));
                        break;
                    }
                    case TextEntity text:
                    {
                        var copy = (TextEntity)text.Duplicate();
                        if (copy.Transform(transform))
                            preview.Texts.Add((copy, color));
                        break;
                    }
                    default:
                        foreach (Mesh mesh in MeshMember(member, meshingParameters))
                        {
                            Mesh copy = mesh.DuplicateMesh();
                            if (copy.Transform(transform))
                                AddMesh(copy, member);
                        }

                        break;
                }
            }
        }

        Collect(definition, Transform.Identity, 0);

        foreach (var (mesh, material) in meshesByMaterial.Values)
            preview.Meshes.Add((PrepareForDisplay(mesh), material));

        return preview.IsEmpty ? null : preview;
    }

    /// <summary>
    /// A block member's shading mesh. Members of a definition are not in the viewport, so they usually
    /// carry no cached render mesh; mesh the geometry directly when they do not.
    /// </summary>
    internal static IEnumerable<Mesh> MeshMember(RhinoObject member, MeshingParameters meshingParameters)
    {
        Mesh[] cached = member.GetMeshes(MeshType.Render);
        if (cached.Length > 0)
            return cached;

        return member.Geometry switch
        {
            Mesh mesh => new[] { mesh },
            Brep brep => Mesh.CreateFromBrep(brep, meshingParameters) ?? Array.Empty<Mesh>(),
            Extrusion extrusion => extrusion.ToBrep() is Brep brep
                ? Mesh.CreateFromBrep(brep, meshingParameters) ?? Array.Empty<Mesh>()
                : Array.Empty<Mesh>(),
            Surface surface => Mesh.CreateFromSurface(surface, meshingParameters) is Mesh mesh
                ? new[] { mesh }
                : Array.Empty<Mesh>(),
            SubD subd => Mesh.CreateFromSubD(subd, 2) is Mesh mesh
                ? new[] { mesh }
                : Array.Empty<Mesh>(),
            _ => Array.Empty<Mesh>()
        };
    }

    private readonly record struct MaterialKey(int Diffuse, int Specular, int Emission, double Transparency, double Shine)
    {
        public static MaterialKey From(Material material) => new(
            material.DiffuseColor.ToArgb(),
            material.SpecularColor.ToArgb(),
            material.EmissionColor.ToArgb(),
            material.Transparency,
            material.Shine);
    }

    private static Mesh PrepareForDisplay(Mesh mesh)
    {
        if (mesh.Normals.Count != mesh.Vertices.Count)
            mesh.Normals.ComputeNormals();
        return mesh;
    }

    private static void OnInstanceDefinitionTableEvent(object? sender, InstanceDefinitionTableEventArgs e) =>
        InvalidateDocument(e.Document.RuntimeSerialNumber);

    private static void OnLayerTableEvent(object? sender, LayerTableEventArgs e) =>
        InvalidateDocument(e.Document.RuntimeSerialNumber);

    private static void OnCloseDocument(object? sender, DocumentEventArgs e) =>
        InvalidateDocument(e.Document.RuntimeSerialNumber);

    private static void InvalidateDocument(uint documentSerial)
    {
        lock (CacheGate)
        {
            CacheKey[] keys = Cache.Keys
                .Where(key => key.DocumentSerial == documentSerial)
                .ToArray();
            foreach (CacheKey key in keys)
                Cache.Remove(key);
        }
    }
}
