using System.Globalization;
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
