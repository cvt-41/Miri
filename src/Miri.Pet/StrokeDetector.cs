namespace Miri.Pet;

/// <summary>
/// Recognises petting: the cursor stroking back and forth over the kitten (no button held).
/// Pure: feed it timestamped cursor positions (seconds, DIPs); <see cref="Add"/> returns true once per stroke burst.
/// </summary>
public sealed class StrokeDetector
{
    public const double WindowSeconds = 1.5;
    public const double MinTravel = 200;   // DIPs, together with at least one reversal
    public const int MinReversals = 3;
    private const double Hysteresis = 6;   // DIPs a turn must go back before it counts (ignores hand jitter)

    private readonly List<(double T, double X, double Y)> _pts = new();

    public bool Add(double t, double x, double y)
    {
        _pts.Add((t, x, y));
        _pts.RemoveAll(p => t - p.T > WindowSeconds);

        double travel = 0;
        for (int i = 1; i < _pts.Count; i++)
            travel += Math.Abs(_pts[i].X - _pts[i - 1].X) + Math.Abs(_pts[i].Y - _pts[i - 1].Y);
        int turns = Reversals(p => p.X) + Reversals(p => p.Y);

        bool stroked = turns >= MinReversals || (travel >= MinTravel && turns >= 1);
        if (stroked) _pts.Clear();   // next purr needs a fresh burst of strokes
        return stroked;
    }

    public void Reset() => _pts.Clear();

    private int Reversals(Func<(double T, double X, double Y), double> axis)
    {
        if (_pts.Count == 0) return 0;
        int dir = 0, count = 0;
        double anchor = axis(_pts[0]);
        foreach (var p in _pts)
        {
            double d = axis(p) - anchor;
            if (Math.Abs(d) < Hysteresis) continue;
            int s = Math.Sign(d);
            if (dir != 0 && s != dir) count++;
            dir = s;
            anchor = axis(p);
        }
        return count;
    }
}
