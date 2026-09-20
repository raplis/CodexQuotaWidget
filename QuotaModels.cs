namespace CodexQuotaWidget;

public sealed class Quota
{
    public double FiveHourUsed { get; set; }
    public double WeekUsed { get; set; }
    public double LunaUsed { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    public DateTime? FiveHourReset { get; set; }
    public DateTime? WeekReset { get; set; }
    public DateTime? LunaReset { get; set; }
    public bool HasLunaReserve { get; set; }
    public bool IsLive { get; set; }
}

public sealed record AuthInfo(string? AccessToken = null, string? AccountId = null);
