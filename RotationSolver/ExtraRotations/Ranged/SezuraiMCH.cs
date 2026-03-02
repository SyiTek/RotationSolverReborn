namespace RotationSolver.ExtraRotations.Ranged;

[Rotation("SezuraiMCH", CombatType.PvE, GameVersion = "7.41",
    Description = "Balance-aligned MCH with double Hypercharge burst, proper FMF timing, and reliable Queen deployment.")]
[SourceCode(Path = "main/ExtraRotations/Ranged/SezuraiMCH.cs")]
[ExtraRotation]
public sealed class SezuraiMCH : MachinistRotation
{
    #region Config Options

    [Range(0f, 0.25f, ConfigUnitType.Percent)]
    [RotationConfig(CombatType.PvE, Name = "Action Ahead Override (0 = use global setting)")]
    public float ActionAheadOverride { get; set; } = 0f;

    [RotationConfig(CombatType.PvE, Name = "Auto Pot Usage (Gemdraught before Wildfire burst + pre-pull)")]
    public bool BurstMed { get; set; } = true;

    [RotationConfig(CombatType.PvE, Name = "Only use Wildfire on Boss targets")]
    public bool WildfireBoss { get; set; } = false;

    [RotationConfig(CombatType.PvE, Name = "Use Bioblaster while moving (AoE DoT)")]
    public bool BioMove { get; set; } = true;

    [RotationConfig(CombatType.PvE, Name = "Restrict Tactician to multiple hostile targets only")]
    public bool MultiTact { get; set; } = false;

    #endregion

    #region Burst State

    /// <summary>
    /// Framework burst enabled.
    /// </summary>
    private bool CanBurst => MergedStatus.HasFlag(AutoStatus.Burst);

    /// <summary>
    /// True during or approaching the 2-minute Wildfire burst window.
    /// </summary>
    private bool InBurstWindow => !WildfirePvE.EnoughLevel
        || WildfirePvE.Cooldown.HasOneCharge
        || HasWildfire
        || WildfirePvE.Cooldown.JustUsedAfter(15);

    /// <summary>
    /// True when Wildfire is coming soon (within 15s) and we should save Heat.
    /// </summary>
    private bool IsPreBurst => WildfirePvE.EnoughLevel
        && WildfirePvE.Cooldown.IsCoolingDown
        && !WildfirePvE.Cooldown.HasOneCharge
        && WildfirePvE.Cooldown.RecastTimeRemain <= 15;

    #endregion

    #region Weave Helpers

    private static float LateWeaveWindow => WeaponTotal * 0.45f;
    private static bool EnoughWeaveTime => WeaponRemain >= 0.6f;
    private static bool CanLateWeave => WeaponRemain <= LateWeaveWindow && EnoughWeaveTime;

    #endregion

    #region UpdateInfo

    protected override void UpdateInfo()
    {
        DataCenter.RotationActionAheadOverride = ActionAheadOverride > 0f ? ActionAheadOverride : null;
    }

    #endregion

    #region Countdown & Opener
    // === MCH OPENER (7.4 Balance) ===
    // Pre-pull: Reassemble(-5s) → Pot(-2s) → Air Anchor precast(-0.6s)
    // GCD1: Air Anchor (Reassembled, highest potency tool) → Gauss Round (weave) → Ricochet (weave)
    // GCD2: Drill → Barrel Stabilizer (weave)
    // GCD3: Chain Saw → GCD4: Excavator (follow-up)
    // GCD5: Full Metal Field → Wildfire (late weave)
    // → Hypercharge → 5x Blazing Shot → Double Check + Checkmate weaves
    // → Automaton Queen deployment after HC
    //
    // === EVEN BURST (120s) ===
    // Wildfire + Full Metal Field + Hypercharge (WF+FMF+HC burst)
    // Reassemble on Air Anchor/Drill for guaranteed crit+DH
    // Queen at 100 Battery for maximum Pile Bunker under raid buffs
    //
    // === ODD BURST (60s) ===
    // Barrel Stabilizer + Reassemble + Drill/Air Anchor
    // Single Hypercharge, save Wildfire + Chain Saw for even
    // Queen at 50+ Battery to avoid overcap
    //
    // === FILLER / SUSTAIN ===
    // Combo: Heated Split Shot → Heated Slug Shot → Heated Clean Shot (Battery gen)
    // Tool priority: Air Anchor > Drill > Chain Saw (use on CD, don't drift)
    // Reassemble: always pair with Air Anchor or Drill for crit+DH guarantee
    // Heat: spend at 50+ on Hypercharge, but pool for Wildfire windows
    // Battery: deploy Queen at 50+ to avoid overcap, hold for 100 in even windows
    // Gauss Round + Ricochet: spend charges to avoid overcap (3 max each)

    protected override IAction? CountDownAction(float remainTime)
    {
        // Reassemble at ~5s prepull (starts CD rolling for an extra use over the fight)
        if (remainTime < 5f && ReassemblePvE.CanUse(out var act))
            return act;

        // Medicine at ~2s
        if (BurstMed && remainTime <= 2f && remainTime > 1f && UseBurstMedicine(out act))
            return act;

        // Air Anchor just before pull (first GCD of opener)
        if (remainTime < 0.6f && AirAnchorPvE.EnoughLevel && AirAnchorPvE.CanUse(out act))
            return act;

        return base.CountDownAction(remainTime);
    }

    #endregion

    #region Emergency Ability

    protected override bool EmergencyAbility(IAction nextGCD, out IAction? act)
    {
        act = null;

        if (!InCombat)
            return base.EmergencyAbility(nextGCD, out act);

        // === MEDICINE ===
        // 5s before Wildfire becomes available
        if (BurstMed && WildfirePvE.EnoughLevel
            && WildfirePvE.Cooldown.WillHaveOneCharge(5)
            && !StatusHelper.PlayerHasStatus(true, StatusID.Medicated)
            && UseBurstMedicine(out act))
        {
            return true;
        }
        // Also pot during active Wildfire if not yet medicated
        if (BurstMed && HasWildfire && UseBurstMedicine(out act))
            return true;

        // === HYPERCHARGE ===
        if (HyperchargePvE.EnoughLevel)
        {
            // Pre-Wildfire level: use HC freely
            if (!WildfirePvE.EnoughLevel && HyperchargePvE.CanUse(out act, skipTTKCheck: true))
                return true;

            // Level 100+: First HC of double-HC burst (right after FMF during Wildfire)
            // Sequence: WF (late weave) -> FMF (GCD) -> HC (this) -> 5x Blazing Shot
            if (FullMetalFieldPvE.EnoughLevel && HasWildfire && IsLastAction(false, FullMetalFieldPvE))
            {
                if (HyperchargePvE.CanUse(out act, skipTTKCheck: true))
                    return true;
            }

            // Second HC during Wildfire: after first 5 Blazing Shots finish
            // Fires when: WF active, not currently overheated, have Heat, not the first HC slot
            if (HasWildfire && !IsOverheated && (Heat >= 50 || HasHypercharged)
                && !IsLastAction(false, FullMetalFieldPvE))
            {
                if (HyperchargePvE.CanUse(out act, skipTTKCheck: true))
                    return true;
            }

            // Pre-FMF level: HC during Wildfire
            if (WildfirePvE.EnoughLevel && !FullMetalFieldPvE.EnoughLevel && HasWildfire)
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
        // BMR-aware: Tactician + Dismantle when raidwide imminent (override burst-skip)
        bool rwSoon = BmrActive && BmrRaidwideIn is > 0 and <= 5f;

        if (rwSoon)
        {
            if (TacticianPvE.CanUse(out act))
                return true;
            if (DismantlePvE.CanUse(out act))
                return true;
            return base.DefenseAreaAbility(nextGCD, out act);
        }

        // Non-BMR: skip during burst
        if (IsOverheated || HasWildfire || HasFullMetalMachinist)
            return base.DefenseAreaAbility(nextGCD, out act);

        if (!MultiTact || NumberOfAllHostilesInMaxRange > 1)
        {
            if (TacticianPvE.CanUse(out act))
                return true;
        }

        if (DismantlePvE.CanUse(out act))
            return true;

        return base.DefenseAreaAbility(nextGCD, out act);
    }

    #endregion

    #region Attack Ability

    protected override bool AttackAbility(IAction nextGCD, out IAction? act)
    {
        act = null;

        // Hold oGCDs right after WF so FMF GCD can resolve first
        if (FullMetalFieldPvE.EnoughLevel && HasFullMetalMachinist && IsLastAction(false, WildfirePvE))
            return base.AttackAbility(nextGCD, out act);

        // === REASSEMBLE ===
        // On any tool GCD. NEVER on FMF (auto crit/DH) or Blazing Shot.
        // All tools have identical potency so priority is: whatever lands in raid buffs.
        if (!HasReassembled && ReassemblePvE.Cooldown.CurrentCharges > 0)
        {
            bool isToolNext = nextGCD.IsTheSameTo(true, ChainSawPvE, ExcavatorPvE)
                || nextGCD.IsTheSameTo(false, AirAnchorPvE)
                || nextGCD.IsTheSameTo(true, DrillPvE);

            // Low level fallback
            if (!ChainSawPvE.EnoughLevel && !DrillPvE.EnoughLevel)
                isToolNext = nextGCD.IsTheSameTo(false, HotShotPvE);

            if (isToolNext && ReassemblePvE.CanUse(out act, usedUp: true))
                return true;
        }

        // === BARREL STABILIZER (120s, grants Hypercharged + FMM) ===
        // Always on CD. Base ActionCheck: InCombat && !HasFullMetalMachinist.
        if (BarrelStabilizerPvE.CanUse(out act))
            return true;

        // === WILDFIRE ===
        if (CanBurst && TryUseWildfire(nextGCD, out act))
            return true;

        // === Start Gauss Round / Ricochet rolling (get CDs ticking) ===
        if (!GaussRoundPvE.Cooldown.IsCoolingDown)
        {
            if (DoubleCheckPvE.EnoughLevel ? DoubleCheckPvE.CanUse(out act) : GaussRoundPvE.CanUse(out act))
                return true;
        }
        if (!RicochetPvE.Cooldown.IsCoolingDown)
        {
            if (CheckmatePvE.EnoughLevel ? CheckmatePvE.CanUse(out act) : RicochetPvE.CanUse(out act))
                return true;
        }

        // === QUEEN ===
        if (TryUseQueen(out act, nextGCD))
            return true;

        // === FILLER HYPERCHARGE ===
        // Use between bursts when: 8-second tool rule passes, not saving for WF
        if (!IsOverheated && !HasReassembled)
        {
            // Hold HC for 15s before Wildfire (need Heat >= 50 for burst), but spend at 100 to prevent overcap
            bool holdForBurst = IsPreBurst && Heat < 100;

            // Don't enter HC if combo would expire during the 7.5s window
            bool comboSafe = LiveComboTime <= 0f || LiveComboTime > 9f;

            // Don't HC in AoE without Auto Crossbow
            bool aoeBlock = !AutoCrossbowPvE.EnoughLevel && SpreadShotPvE.CanUse(out _);

            if (!holdForBurst && comboSafe && !aoeBlock && ToolChargeSoon(out act))
                return true;
        }

        // === DOUBLE CHECK / CHECKMATE charge spending ===
        bool spendCharges = InBurstWindow || IsOverheated;

        // Don't weave charges right before FMF (need oGCD slot for WF)
        if (!HasFullMetalMachinist || !nextGCD.IsTheSameTo(false, FullMetalFieldPvE))
        {
            // Alternate based on which CD has been rolling longer
            bool gaussFirst = !RicochetPvE.EnoughLevel
                || GaussRoundPvE.Cooldown.RecastTimeElapsed >= RicochetPvE.Cooldown.RecastTimeElapsed;

            if (gaussFirst)
            {
                if (DoubleCheckPvE.EnoughLevel ? DoubleCheckPvE.CanUse(out act, usedUp: spendCharges)
                    : GaussRoundPvE.CanUse(out act, usedUp: spendCharges))
                    return true;
                if (CheckmatePvE.EnoughLevel ? CheckmatePvE.CanUse(out act, usedUp: spendCharges)
                    : RicochetPvE.CanUse(out act, usedUp: spendCharges))
                    return true;
            }
            else
            {
                if (CheckmatePvE.EnoughLevel ? CheckmatePvE.CanUse(out act, usedUp: spendCharges)
                    : RicochetPvE.CanUse(out act, usedUp: spendCharges))
                    return true;
                if (DoubleCheckPvE.EnoughLevel ? DoubleCheckPvE.CanUse(out act, usedUp: spendCharges)
                    : GaussRoundPvE.CanUse(out act, usedUp: spendCharges))
                    return true;
            }
        }

        return base.AttackAbility(nextGCD, out act);
    }

    #endregion

    #region GCD Logic

    protected override bool GeneralGCD(out IAction? act)
    {
        // === OVERHEATED GCDs (highest priority during overheat) ===
        if (AutoCrossbowPvE.CanUse(out act))
            return true;
        if (BlazingShotPvE.EnoughLevel && BlazingShotPvE.CanUse(out act))
            return true;
        if (!BlazingShotPvE.EnoughLevel && HeatBlastPvE.CanUse(out act))
            return true;

        // Let Hypercharge status resolve before other GCDs
        if (IsLastAction(false, HyperchargePvE) && HeatBlastPvE.EnoughLevel)
            return base.GeneralGCD(out act);

        // === COMBO PROTECTION: finish combo before it expires ===
        if (!IsOverheated)
        {
            if (IsLastComboAction(true, SlugShotPvE)
                && LiveComboTime > 0f && LiveComboTime <= GCDTime(2))
            {
                if (HeatedCleanShotPvE.EnoughLevel && HeatedCleanShotPvE.CanUse(out act))
                    return true;
                if (!HeatedCleanShotPvE.EnoughLevel && CleanShotPvE.CanUse(out act))
                    return true;
            }

            if (IsLastComboAction(true, SplitShotPvE)
                && LiveComboTime > 0f && LiveComboTime <= GCDTime(2))
            {
                if (HeatedSlugShotPvE.EnoughLevel && HeatedSlugShotPvE.CanUse(out act))
                    return true;
                if (!HeatedSlugShotPvE.EnoughLevel && SlugShotPvE.CanUse(out act))
                    return true;
            }
        }

        // === BIOBLASTER (AoE Drill, shares charges with Drill) ===
        if ((BioMove || !IsMoving) && BioblasterPvE.CanUse(out act, usedUp: true))
            return true;

        // === TOOLS: Air Anchor > Drill > Chain Saw > Excavator ===
        // Air Anchor first: grants 20 Battery, critical for Queen timing
        if (HotShotMasteryTrait.EnoughLevel && AirAnchorPvE.CanUse(out act))
            return true;
        if (!HotShotMasteryTrait.EnoughLevel && HotShotPvE.CanUse(out act))
            return true;

        // Drill: 2 charges, first charge
        if (DrillPvE.CanUse(out act, usedUp: false))
            return true;

        // Chain Saw -> Excavator (Excavator is the follow-up, grants 20 Battery)
        if (ChainSawPvE.CanUse(out act))
            return true;
        if (ExcavatorPvE.CanUse(out act))
            return true;

        // === FULL METAL FIELD (auto crit/DH, ~900 potency) ===
        // Use after all tools are spent. WF alignment happens naturally through TryUseWildfire
        // checking nextGCD == FMF, so WF late-weaves before FMF fires.
        if (HasFullMetalMachinist
            && !IsLastGCD(false, ChainSawPvE)     // Excavator should come first
            && !HasExcavatorReady                   // Use Excavator before FMF
            && !AirAnchorPvE.Cooldown.HasOneCharge  // Use AA before FMF
            && !ChainSawPvE.Cooldown.HasOneCharge)  // Use CS before FMF
        {
            if (FullMetalFieldPvE.CanUse(out act))
                return true;
        }

        // FMF expiry protection: use before buff falls off regardless
        if (HasFullMetalMachinist && StatusHelper.PlayerWillStatusEnd(5, true, StatusID.FullMetalMachinist))
        {
            if (FullMetalFieldPvE.CanUse(out act))
                return true;
        }

        // Second Drill charge (prevent overcap)
        if (DrillPvE.CanUse(out act, usedUp: true))
            return true;

        // Excavator expiry protection
        if (HasExcavatorReady && StatusHelper.PlayerWillStatusEnd(5, true, StatusID.ExcavatorReady))
        {
            if (ExcavatorPvE.CanUse(out act))
                return true;
        }

        // === AOE FILLER ===
        if (!IsOverheated)
        {
            if (ScattergunPvE.EnoughLevel && ScattergunPvE.CanUse(out act))
                return true;
            if (!ScattergunPvE.EnoughLevel && SpreadShotPvE.CanUse(out act))
                return true;
        }

        // === ST COMBO (1-2-3) ===
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
    /// 8-second tool rule: returns true (with HC action) if no tool comes off CD within 8 seconds.
    /// A full Hypercharge window (5x Blazing Shot at 1.5s each) takes 7.5s. If a tool comes
    /// off cooldown during HC, it drifts — the #1 DPS loss for MCH.
    /// </summary>
    private bool ToolChargeSoon(out IAction? act)
    {
        const float REST_TIME = 8f;

        bool isAoE = SpreadShotPvE.CanUse(out _);

        if (!isAoE)
        {
            if ((AirAnchorPvE.EnoughLevel && AirAnchorPvE.Cooldown.WillHaveOneCharge(REST_TIME))
                || (!AirAnchorPvE.EnoughLevel && HotShotPvE.EnoughLevel
                    && HotShotPvE.Cooldown.WillHaveOneCharge(REST_TIME))
                || (DrillPvE.EnoughLevel
                    && DrillPvE.Cooldown.WillHaveXCharges(DrillPvE.Cooldown.MaxCharges, REST_TIME))
                || (ChainSawPvE.EnoughLevel && ChainSawPvE.Cooldown.WillHaveOneCharge(REST_TIME)))
            {
                act = null;
                return false;
            }
        }

        return HyperchargePvE.CanUse(out act, skipTTKCheck: true);
    }

    /// <summary>
    /// Wildfire timing. Level 100+: late-weave WF when next GCD is FMF.
    /// This creates the burst sequence: WF (oGCD) -> FMF (GCD) -> HC -> 5x Blazing -> HC -> 5x Blazing.
    /// Pre-100: late-weave WF when HC is ready and tools are clear.
    /// </summary>
    private bool TryUseWildfire(IAction nextGCD, out IAction? act)
    {
        act = null;

        if (WildfireBoss && !(WildfirePvE.Target.Target?.IsBossFromIcon() ?? false))
            return false;

        if (FullMetalFieldPvE.EnoughLevel)
        {
            // Level 100+: WF late-weave when next GCD is FMF
            if ((Heat >= 50 || HasHypercharged)
                && CanLateWeave
                && nextGCD.IsTheSameTo(false, FullMetalFieldPvE))
            {
                return WildfirePvE.CanUse(out act);
            }

            // Fallback: if FMM buff is expiring, fire WF when we have Heat
            if (HasFullMetalMachinist
                && StatusHelper.PlayerWillStatusEnd(8, true, StatusID.FullMetalMachinist)
                && (Heat >= 50 || HasHypercharged)
                && CanLateWeave)
            {
                return WildfirePvE.CanUse(out act);
            }
        }
        else
        {
            // Pre-100: WF when HC is ready and tools are clear
            bool aoeBlock = !AutoCrossbowPvE.EnoughLevel && SpreadShotPvE.CanUse(out _);
            if ((Heat >= 50 || HasHypercharged) && ToolChargeSoon(out _) && !aoeBlock
                && CanLateWeave)
            {
                return WildfirePvE.CanUse(out act);
            }
        }

        return false;
    }

    /// <summary>
    /// Queen deployment based on battery thresholds and burst alignment.
    /// Queen mirrors raid buffs in real time, so align with party buffs when possible.
    /// Balance guide: opener at 60, even-minutes at 100, odd-minutes at 50-90.
    /// </summary>
    private bool TryUseQueen(out IAction? act, IAction nextGCD)
    {
        act = null;
        if (!InCombat || IsRobotActive)
            return false;

        // Opener: deploy at 60 after Excavator
        if (Battery >= 50 && CombatTime < 20 && IsLastGCD(false, ExcavatorPvE))
            return SummonQueen(out act);

        // Even-minute burst: deploy at 100 during burst window
        if (Battery == 100 && InBurstWindow)
            return SummonQueen(out act);

        // Pre-burst alignment: deploy at 80+ when Wildfire is coming within 15s
        // Gets Queen active during the raid buff window
        if (Battery >= 80 && IsPreBurst)
            return SummonQueen(out act);

        // Overcap protection: deploy before battery-generating GCDs would waste gauge
        if (nextGCD.IsTheSameTo(false, AirAnchorPvE, ChainSawPvE, ExcavatorPvE) && Battery > 80)
            return SummonQueen(out act);
        if (nextGCD.IsTheSameTo(false, CleanShotPvE, HeatedCleanShotPvE) && Battery > 90)
            return SummonQueen(out act);

        // Odd-minute filler: deploy at 50+ when burst is far away (30s+)
        // Prevents long-term battery overcap while saving gauge for even-minute 100 deploys
        if (Battery >= 50 && !IsPreBurst
            && WildfirePvE.EnoughLevel
            && WildfirePvE.Cooldown.IsCoolingDown
            && !WildfirePvE.Cooldown.WillHaveOneCharge(30))
        {
            return SummonQueen(out act);
        }

        // Hard overcap: always deploy at 100
        if (Battery == 100)
            return SummonQueen(out act);

        return false;
    }

    private bool SummonQueen(out IAction? act)
    {
        if (AutomatonQueenPvE.EnoughLevel && AutomatonQueenPvE.CanUse(out act, skipTTKCheck: true))
            return true;
        if (!AutomatonQueenPvE.EnoughLevel && RookAutoturretPvE.CanUse(out act, skipTTKCheck: true))
            return true;
        act = null;
        return false;
    }

    #endregion

    #region Debug Display

    public override void DisplayRotationStatus()
    {
        ImGui.Text("--- Burst ---");
        ImGui.Text($"CanBurst: {CanBurst}");
        ImGui.Text($"InBurstWindow: {InBurstWindow}");
        ImGui.Text($"IsPreBurst: {IsPreBurst}");
        ImGui.Text($"HasWildfire: {HasWildfire}");
        ImGui.Text($"WildfireCD: {(WildfirePvE.Cooldown.IsCoolingDown ? $"{WildfirePvE.Cooldown.RecastTimeRemain:F1}s" : "Ready")}");
        ImGui.Text($"Medicated: {StatusHelper.PlayerHasStatus(true, StatusID.Medicated)}");
        ImGui.Text("--- Gauge ---");
        ImGui.Text($"Heat: {Heat} | Battery: {Battery}");
        ImGui.Text($"IsOverheated: {IsOverheated} | Stacks: {OverheatedStacks}");
        ImGui.Text($"IsRobotActive: {IsRobotActive}");
        ImGui.Text($"LastSummonBattery: {LastSummonBatteryPower}");
        ImGui.Text("--- Weave ---");
        ImGui.Text($"WeaponRemain: {WeaponRemain:F2}s | WeaponTotal: {WeaponTotal:F2}s");
        ImGui.Text($"CanLateWeave: {CanLateWeave}");
        ImGui.Text($"EnoughWeaveTime: {EnoughWeaveTime}");
        ImGui.Text("--- Buffs ---");
        ImGui.Text($"Reassembled: {HasReassembled}");
        ImGui.Text($"FMM: {HasFullMetalMachinist}");
        ImGui.Text($"Excavator: {HasExcavatorReady}");
        ImGui.Text($"Hypercharged: {HasHypercharged}");
        ImGui.Text("--- CDs ---");
        ImGui.Text($"BarrelStab: {(BarrelStabilizerPvE.Cooldown.IsCoolingDown ? $"{BarrelStabilizerPvE.Cooldown.RecastTimeRemain:F1}s" : "Ready")}");
        ImGui.Text($"AA: {(AirAnchorPvE.Cooldown.IsCoolingDown ? $"{AirAnchorPvE.Cooldown.RecastTimeRemain:F1}s" : "Ready")}");
        ImGui.Text($"Drill: {DrillPvE.Cooldown.CurrentCharges}/{DrillPvE.Cooldown.MaxCharges} ({(DrillPvE.Cooldown.IsCoolingDown ? $"{DrillPvE.Cooldown.RecastTimeRemain:F1}s" : "Ready")})");
        ImGui.Text($"CS: {(ChainSawPvE.Cooldown.IsCoolingDown ? $"{ChainSawPvE.Cooldown.RecastTimeRemain:F1}s" : "Ready")}");
        ImGui.Text($"LiveCombo: {(LiveComboTime > 0 ? $"{LiveComboTime:F1}s" : "None")}");
        ImGui.Text("--- BMR Timeline ---");
        ImGui.Text($"Active: {BmrActive}{(BmrActive ? $" ({DataCenter.BmrActiveModuleName})" : "")}");
        if (BmrActive)
        {
            ImGui.Text($"Raidwide In: {(BmrRaidwideIn < 9999f ? $"{BmrRaidwideIn:F1}s" : "None")}");
            ImGui.Text($"Knockback In: {(BmrKnockbackIn < 9999f ? $"{BmrKnockbackIn:F1}s" : "None")}");
            ImGui.Text($"Downtime In: {(BmrDowntimeIn < 9999f ? $"{BmrDowntimeIn:F1}s" : "None")}");
        }
    }

    #endregion
}
