namespace Miri.Core;

/// <summary>
/// Something inside an app window the kitten can stand on, e.g. a YouTube video player:
/// its bottom edge becomes a ledge. Coordinates are physical screen pixels.
/// </summary>
public interface ISurface
{
    /// <summary>Top-level window that owns the surface.</summary>
    IntPtr Hwnd { get; }
    string Name { get; }
    /// <summary>Live bounds, or null when the surface is gone / off screen. Safe to call from a background thread.</summary>
    System.Drawing.Rectangle? CurrentBounds();
    /// <summary>True: she stands on the surface's top edge (a card, toolbar, image she landed on). False: its bottom edge.</summary>
    bool TopEdge => false;
}

/// <summary>Finds the surface under a point (implemented with UI Automation in Miri.Hands).</summary>
public interface ISurfaceFinder
{
    /// <param name="physical">Probe point in physical pixels (just below the kitten's paws).</param>
    /// <param name="minWidth">Smallest useful ledge width in physical pixels.</param>
    ISurface? FindAt(System.Drawing.Point physical, int minWidth);
}
