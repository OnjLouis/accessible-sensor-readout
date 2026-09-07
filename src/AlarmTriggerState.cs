using System;

internal sealed class AlarmTriggerState
{
    private DateTime? lastTriggeredUtc;
    private bool announcedCurrentCondition;

    internal bool ShouldTrigger(bool? conditionMatches, bool repeatWhileActive, int cooldownSeconds, DateTime nowUtc)
    {
        // A missing reading is not evidence of recovery.
        if (!conditionMatches.HasValue) return false;
        if (!conditionMatches.Value)
        {
            announcedCurrentCondition = false;
            return false;
        }

        if (announcedCurrentCondition && !repeatWhileActive) return false;

        if (lastTriggeredUtc.HasValue && (nowUtc - lastTriggeredUtc.Value).TotalSeconds < Math.Max(0, cooldownSeconds))
        {
            return false;
        }

        lastTriggeredUtc = nowUtc;
        announcedCurrentCondition = true;
        return true;
    }
}
