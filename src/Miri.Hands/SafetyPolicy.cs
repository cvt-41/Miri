using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Miri.Core;

namespace Miri.Hands;

/// <summary>
/// Decides how risky a proposed UI action is.
/// Blocked: never executed. Dangerous: needs an explicit per-action approval. Normal: everything else.
/// </summary>
public static class SafetyPolicy
{
    // Banking / payment keywords checked against the window title (and app targets).
    private static readonly string[] BankingKeywords =
        { "bank", "ngân hàng", "vietcombank", "techcombank", "momo", "paypal", "wallet" };

    // Dangerous verbs. Vietnamese entries are written with diacritics; their unaccented forms are derived.
    private static readonly string[] DangerousEnglish = { "send", "delete", "buy", "pay", "post", "submit", "confirm" };
    private static readonly string[] DangerousVietnamese = { "gửi", "xóa", "xoá", "mua", "thanh toán", "đăng", "xác nhận" };

    private static readonly string[] MessagingApps =
    {
        "telegram", "zalo", "messenger", "discord", "slack", "teams", "whatsapp", "signal", "skype",
        "viber", "line", "wechat", "facebook", "instagram", "outlook", "thunderbird", "mail",
    };

    private static readonly Regex AccentedPatterns = BuildRegex(
        DangerousVietnamese.Select(v => Regex.Escape(v.Normalize(NormalizationForm.FormC)))
    );

    private static readonly Regex AsciiPatterns = BuildRegex(
        DangerousEnglish.Select(e => Regex.Escape(e) + "(?:s|es|ed|ing|ment)?")
            .Concat(DangerousVietnamese.Select(v => Regex.Escape(StripDiacritics(v))))
            .Distinct()
    );

    private static Regex BuildRegex(IEnumerable<string> alternatives) =>
        new(@"(?<![\p{L}\p{N}])(?:" + string.Join("|", alternatives) + @")(?![\p{L}\p{N}])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static ActionRisk Assess(UiAction action, UiSnapshot snapshot, MiriSettings settings)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(snapshot);
        settings ??= new MiriSettings();

        var target = action.ElementId is int id ? snapshot.Elements.FirstOrDefault(e => e.Id == id) : null;

        // ---------- Blocked ----------
        if (target?.IsPassword == true)
            return new(RiskLevel.Blocked, "That's a password field. I never touch passwords.");

        var blockedApp = MatchBlockedApp(settings, snapshot.ProcessName, snapshot.WindowTitle);
        if (blockedApp != null && action.Kind != ActionKind.OpenApp)
            return new(RiskLevel.Blocked, $"{blockedApp} is on my blocked list.");

        var bankingWord = FindBankingKeyword(snapshot.WindowTitle);
        if (bankingWord != null && action.Kind != ActionKind.OpenApp)
            return new(RiskLevel.Blocked, "This looks like a banking or payment window, so I won't touch it.");

        if (action.Kind is ActionKind.OpenApp or ActionKind.FocusWindow && !string.IsNullOrWhiteSpace(action.Target))
        {
            var app = MatchBlockedApp(settings, action.Target);
            if (app != null) return new(RiskLevel.Blocked, $"{app} is on my blocked list.");
            if (FindBankingKeyword(action.Target) != null)
                return new(RiskLevel.Blocked, "That looks like a banking or payment app, so I won't touch it.");
        }

        if (action.Kind == ActionKind.TypeText && LooksLikeCredential(action.Text))
            return new(RiskLevel.Blocked, "That text looks like a password or secret key. I won't type it.");

        // ---------- Dangerous ----------
        var word = FindDangerousWord(target?.Name) ?? FindDangerousWord(action.Text) ?? FindDangerousWord(action.Keys);
        if (word != null)
            return new(RiskLevel.Dangerous, $"This could be hard to undo (\"{word}\").");

        var messaging = IsMessagingApp(snapshot.ProcessName, snapshot.WindowTitle);

        if (action.Kind == ActionKind.PressKeys && KeyParser.TryParse(action.Keys, out var chords, out _))
        {
            foreach (var c in chords)
            {
                var k = c.Canonical;
                if (k == "ctrl+enter") return new(RiskLevel.Dangerous, "Ctrl+Enter usually sends or submits.");
                if (k == "alt+f4") return new(RiskLevel.Dangerous, "Alt+F4 closes the window and may lose work.");
                if (k == "enter" && messaging) return new(RiskLevel.Dangerous, "Enter sends the message in this chat app.");
            }
        }

        if (action.Kind == ActionKind.TypeText && messaging && action.Text != null &&
            (action.Text.Contains('\n') || action.Text.Contains('\r')))
            return new(RiskLevel.Dangerous, "The text contains a line break, which sends the message in this chat app.");

        return new(RiskLevel.Normal, "Looks harmless.");
    }

    // ---------------- helpers ----------------

    internal static string StripDiacritics(string s)
    {
        if (string.IsNullOrEmpty(s)) return s ?? "";
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var ch in d)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(ch switch { 'đ' => 'd', 'Đ' => 'D', _ => ch });
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    private static string Fold(string s) => StripDiacritics(s).ToLowerInvariant();

    private static string? MatchBlockedApp(MiriSettings settings, params string?[] haystacks)
    {
        if (settings.BlockedApps == null) return null;
        foreach (var app in settings.BlockedApps)
        {
            if (string.IsNullOrWhiteSpace(app)) continue;
            var needle = Fold(app.Trim());
            foreach (var h in haystacks)
            {
                if (!string.IsNullOrEmpty(h) && Fold(h).Contains(needle, StringComparison.Ordinal)) return app;
            }
        }
        return null;
    }

    private static string? FindBankingKeyword(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var folded = Fold(text);
        foreach (var k in BankingKeywords)
        {
            if (folded.Contains(Fold(k), StringComparison.Ordinal)) return k;
        }
        return null;
    }

    private static bool IsMessagingApp(params string?[] haystacks)
    {
        foreach (var h in haystacks)
        {
            if (string.IsNullOrWhiteSpace(h)) continue;
            var folded = Fold(h);
            foreach (var app in MessagingApps)
            {
                if (Regex.IsMatch(folded, @"(?<![a-z0-9])" + Regex.Escape(app) + @"(?![a-z0-9])")) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Finds a dangerous verb. Accented words match their accented forms; unaccented words also match
    /// the stripped Vietnamese forms (so "gui"/"xoa"/"thanh toan" match), but an accented word like
    /// "đang" or "mùa" never matches "đăng"/"mua" by accident.
    /// </summary>
    internal static string? FindDangerousWord(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var nfc = text.Normalize(NormalizationForm.FormC).ToLowerInvariant();

        var m = AccentedPatterns.Match(nfc);
        if (m.Success) return m.Value;

        // Blank out words that contain non-ASCII letters so their stripped form can't produce false hits.
        var asciiOnly = Regex.Replace(nfc, @"[\p{L}\p{M}\p{N}]+", w => w.Value.All(c => c < 128) ? w.Value : " ");
        m = AsciiPatterns.Match(asciiOnly);
        return m.Success ? m.Value : null;
    }

    /// <summary>≥ 12 chars, no spaces, random-looking mix of letters, digits and symbols (not a URL or e-mail).</summary>
    internal static bool LooksLikeCredential(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        foreach (var token in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length < 12) continue;
            if (token.Contains("://") || token.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) continue;
            if (Regex.IsMatch(token, @"^[^@\s]+@[^@\s]+\.[a-zA-Z]{2,}$")) continue;   // e-mail

            int digits = 0, upper = 0, lower = 0, symbols = 0;
            foreach (var c in token)
            {
                if (char.IsDigit(c)) digits++;
                else if (char.IsUpper(c)) upper++;
                else if (char.IsLower(c)) lower++;
                else symbols++;
            }
            if (digits == 0 || upper + lower == 0) continue;
            var mixed = symbols > 0 || (upper > 0 && lower > 0 && digits >= 2);
            if (!mixed) continue;
            if (Entropy(token) < 3.0) continue;
            return true;
        }
        return false;
    }

    private static double Entropy(string s)
    {
        var counts = new Dictionary<char, int>();
        foreach (var c in s) counts[c] = counts.TryGetValue(c, out var n) ? n + 1 : 1;
        double h = 0;
        foreach (var n in counts.Values)
        {
            var p = (double)n / s.Length;
            h -= p * Math.Log2(p);
        }
        return h;
    }
}
