using System.Net.Http.Headers;
using System.Net.Http;
using System.IO;
using System.Text.Json;

namespace CodexQuotaWidget;

public sealed class CodexUsageClient
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private readonly string _authPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "auth.json");

    public async Task<Quota> FetchAsync(CancellationToken cancellationToken = default)
    {
        var auth = ReadAuth();
        if (string.IsNullOrWhiteSpace(auth.AccessToken)) throw new InvalidOperationException("未找到 Codex 登录态");
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://chatgpt.com/backend-api/wham/usage");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        request.Headers.UserAgent.ParseAdd("codex_quota_widget/1.0");
        if (!string.IsNullOrWhiteSpace(auth.AccountId)) request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", auth.AccountId);
        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return Parse(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    private AuthInfo ReadAuth()
    {
        if (!File.Exists(_authPath)) return new();
        using var doc = JsonDocument.Parse(File.ReadAllText(_authPath));
        var root = doc.RootElement; var tokens = root.TryGetProperty("tokens", out var t) ? t : root;
        return new(GetString(tokens, "access_token"), GetString(tokens, "account_id") ?? GetString(root, "account_id"));
    }
    private static string? GetString(JsonElement e, string name) => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
    private static Quota Parse(string json)
    {
        using var doc = JsonDocument.Parse(json); var root = doc.RootElement;
        var rate = root.TryGetProperty("rate_limit", out var r) ? r : root;
        var primary = rate.TryGetProperty("primary_window", out var p) ? p : default;
        var secondary = rate.TryGetProperty("secondary_window", out var s) ? s : default;
        var windows = new[] { primary, secondary }.Where(w => w.ValueKind != JsonValueKind.Undefined && w.ValueKind != JsonValueKind.Null).ToArray();
        var weekly = windows.FirstOrDefault(IsWeeklyWindow);
        var shortWindow = windows.FirstOrDefault(w => !IsWeeklyWindow(w));
        var lunaLimit = root.TryGetProperty("additional_rate_limits", out var additional) && additional.ValueKind == JsonValueKind.Array
            ? additional.EnumerateArray().FirstOrDefault(IsLunaReserve)
            : default;
        var lunaRate = Property(lunaLimit, "rate_limit");
        var lunaWindow = Property(lunaRate, "primary_window");
        return new Quota
        {
            FiveHourUsed = Number(shortWindow, "used_percent"),
            WeekUsed = Number(weekly, "used_percent"),
            LunaUsed = Number(lunaWindow, "used_percent"),
            FiveHourReset = Unix(shortWindow, "reset_at"),
            WeekReset = Unix(weekly, "reset_at"),
            LunaReset = Unix(lunaWindow, "reset_at"),
            HasLunaReserve = lunaLimit.ValueKind != JsonValueKind.Undefined && lunaLimit.ValueKind != JsonValueKind.Null,
            UpdatedAt = DateTime.Now,
            IsLive = true
        };
    }
    private static bool IsLunaReserve(JsonElement e) =>
        (e.TryGetProperty("normal_model_slug", out var model) && model.GetString()?.Contains("luna", StringComparison.OrdinalIgnoreCase) == true)
        || (e.TryGetProperty("limit_name", out var name) && name.GetString()?.Contains("reserve", StringComparison.OrdinalIgnoreCase) == true);
    private static JsonElement Property(JsonElement e, string name) => e.ValueKind != JsonValueKind.Undefined && e.ValueKind != JsonValueKind.Null && e.TryGetProperty(name, out var p) ? p : default;
    private static bool IsWeeklyWindow(JsonElement e) => Seconds(e, "limit_window_seconds") >= 6 * 24 * 60 * 60 || Seconds(e, "window_minutes") >= 6 * 24 * 60;
    private static long Seconds(JsonElement e, string name) => e.ValueKind != JsonValueKind.Undefined && e.TryGetProperty(name, out var p) && p.TryGetInt64(out var n) ? n : 0;
    private static double Number(JsonElement e, string name) => e.ValueKind != JsonValueKind.Undefined && e.TryGetProperty(name, out var p) && p.TryGetDouble(out var n) ? n : 0;
    private static DateTime? Unix(JsonElement e, string name) => e.ValueKind != JsonValueKind.Undefined && e.TryGetProperty(name, out var p) && p.TryGetInt64(out var n) ? DateTimeOffset.FromUnixTimeSeconds(n).LocalDateTime : null;
}
