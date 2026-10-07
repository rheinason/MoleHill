using MoleHill.Rhino.Model;
using MoleHill.Rhino.Registry;
using MoleHill.Shared;
using RhinoMesh = Rhino.Geometry.Mesh;

namespace MoleHill.Rhino.Services;

/// <summary>
/// Per-modifier-type build-stage bodies, dispatched from the registry instead of a central switch.
/// Each <c>RunXStage</c> mirrors exactly one former <c>case</c> in <c>Build</c>'s modifier loop:
/// it reads the incoming mesh/fingerprint from the <see cref="ModifierBuildContext"/> and writes back the
/// outgoing mesh/fingerprint (and, for triangulate, the captured base mesh). The heavy lifting still
/// lives in the private <c>ApplyX</c>/<c>BuildX</c> helpers; these are thin shims so a modifier
/// descriptor can own its build step (Blender-style "register a type, it builds itself").
/// </summary>
internal sealed partial class TerrainBuildService
{}
