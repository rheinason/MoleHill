# TriangleNet And Triangle-Derived Code

`src/TriangleNet/` is excluded from the MoleHill GPL license.

This subtree contains a vendored copy of Triangle.NET, a C# port by Christian Woltering of Jonathan
Richard Shewchuk's Triangle. The files retain their upstream notices and terms.

Observed notices in the vendored sources include:

- Jonathan Richard Shewchuk on many Triangle-derived files
- Christian Woltering on Triangle.NET files
- Frank Dockhorn on `src/TriangleNet/Tools/TriangleQuadTree.cs`

## Terms

Triangle's distribution terms are quoted verbatim in section 2 of the repository `LICENSE`. In short:
free redistribution with the copyright notices kept and no compensation received; modified versions
only if the code stays under the original author's copyright, source and object code are freely
available without charge, and the modifications are clearly noted; distribution as part of a
commercial system only by direct arrangement with the author. Triangle's project page adds that it
"may not be sold or included in commercial products without a license."

Triangle.NET was published under the MIT license, but its author notes that Triangle's license
"isn't very clear about how a derived work like Triangle.NET should be handled", recommends not using
the files carrying a Shewchuk copyright header in a commercial context, and for that reason publishes
no NuGet package.

Important points:

- `src/TriangleNet/` is not relicensed by MoleHill.
- The combined MoleHill binaries include code from this subtree, and MoleHill is distributed free of
  charge as source and binaries.
- Selling MoleHill, or including it in a commercial product, needs a license from Jonathan Richard
  Shewchuk for the Triangle-derived parts.
- The repository `LICENSE` grants a GPL section 7 additional permission so MoleHill's GPL code may be
  combined with this subtree. It does not relax Triangle's terms.

## Modifications made for MoleHill

As Triangle's terms require, the changes MoleHill has made to the vendored sources are listed here, and
each changed file carries a "Modified for MoleHill" note under its copyright header.

| File | Change |
|---|---|
| `TriangleNet.csproj` | Retargeted to `net7.0` and built as part of the MoleHill solution. |
| `Configuration.cs` | A fixed random seed, so the same input always produces the same triangulation. |
| `Mesh.cs` | Incremental editing: `TryInsertPoint`, `CanDeletePoint` and `TryDeletePoint`, used by `TinEngine` for single-point edits. |
| `Meshing/GenericMesher.cs` | Skips the quality-refinement pass when no quality or conforming option is requested. |
| `Meshing/QualityMesher.cs` | Creates its large refinement workspace lazily, only when refinement or an incremental edit needs it. |

The full history of each change is in the repository's git log for `src/TriangleNet/`.

## Upstream references

- Triangle.NET repository: https://github.com/wo80/Triangle.NET
- Triangle project page: https://www.cs.cmu.edu/~quake/triangle.html
