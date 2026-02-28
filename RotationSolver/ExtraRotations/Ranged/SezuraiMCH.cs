namespace RotationSolver.ExtraRotations.Ranged;

[Rotation("SezuraiMCH", CombatType.PvE, GameVersion = "7.41",
    Description = "Balance-aligned MCH with proper burst timing, 8-second tool rule, and Queen step tracking.")]
[SourceCode(Path = "main/ExtraRotations/Ranged/SezuraiMCH.cs")]
[ExtraRotation]
public sealed class SezuraiMCH : MachinistRotation
{
    #region Config Options

    [RotationConfig(CombatType.PvE, Name = "Use burst medicine in countdown")]
    private bool OpenerBurstMeds { get; set; } = false;

    [RotationConfig(CombatType.PvE, Name = "Use Bioblaster while moving")]
    private bool BioMove { get; set; } = true;

    [RotationConfig(CombatType.PvE, Name = "Only use Wildfire on Boss targets")]
    private bool WildfireBoss { get; set; } = false;

    [RotationConfig(CombatType.PvE, Name = "Restrict mitigations to not overlap")]
    private bool MitOverlap { get; set; } = false;

    [RotationConfig(CombatType.PvE, Name = "Restrict Tactician to multiple hostile targets only")]
    private bool MultiTact { get; set; } = false;

    [Range(0f, 0.25f, ConfigUnitType.Percent)]
    [RotationConfig(CombatType.PvE, Name = "Action Ahead Override (0 = use global setting)")]
    public float ActionAheadOverride { get; set; } = 0f;

    #endregion

    #region Burst State

    /// <summary>
    /// Framework burst enabled. Uses MergedStatus instead of IsBurst (always true).
    /// </summary>
    private bool CanBurst => MergedStatus.HasFlag(AutoStatus.Burst);

    /// <summary>
    /// True when in or near the 2-minute Wildfire burst window.
    /// </summary>
    private bool InBurstWindow => !WildfirePvE.EnoughLevel
        || WildfirePvE.Cooldown.HasOneCharge
        || HasWildfire
        || WildfirePvE.Cooldown.JustUsedAfter(15);

    /// <summary>
    /// Late-weave window: last ~45% of GCD where a single oGCD can safely fit without clipping.
    /// </summary>
    private static float LateWeaveWindow => WeaponTotal * 0.45f;

    /// <summary>
    /// True when there's enough remaining GCD time to safely weave an oGCD without clipping.
    /// </summary>
    private static bool EnoughWeaveTime => WeaponRemain >= 0.6f;

    /// <summary>
    /// True when in the late-weave window and weaving is safe.
    /// </summary>
    private static bool CanLateWeave => WeaponRemain <= LateWeaveWindow && EnoughWeaveTime;

    #endregion

    #region Queen Step Tracking

    // Battery step cycle for Queen deployment timing.
    // Odd-minute deploys cycle: 60 → 70 → 80 → 60 → ...
    private readonly (byte from, byte to, int step)[] _stepPairs =
    [
        (0, 60, 0),     // Opener: deploy at 60 after Excavator
        (60, 90, 1),    // Build to 90
        (90, 100, 2),   // Even: deploy at 100
        (100, 50, 3),   // After 100 deploy
        (50, 60, 4),    // Odd: deploy at 60
        (60, 100, 5),   // Even: deploy at 100
        (100, 50, 6),   // After 100 deploy
        (50, 70, 7),    // Odd: deploy at 70
        (70, 100, 8),   // Even: deploy at 100
        (100, 50, 9),   // After 100 deploy
        (50, 80, 10),   // Odd: deploy at 80
        (80, 100, 11),  // Even: deploy at 100 (fixed from MCH_Reborn's 70→100)
        (100, 50, 12),  // After 100 deploy
        (50, 60, 13)    // Odd: deploy at 60 (cycle restarts)
    ];

    private int _currentStep;
    private byte _lastTrackedBattery;
    private bool _foundStepPair;

    private void UpdateQueenStep()
    {
        if (_lastTrackedBattery != LastSummonBatteryPower)
        {
            _lastTrackedBattery = LastSummonBatteryPower;
            _currentStep++;
        }
    }

    private void UpdateFoundStepPair()
    {
        if (_currentStep < _stepPairs.Length)
        {
            var (from, to, _) = _stepPairs[_currentStep];
            _foundStepPair = LastSummonBatteryPower == from && Battery == to;
        }
        else
        {
            _foundStepPair = false;
        }
    }

    private void ResetQueenTracking()
    {
        _currentStep = 0;
        _lastTrackedBattery = 0;
        _foundStepPair = false;
    }

    #endregion

    #region UpdateInfo

    protected override void UpdateInfo()
    {
        DataCenter.RotationActionAheadOverride = ActionAheadOverride > 0f ? ActionAheadOverride : null;
    }

    #endregion

    #region Countdown

    protected override IAction? CountDownAction(float remainTime)
    {
        ResetQueenTracking();

        if (remainTime < 5f && ReassemblePvE.CanUse(out var act))
            return act;

        if (CanBurst && OpenerBurstMeds && remainTime <= 1.5f && UseBurstMedicine(out act))
            return act;

        if (remainTime < 0.6f && AirAnchorPvE.EnoughLevel && AirAnchorPvE.CanUse(out act))
            return act;

        return base.CountDownAction(remainTime);
    }

    #endregion

    #region Emergency Ability

    protected override bool EmergencyAbility(IAction nextGCD, out IAction? act)
    {
        if (InCombat)
        {
            UpdateQueenStep();
            UpdateFoundStepPair();
        }

        // Medicine before Wildfire burst window (5s ahead to weave before WF goes out)
        if (CanBurst && WildfirePvE.EnoughLevel
            && WildfirePvE.Cooldown.WillHaveOneCharge(5)
            && !StatusHelper.PlayerHasStatus(true, StatusID.Medicated)
            && UseBurstMedicine(out act))
        {
            return true;
        }

        if (HyperchargePvE.EnoughLevel)
        {
            // Pre-Wildfire level: HC freely
            if (!WildfirePvE.EnoughLevel)
            {
                if (HyperchargePvE.CanUse(out act, skipTTKCheck: true))
                    return true;
            }

            // Pre-FMF level: HC when WF active or battery capped
            if (WildfirePvE.EnoughLevel && !FullMetalFieldPvE.EnoughLevel
                && (HasWildfire || (WildfirePvE.Cooldown.IsCoolingDown && Battery == 100)))
            {
                if (HyperchargePvE.CanUse(out act, skipTTKCheck: true))
                    return true;
            }

            // Level 100+: HC immediately after FMF during Wildfire
            // Sequence: WF (late weave) → FMF (GCD) → HC → 5x Blazing Shot
            if (HasWildfire && IsLastAction(false, FullMetalFieldPvE))
            {
                if (HyperchargePvE.CanUse(out act, skipTTKCheck: true))
                    return true;
            }
        }

        return base.EmergencyAbility(nextGCD, out act);
    }

    #endregion

    #region Defense

    [RotationDesc(ActionID.TacticianPvE, ActionID.DismantlePvE)]
    protected override bool DefenseAreaAbility(IAction nextGCD, out IAction? act)
    {
        if (IsOverheated || HasWildfire || HasFullMetalMachinist)
            return base.DefenseAreaAbility(nextGCD, out act);

        if (!MultiTact || NumberOfAllHostilesInMaxRange > 1)
        {
            if (TacticianPvE.CanUse(out act))
                return true;
        }

        if (!MitOverlap || !StatusHelper.PlayerHasStatus(true, StatusID.Tactician_1951))
        {
            if (DismantlePvE.CanUse(out act))
                return true;
        }

        return base.DefenseAreaAbility(nextGCD, out act);
    }

    #endregion

    #region Attack Ability

    protected override bool AttackAbility(IAction nextGCD, out IAction? act)
    {
        // Hold oGCDs right after WF so FMF can resolve first
        if (FullMetalFieldPvE.EnoughLevel && HasFullMetalMachinist && IsLastAction(false, WildfirePvE))
            return base.AttackAbility(nextGCD, out act);

        // --- Reassemble ---
        // Priority: Chain Saw/Excavator > Air Anchor > Drill. Never on FMF (auto crit/DH).
        bool isReassembleTarget =
            ReassemblePvE.Cooldown.CurrentCharges > 0 && !HasReassembled
            && (nextGCD.IsTheSameTo(true, ChainSawPvE, ExcavatorPvE)
                || nextGCD.IsTheSameTo(false, AirAnchorPvE)
                || (!ChainSawPvE.EnoughLevel && nextGCD.IsTheSameTo(true, DrillPvE))
                || (!DrillPvE.EnoughLevel && nextGCD.IsTheSameTo(true, CleanShotPvE))
                || (!CleanShotPvE.EnoughLevel && nextGCD.IsTheSameTo(false, HotShotPvE))
                || (!ChainSawPvE.EnoughLevel && nextGCD.IsTheSameTo(true, SpreadShotPvE)
                    && ((IBaseAction)nextGCD).Target.AffectedTargets.Length
                        >= (SpreadShotMasteryTrait.EnoughLevel ? 4 : 5)));

        if (isReassembleTarget && ReassemblePvE.CanUse(out act, usedUp: true))
            return true;

        // --- Start Gauss/Ricochet rolling ---
        if (!RicochetPvE.Cooldown.IsCoolingDown)
        {
            if (CheckmatePvE.EnoughLevel ? CheckmatePvE.CanUse(out act) : RicochetPvE.CanUse(out act))
                return true;
        }
        if (!GaussRoundPvE.Cooldown.IsCoolingDown)
        {
            if (DoubleCheckPvE.EnoughLevel ? DoubleCheckPvE.CanUse(out act) : GaussRoundPvE.CanUse(out act))
                return true;
        }

        // --- Barrel Stabilizer: always on CD (grants Hypercharged + FMM) ---
        if (BarrelStabilizerPvE.CanUse(out act))
            return true;

        // --- Wildfire ---
        if (CanBurst && TryUseWildfire(nextGCD, out act))
            return true;

        // --- Queen ---
        if (TryUseQueen(out act, nextGCD))
            return true;

        // --- Filler Hypercharge ---
        bool lowLevelAoeBlock = !AutoCrossbowPvE.EnoughLevel && SpreadShotPvE.CanUse(out _);
        bool holdForWildfire = WildfirePvE.EnoughLevel
            && WildfirePvE.Cooldown.IsCoolingDown
            && WildfirePvE.Cooldown.WillHaveOneCharge(30);

        if (!lowLevelAoeBlock && !HasReassembled && (!holdForWildfire || Heat == 100))
        {
            if (!(LiveComboTime <= 9f && LiveComboTime > 0f) && ToolChargeSoon(out act))
                return true;
        }

        // --- Double Check / Checkmate charge spending ---
        bool spendCharges = (CanBurst && InBurstWindow) || IsOverheated;

        if (!FullMetalFieldPvE.EnoughLevel || !nextGCD.IsTheSameTo(false, FullMetalFieldPvE))
        {
            bool ricoFirst = RicochetPvE.EnoughLevel
                && RicochetPvE.Cooldown.RecastTimeElapsed >= GaussRoundPvE.Cooldown.RecastTimeElapsed;

            if (ricoFirst)
            {
                if (CheckmatePvE.EnoughLevel ? CheckmatePvE.CanUse(out act, usedUp: spendCharges)
                    : RicochetPvE.CanUse(out act, usedUp: spendCharges))
                    return true;
                if (DoubleCheckPvE.EnoughLevel ? DoubleCheckPvE.CanUse(out act, usedUp: spendCharges)
                    : GaussRoundPvE.CanUse(out act, usedUp: spendCharges))
                    return true;
            }
            else
            {
                if (DoubleCheckPvE.EnoughLevel ? DoubleCheckPvE.CanUse(out act, usedUp: spendCharges)
                    : GaussRoundPvE.CanUse(out act, usedUp: spendCharges))
                    return true;
                if (CheckmatePvE.EnoughLevel ? CheckmatePvE.CanUse(out act, usedUp: spendCharges)
                    : RicochetPvE.CanUse(out act, usedUp: spendCharges))
                    return true;
            }
        }

        return base.AttackAbility(nextGCD, out act);
    }

    #endregion

    #region GCD Logic

    protected override bool GeneralGCD(out IAction? act)
    {
        // --- Combo protection: finish combo before it drops (ok to drop during overheat) ---
        if (IsLastComboAction(true, SlugShotPvE)
            && LiveComboTime >= GCDTime(1) && LiveComboTime <= GCDTime(2) && !IsOverheated)
        {
            if (HeatedCleanShotPvE.EnoughLevel && HeatedCleanShotPvE.CanUse(out act))
                return true;
            if (!HeatedCleanShotPvE.EnoughLevel && CleanShotPvE.CanUse(out act))
                return true;
        }

        if (IsLastComboAction(true, SplitShotPvE)
            && LiveComboTime >= GCDTime(1) && LiveComboTime <= GCDTime(2) && !IsOverheated)
        {
            if (HeatedSlugShotPvE.EnoughLevel && HeatedSlugShotPvE.CanUse(out act))
                return true;
            if (!HeatedSlugShotPvE.EnoughLevel && SlugShotPvE.CanUse(out act))
                return true;
        }

        // --- Overheated GCDs ---
        if (AutoCrossbowPvE.CanUse(out act))
            return true;
        if (BlazingShotPvE.EnoughLevel && BlazingShotPvE.CanUse(out act))
            return true;
        if (!BlazingShotPvE.EnoughLevel && HeatBlastPvE.CanUse(out act))
            return true;

        // Let Hypercharge resolve before other GCDs
        if (IsLastAction(false, HyperchargePvE) && HeatBlastPvE.EnoughLevel)
            return base.GeneralGCD(out act);

        // --- Bioblaster (AoE Drill) ---
        if ((BioMove || !IsMoving) && BioblasterPvE.CanUse(out act, usedUp: true))
            return true;

        // --- Tools: Air Anchor → Drill → Chain Saw → Excavator ---
        if (HotShotMasteryTrait.EnoughLevel && AirAnchorPvE.CanUse(out act))
            return true;

        if (DrillPvE.CanUse(out act, usedUp: false))
            return true;

        if (!HotShotMasteryTrait.EnoughLevel && HotShotPvE.CanUse(out act))
            return true;

        if (ChainSawPvE.CanUse(out act))
            return true;

        if (ExcavatorPvE.CanUse(out act))
            return true;

        // --- Full Metal Field ---
        // After tools spent, Excavator done, not right after Chain Saw
        if (!AirAnchorPvE.CanUse(out _) && !ChainSawPvE.CanUse(out _)
            && !ExcavatorPvE.CanUse(out _) && !HasExcavatorReady
            && !IsLastGCD(false, ChainSawPvE)
            && DrillPvE.Cooldown.CurrentCharges < 2
            && (!WildfirePvE.Cooldown.IsCoolingDown || IsLastAction(false, WildfirePvE)))
        {
            if (FullMetalFieldPvE.CanUse(out act))
                return true;
        }

        // Second Drill charge
        if (DrillPvE.CanUse(out act, usedUp: true))
            return true;

        // Expiry protection
        if (StatusHelper.PlayerWillStatusEnd(3, true, StatusID.FullMetalMachinist))
        {
            if (FullMetalFieldPvE.CanUse(out act))
                return true;
        }
        if (StatusHelper.PlayerWillStatusEnd(3, true, StatusID.ExcavatorReady))
        {
            if (ExcavatorPvE.CanUse(out act))
                return true;
        }

        // --- AoE filler ---
        if (!IsOverheated)
        {
            if (ScattergunPvE.EnoughLevel && ScattergunPvE.CanUse(out act))
                return true;
            if (!ScattergunPvE.EnoughLevel && SpreadShotPvE.CanUse(out act))
                return true;
        }

        // --- ST combo (1-2-3) ---
        if (HeatedCleanShotPvE.EnoughLevel && HeatedCleanShotPvE.CanUse(out act))
            return true;
        if (!HeatedCleanShotPvE.EnoughLevel && CleanShotPvE.CanUse(out act))
            return true;

        if (HeatedSlugShotPvE.EnoughLevel && HeatedSlugShotPvE.CanUse(out act))
            return true;
        if (!HeatedSlugShotPvE.EnoughLevel && SlugShotPvE.CanUse(out act))
            return true;

        if (HeatedSplitShotPvE.EnoughLevel && HeatedSplitShotPvE.CanUse(out act))
            return true;
        if (!HeatedSplitShotPvE.EnoughLevel && SplitShotPvE.CanUse(out act))
            return true;

        return base.GeneralGCD(out act);
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// 8-second tool rule: returns true (with HC action) if tools are clear.
    /// Returns false if any tool comes off CD within 8 seconds.
    /// </summary>
    private bool ToolChargeSoon(out IAction? act)
    {
        const float REST_TIME = 8f;

        if (!SpreadShotPvE.CanUse(out _)
            && ((AirAnchorPvE.EnoughLevel && AirAnchorPvE.Cooldown.WillHaveOneCharge(REST_TIME))
                || (!AirAnchorPvE.EnoughLevel && HotShotPvE.EnoughLevel
                    && HotShotPvE.Cooldown.WillHaveOneCharge(REST_TIME))
                || (DrillPvE.EnoughLevel
                    && DrillPvE.Cooldown.WillHaveXCharges(DrillPvE.Cooldown.MaxCharges, REST_TIME))
                || (ChainSawPvE.EnoughLevel && ChainSawPvE.Cooldown.WillHaveOneCharge(REST_TIME))))
        {
            act = null;
            return false;
        }

        return HyperchargePvE.CanUse(out act, skipTTKCheck: true);
    }

    /// <summary>
    /// Wildfire timing. Level 100+: late weave before FMF for 6 GCDs under WF.
    /// Pre-100: fire when HC is available.
    /// </summary>
    private bool TryUseWildfire(IAction nextGCD, out IAction? act)
    {
        act = null;

        if (WildfireBoss && !(WildfirePvE.Target.Target?.IsBossFromIcon() ?? false))
            return false;

        if (FullMetalFieldPvE.EnoughLevel)
        {
            // Level 100+: late-weave WF right before FMF for maximum GCDs under Wildfire
            if ((Heat >= 50 || HasHypercharged)
                && CanLateWeave
                && nextGCD.IsTheSameTo(false, FullMetalFieldPvE))
            {
                return WildfirePvE.CanUse(out act);
            }
        }
        else
        {
            // Pre-100: late-weave WF when HC is ready and tools are clear
            bool lowLevelAoeBlock = !AutoCrossbowPvE.EnoughLevel && SpreadShotPvE.CanUse(out _);
            if ((Heat >= 50 || HasHypercharged) && ToolChargeSoon(out _) && !lowLevelAoeBlock
                && CanLateWeave)
            {
                return WildfirePvE.CanUse(out act);
            }
        }

        return false;
    }

    /// <summary>
    /// Queen deployment with step tracking, opener logic, and overcap protection.
    /// </summary>
    private bool TryUseQueen(out IAction? act, IAction nextGCD)
    {
        act = null;
        if (!InCombat || IsRobotActive)
            return false;

        // Opener: deploy at 60 after Excavator
        if (Battery == 60 && IsLastGCD(false, ExcavatorPvE) && CombatTime < 15)
        {
            if (AutomatonQueenPvE.EnoughLevel && AutomatonQueenPvE.CanUse(out act, skipTTKCheck: true))
                return true;
            if (!AutomatonQueenPvE.EnoughLevel && RookAutoturretPvE.CanUse(out act, skipTTKCheck: true))
                return true;
        }

        // Step-based deployment
        if (_foundStepPair)
        {
            if (AutomatonQueenPvE.EnoughLevel && AutomatonQueenPvE.CanUse(out act, skipTTKCheck: true))
                return true;
            if (!AutomatonQueenPvE.EnoughLevel && RookAutoturretPvE.CanUse(out act, skipTTKCheck: true))
                return true;
        }

        // Overcap protection
        if ((nextGCD.IsTheSameTo(false, CleanShotPvE, HeatedCleanShotPvE) && Battery > 90)
            || (nextGCD.IsTheSameTo(false, HotShotPvE, AirAnchorPvE, ChainSawPvE, ExcavatorPvE)
                && Battery > 80))
        {
            if (AutomatonQueenPvE.EnoughLevel && AutomatonQueenPvE.CanUse(out act, skipTTKCheck: true))
                return true;
            if (!AutomatonQueenPvE.EnoughLevel && RookAutoturretPvE.CanUse(out act, skipTTKCheck: true))
                return true;
        }

        // Fallback: deploy at 100 if step tracking drifted
        if (Battery == 100)
        {
            if (AutomatonQueenPvE.EnoughLevel && AutomatonQueenPvE.CanUse(out act, skipTTKCheck: true))
                return true;
            if (!AutomatonQueenPvE.EnoughLevel && RookAutoturretPvE.CanUse(out act, skipTTKCheck: true))
                return true;
        }

        return false;
    }

    #endregion

    #region Debug Display

    public override void DisplayRotationStatus()
    {
        ImGui.Text($"--- Burst ---");
        ImGui.Text($"CanBurst: {CanBurst}");
        ImGui.Text($"InBurstWindow: {InBurstWindow}");
        ImGui.Text($"HasWildfire: {HasWildfire}");
        ImGui.Text($"WildfireCD: {(WildfirePvE.Cooldown.IsCoolingDown ? $"{WildfirePvE.Cooldown.RecastTimeRemain:F1}s" : "Ready")}");
        ImGui.Text($"Medicated: {StatusHelper.PlayerHasStatus(true, StatusID.Medicated)}");
        ImGui.Text($"--- Gauge ---");
        ImGui.Text($"Heat: {Heat} | Battery: {Battery}");
        ImGui.Text($"IsOverheated: {IsOverheated} | Stacks: {OverheatedStacks}");
        ImGui.Text($"IsRobotActive: {IsRobotActive}");
        ImGui.Text($"--- Queen ---");
        ImGui.Text($"Step: {_currentStep}/{_stepPairs.Length - 1}");
        ImGui.Text($"StepPairFound: {_foundStepPair}");
        ImGui.Text($"LastBattery: {_lastTrackedBattery}");
        ImGui.Text($"--- Weave ---");
        ImGui.Text($"WeaponRemain: {WeaponRemain:F2}s");
        ImGui.Text($"CanLateWeave: {CanLateWeave}");
        ImGui.Text($"EnoughWeaveTime: {EnoughWeaveTime}");
        ImGui.Text($"--- Buffs ---");
        ImGui.Text($"Reassembled: {HasReassembled}");
        ImGui.Text($"FMM: {HasFullMetalMachinist} | Excavator: {HasExcavatorReady}");
        ImGui.Text($"Hypercharged: {HasHypercharged}");
    }

    #endregion
}
