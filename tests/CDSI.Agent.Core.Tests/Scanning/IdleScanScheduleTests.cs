using CDSI.Agent.Core.Scanning;

namespace CDSI.Agent.Core.Tests.Scanning;

public sealed class IdleScanScheduleTests
{
    [Theory]
    [InlineData(IdleScanIntervalUnit.Minutes, 3, 3)]
    [InlineData(IdleScanIntervalUnit.Hours, 2, 120)]
    [InlineData(IdleScanIntervalUnit.Days, 2, 2880)]
    public void Duration_UsesTheSelectedUnit(
        IdleScanIntervalUnit unit,
        int interval,
        double expectedMinutes)
    {
        var schedule = new IdleScanSchedule(true, interval, unit);

        Assert.Equal(expectedMinutes, schedule.Duration.TotalMinutes);
    }

    [Fact]
    public void IsDue_RequiresAnEnabledElapsedSchedule()
    {
        var anchor = DateTimeOffset.Parse("2026-08-28T10:00:00+08:00");
        var enabled = new IdleScanSchedule(true, 30, IdleScanIntervalUnit.Minutes);
        var disabled = new IdleScanSchedule(false, 30, IdleScanIntervalUnit.Minutes);

        Assert.False(enabled.IsDue(anchor, anchor.AddMinutes(29)));
        Assert.True(enabled.IsDue(anchor, anchor.AddMinutes(30)));
        Assert.False(disabled.IsDue(anchor, anchor.AddHours(1)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1000)]
    public void Constructor_RejectsAnOutOfRangeInterval(int interval)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new IdleScanSchedule(true, interval, IdleScanIntervalUnit.Hours));
    }
}
