using Xunit;

namespace MoleHill.Core.Tests;

/// <summary>
/// Output meshes are oriented in one place, <c>MoleHill.Shared.MeshNormalOrientation</c>, for both hosts.
/// Hand-written copies had drifted three ways: some skipped the winding check that saves ~40 ms per
/// 111k-face mesh, some computed vertex normals before unifying and so could keep normals from the
/// pre-flip winding, and one unified before computing. A call site cannot show which it is, so this
/// scans the shipped sources.
/// </summary>
public class MeshNormalOrientationGuardTests
{
    private const string Owner = "src/MoleHill.Shared/MeshNormalOrientation.cs";

    [Fact]
    public void UnifyNormals_IsOnlyCalledByMeshNormalOrientation()
    {
        string root = RepositoryPaths.FindRoot();
        var offenders = new List<string>();

        foreach (string file in RepositoryPaths.EnumerateShippedSources(root))
        {
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative == Owner)
                continue;

            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains(".UnifyNormals(", StringComparison.Ordinal))
                    offenders.Add($"{relative}:{i + 1}");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Orient meshes with MeshNormalOrientation.UnifyAndComputeNormals, not UnifyNormals directly:\n" +
            string.Join("\n", offenders));
    }
}
