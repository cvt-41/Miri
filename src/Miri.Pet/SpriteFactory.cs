using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Miri.Pet;

/// <summary>
/// Builds Miri's sprite sheet procedurally, exports it as <c>miri.png</c> + <c>miri.json</c>
/// (one row per animation, frames left to right) and loads a user-edited sheet if one exists.
/// </summary>
public static class SpriteFactory
{
    public const string PngName = "miri.png";
    public const string JsonName = "miri.json";

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    /// <summary>Generates the procedural sheet (no files involved).</summary>
    public static SpriteSheet Generate()
    {
        var dict = new Dictionary<PetAnimation, SpriteAnimation>();
        foreach (var def in PixelArt.BuildAll())
        {
            var anim = Enum.Parse<PetAnimation>(def.Name);
            var frames = def.Frames.Select(p => new SpriteFrame(p, PixelArt.Size, PixelArt.Size)).ToList();
            dict[anim] = new SpriteAnimation(def.Name, frames, def.DurationsMs);
        }
        return new SpriteSheet(PixelArt.Size, PixelArt.Size, dict, "procedural");
    }

    /// <summary>
    /// Loads <c>miri.png</c>+<c>miri.json</c> from next to the exe (or exeDir\assets\sprites) when present,
    /// otherwise generates the sheet and exports it to <paramref name="exportDir"/> if the files are missing there.
    /// Never throws: falls back to the procedural sheet.
    /// </summary>
    public static SpriteSheet LoadOrCreate(string exeDir, string? exportDir)
    {
        foreach (var dir in new[] { exeDir, Path.Combine(exeDir, "assets", "sprites") })
        {
            var png = Path.Combine(dir, PngName);
            var json = Path.Combine(dir, JsonName);
            if (File.Exists(png) && File.Exists(json))
            {
                try { return Load(png, json); }
                catch { /* broken user sheet: fall through to procedural */ }
            }
        }

        var sheet = Generate();
        if (exportDir != null)
        {
            try
            {
                if (!File.Exists(Path.Combine(exportDir, PngName)) || !File.Exists(Path.Combine(exportDir, JsonName)))
                    Export(sheet, exportDir);
            }
            catch { /* read-only location etc. – not fatal */ }
        }
        return sheet;
    }

    /// <summary>Finds the repo's assets/sprites folder by walking up from <paramref name="startDir"/> to Miri.sln.</summary>
    public static string? FindRepoSpriteDir(string startDir)
    {
        var d = new DirectoryInfo(startDir);
        while (d != null)
        {
            if (File.Exists(Path.Combine(d.FullName, "Miri.sln")))
                return Path.Combine(d.FullName, "assets", "sprites");
            d = d.Parent;
        }
        return null;
    }

    public static void Export(SpriteSheet sheet, string dir)
    {
        Directory.CreateDirectory(dir);
        int fw = sheet.FrameWidth, fh = sheet.FrameHeight;
        var order = Enum.GetValues<PetAnimation>().Where(sheet.Animations.ContainsKey).ToList();
        int cols = order.Max(a => sheet[a].Frames.Count);
        int w = cols * fw, h = order.Count * fh;
        var pixels = new uint[w * h];
        var meta = new SheetJson { FrameWidth = fw, FrameHeight = fh };

        for (int row = 0; row < order.Count; row++)
        {
            var anim = sheet[order[row]];
            for (int f = 0; f < anim.Frames.Count; f++)
            {
                var src = anim.Frames[f].Pixels;
                for (int y = 0; y < fh; y++)
                    Array.Copy(src, y * fw, pixels, (row * fh + y) * w + f * fw, fw);
            }
            meta.Animations.Add(new AnimJson
            {
                Name = anim.Name,
                Row = row,
                Frames = anim.Frames.Count,
                Fps = Math.Round(anim.Fps, 2),
                DurationsMs = anim.DurationsMs.ToArray(),
            });
        }

        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using (var fs = File.Create(Path.Combine(dir, PngName)))
            enc.Save(fs);
        File.WriteAllText(Path.Combine(dir, JsonName), JsonSerializer.Serialize(meta, JsonOpts));
    }

    public static SpriteSheet Load(string pngPath, string jsonPath)
    {
        var meta = JsonSerializer.Deserialize<SheetJson>(File.ReadAllText(jsonPath), JsonOpts)
                   ?? throw new InvalidDataException("empty sprite json");

        BitmapSource src;
        using (var fs = File.OpenRead(pngPath))
        {
            var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            src = dec.Frames[0];
        }
        if (src.Format != PixelFormats.Bgra32)
            src = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int w = src.PixelWidth, h = src.PixelHeight;
        var all = new uint[w * h];
        src.CopyPixels(all, w * 4, 0);

        int fw = meta.FrameWidth, fh = meta.FrameHeight;
        var dict = new Dictionary<PetAnimation, SpriteAnimation>();
        foreach (var a in meta.Animations)
        {
            if (!Enum.TryParse<PetAnimation>(a.Name, true, out var kind)) continue;
            var frames = new List<SpriteFrame>();
            for (int f = 0; f < a.Frames; f++)
            {
                var p = new uint[fw * fh];
                for (int y = 0; y < fh; y++)
                {
                    int sy = a.Row * fh + y;
                    if (sy >= h || (f + 1) * fw > w) throw new InvalidDataException("frame outside png");
                    Array.Copy(all, sy * w + f * fw, p, y * fw, fw);
                }
                frames.Add(new SpriteFrame(p, fw, fh));
            }
            int[] durations = a.DurationsMs is { Length: > 0 } d && d.Length == a.Frames
                ? d
                : Enumerable.Repeat((int)Math.Round(1000.0 / Math.Max(0.1, a.Fps)), a.Frames).ToArray();
            dict[kind] = new SpriteAnimation(kind.ToString(), frames, durations);
        }

        // Anything the user's sheet lacks comes from the procedural set.
        var fallback = Generate();
        foreach (var kind in Enum.GetValues<PetAnimation>())
            if (!dict.ContainsKey(kind)) dict[kind] = fallback[kind];

        return new SpriteSheet(fw, fh, dict, pngPath);
    }

    private sealed class SheetJson
    {
        public int FrameWidth { get; set; } = 32;
        public int FrameHeight { get; set; } = 32;
        public string Layout { get; set; } = "one row per animation, frames left to right; Walk faces right (mirrored at runtime for left)";
        public List<AnimJson> Animations { get; set; } = new();
    }

    private sealed class AnimJson
    {
        public string Name { get; set; } = "";
        public int Row { get; set; }
        public int Frames { get; set; }
        public double Fps { get; set; }
        public int[]? DurationsMs { get; set; }
    }
}
