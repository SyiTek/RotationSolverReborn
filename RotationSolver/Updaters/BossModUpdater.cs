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

            // Store whether IPC Funcs are bound (null = BMR doesn't have that endpoint)
            DataCenter.BmrDebugTimelineRwFunc = BossModTimeline_IPCSubscriber.NextRaidwideIn != null;
            DataCenter.BmrDebugTimelineTbFunc = BossModTimeline_IPCSubscriber.NextTankbusterIn != null;
            DataCenter.BmrDebugHintsRwFunc = BossModTimeline_IPCSubscriber.NextRaidwideDamageIn != null;
            DataCenter.BmrDebugHintsTbFunc = BossModTimeline_IPCSubscriber.NextTankbusterDamageIn != null;

            // Poll Timeline endpoints (state machine flags)
            var timelineRaidwide = BossModTimeline_IPCSubscriber.NextRaidwideIn?.Invoke() ?? float.MaxValue;
            var timelineTankbuster = BossModTimeline_IPCSubscriber.NextTankbusterIn?.Invoke() ?? float.MaxValue;
            DataCenter.BmrNextKnockbackIn = BossModTimeline_IPCSubscriber.NextKnockbackIn?.Invoke() ?? float.MaxValue;
            DataCenter.BmrNextDowntimeIn = BossModTimeline_IPCSubscriber.NextDowntimeIn?.Invoke() ?? float.MaxValue;
            DataCenter.BmrNextDowntimeEndIn = BossModTimeline_IPCSubscriber.NextDowntimeEndIn?.Invoke() ?? float.MaxValue;
            DataCenter.BmrNextVulnerableIn = BossModTimeline_IPCSubscriber.NextVulnerableIn?.Invoke() ?? float.MaxValue;
            DataCenter.BmrNextVulnerableEndIn = BossModTimeline_IPCSubscriber.NextVulnerableEndIn?.Invoke() ?? float.MaxValue;
            DataCenter.BmrDebugTimelineRaidwide = timelineRaidwide;
            DataCenter.BmrDebugTimelineTankbuster = timelineTankbuster;

            // Poll Hints endpoints (component-level damage predictions)
            var damageIn = BossModTimeline_IPCSubscriber.NextDamageIn?.Invoke() ?? float.MaxValue;
            var damageType = BossModTimeline_IPCSubscriber.NextDamageType?.Invoke() ?? 0;
            DataCenter.BmrNextDamageIn = damageIn;
            DataCenter.BmrNextDamageType = damageType;
            DataCenter.BmrDebugGenericDamageIn = damageIn;
            DataCenter.BmrDebugGenericDamageType = damageType;

            // Type-specific Hints endpoints
            var hintsRaidwide = BossModTimeline_IPCSubscriber.NextRaidwideDamageIn?.Invoke() ?? float.MaxValue;
            var hintsTankbuster = BossModTimeline_IPCSubscriber.NextTankbusterDamageIn?.Invoke() ?? float.MaxValue;
            DataCenter.BmrDebugHintsRaidwide = hintsRaidwide;
            DataCenter.BmrDebugHintsTankbuster = hintsTankbuster;

            // Filter out invalid values (<=0 means endpoint missing/SafeWrapper default or damage already resolved)
            if (hintsRaidwide <= 0f) hintsRaidwide = float.MaxValue;
            if (hintsTankbuster <= 0f) hintsTankbuster = float.MaxValue;

            // Final fallback: use generic damage prediction if type matches
            var genericRaidwide = (damageType == 2 && damageIn > 0f) ? damageIn : float.MaxValue;
            var genericTankbuster = (damageType == 1 && damageIn > 0f) ? damageIn : float.MaxValue;

            // Merge all sources: Timeline OR type-specific Hints OR generic damage prediction
            DataCenter.BmrNextRaidwideIn = Math.Min(Math.Min(timelineRaidwide, hintsRaidwide), genericRaidwide);
            DataCenter.BmrNextTankbusterIn = Math.Min(Math.Min(timelineTankbuster, hintsTankbuster), genericTankbuster);

            DataCenter.BmrSpecialModeIn = BossModTimeline_IPCSubscriber.SpecialModeIn?.Invoke() ?? float.MaxValue;
            DataCenter.BmrSpecialModeType = BossModTimeline_IPCSubscriber.SpecialModeType?.Invoke() ?? 0;
            DataCenter.BmrDebugTimelineWalk = BossModTimeline_IPCSubscriber.DebugTimelineWalk?.Invoke();
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
