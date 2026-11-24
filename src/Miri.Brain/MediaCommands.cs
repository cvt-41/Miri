using System.Text;
using System.Text.RegularExpressions;

namespace Miri.Brain;

/// <summary>A music request: a transport control (media keys) or "play &lt;song&gt;" in a music app.</summary>
internal sealed record MediaCommand(MediaCommandKind Kind, string? Keys = null, string? Query = null, string? App = null)
{
    /// <summary>English + Vietnamese label for the approval card / status.</summary>
    public (string En, string Vi) Label => Keys switch
    {
        "playpause" when Kind == MediaCommandKind.Control => ("Resume the music", "Tiếp tục phát nhạc"),
        "mediastop" => ("Pause the music", "Tạm dừng nhạc"),
        "nexttrack" => ("Skip to the next song", "Chuyển sang bài tiếp theo"),
        "prevtrack" => ("Go back to the previous song", "Quay lại bài trước"),
        "volumeup" => ("Turn the volume up", "Tăng âm lượng"),
        "volumedown" => ("Turn the volume down", "Giảm âm lượng"),
        "volumemute" => ("Mute / unmute", "Tắt / bật tiếng"),
        _ => ("Media control", "Điều khiển nhạc"),
    };
}

internal enum MediaCommandKind
{
    /// <summary>Global media key (works for any player that registers with Windows media controls).</summary>
    Control,
    /// <summary>Open/focus the music app and press play (no specific song).</summary>
    PlayAnything,
    /// <summary>Open/focus the music app, search for <see cref="MediaCommand.Query"/> and play the best result.</summary>
    PlaySong,
}

internal static class MediaCommands
{
    private const RegexOptions Opt = RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    // Music apps Miri knows how to drive with the search recipe. Others still get media keys.
    private const string Apps = @"sonora|spotify|youtube music|yt music|youtube|apple music|itunes|groove|media player|vlc|foobar2000|musicbee|aimp|winamp";

    private static readonly Regex AppSuffix = new(
        @"\s+(?:on|in|using|with|from|via|trên|bằng|ở|trong|qua)\s+(?:the\s+|app\s+|ứng dụng\s+)?(?<app>" + Apps + @")(?:\s+app)?$", Opt);

    private static readonly Regex PoliteSuffix = new(
        @"(?:\s+(?:please|pls|for me|now|thanks|giúp(?: tôi| mình| em| tớ)?|cho (?:tôi|mình|em|tớ)|nhé|nha|đi|với|ạ|đó|luôn|đi nào))+$", Opt);

    private static readonly (Regex Pattern, string Keys)[] Controls =
    {
        (new(@"^(?:pause|stop|tạm dừng|dừng|ngừng|tắt|ngưng)(?:\s+(?:the\s+)?(?:music|song|playback|track|it|nhạc|bài hát|bài|phát nhạc))?$", Opt), "mediastop"),
        (new(@"^(?:resume|unpause|continue(?: playing)?|tiếp tục(?: phát| nghe| bật)?)(?:\s+(?:the\s+)?(?:music|song|playback|nhạc|bài hát|bài))?$", Opt), "playpause"),
        (new(@"^(?:next|skip)(?:\s+(?:this\s+|the\s+)?(?:song|track|one))?$|^(?:play\s+)?(?:the\s+)?next\s+(?:song|track)$|^(?:bài|chuyển bài|qua bài|sang bài|next bài)(?:\s+(?:tiếp|tiếp theo|khác|mới))?$|^(?:chuyển|qua|sang)\s+bài(?:\s+(?:tiếp|tiếp theo|khác))?$", Opt), "nexttrack"),
        (new(@"^(?:previous|prev|last|go back to the previous|back)(?:\s+(?:song|track))$|^(?:play\s+)?(?:the\s+)?(?:previous|last)\s+(?:song|track)$|^(?:quay lại\s+|về\s+)?bài\s+(?:trước|trước đó)$", Opt), "prevtrack"),
        (new(@"^(?:turn\s+(?:the\s+)?volume\s+up|volume\s+up|louder|tăng\s+(?:âm lượng|âm thanh|tiếng)|to\s+(?:lên|tiếng lên))$", Opt), "volumeup"),
        (new(@"^(?:turn\s+(?:the\s+)?volume\s+down|volume\s+down|quieter|giảm\s+(?:âm lượng|âm thanh|tiếng)|nhỏ\s+(?:lại|tiếng lại))$", Opt), "volumedown"),
        (new(@"^(?:mute|unmute|tắt tiếng|bật tiếng)$", Opt), "volumemute"),
    };

    private static readonly Regex OpenThen = new(
        @"^(?:open|launch|start|mở|bật)\s+(?<app>" + Apps + @")(?:\s+app)?\s*(?:,|and|then|và|rồi|xong)\s+(?:then\s+|rồi\s+)?(?<rest>.+)$", Opt);

    // "play X", "phát bài X", "mở bài X", "nghe X", "bật bài X"
    private static readonly Regex PlayVerb = new(
        @"^(?:play|put on|listen to|phát|nghe|cho (?:tôi|mình|em|tớ) nghe|bật|mở)\s+(?<rest>.+)$", Opt);

    private static readonly Regex SongMarker = new(@"^(?:the\s+)?(?:song|track|bài hát|bài|ca khúc|nhạc của|album|playlist)\s+(?<q>.+)$", Opt);

    private static readonly Regex Generic = new(
        @"^(?:some\s+|a\s+|any\s+|my\s+|the\s+)?(?:music|songs?|tunes|something|anything|nhạc|bài hát|một bài|bài gì đó|nhạc gì đó|chút nhạc|nhạc đi)$", Opt);

    /// <summary>Parses a (lower-cased, polite-prefix-stripped) request. Null when it isn't about music.</summary>
    public static MediaCommand? TryParse(string request, string defaultApp)
    {
        var t = IntentRouter.StripPolite(request.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormC));
        bool asked = t.EndsWith('?');
        t = Regex.Replace(t, @"[.!?]+$", "").Trim();

        // "open sonora and play X" / "mở sonora rồi phát X"
        var om = OpenThen.Match(t);
        if (om.Success)
            t = om.Groups["rest"].Value.Trim() + " on " + om.Groups["app"].Value;
        t = PoliteSuffix.Replace(t, "").Trim();

        string? app = null;
        var am = AppSuffix.Match(t);
        if (am.Success)
        {
            app = am.Groups["app"].Value;
            t = t[..am.Index].Trim();
            t = PoliteSuffix.Replace(t, "").Trim();
        }

        foreach (var (pattern, keys) in Controls)
            if (pattern.IsMatch(t))
                return new MediaCommand(MediaCommandKind.Control, Keys: keys, App: app);

        var pm = PlayVerb.Match(t);
        if (!pm.Success)
            return t is "play" or "phát" or "phát nhạc" or "bật nhạc" or "mở nhạc" or "nghe nhạc"
                ? new MediaCommand(MediaCommandKind.PlayAnything, App: app ?? defaultApp)
                : null;

        var verb = t[..pm.Groups["rest"].Index].Trim();
        var rest = pm.Groups["rest"].Value.Trim();
        if (Generic.IsMatch(rest))
            return new MediaCommand(MediaCommandKind.PlayAnything, App: app ?? defaultApp);

        // "can you play guitar?" / "play a game" are questions or not music at all
        if (asked && app == null) return null;
        if (Regex.IsMatch(rest, @"^(?:a |an |some |the )?(?:game|games|guitar|piano|football|soccer|chess|trò chơi|game gì)\b")) return null;

        var sm = SongMarker.Match(rest);
        bool marked = sm.Success;
        var query = (marked ? sm.Groups["q"].Value : rest).Trim().Trim('"', '\'', '“', '”');
        if (query.Length == 0 || query.Length > 80) return null;

        // "mở zalo" / "bật wifi" are not music: "mở"/"bật" only count with a song marker or a music app.
        bool weakVerb = verb is "bật" or "mở";
        if (weakVerb && !marked && app == null) return null;
        // "play a game", "nghe này" ... need a song marker, a music app, or the verb "play"/"phát"/"nghe" + a real title.
        if (!marked && app == null && query.Split(' ').Length == 1 && query is "game" or "games" or "này" or "đi" or "tôi" or "mình") return null;

        return new MediaCommand(MediaCommandKind.PlaySong, Query: query, App: app ?? defaultApp);
    }
}
