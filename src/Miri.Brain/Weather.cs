using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using Miri.Core;
using static Miri.Brain.TextUtil;

namespace Miri.Brain;

/// <summary>
/// "What's the weather?" through Open-Meteo (free, no key, CC BY 4.0 data; free tier is for non-commercial use).
/// Location: a city the user names, else Windows location (respects the Windows privacy switch, rounded to 0.1° ≈ 10 km), else settings.WeatherCity.
/// Only runs when the user asks; nothing is stored or sent otherwise.
/// </summary>
internal sealed class Weather
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private readonly MiriSettings _settings;
    public Weather(MiriSettings settings) => _settings = settings;

    private static readonly Regex Ask = new(
        @"(?<![\p{L}])(weather|forecast|temperature|how (hot|cold|warm) is it|is it (raining|going to rain|cold|hot)|will it rain|umbrella|" +
        @"thời tiết|nhiệt độ|dự báo|trời (có )?(mưa|nắng|lạnh|nóng)|có mưa không|mang ô)(?![\p{L}])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex NotWeather = new(@"^\s*(play|open|bật|mở|phát)(?![\p{L}])", RegexOptions.IgnoreCase);
    private static readonly Regex City = new(
        @"(?:\b(?:in|at|for)|(?<![\p{L}])(?:ở|tại))\s+([\p{L}][\p{L}\p{M} .'-]{1,40}?)\s*(?:today|tomorrow|right now|now|hôm nay|ngày mai|bây giờ|thế nào|ra sao|không|nhé|vậy)?\s*[?!.]*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool IsWeatherQuestion(string text) => Ask.IsMatch(text) && !NotWeather.IsMatch(text);

    public static string? CityIn(string text)
    {
        var m = City.Match(text);
        if (!m.Success) return null;
        var c = m.Groups[1].Value.Trim();
        return c.Length < 2 || Regex.IsMatch(c, @"^(the|my|here|đây|this)$", RegexOptions.IgnoreCase) ? null : c;
    }

    public async Task<string> AnswerAsync(string text, Lang lang, CancellationToken ct)
    {
        string? place = CityIn(text);
        (double Lat, double Lon)? at = null;
        if (place != null) (at, place) = await GeocodeAsync(place, lang, ct);
        else if (_settings.WeatherUseLocation && await DeviceLocationAsync(ct) is { } here) at = here;
        else if (_settings.WeatherCity.Length > 0) (at, place) = await GeocodeAsync(_settings.WeatherCity, lang, ct);

        if (at is not { } p)
            return L(lang,
                "I don't know where you are, meow. Name a city (\"weather in Hanoi\"), turn on Windows location for desktop apps, or set \"WeatherCity\" in my settings.",
                "Miri chưa biết bạn ở đâu, meo. Bạn nói tên thành phố (\"thời tiết ở Hà Nội\"), bật Vị trí cho ứng dụng máy tính trong Windows, hoặc đặt \"WeatherCity\" trong cài đặt nhé.");

        var url = string.Format(CultureInfo.InvariantCulture,
            "https://api.open-meteo.com/v1/forecast?latitude={0:0.0}&longitude={1:0.0}&current=temperature_2m,apparent_temperature,weather_code&daily=temperature_2m_max,temperature_2m_min,precipitation_probability_max&timezone=auto&forecast_days=1",
            p.Lat, p.Lon);
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url, ct));
        var cur = doc.RootElement.GetProperty("current");
        var day = doc.RootElement.GetProperty("daily");
        double t = cur.GetProperty("temperature_2m").GetDouble(), feels = cur.GetProperty("apparent_temperature").GetDouble();
        int code = cur.GetProperty("weather_code").GetInt32();
        double hi = day.GetProperty("temperature_2m_max")[0].GetDouble(), lo = day.GetProperty("temperature_2m_min")[0].GetDouble();
        var rp = day.GetProperty("precipitation_probability_max")[0];
        int rain = rp.ValueKind == JsonValueKind.Number ? rp.GetInt32() : 0;
        var (en, vi) = Describe(code);
        string where = place ?? L(lang, "Where you are", "Chỗ bạn");
        return L(lang,
            $"{where}: {en}, {t:0}°C (feels like {feels:0}°C). Today {lo:0}–{hi:0}°C, {rain}% chance of rain.{(rain >= 50 ? " Take an umbrella, meow!" : "")} (weather data: Open-Meteo.com)",
            $"{where}: {vi}, {t:0}°C (cảm giác {feels:0}°C). Hôm nay {lo:0}–{hi:0}°C, {rain}% khả năng mưa.{(rain >= 50 ? " Nhớ mang ô nha, meo!" : "")} (dữ liệu: Open-Meteo.com)");
    }

    private static async Task<((double, double)?, string?)> GeocodeAsync(string name, Lang lang, CancellationToken ct)
    {
        var url = $"https://geocoding-api.open-meteo.com/v1/search?count=1&language={(lang == Lang.Vietnamese ? "vi" : "en")}&name={Uri.EscapeDataString(name)}";
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url, ct));
        if (!doc.RootElement.TryGetProperty("results", out var r) || r.GetArrayLength() == 0) return (null, name);
        var x = r[0];
        return ((x.GetProperty("latitude").GetDouble(), x.GetProperty("longitude").GetDouble()), x.GetProperty("name").GetString());
    }

    /// <summary>Windows location, only if the user allowed desktop apps to use it. Null when denied or off.</summary>
    private static async Task<(double, double)?> DeviceLocationAsync(CancellationToken ct)
    {
        try
        {
            if (await Windows.Devices.Geolocation.Geolocator.RequestAccessAsync() != Windows.Devices.Geolocation.GeolocationAccessStatus.Allowed) return null;
            var g = new Windows.Devices.Geolocation.Geolocator { DesiredAccuracy = Windows.Devices.Geolocation.PositionAccuracy.Default };
            var pos = await g.GetGeopositionAsync(TimeSpan.FromMinutes(30), TimeSpan.FromSeconds(6)).AsTask(ct);
            var c = pos.Coordinate.Point.Position;
            return (Math.Round(c.Latitude, 1), Math.Round(c.Longitude, 1));   // ~10 km is plenty for weather
        }
        catch (Exception) when (!ct.IsCancellationRequested) { return null; }
    }

    // WMO weather codes
    private static (string, string) Describe(int c) => c switch
    {
        0 => ("clear sky", "trời quang"),
        1 or 2 => ("partly cloudy", "có mây rải rác"),
        3 => ("overcast", "nhiều mây"),
        45 or 48 => ("foggy", "có sương mù"),
        >= 51 and <= 57 => ("drizzle", "mưa phùn"),
        (>= 61 and <= 67) or (>= 80 and <= 82) => ("rain", "mưa"),
        (>= 71 and <= 77) or 85 or 86 => ("snow", "tuyết"),
        >= 95 => ("thunderstorm", "dông"),
        _ => ("mixed weather", "thời tiết thất thường"),
    };
}
