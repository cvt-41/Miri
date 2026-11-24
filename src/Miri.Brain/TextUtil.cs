using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Miri.Brain;

internal enum Lang { English, Vietnamese }

internal static class TextUtil
{
    private static readonly Regex TokenSplit = new(@"[^\p{L}\p{Nd}]+", RegexOptions.Compiled);

    // Characters that only occur in Vietnamese (among VI/EN).
    private const string VietnameseChars =
        "ăâđêôơưàảãáạằẳẵắặầẩẫấậèẻẽéẹềểễếệìỉĩíịòỏõóọồổỗốộờởỡớợùủũúụừửữứựỳỷỹýỵ";

    // Common unaccented Vietnamese words (for users typing without diacritics).
    private static readonly HashSet<string> UnaccentedVi = new(StringComparer.Ordinal)
    {
        "khong", "toi", "minh", "ban", "duoc", "nhu", "nao", "lam", "cach", "nhung", "voi", "nhe",
        "giup", "hay", "cua", "trong", "mot", "nay", "kia", "vay", "sao", "gi", "nen", "phai", "meo",
        "ca", "phe", "sua", "uong", "con", "oi", "chao", "cam", "on", "bao", "nhieu", "ngay", "dang",
        "muon", "hoi", "biet", "tai", "thi", "cho", "va", "la", "em", "anh", "chi", "tro", "man", "hinh",
    };

    /// <summary>Lower-case, remove diacritics (incl. đ→d).</summary>
    public static string StripDiacritics(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var normalized = text.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(ch == 'đ' ? 'd' : ch);
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>Diacritic-free lowercase tokens.</summary>
    public static List<string> Tokenize(string text) =>
        TokenSplit.Split(StripDiacritics(text)).Where(t => t.Length > 0).ToList();

    public static Lang DetectLanguage(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Lang.English;
        var lower = text.ToLowerInvariant().Normalize(NormalizationForm.FormC);
        foreach (var ch in lower)
            if (VietnameseChars.IndexOf(ch) >= 0) return Lang.Vietnamese;

        var tokens = Tokenize(text);
        if (tokens.Count == 0) return Lang.English;
        int hits = tokens.Count(t => UnaccentedVi.Contains(t));
        // "a", "la", "on", "con"... overlap with English, so require a clear share.
        return hits >= 2 && hits * 3 >= tokens.Count ? Lang.Vietnamese : Lang.English;
    }

    public static string L(Lang lang, string en, string vi) => lang == Lang.Vietnamese ? vi : en;

    public static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "…";
}

/// <summary>
/// Fixes typos in the words that decide what Miri does: the command verb, the app after "on/in", and weather words.
/// Song titles, names and free text are left alone (YouTube and the apps' own search handle those).
/// </summary>
internal static class TypoFixer
{
    private static readonly string[] Verbs = { "play", "open", "pause", "resume", "next", "skip", "previous", "close", "launch", "search", "start", "stop", "louder", "quieter", "mute", "unmute" };
    private static readonly string[] Apps = { "youtube", "spotify", "sonora", "chrome", "edge", "brave", "notepad", "telegram", "zalo", "discord" };
    private static readonly string[] WeatherWords = { "weather", "forecast", "temperature", "umbrella", "raining" };
    private static readonly Dictionary<string, string> Short = new(StringComparer.OrdinalIgnoreCase) { ["ply"] = "play", ["opn"] = "open", ["pley"] = "play", ["paly"] = "play"};   // common slips that happen to be dictionary words

    /// <summary>Returns the corrected text, or the same instance when nothing changed.</summary>
    public static string Fix(string text)
    {
        var words = text.Split(' ');
        bool changed = false;
        for (int i = 0; i < words.Length; i++)
        {
            string w = words[i], core = w.TrimEnd('.', '!', '?', ',');
            string? fix = i == 0 ? Closest(core, Verbs) ?? Closest(core, WeatherWords)
                : i > 0 && Regex.IsMatch(words[i - 1], "^(on|in|trên|bằng)$", RegexOptions.IgnoreCase) ? Closest(core, Apps)
                : Closest(core, WeatherWords);
            if (fix == null) continue;
            words[i] = fix + w[core.Length..];
            changed = true;
        }
        return changed ? string.Join(' ', words) : text;
    }

    private static string? Closest(string word, string[] vocab)
    {
        var lower = word.ToLowerInvariant();
        if (vocab.Contains(lower)) return null;                                   // already right
        if (Short.TryGetValue(lower, out var s) && vocab.Contains(s)) return s;
        if (lower.Length < 4 || !lower.All(char.IsLetter)) return null;          // "pay", "the": too short to guess
        if (!(vocab == Apps || SpellCheck.IsMisspelled(lower))) return null;   // real words ("waiting", "writing") are never typos; brand names aren't in the dictionary
        var hit = vocab.Where(v => Math.Abs(v.Length - lower.Length) <= 1 && Distance(lower, v) <= 1).ToList();   // 2 typos turns real words into commands ("waiting" -> "raining")
        return hit.Count == 1 ? hit[0] : null;                                    // ambiguous: leave it
    }

    /// <summary>Windows' own spell checker (Windows 8+), English. Null result = unknown, treated as "not a typo".</summary>
    private static class SpellCheck
    {
        private static readonly Lazy<ISpellChecker?> Checker = new(() =>
        {
            try
            {
                var f = (ISpellCheckerFactory)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("7AB36653-1796-484B-BDFA-E74F1DB7C1DC"))!)!;
                return f.CreateSpellChecker("en-US");
            }
            catch (Exception) { return null; }   // no spell checker: only obvious fixes (short list, app names) happen
        });

        public static bool IsMisspelled(string word)
        {
            try
            {
                if (Checker.Value is not { } c) return false;
                return c.Check(word).Next(out _) == 0;   // S_OK: an error was found
            }
            catch (Exception) { return false; }
        }
    }

    [ComImport, Guid("8E018A9D-2415-4677-BF08-794EA61F94BB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISpellCheckerFactory
    {
        object SupportedLanguages { get; }
        int IsSupported([MarshalAs(UnmanagedType.LPWStr)] string languageTag);
        ISpellChecker CreateSpellChecker([MarshalAs(UnmanagedType.LPWStr)] string languageTag);
    }

    [ComImport, Guid("B6FD0B71-E2BC-4653-8D05-F197E412770B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISpellChecker
    {
        [return: MarshalAs(UnmanagedType.LPWStr)] string get_LanguageTag();
        IEnumSpellingError Check([MarshalAs(UnmanagedType.LPWStr)] string text);
        // later vtable slots unused
    }

    [ComImport, Guid("803E3BD4-2828-4410-8290-418D1D73C762"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumSpellingError
    {
        [PreserveSig] int Next([MarshalAs(UnmanagedType.IUnknown)] out object? error);
    }

    /// <summary>Edit distance where swapping two neighbours ("paly") counts as one typo.</summary>
    private static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1]) d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
            }
        return d[a.Length, b.Length];
    }
}
