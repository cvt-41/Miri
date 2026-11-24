using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Miri.Pet;

/// <summary>Shows the controller's current frame, crisp (nearest neighbour) at the controller's size.</summary>
public sealed class PetView : Image
{
    public PetView(PetController controller)
    {
        Controller = controller;
        Stretch = Stretch.Fill;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
        Source = controller.CurrentFrame.Bitmap;
        ApplySize();
        controller.FrameChanged += () => Source = controller.CurrentFrame.Bitmap;
        controller.SizeChanged += ApplySize;
    }

    public PetController Controller { get; }

    private void ApplySize()
    {
        Width = Controller.Size;
        Height = Controller.Size;
    }

    protected override HitTestResult? HitTestCore(PointHitTestParameters p) =>
        Controller.HitTest(p.HitPoint) ? new PointHitTestResult(this, p.HitPoint) : null;
}
