using System;

internal static class AlarmTriggerStateTests
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static void Main()
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var once = new AlarmTriggerState();
        Require(once.ShouldTrigger(true, false, 60, start), "Initial warning was missed.");
        Require(!once.ShouldTrigger(true, false, 60, start.AddSeconds(60)), "Unchanged disk warning repeated after cooldown.");
        Require(!once.ShouldTrigger(null, false, 60, start.AddSeconds(70)), "Missing reading triggered an alarm.");
        Require(!once.ShouldTrigger(true, false, 60, start.AddSeconds(80)), "Missing reading incorrectly rearmed the alarm.");
        Require(!once.ShouldTrigger(false, false, 60, start.AddSeconds(90)), "Healthy reading triggered an alarm.");
        Require(once.ShouldTrigger(true, false, 60, start.AddSeconds(100)), "Recovery did not rearm the alarm.");
        Require(!once.ShouldTrigger(false, false, 60, start.AddSeconds(110)), "Second recovery triggered an alarm.");
        Require(!once.ShouldTrigger(true, false, 60, start.AddSeconds(120)), "New episode bypassed cooldown.");
        Require(once.ShouldTrigger(true, false, 60, start.AddSeconds(160)), "New episode was lost during cooldown.");
        Require(!once.ShouldTrigger(true, false, 0, start.AddDays(1)), "Zero cooldown repeated a one-time alarm.");
        var repeating = new AlarmTriggerState();
        Require(repeating.ShouldTrigger(true, true, 60, start), "Repeating alarm did not start.");
        Require(!repeating.ShouldTrigger(true, true, 60, start.AddSeconds(59)), "Repeat ignored cooldown.");
        Require(repeating.ShouldTrigger(true, true, 60, start.AddSeconds(60)), "Repeat did not fire at cooldown boundary.");
        Require(repeating.ShouldTrigger(true, true, 0, start.AddSeconds(61)), "Zero cooldown repeat changed behavior.");
        Require(!repeating.ShouldTrigger(null, true, 0, start.AddSeconds(62)), "Repeating alarm fired on missing data.");
        Require(new AlarmTriggerState().ShouldTrigger(true, false, 60, start), "New session suppressed a persistent warning.");
        Console.WriteLine("Alarm trigger regression checks passed.");
    }
}
