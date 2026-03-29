# TriangleNet And Triangle-Derived Code

`src/TriangleNet/` is excluded from the MoleHill GPL license.

This subtree contains a vendored copy of Triangle.NET code and files derived
from Jonathan Richard Shewchuk's Triangle implementation. The files in that
directory retain their upstream notices and terms.

Observed notices in the vendored sources include:

- Jonathan Richard Shewchuk on many Triangle-derived files
- Christian Woltering on Triangle.NET files
- Frank Dockhorn on `src/TriangleNet/Tools/TriangleQuadTree.cs`

Important points:

- `src/TriangleNet/` is not relicensed by MoleHill.
- The combined MoleHill binaries include code from this subtree.
- Any use or redistribution of combined builds must account for these upstream
  terms in addition to the MoleHill license.

Upstream references:

- Triangle.NET repository: https://github.com/wo80/Triangle.NET
- Triangle project page: https://www.cs.cmu.edu/~quake/triangle.html

Local examples of retained notices:

- `src/TriangleNet/Behavior.cs`
- `src/TriangleNet/Mesh.cs`
- `src/TriangleNet/Tools/TriangleQuadTree.cs`
