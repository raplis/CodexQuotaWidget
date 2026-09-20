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
        var usageJson = await response.Content.ReadAsStringAsync(cancellationToken);
        var quota = Parse(usageJson);
        if (quota.ResetCreditCount > 0)
        {
            quota.RecentCreditExpiry = await FetchRecentCreditExpiryAsync(auth, cancellationToken);
        }
        return quota;
    }

    private async Task<DateTime?> FetchRecentCreditExpiryAsync(AuthInfo auth, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://chatgpt.com/backend-api/wham/rate-limit-reset-credits");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.TryAddWithoutValidation("oai-product-sku", "CODEX");
            request.Headers.TryAddWithoutValidation("originator", "Codex Desktop");
            request.Headers.UserAgent.ParseAdd("codex_quota_widget/1.0");
            if (!string.IsNullOrWhiteSpace(auth.AccountId)) request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", auth.AccountId);
            using var response = await _http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (!doc.RootElement.TryGetProperty("credits", out var credits) || credits.ValueKind != JsonValueKind.Array) return null;

            var now = DateTimeOffset.UtcNow;
            var upcoming = credits.EnumerateArray()
                .Where(IsAvailableCredit)
                .Select(c => ParseTimestamp(Property(c, "expires_at")))
                .Where(value => value.HasValue && value.Value > now)
                .OrderBy(value => value)
                .FirstOrDefault();
            return upcoming?.LocalDateTime;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Credit details are supplementary; quota rendering should still work if this endpoint changes.
            return null;
        }
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
        var resetCredits = Property(root, "rate_limit_reset_credits");
        return new Quota
        {
            FiveHourUsed = Number(shortWindow, "used_percent"),
            WeekUsed = Number(weekly, "used_percent"),
            LunaUsed = Number(lunaWindow, "used_percent"),
            FiveHourReset = Unix(shortWindow, "reset_at"),
            WeekReset = Unix(weekly, "reset_at"),
            LunaReset = Unix(lunaWindow, "reset_at"),
            ResetCreditCount = Math.Max(0, (int)Number(resetCredits, "available_count")),
            HasLunaReserve = lunaLimit.ValueKind != JsonValueKind.Undefined && lunaLimit.ValueKind != JsonValueKind.Null,
            UpdatedAt = DateTime.Now,
            IsLive = true
        };
    }
    private static bool IsAvailableCredit(JsonElement credit)
    {
        var status = GetString(credit, "status");
        return string.IsNullOrWhiteSpace(status) || status.Equals("available", StringComparison.OrdinalIgnoreCase);
    }
    private static DateTimeOffset? ParseTimestamp(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(value.GetString(), null, System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed)) return parsed.ToUniversalTime();
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var unix)) return unix > 10_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(unix) : DateTimeOffset.FromUnixTimeSeconds(unix);
        return null;
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
