using System.Drawing;
using Rhino.Display;
using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

internal sealed class ObjectPlacementBasePreview : DisplayConduit, IDisposable
{
    private readonly uint _documentSerial;
    private readonly IReadOnlyList<ObjectPlacementReference> _references;

    internal ObjectPlacementBasePreview(uint documentSerial, IReadOnlyList<ObjectPlacementReference> references)
    {
        _documentSerial = documentSerial;
        _references = references;
        Enabled = true;
    }

    protected override void DrawOverlay(DrawEventArgs e)
    {
        if (e.RhinoDoc?.RuntimeSerialNumber != _documentSerial) return;
        e.Display.PushDepthTesting(false);
        e.Display.PushDepthWriting(false);
        try
        {
            foreach (var reference in _references)
            {
                if (!reference.TryGetPlane(out var plane)) continue;
                var corners = reference.BaseCorners;
                e.Display.DrawPolyline(corners.Concat(new[] { corners[0] }).ToArray(), Color.LimeGreen, 3);
                double size = Math.Max((corners[1] - corners[0]).Length, (corners[3] - corners[0]).Length) * 0.25;
                e.Display.DrawArrow(new Line(plane.Origin, plane.Origin + plane.Normal * size), Color.LimeGreen);
                e.Display.DrawArrow(new Line(plane.Origin, plane.Origin + plane.XAxis * size), Color.Coral);
            }
        }
        finally { e.Display.PopDepthWriting(); e.Display.PopDepthTesting(); }
    }

    public void Dispose() => Enabled = false;
}
