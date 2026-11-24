using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Miri.Pet;

public enum PetAnimation { Sit, Walk, Sleep, Think, Talk, Happy, Confused, Point, Crouch, Jump, Fall, ReachUp, ReachDown, Purr, Eat, Smug }

/// <summary>One frame: a frozen 32x32 bitmap plus its raw BGRA pixels (for hit testing).</summary>
public sealed class SpriteFrame
{
    public SpriteFrame(uint[] pixels, int width, int height)
    {
        Pixels = pixels;
        Width = width;
        Height = height;
        var bmp = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bmp.Freeze();
        Bitmap = bmp;
    }

    public uint[] Pixels { get; }
    public int Width { get; }
    public int Height { get; }
    public BitmapSource Bitmap { get; }

    public bool IsOpaque(int x, int y) =>
        x >= 0 && y >= 0 && x < Width && y < Height && (Pixels[y * Width + x] >> 24) != 0;

    public SpriteFrame Mirrored()
    {
        var p = new uint[Pixels.Length];
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
                p[y * Width + x] = Pixels[y * Width + (Width - 1 - x)];
        return new SpriteFrame(p, Width, Height);
    }
}

public sealed class SpriteAnimation
{
    public SpriteAnimation(string name, IReadOnlyList<SpriteFrame> frames, IReadOnlyList<int> durationsMs)
    {
        Name = name;
        Frames = frames;
        DurationsMs = durationsMs;
        MirroredFrames = frames.Select(f => f.Mirrored()).ToList();
    }

    public string Name { get; }
    public IReadOnlyList<SpriteFrame> Frames { get; }
    /// <summary>Same frames flipped horizontally (walking left, pointing left).</summary>
    public IReadOnlyList<SpriteFrame> MirroredFrames { get; }
    public IReadOnlyList<int> DurationsMs { get; }
    public double Fps => 1000.0 * Frames.Count / Math.Max(1, DurationsMs.Sum());
}

public sealed class SpriteSheet
{
    public SpriteSheet(int frameWidth, int frameHeight, IReadOnlyDictionary<PetAnimation, SpriteAnimation> animations, string source)
    {
        FrameWidth = frameWidth;
        FrameHeight = frameHeight;
        Animations = animations;
        Source = source;
    }

    public int FrameWidth { get; }
    public int FrameHeight { get; }
    public IReadOnlyDictionary<PetAnimation, SpriteAnimation> Animations { get; }
    /// <summary>"procedural" or the path of the png that was loaded.</summary>
    public string Source { get; }

    public SpriteAnimation this[PetAnimation a] => Animations[a];
}
