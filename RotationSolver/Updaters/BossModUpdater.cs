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
            DataCenter.BmrNextRaidwideIn = BossModTimeline_IPCSubscriber.NextRaidwideIn?.Invoke() ?? float.MaxValue;
            DataCenter.BmrNextTankbusterIn = BossModTimeline_IPCSubscriber.NextTankbusterIn?.Invoke() ?? float.MaxValue;
            DataCenter.BmrNextKnockbackIn = BossModTimeline_IPCSubscriber.NextKnockbackIn?.Invoke() ?? float.MaxValue;
            DataCenter.BmrNextDamageIn = BossModTimeline_IPCSubscriber.NextDamageIn?.Invoke() ?? float.MaxValue;
            DataCenter.BmrNextDamageType = BossModTimeline_IPCSubscriber.NextDamageType?.Invoke() ?? 0;
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
