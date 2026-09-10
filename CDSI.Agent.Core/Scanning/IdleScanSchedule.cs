namespace CDSI.Agent.Core.Scanning;

public sealed record IdleScanSchedule
{
    public const int MinimumInterval = 1;
    public const int MaximumInterval = 999;

    public IdleScanSchedule(
        bool enabled,
        int interval,
        IdleScanIntervalUnit unit)
    {
        if (interval is < MinimumInterval or > MaximumInterval)
        {
            throw new ArgumentOutOfRangeException(
                nameof(interval),
                $"Idle scan interval must be between {MinimumInterval} and {MaximumInterval}.");
        }

        if (!Enum.IsDefined(unit))
        {
            throw new ArgumentOutOfRangeException(nameof(unit));
        }

        Enabled = enabled;
        Interval = interval;
        Unit = unit;
    }

    public static IdleScanSchedule Disabled { get; } =
        new(false, 1, IdleScanIntervalUnit.Hours);

    public bool Enabled { get; }

    public int Interval { get; }

    public IdleScanIntervalUnit Unit { get; }

    public TimeSpan Duration => Unit switch
    {
        IdleScanIntervalUnit.Minutes => TimeSpan.FromMinutes(Interval),
        IdleScanIntervalUnit.Hours => TimeSpan.FromHours(Interval),
        IdleScanIntervalUnit.Days => TimeSpan.FromDays(Interval),
        _ => throw new ArgumentOutOfRangeException(nameof(Unit))
    };

    public bool IsDue(DateTimeOffset previousScanOrConfigurationAt, DateTimeOffset now)
    {
        return Enabled && now >= previousScanOrConfigurationAt + Duration;
    }
}

public enum IdleScanIntervalUnit
{
    Minutes,
    Hours,
    Days
}
