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
            DataCenter.BmrNextDamageIn = BossModTimeline_IPCSubscriber.NextDamageIn?.Invoke() ?? float.MaxValue;
            DataCenter.BmrNextDamageType = BossModTimeline_IPCSubscriber.NextDamageType?.Invoke() ?? 0;
            var hintsRaidwide = BossModTimeline_IPCSubscriber.NextRaidwideDamageIn?.Invoke() ?? float.MaxValue;
            var hintsTankbuster = BossModTimeline_IPCSubscriber.NextTankbusterDamageIn?.Invoke() ?? float.MaxValue;

            // Merge: use the nearest source (Timeline state flags OR Hints damage predictions)
            DataCenter.BmrNextRaidwideIn = Math.Min(timelineRaidwide, hintsRaidwide);
            DataCenter.BmrNextTankbusterIn = Math.Min(timelineTankbuster, hintsTankbuster);

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
