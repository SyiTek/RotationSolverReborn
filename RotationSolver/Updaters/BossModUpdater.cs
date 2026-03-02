using RotationSolver.IPC;

namespace RotationSolver.Updaters;

internal static class BossModUpdater
{
    private static bool _checkedAvailability;
    private static bool _isAvailable;

    public static void Update()
    {
        if (!Service.Config.UseBmrTimeline)
        {
            if (DataCenter.BmrHasActiveModule)
                DataCenter.ResetBmrData();
            return;
        }

        if (!_checkedAvailability)
        {
            _isAvailable = BossModTimeline_IPCSubscriber.IsEnabled;
            _checkedAvailability = true;
        }

        if (!_isAvailable)
        {
            DataCenter.ResetBmrData();
            return;
        }

        try
        {
            DataCenter.BmrHasActiveModule = BossModTimeline_IPCSubscriber.HasActiveModule?.Invoke() ?? false;

            if (!DataCenter.BmrHasActiveModule)
            {
                DataCenter.ResetBmrData();
                return;
            }

            DataCenter.BmrActiveModuleName = BossModTimeline_IPCSubscriber.ActiveModuleName?.Invoke();

            // Poll Timeline endpoints (state machine flags — may be empty for many fights)
            var timelineRaidwide = BossModTimeline_IPCSubscriber.NextRaidwideIn?.Invoke() ?? float.MaxValue;
            var timelineTankbuster = BossModTimeline_IPCSubscriber.NextTankbusterIn?.Invoke() ?? float.MaxValue;
            DataCenter.BmrNextKnockbackIn = BossModTimeline_IPCSubscriber.NextKnockbackIn?.Invoke() ?? float.MaxValue;
            DataCenter.BmrNextDowntimeIn = BossModTimeline_IPCSubscriber.NextDowntimeIn?.Invoke() ?? float.MaxValue;
            DataCenter.BmrNextDowntimeEndIn = BossModTimeline_IPCSubscriber.NextDowntimeEndIn?.Invoke() ?? float.MaxValue;
            DataCenter.BmrNextVulnerableIn = BossModTimeline_IPCSubscriber.NextVulnerableIn?.Invoke() ?? float.MaxValue;
            DataCenter.BmrNextVulnerableEndIn = BossModTimeline_IPCSubscriber.NextVulnerableEndIn?.Invoke() ?? float.MaxValue;

            // Poll Hints endpoints (component-level damage predictions — works even without state machine flags)
            var damageIn = BossModTimeline_IPCSubscriber.NextDamageIn?.Invoke() ?? float.MaxValue;
            var damageType = BossModTimeline_IPCSubscriber.NextDamageType?.Invoke() ?? 0;
            DataCenter.BmrNextDamageIn = damageIn;
            DataCenter.BmrNextDamageType = damageType;

            // Type-specific Hints endpoints (may return 0.0 if BMR doesn't have them — SafeWrapper default)
            var hintsRaidwide = BossModTimeline_IPCSubscriber.NextRaidwideDamageIn?.Invoke() ?? float.MaxValue;
            var hintsTankbuster = BossModTimeline_IPCSubscriber.NextTankbusterDamageIn?.Invoke() ?? float.MaxValue;

            // Filter out invalid values (<=0 means endpoint missing/SafeWrapper default or damage already resolved)
            if (hintsRaidwide <= 0f) hintsRaidwide = float.MaxValue;
            if (hintsTankbuster <= 0f) hintsTankbuster = float.MaxValue;

            // Final fallback: use generic damage prediction if type matches
            // This works even when type-specific endpoints aren't available in older BMR builds
            var genericRaidwide = (damageType == 2 && damageIn > 0f) ? damageIn : float.MaxValue; // 2 = Raidwide
            var genericTankbuster = (damageType == 1 && damageIn > 0f) ? damageIn : float.MaxValue; // 1 = Tankbuster

            // Merge all sources: Timeline OR type-specific Hints OR generic damage prediction
            DataCenter.BmrNextRaidwideIn = Math.Min(Math.Min(timelineRaidwide, hintsRaidwide), genericRaidwide);
            DataCenter.BmrNextTankbusterIn = Math.Min(Math.Min(timelineTankbuster, hintsTankbuster), genericTankbuster);

            DataCenter.BmrSpecialModeIn = BossModTimeline_IPCSubscriber.SpecialModeIn?.Invoke() ?? float.MaxValue;
            DataCenter.BmrSpecialModeType = BossModTimeline_IPCSubscriber.SpecialModeType?.Invoke() ?? 0;
        }
        catch
        {
            DataCenter.ResetBmrData();
            _checkedAvailability = false;
        }
    }

    public static void ResetAvailabilityCheck()
    {
        _checkedAvailability = false;
    }
}
