using ECommons.EzIpcManager;
using System.Numerics;

#pragma warning disable CS0649 // Field is never assigned to, and will always have its default value

namespace RotationSolver.IPC;

internal static class BMRTimeline_IPCSubscriber
{
    private static readonly EzIPCDisposalToken[] _disposalTokens =
        EzIPC.Init(typeof(BMRTimeline_IPCSubscriber), "BossMod", SafeWrapper.AnyException);

    internal static bool IsEnabled => IPCSubscriber_Common.IsReady("BossModReborn")
                                      || IPCSubscriber_Common.IsReady("BossMod");

    [EzIPC("HasActiveModule", true)]
    internal static readonly Func<bool>? HasActiveModule;

    [EzIPC("ActiveModuleName", true)]
    internal static readonly Func<string?>? ActiveModuleName;

    // Timeline: state-machine countdown endpoints (seconds until next event, float.MaxValue = not predicted)
    [EzIPC("Timeline.NextRaidwideIn", true)]
    internal static readonly Func<float>? NextRaidwideIn;

    [EzIPC("Timeline.NextTankbusterIn", true)]
    internal static readonly Func<float>? NextTankbusterIn;

    [EzIPC("Timeline.NextKnockbackIn", true)]
    internal static readonly Func<float>? NextKnockbackIn;

    [EzIPC("Timeline.NextDowntimeIn", true)]
    internal static readonly Func<float>? NextDowntimeIn;

    [EzIPC("Timeline.NextDowntimeEndIn", true)]
    internal static readonly Func<float>? NextDowntimeEndIn;

    [EzIPC("Timeline.NextVulnerableIn", true)]
    internal static readonly Func<float>? NextVulnerableIn;

    [EzIPC("Timeline.NextVulnerableEndIn", true)]
    internal static readonly Func<float>? NextVulnerableEndIn;

    // Hints: component-level short-window damage predictions
    [EzIPC("Hints.NextDamageIn", true)]
    internal static readonly Func<float>? NextDamageIn;

    [EzIPC("Hints.NextDamageType", true)]
    internal static readonly Func<int>? NextDamageType;

    [EzIPC("Hints.NextRaidwideDamageIn", true)]
    internal static readonly Func<float>? NextRaidwideDamageIn;

    [EzIPC("Hints.NextTankbusterDamageIn", true)]
    internal static readonly Func<float>? NextTankbusterDamageIn;

    // Hints: special mechanic mode (Pyretic, NoMovement, Freezing, Misdirection)
    [EzIPC("Hints.SpecialModeIn", true)]
    internal static readonly Func<float>? SpecialModeIn;

    [EzIPC("Hints.SpecialModeType", true)]
    internal static readonly Func<int>? SpecialModeType;

    // Hints: positional safety checks for movement/dash abilities
    [EzIPC("Hints.IsPositionSafe", true)]
    internal static readonly Func<Vector3, bool>? IsPositionSafe;

    [EzIPC("Hints.IsDashSafe", true)]
    internal static readonly Func<Vector3, Vector3, bool>? IsDashSafe;

    [EzIPC("Hints.IsFixedDashSafe", true)]
    internal static readonly Func<float, bool, bool>? IsFixedDashSafe;

    [EzIPC("Hints.IsBackdashSafe", true)]
    internal static readonly Func<Vector3, float, bool>? IsBackdashSafe;

    // Hints: cast interrupt and cast time limits
    [EzIPC("Hints.ForceCancelCast", true)]
    internal static readonly Func<bool>? ForceCancelCast;

    [EzIPC("Hints.MaxCastTime", true)]
    internal static readonly Func<float>? MaxCastTime;

    // Hints: recommended positional (0=any, 1=flank, 2=rear, 3=front)
    [EzIPC("Hints.RecommendedPositional", true)]
    internal static readonly Func<int>? RecommendedPositional;

    [EzIPC("Hints.ArenaCenter", true)]
    internal static readonly Func<Vector3>? ArenaCenter;

    [EzIPC("Hints.ArenaRadius", true)]
    internal static readonly Func<float>? ArenaRadius;

    [EzIPC("Debug.TimelineWalk", true)]
    internal static readonly Func<string?>? DebugTimelineWalk;

    internal static void Dispose() => IPCSubscriber_Common.DisposeAll(_disposalTokens);
}
