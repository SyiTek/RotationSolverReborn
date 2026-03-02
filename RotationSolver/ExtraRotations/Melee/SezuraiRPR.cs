namespace RotationSolver.ExtraRotations.Melee;

[Rotation("SezuraiRPR", CombatType.PvE, GameVersion = "7.41", Description = "Balance-aligned RPR with double Enshroud burst, Arcane Circle alignment, gauge optimization, and BMR timeline integration.")]
[SourceCode(Path = "main/ExtraRotations/Melee/SezuraiRPR.cs")]
[ExtraRotation]
public sealed class SezuraiRPR : ReaperRotation
{
    #region Config Options

    [Range(0f, 0.25f, ConfigUnitType.Percent)]
    [RotationConfig(CombatType.PvE, Name = "Action Ahead Override (0 = use global setting)")]
    public float ActionAheadOverride { get; set; } = 0f;

    [RotationConfig(CombatType.PvE, Name = "Auto Pot Usage (Gemdraught during Arcane Circle + pre-pull in countdown)")]
    public bool BurstMed { get; set; } = true;

    [RotationConfig(CombatType.PvE, Name = "Pool Shroud for Arcane Circle windows")]
    public bool EnshroudPooling { get; set; } = true;

    [RotationConfig(CombatType.PvE, Name = "Use custom timing to refresh Death's Design")]
    public bool UseCustomDDTiming { get; set; } = false;

    [Range(3, 30, ConfigUnitType.None, 1)]
    [RotationConfig(CombatType.PvE, Name = "Refresh Death's Design with this many seconds remaining", Parent = nameof(UseCustomDDTiming))]
    public int RefreshDDSeconds { get; set; } = 10;

    [RotationConfig(CombatType.PvE, Name = "Use Harvest Moon as ranged filler when out of melee range")]
    public bool UseHarvestMoonRanged { get; set; } = true;

    [RotationConfig(CombatType.PvE, Name = "Use BMR timeline for proactive mitigation, downtime planning, and burst hold")]
    public bool UseBmr { get; set; } = true;

    #endregion

    #region Burst State

    /// <summary>
    /// Whether the user has burst enabled in the framework.
    /// </summary>
    private bool CanBurst => MergedStatus.HasFlag(AutoStatus.Burst);

    /// <summary>
    /// True when Arcane Circle has a charge ready OR was just used (within 20s).
    /// The broad "burst is available/active" window for 120s alignment.
    /// </summary>
    private bool InBurstWindow => ArcaneCirclePvE.EnoughLevel
        && (ArcaneCirclePvE.Cooldown.HasOneCharge || ArcaneCirclePvE.Cooldown.JustUsedAfter(20));

    /// <summary>
    /// True during the active burst sequence: Arcane Circle was just pressed,
    /// or we have Enshrouded / Ideal Host / Immortal Sacrifice to spend.
    /// </summary>
    private bool InActiveBurst => ArcaneCirclePvE.EnoughLevel
        && (ArcaneCirclePvE.Cooldown.JustUsedAfter(20) || HasArcaneCircle || HasEnshrouded || HasIdealHost);

    /// <summary>
    /// True when we have the resources ready for a full burst window:
    /// Shroud gauge >= 50 (or Ideal Host) and Gluttony available/coming soon.
    /// </summary>
    private bool HasBurstReady => (Shroud >= 50 || HasIdealHost)
        && ArcaneCirclePvE.EnoughLevel
        && ArcaneCirclePvE.Cooldown.WillHaveOneCharge(8);

    /// <summary>
    /// True when we are within 10 seconds of Arcane Circle coming off cooldown.
    /// Used to pool resources and avoid spending Shroud/Gluttony prematurely.
    /// </summary>
    private bool IsPreBurst => ArcaneCirclePvE.EnoughLevel
        && ArcaneCirclePvE.Cooldown.IsCoolingDown
        && !ArcaneCirclePvE.Cooldown.HasOneCharge
        && ArcaneCirclePvE.Cooldown.RecastTimeRemain <= 10;

    /// <summary>
    /// True when Gluttony is in the 60s mini-burst window (not aligned with Arcane Circle).
    /// Gluttony is 60s CD while Arcane Circle is 120s, so every other Gluttony is a mini-window.
    /// </summary>
    private bool InMiniWindow => GluttonyPvE.EnoughLevel
        && GluttonyPvE.Cooldown.HasOneCharge
        && !InBurstWindow;

    #endregion

    #region Weave Helpers

    /// <summary>
    /// Late-weave window: last ~45% of GCD where a single oGCD fits without clipping.
    /// </summary>
    private static float LateWeaveWindow => WeaponTotal * 0.45f;

    /// <summary>
    /// True when there's enough remaining GCD time to safely weave an oGCD.
    /// </summary>
    private static bool EnoughWeaveTime => WeaponRemain >= 0.6f;

    /// <summary>
    /// True when in the late-weave window and weaving is safe.
    /// </summary>
    private static bool CanLateWeave => WeaponRemain <= LateWeaveWindow && EnoughWeaveTime;

    /// <summary>
    /// True when not in a special combo state that would be interrupted by defensives.
    /// RPR equivalent of VPR's NoAbilityReady — blocks defensives during active combos.
    /// </summary>
    private static bool NotInActiveCombo => !HasSoulReaver && !HasEnshrouded && !HasExecutioner;

    #endregion

    #region BMR Helpers

    /// <summary>
    /// True when BMR is active AND the user has enabled our BMR config toggle.
    /// All BMR checks go through this so there's a single kill-switch.
    /// </summary>
    private bool BmrUsable => UseBmr && BmrActive;

    /// <summary>
    /// BMR: downtime is imminent and close enough to worry about (~20s).
    /// Used for Arcane Circle dump decisions and resource management.
    /// </summary>
    private bool BmrDowntimeSoon => BmrUsable && BmrDowntimeIn is > 0 and <= 20f;

    /// <summary>
    /// BMR: downtime is very close (~8s). Dump remaining Soul gauge via Blood Stalk / Gibbet/Gallows.
    /// Also use Harvest Moon and Soulsow prep.
    /// </summary>
    private bool BmrDowntimeImminent => BmrUsable && BmrDowntimeIn is > 0 and <= 8f;

    /// <summary>
    /// BMR: downtime too close for Enshroud (~12s). Full Enshroud window is 5 GCDs + Communio + Perfectio.
    /// At ~2.5s GCD that's roughly 12-13s minimum. Don't enter if we can't finish.
    /// </summary>
    private bool BmrBlockEnshroud => BmrUsable && BmrDowntimeIn is > 0 and <= 12f;

    /// <summary>
    /// BMR: downtime too close to start a new 1-2-3 combo (~5s). 3 GCDs at 2.5s = 7.5s,
    /// but a partial combo is worse than ranged filler or gauge dump.
    /// </summary>
    private bool BmrBlockNewCombo => BmrUsable && BmrDowntimeIn is > 0 and <= 5f;

    /// <summary>
    /// BMR: vulnerability window coming within 30s -- hold Arcane Circle for it.
    /// Per Balance: align burst with party buff / vuln windows for maximum value.
    /// </summary>
    private bool BmrHoldACForVuln => BmrUsable
        && BmrVulnerableIn is > 0 and <= 30f
        && ArcaneCirclePvE.Cooldown.HasOneCharge;

    /// <summary>
    /// BMR: Arcane Circle won't be useful before downtime (too late to burst).
    /// Don't waste AC if downtime < 15s and we can't meaningfully use the entire burst.
    /// The double Enshroud + Gluttony window needs ~12-15s minimum.
    /// </summary>
    private bool BmrBlockACBeforeDowntime => BmrUsable
        && BmrDowntimeIn is > 0 and <= 15f
        && !InActiveBurst;

    /// <summary>
    /// BMR: raidwide damage incoming soon (~3s). Use self-healing proactively.
    /// Bloodbath before raidwide heals on damage dealt, Second Wind is a flat heal.
    /// </summary>
    private bool BmrRaidwideSoon => BmrUsable && BmrRaidwideIn is > 0 and <= 3f;

    /// <summary>
    /// BMR: raidwide damage incoming within Feint/Arcane Crest application window (~5s).
    /// Feint lasts 10s and reduces physical damage by 10% + magic by 5%.
    /// Arcane Crest provides a self shield that grants party regen when broken.
    /// </summary>
    private bool BmrFeintWindow => BmrUsable && BmrRaidwideIn is > 0 and <= 5f;

    /// <summary>
    /// BMR: knockback incoming within Arm's Length application window (~5s).
    /// </summary>
    private bool BmrKnockbackSoon => BmrUsable && BmrKnockbackIn is > 0 and <= 5f;

    #endregion

    #region Countdown & Opener
    // === RPR OPENER (7.4 Balance) ===
    // Pre-pull: Soulsow → Pot(-2s) → Harpe precast(-1.3s)
    // GCD1: Shadow of Death (Death's Design 60s) → GCD2: Soul Slice (50 Soul)
    // → Arcane Circle (late weave, party buff) → GCD3: Soul Slice (100 Soul)
    // → Gluttony (weave, -50 Soul → 2 Executioner stacks)
    // GCD4: Executioner's Gibbet → GCD5: Executioner's Gallows
    // → Plentiful Harvest (weave, after Bloodsown Circle expires)
    // → Enshroud → Void Reaping → Cross Reaping → Lemure's Slice
    // → Void Reaping → Cross Reaping → Lemure's Slice → Communio → Perfectio
    // → Sacrificium (weave during Enshroud)
    //
    // === EVEN BURST (120s) ===
    // Arcane Circle (party buff) + double Enshroud + Gluttony + Plentiful Harvest
    // Both Enshroud chains under Arcane Circle with Sacrificium + Perfectio
    // Use Harvest Moon under raid buffs if available
    //
    // === ODD BURST (60s) ===
    // Gluttony + 1x Enshroud only — Arcane Circle is 120s
    // Save Plentiful Harvest + second Enshroud for even windows
    // Note: 10-minute deadzone where resources don't align perfectly
    //
    // === FILLER / SUSTAIN ===
    // Combo: Slice → Waxing Slice → Infernal Slice (build Soul gauge)
    // Shadow of Death: refresh when <=30s remains (don't clip too early)
    // Soul Slice: use charges to build gauge, don't overcap at 2 charges
    // Spend Soul: Gibbet/Gallows at 50+ to avoid overcap, pool for burst
    // Harvest Moon: ranged GCD for movement if Soulsow was prepped
    // Pool Shroud to 50+ before Arcane Circle windows for double Enshroud

    protected override IAction? CountDownAction(float remainTime)
    {
        // Pre-pull medicine at ~2s (pot animation takes ~1s, lands before first GCD)
        if (BurstMed && remainTime <= 2f && remainTime > 1f && UseBurstMedicine(out var act))
            return act;

        // Harpe at cast time before pull (approx 1.3s cast)
        if (remainTime < HarpePvE.Info.CastTime + CountDownAhead
            && HarpePvE.CanUse(out act))
            return act;

        // Soulsow pre-pull (instant, gives Harvest Moon for later use)
        if (SoulsowPvE.CanUse(out act))
            return act;

        return base.CountDownAction(remainTime);
    }

    #endregion

    #region UpdateInfo

    protected override void UpdateInfo()
    {
        DataCenter.RotationActionAheadOverride = ActionAheadOverride > 0f ? ActionAheadOverride : null;
    }

    #endregion

    #region Status Display

    public override void DisplayRotationStatus()
    {
        ImGui.Text("--- Burst State ---");
        ImGui.Text($"CanBurst: {CanBurst}");
        ImGui.Text($"InBurstWindow: {InBurstWindow}");
        ImGui.Text($"InActiveBurst: {InActiveBurst}");
        ImGui.Text($"HasBurstReady: {HasBurstReady}");
        ImGui.Text($"IsPreBurst: {IsPreBurst}");
        ImGui.Text($"InMiniWindow: {InMiniWindow}");
        ImGui.Text($"ArcaneCircle CD: {(ArcaneCirclePvE.Cooldown.IsCoolingDown ? $"{ArcaneCirclePvE.Cooldown.RecastTimeRemain:F1}s" : "Ready")}");
        ImGui.Text($"Gluttony CD: {(GluttonyPvE.Cooldown.IsCoolingDown ? $"{GluttonyPvE.Cooldown.RecastTimeRemain:F1}s" : "Ready")}");
        ImGui.Text("--- Gauge ---");
        ImGui.Text($"Soul: {Soul}/100");
        ImGui.Text($"Shroud: {Shroud}/100");
        ImGui.Text($"LemureShroud: {LemureShroud}");
        ImGui.Text($"VoidShroud: {VoidShroud}");
        ImGui.Text($"EnshroudedTimeRemaining: {EnshroudedTiemRemaining:F1}ms");
        ImGui.Text("--- Buffs ---");
        ImGui.Text($"HasEnshrouded: {HasEnshrouded}");
        ImGui.Text($"HasSoulReaver: {HasSoulReaver}");
        ImGui.Text($"HasExecutioner: {HasExecutioner}");
        ImGui.Text($"HasIdealHost: {HasIdealHost}");
        ImGui.Text($"HasArcaneCircle: {HasArcaneCircle}");
        ImGui.Text($"HasBloodsownCircleSelf: {HasBloodsownCircleSelf}");
        ImGui.Text($"HasImmortalSacrifice: {HasImmortalSacrifice}");
        ImGui.Text($"HasOblatio: {HasOblatio}");
        ImGui.Text($"HasPerfectioParata: {HasPerfectioParata}");
        ImGui.Text($"HasSoulsow: {HasSoulsow}");
        ImGui.Text("--- Enhancements ---");
        ImGui.Text($"HasEnhancedGibbet: {HasEnhancedGibbet}");
        ImGui.Text($"HasEnhancedGallows: {HasEnhancedGallows}");
        ImGui.Text($"HasEnhancedVoidReaping: {HasEnhancedVoidReaping}");
        ImGui.Text($"HasEnhancedCrossReaping: {HasEnhancedCrossReaping}");
        ImGui.Text($"Medicated: {StatusHelper.PlayerHasStatus(true, StatusID.Medicated)}");
        ImGui.Text("--- Weave ---");
        ImGui.Text($"WeaponRemain: {WeaponRemain:F2}s | WeaponTotal: {WeaponTotal:F2}s");
        ImGui.Text($"CanLateWeave: {CanLateWeave} | EnoughWeaveTime: {EnoughWeaveTime}");
        ImGui.Text($"--- BMR Decisions ---");
        ImGui.Text($"BmrUsable: {BmrUsable} | UseBmr: {UseBmr}");
        ImGui.Text($"BlockEnshroud: {BmrBlockEnshroud} | BlockNewCombo: {BmrBlockNewCombo}");
        ImGui.Text($"DowntimeSoon: {BmrDowntimeSoon} | DowntimeImminent: {BmrDowntimeImminent}");
        ImGui.Text($"HoldACForVuln: {BmrHoldACForVuln} | BlockACBeforeDowntime: {BmrBlockACBeforeDowntime}");
        ImGui.Text($"FeintWindow: {BmrFeintWindow} | RaidwideSoon: {BmrRaidwideSoon}");
        ImGui.Text($"KnockbackSoon: {BmrKnockbackSoon}");
        ImGui.Text($"--- BMR Timeline ---");
        ImGui.Text($"Active: {BmrActive}{(BmrActive ? $" ({DataCenter.BmrActiveModuleName})" : "")}");
        ImGui.Text($"UseBmrTimeline: {Service.Config.UseBmrTimeline}");
        if (BmrActive)
        {
            ImGui.Text($"-- Final Merged Values --");
            ImGui.Text($"Raidwide In: {(BmrRaidwideIn < 9999f ? $"{BmrRaidwideIn:F1}s" : "None")}");
            ImGui.Text($"Tankbuster In: {(BmrTankbusterIn < 9999f ? $"{BmrTankbusterIn:F1}s" : "None")}");
            ImGui.Text($"Knockback In: {(BmrKnockbackIn < 9999f ? $"{BmrKnockbackIn:F1}s" : "None")}");
            ImGui.Text($"Downtime In: {(BmrDowntimeIn < 9999f ? $"{BmrDowntimeIn:F1}s" : "None")}");
            ImGui.Text($"Vulnerable In: {(BmrVulnerableIn < 9999f ? $"{BmrVulnerableIn:F1}s" : "None")}");
            ImGui.Text($"Damage In: {(BmrDamageIn < 9999f ? $"{BmrDamageIn:F1}s" : "None")}");
            ImGui.Text($"-- IPC Func Binding --");
            ImGui.Text($"TL.RW: {(DataCenter.BmrDebugTimelineRwFunc ? "BOUND" : "NULL")} | TL.TB: {(DataCenter.BmrDebugTimelineTbFunc ? "BOUND" : "NULL")}");
            ImGui.Text($"Hints.RW: {(DataCenter.BmrDebugHintsRwFunc ? "BOUND" : "NULL")} | Hints.TB: {(DataCenter.BmrDebugHintsTbFunc ? "BOUND" : "NULL")}");
            ImGui.Text($"-- Raw Timeline (StateMachine) --");
            ImGui.Text($"TL Raidwide: {(DataCenter.BmrDebugTimelineRaidwide < 9999f ? $"{DataCenter.BmrDebugTimelineRaidwide:F1}s" : "MAX")}");
            ImGui.Text($"TL Tankbuster: {(DataCenter.BmrDebugTimelineTankbuster < 9999f ? $"{DataCenter.BmrDebugTimelineTankbuster:F1}s" : "MAX")}");
            ImGui.Text($"-- Raw Hints (PredictedDamage) --");
            ImGui.Text($"Hints RW: {(DataCenter.BmrDebugHintsRaidwide < 9999f ? $"{DataCenter.BmrDebugHintsRaidwide:F1}s" : "MAX")}");
            ImGui.Text($"Hints TB: {(DataCenter.BmrDebugHintsTankbuster < 9999f ? $"{DataCenter.BmrDebugHintsTankbuster:F1}s" : "MAX")}");
            ImGui.Text($"Generic Dmg: {(DataCenter.BmrDebugGenericDamageIn < 9999f ? $"{DataCenter.BmrDebugGenericDamageIn:F1}s type={DataCenter.BmrDebugGenericDamageType}" : "MAX")}");
            ImGui.Text($"-- State Machine Walk --");
            ImGui.TextWrapped($"{DataCenter.BmrDebugTimelineWalk ?? "N/A"}");
        }
    }

    #endregion

    #region Additional oGCD Logic

    [RotationDesc]
    protected override bool EmergencyAbility(IAction nextGCD, out IAction? act)
    {
        // Burst medicine: use when Arcane Circle is active
        if (BurstMed && HasArcaneCircle && UseBurstMedicine(out act))
            return true;

        return base.EmergencyAbility(nextGCD, out act);
    }

    [RotationDesc(ActionID.HellsIngressPvE)]
    protected override bool MoveForwardAbility(IAction nextGCD, out IAction? act)
    {
        if (HellsIngressPvE.CanUse(out act))
            return true;
        return base.MoveForwardAbility(nextGCD, out act);
    }

    [RotationDesc(ActionID.HellsEgressPvE)]
    protected override bool MoveBackAbility(IAction nextGCD, out IAction? act)
    {
        if (HellsEgressPvE.CanUse(out act))
            return true;
        return base.MoveBackAbility(nextGCD, out act);
    }

    [RotationDesc(ActionID.SecondWindPvE)]
    protected override bool HealSingleAbility(IAction nextGCD, out IAction? act)
    {
        // BMR-proactive: pre-heal before raidwide hits so we survive the damage
        // Bloodbath first (heals on damage dealt — active mitigation during the hit)
        if (BmrRaidwideSoon && NotInActiveCombo && BloodbathPvE.CanUse(out act))
            return true;
        if (BmrRaidwideSoon && NotInActiveCombo && SecondWindPvE.CanUse(out act))
            return true;

        // Standard self-healing (framework-triggered or non-BMR)
        if (SecondWindPvE.CanUse(out act))
            return true;
        if (BloodbathPvE.CanUse(out act))
            return true;
        return base.HealSingleAbility(nextGCD, out act);
    }

    [RotationDesc(ActionID.FeintPvE, ActionID.ArcaneCrestPvE)]
    protected sealed override bool DefenseAreaAbility(IAction nextGCD, out IAction? act)
    {
        // Skip during active combos (Soul Reaver / Enshroud / Executioner) to avoid clipping
        if (!NotInActiveCombo)
            return base.DefenseAreaAbility(nextGCD, out act);

        // BMR-aware: Feint + Arcane Crest proactively when raidwide imminent
        // Arcane Crest grants party shield on break -- great for raidwides
        // Skip Feint during active burst to preserve weave slots for damage oGCDs
        if (BmrFeintWindow)
        {
            if (!InActiveBurst && FeintPvE.CanUse(out act))
                return true;
            if (ArcaneCrestPvE.CanUse(out act))
                return true;
            return base.DefenseAreaAbility(nextGCD, out act);
        }

        // Non-BMR fallback: Feint when framework triggers defense
        if (!BmrUsable && FeintPvE.CanUse(out act))
            return true;

        return base.DefenseAreaAbility(nextGCD, out act);
    }

    [RotationDesc(ActionID.ArcaneCrestPvE, ActionID.BloodbathPvE)]
    protected sealed override bool DefenseSingleAbility(IAction nextGCD, out IAction? act)
    {
        if (!NotInActiveCombo)
            return base.DefenseSingleAbility(nextGCD, out act);

        // BMR-aware: Arcane Crest before raidwide for self-shield (breaks into party regen)
        if (BmrRaidwideSoon && ArcaneCrestPvE.CanUse(out act))
            return true;

        // BMR-aware: Bloodbath before raidwide for self-sustain (heals on damage dealt)
        if (BmrRaidwideSoon && BloodbathPvE.CanUse(out act))
            return true;

        // Non-BMR fallback: Arcane Crest when framework triggers personal defense
        if (!BmrUsable && ArcaneCrestPvE.CanUse(out act))
            return true;

        return base.DefenseSingleAbility(nextGCD, out act);
    }

    [RotationDesc(ActionID.ArmsLengthPvE)]
    protected sealed override bool AntiKnockbackAbility(IAction nextGCD, out IAction? act)
    {
        if (ArmsLengthPvE.CanUse(out act))
            return true;
        return base.AntiKnockbackAbility(nextGCD, out act);
    }

    [RotationDesc(ActionID.LegSweepPvE)]
    protected sealed override bool InterruptAbility(IAction nextGCD, out IAction? act)
    {
        if (LegSweepPvE.CanUse(out act))
            return true;
        return base.InterruptAbility(nextGCD, out act);
    }

    #endregion

    #region oGCD Logic

    protected override bool AttackAbility(IAction nextGCD, out IAction? act)
    {
        bool isTargetBoss = CurrentTarget?.IsBossFromTTK() ?? false;
        bool isTargetDying = CurrentTarget?.IsDying() ?? false;

        // === BMR: Dump Arcane Circle before downtime ===
        // If downtime < 20s and AC is available, fire it now to spend resources before boss leaves.
        // But don't fire if vuln window is coming (hold for better timing).
        if (BmrDowntimeSoon && !BmrHoldACForVuln && CanBurst
            && (CurrentTarget?.HasStatus(true, StatusID.DeathsDesign) ?? false)
            && ArcaneCirclePvE.CanUse(out act, skipAoeCheck: true))
        {
            return true;
        }

        // === BMR: Hold Arcane Circle for vulnerability window ===
        // Don't use AC if a vuln window is coming within 30s -- save burst for it.
        // Also don't use if downtime is < 15s and we can't meaningfully complete the burst.
        bool bmrBlockAC = BmrHoldACForVuln || BmrBlockACBeforeDowntime;

        // --- Arcane Circle (120s party buff) ---
        // Use when: burst enabled, Death's Design on target, not too early in combat
        if (!bmrBlockAC
            && CanBurst
            && (CurrentTarget?.HasStatus(true, StatusID.DeathsDesign) ?? false)
            && !CombatElapsedLess(3.5f)
            && ArcaneCirclePvE.CanUse(out act, skipAoeCheck: true))
        {
            return true;
        }

        // --- Enshroud ---
        // Priority 1: Ideal Host (free Enshroud from Plentiful Harvest) - always use immediately
        // BMR: still block even Ideal Host if downtime is too close -- Enshroud is wasted if incomplete
        if (!BmrBlockEnshroud && HasIdealHost && !HasExecutioner && EnshroudPvE.CanUse(out act))
        {
            return true;
        }

        // Priority 2: During Arcane Circle burst or when AC is coming up soon
        // The base class ActionCheck for Enshroud uses Soul >= 50, but Enshroud costs Shroud gauge.
        // We must check Shroud >= 50 ourselves before calling CanUse.
        // BMR-aware: Don't enter Enshroud if downtime < 12s (5 GCDs + Communio takes ~12s)
        if (!BmrBlockEnshroud && !HasExecutioner && Shroud >= 50)
        {
            // During active burst window (Arcane Circle active or just used)
            if (HasArcaneCircle && EnshroudPvE.CanUse(out act))
                return true;

            // Pooling mode: use when Arcane Circle is coming soon, or is active, or when AC is in an odd window
            if (EnshroudPooling)
            {
                // AC coming off cooldown within 8s - enter Enshroud for alignment
                if (ArcaneCirclePvE.Cooldown.WillHaveOneCharge(8) && EnshroudPvE.CanUse(out act))
                    return true;

                // Mid-cycle Enshroud: need 3 Enshrouds per 120s (double burst + 1 mid-cycle)
                // Balance: use when AC is 40-80s away to ensure mid-cycle Enshroud fires
                if (!HasArcaneCircle
                    && ArcaneCirclePvE.Cooldown.WillHaveOneCharge(80)
                    && !ArcaneCirclePvE.Cooldown.WillHaveOneCharge(40)
                    && EnshroudPvE.CanUse(out act))
                    return true;

                // Overcap protection: if Shroud >= 90, use to avoid waste
                if (!HasArcaneCircle && Shroud >= 90 && EnshroudPvE.CanUse(out act))
                    return true;
            }
            else
            {
                // No pooling: use Enshroud freely when available
                if (EnshroudPvE.CanUse(out act))
                    return true;
            }

            // BMR: dump Enshroud before downtime when we have time to finish but AC isn't coming
            // If downtime in 12-20s, we have time to complete one Enshroud phase
            if (BmrDowntimeSoon && !BmrBlockEnshroud && EnshroudPvE.CanUse(out act))
                return true;

            // Boss dying: dump Enshroud
            if (isTargetBoss && isTargetDying && EnshroudPvE.CanUse(out act))
                return true;
        }

        // --- Enshroud oGCDs: Sacrificium and Lemure's Slice/Scythe ---
        // Sacrificium: use once per Enshroud when Oblatio buff is present
        if (SacrificiumPvE.CanUse(out act, skipAoeCheck: true, usedUp: true))
        {
            return true;
        }

        // Lemure's Slice/Scythe: use when we have 2+ Void Shroud
        // During burst (Arcane Circle), use freely; outside burst, use when Lemure Shroud < 3
        // to avoid running out of Enshroud time
        if (HasEnshrouded && (HasArcaneCircle || LemureShroud < 3))
        {
            if (LemuresScythePvE.CanUse(out act, usedUp: true))
                return true;

            if (LemuresSlicePvE.CanUse(out act, usedUp: true))
                return true;
        }

        // --- Gluttony (60s CD, costs 50 Soul, grants 2 Executioner stacks) ---
        // Do not use if Plentiful Harvest or Perfectio are pending (avoid overwriting stacks)
        // BMR: allow Gluttony dump before downtime even if we'd normally hold it
        if (!HasPerfectioParata && !HasImmortalSacrifice
            && (PlentifulHarvestPvE.EnoughLevel ? !HasBloodsownCircleSelf : true))
        {
            // BMR: dump Gluttony before downtime -- Executioner stacks are instant GCDs
            // and spending Soul is better than losing it to downtime
            if (BmrDowntimeImminent && Soul >= 50 && GluttonyPvE.CanUse(out act, skipAoeCheck: true))
                return true;

            if (GluttonyPvE.CanUse(out act, skipAoeCheck: true))
                return true;
        }

        // --- Unveiled Gibbet/Gallows (oGCD follow-ups from Soul Reaver GCDs) ---
        // These replace Blood Stalk when available, so they are higher priority
        if (UnveiledGibbetPvE.CanUse(out act))
            return true;

        if (UnveiledGallowsPvE.CanUse(out act))
            return true;

        // --- Blood Stalk / Grim Swathe (50 Soul -> Soul Reaver) ---
        // Use to convert Soul gauge into Shroud via Soul Reaver GCDs
        // Do not use if: Bloodsown Circle active (wait for PH), Perfectio pending,
        // Executioner stacks pending, Immortal Sacrifice pending, or Gluttony coming soon
        // Balance: hold Blood Stalk ~2 GCDs before Gluttony, overcap at 90+ Soul
        if (!HasBloodsownCircleSelf && !HasPerfectioParata && !HasExecutioner && !HasImmortalSacrifice
            && ((GluttonyPvE.EnoughLevel && !GluttonyPvE.Cooldown.WillHaveOneChargeGCD(2))
                || !GluttonyPvE.EnoughLevel
                || Soul >= 90))
        {
            // BMR: dump Soul gauge before downtime to avoid waste
            // Lower the threshold -- spend at 50+ Soul when downtime is imminent
            if (BmrDowntimeImminent && Soul >= 50)
            {
                if (GrimSwathePvE.CanUse(out act))
                    return true;
                if (BloodStalkPvE.CanUse(out act))
                    return true;
            }

            if (GrimSwathePvE.CanUse(out act))
                return true;

            if (BloodStalkPvE.CanUse(out act))
                return true;
        }

        // BMR: extra Soul dump before downtime -- even if Gluttony is coming, don't let Soul rot
        if (BmrDowntimeImminent && !HasBloodsownCircleSelf && !HasPerfectioParata
            && !HasExecutioner && !HasImmortalSacrifice && Soul >= 50)
        {
            if (GrimSwathePvE.CanUse(out act))
                return true;
            if (BloodStalkPvE.CanUse(out act))
                return true;
        }

        return base.AttackAbility(nextGCD, out act);
    }

    #endregion

    #region GCD Logic

    protected override bool GeneralGCD(out IAction? act)
    {
        // ======================================================================
        // 1. PERFECTIO (always finish - highest priority)
        // After Communio, Perfectio becomes available. Must be used immediately.
        // ======================================================================
        if (!HasExecutioner && !HasSoulReaver)
        {
            if (PerfectioPvE.CanUse(out act, skipAoeCheck: true))
                return true;
        }

        // ======================================================================
        // 2. COMMUNIO (at 1 Lemure Shroud in Enshroud)
        // Final GCD of Enshroud phase. Has a cast time, so skip if moving.
        // BMR: Force Communio before downtime even if we have extra Lemure Shroud
        // remaining -- losing Communio + Perfectio is a huge DPS loss.
        // ======================================================================
        if (HasEnshrouded && LemureShroud == 1)
        {
            if (CommunioPvE.EnoughLevel)
            {
                if (!IsMoving && CommunioPvE.CanUse(out act, skipAoeCheck: true))
                    return true;

                // If moving and can't cast Communio, use Shadow of Death as filler
                // to avoid wasting the GCD (Enshroud will hold for one more GCD)
                if (IsMoving && ShadowOfDeathPvE.CanUse(out act, skipAoeCheck: true))
                    return true;
            }
            else
            {
                // Below Communio level: finish Enshroud with regular reaping GCDs
                if (GrimReapingPvE.CanUse(out act))
                    return true;

                if (UseEnhancedReapingGCD(out act))
                    return true;
            }
        }

        // BMR: Emergency Communio -- if we're in Enshroud and downtime is imminent,
        // skip remaining reaping GCDs and fire Communio early to ensure we get the finisher.
        // Communio + Perfectio is worth far more than extra reaping GCDs.
        // Only do this when downtime < 5s (one more GCD might not happen).
        if (HasEnshrouded && LemureShroud >= 2 && CommunioPvE.EnoughLevel
            && BmrUsable && BmrDowntimeIn is > 0 and <= 5f)
        {
            // Override: force Communio if we can cast it
            if (!IsMoving && CommunioPvE.CanUse(out act, skipAoeCheck: true))
                return true;
        }

        // ======================================================================
        // 3. ENSHROUD GCDs (Void Reaping / Cross Reaping / Grim Reaping)
        // Alternate based on Enhanced buffs. Each costs 1 Lemure Shroud, builds Void Shroud.
        // ======================================================================
        if (HasEnshrouded && LemureShroud > 1)
        {
            // Pre-burst Shadow of Death extension during Enshroud
            // If Arcane Circle is coming soon, extend Death's Design to cover the full burst
            if (PlentifulHarvestPvE.EnoughLevel && ArcaneCirclePvE.Cooldown.WillHaveOneCharge(9))
            {
                if ((LemureShroud == 4 && (CurrentTarget?.WillStatusEnd(30, true, StatusID.DeathsDesign) ?? false))
                    || (LemureShroud == 3 && (CurrentTarget?.WillStatusEnd(50, true, StatusID.DeathsDesign) ?? false)))
                {
                    if (ShadowOfDeathPvE.CanUse(out act, skipStatusProvideCheck: true))
                        return true;
                }
            }

            // AoE reaping
            if (GrimReapingPvE.CanUse(out act))
                return true;

            // ST reaping with Enhanced buff priority
            if (UseEnhancedReapingGCD(out act))
                return true;
        }

        // ======================================================================
        // 4. EXECUTIONER COMBO (from Gluttony)
        // Gluttony grants 2 Executioner stacks. Use Executioner's Gibbet/Gallows/Guillotine.
        // These are the same as Soul Reaver GCDs but with Executioner-specific actions.
        // ======================================================================
        if (HasExecutioner)
        {
            // AoE: Executioner's Guillotine
            if (ExecutionersGuillotinePvE.CanUse(out act))
                return true;

            // ST: Prioritize the Enhanced positional, then try to hit any positional,
            // then fall back to whatever is available
            if (UseExecutionerCombo(out act))
                return true;
        }

        // ======================================================================
        // 5. SOUL REAVER COMBO (from Blood Stalk / Grim Swathe)
        // Blood Stalk grants 1 Soul Reaver stack. Use Gibbet/Gallows/Guillotine.
        // Each grants 10 Shroud gauge.
        // ======================================================================
        if (HasSoulReaver)
        {
            // AoE: Guillotine
            if (GuillotinePvE.CanUse(out act))
                return true;

            // ST: Prioritize the Enhanced positional
            if (UseSoulReaverCombo(out act))
                return true;
        }

        // ======================================================================
        // 6. SOULSOW (out of combat only - the base class ActionCheck enforces this)
        // ======================================================================
        if (SoulsowPvE.CanUse(out act))
            return true;

        // ======================================================================
        // 7. PLENTIFUL HARVEST (after Bloodsown Circle expires)
        // Available when Immortal Sacrifice stacks are present and Bloodsown Circle is gone.
        // Not available during the first few GCDs (opener timing).
        // ======================================================================
        if (!CombatElapsedLessGCD(2) && PlentifulHarvestPvE.CanUse(out act, skipAoeCheck: true))
            return true;

        // ======================================================================
        // 8. DEATH'S DESIGN MAINTENANCE (Shadow of Death / Whorl of Death)
        // 10% damage buff on target. Refresh before it falls off.
        // BMR: skip refreshing Death's Design if downtime < 3s -- waste of a GCD
        // when the buff will persist through downtime anyway.
        // ======================================================================
        bool bmrSkipDDRefresh = BmrUsable && BmrDowntimeIn is > 0 and <= 3f;

        if (!bmrSkipDDRefresh)
        {
            // AoE Death's Design: Whorl of Death
            // Check if at least 2 targets in the AoE area need/benefit from Death's Design
            int ddNeeds = 0;
            if (WhorlOfDeathPvE.CanUse(out _, skipAoeCheck: true) && WhorlOfDeathPvE.PreviewTarget.HasValue)
            {
                ddNeeds = WhorlOfDeathPvE.PreviewTarget.Value.AffectedTargets?.Length ?? 0;
            }
            if (ddNeeds >= 2)
            {
                if (WhorlOfDeathPvE.CanUse(out act, skipStatusProvideCheck: true))
                    return true;
            }

            // Standard Whorl of Death (framework-managed refresh)
            if (WhorlOfDeathPvE.CanUse(out act))
                return true;

            // Shadow of Death (single target)
            if (UseCustomDDTiming)
            {
                // Custom timing: refresh when target lacks DD or DD is about to expire
                if ((!CurrentTarget?.HasStatus(true, StatusID.DeathsDesign) ?? false)
                    || (CurrentTarget?.WillStatusEnd(RefreshDDSeconds, true, StatusID.DeathsDesign) ?? false))
                {
                    if (ShadowOfDeathPvE.CanUse(out act, skipStatusProvideCheck: true))
                        return true;
                }
            }
            else
            {
                // Default: let the framework handle refresh timing via TargetStatusProvide
                if (ShadowOfDeathPvE.CanUse(out act))
                    return true;
            }
        }

        // ======================================================================
        // 9. HARVEST MOON (ranged GCD from Soulsow)
        // Use when out of melee range for movement, or as a strong filler.
        // BMR: also use before downtime as a strong instant GCD to maximize damage
        // ======================================================================
        if (UseHarvestMoonRanged && InCombat && !HasSoulReaver && !HasHostilesInRange
            && HarvestMoonPvE.CanUse(out act, skipAoeCheck: true))
        {
            return true;
        }

        // BMR: dump Harvest Moon before downtime -- it's a strong instant GCD
        // Better to use it than let it sit through a downtime phase
        if (BmrDowntimeImminent && InCombat && !HasSoulReaver
            && HarvestMoonPvE.CanUse(out act, skipAoeCheck: true))
        {
            return true;
        }

        // ======================================================================
        // 10. SOUL SLICE / SOUL SCYTHE (gauge generation, 2 charges)
        // Generates 50 Soul directly. Use to avoid overcapping charges.
        // BMR: Don't start Soul Slice if downtime < 3s and Soul is already high,
        // since the generated gauge will be wasted.
        // ======================================================================
        bool bmrSkipSoulSlice = BmrUsable && BmrDowntimeIn is > 0 and <= 3f && Soul >= 80;

        if (!bmrSkipSoulSlice)
        {
            if (SoulScythePvE.CanUse(out act, usedUp: true))
                return true;

            if (SoulSlicePvE.CanUse(out act, usedUp: true))
                return true;
        }

        // ======================================================================
        // 11. COMBO CHAIN (1-2-3 / AoE equivalents)
        // Basic combo to build Soul gauge (10 per hit).
        // BMR: Don't start a NEW combo if downtime < 5s -- use ranged GCDs instead.
        // Always finish an in-progress combo though (framework handles combo state).
        // ======================================================================

        // AoE combo
        if (NightmareScythePvE.CanUse(out act))
            return true;

        if (SpinningScythePvE.CanUse(out act))
            return true;

        // ST combo (don't break Executioner stacks)
        if (!HasExecutioner)
        {
            // Always finish an in-progress combo (Waxing/Infernal are combo continuations)
            if (InfernalSlicePvE.CanUse(out act))
                return true;

            if (WaxingSlicePvE.CanUse(out act))
                return true;

            // BMR: don't start a fresh combo if downtime < 5s
            // Use ranged GCDs (Harvest Moon / Harpe) instead for the remaining time
            if (!BmrBlockNewCombo)
            {
                if (SlicePvE.CanUse(out act))
                    return true;
            }
        }

        // ======================================================================
        // 12. RANGED FALLBACKS
        // Harvest Moon when in combat but no melee targets, then Harpe.
        // ======================================================================
        if (InCombat && !HasSoulReaver && HarvestMoonPvE.CanUse(out act, skipAoeCheck: true))
            return true;

        if (HarpePvE.CanUse(out act))
            return true;

        return base.GeneralGCD(out act);
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Handles Enhanced Void Reaping / Cross Reaping priority during Enshroud.
    /// These alternate: using Void Reaping grants Enhanced Cross Reaping, and vice versa.
    /// Always use the Enhanced version for bonus potency.
    /// </summary>
    private bool UseEnhancedReapingGCD(out IAction? act)
    {
        switch ((HasEnhancedCrossReaping, HasEnhancedVoidReaping))
        {
            case (true, _):
                if (CrossReapingPvE.CanUse(out act))
                    return true;
                break;
            case (_, true):
                if (VoidReapingPvE.CanUse(out act))
                    return true;
                break;
            case (false, false):
                // No enhancement active (first GCD of Enshroud or both fell off):
                // Default to Void Reaping to start the alternation
                if (VoidReapingPvE.CanUse(out act))
                    return true;
                if (CrossReapingPvE.CanUse(out act))
                    return true;
                break;
        }
        act = null;
        return false;
    }

    /// <summary>
    /// Handles Executioner's Gibbet/Gallows with positional priority.
    /// Gibbet = Flank, Gallows = Rear. Prefer the Enhanced version,
    /// then try to hit the correct positional, then fall back.
    /// </summary>
    private bool UseExecutionerCombo(out IAction? act)
    {
        switch ((HasEnhancedGallows, HasEnhancedGibbet))
        {
            case (true, true):
                // Both enhanced (shouldn't normally happen, but handle gracefully):
                // Try positionals first
                if (ExecutionersGallowsPvE.CanUse(out act, skipComboCheck: true)
                    && ExecutionersGallowsPvE.Target.Target != null
                    && CanHitPositional(EnemyPositional.Rear, ExecutionersGallowsPvE.Target.Target))
                    return true;
                if (ExecutionersGibbetPvE.CanUse(out act, skipComboCheck: true)
                    && ExecutionersGibbetPvE.Target.Target != null
                    && CanHitPositional(EnemyPositional.Flank, ExecutionersGibbetPvE.Target.Target))
                    return true;
                // Fall back to any
                if (ExecutionersGallowsPvE.CanUse(out act, skipComboCheck: true))
                    return true;
                if (ExecutionersGibbetPvE.CanUse(out act, skipComboCheck: true))
                    return true;
                break;

            case (true, false):
                // Enhanced Gallows: use Gallows (Rear)
                if (ExecutionersGallowsPvE.CanUse(out act, skipComboCheck: true))
                    return true;
                break;

            case (false, true):
                // Enhanced Gibbet: use Gibbet (Flank)
                if (ExecutionersGibbetPvE.CanUse(out act, skipComboCheck: true))
                    return true;
                break;

            case (false, false):
                // No enhancement: try to hit a positional for the bonus
                if (ExecutionersGallowsPvE.CanUse(out act, skipComboCheck: true)
                    && ExecutionersGallowsPvE.Target.Target != null
                    && CanHitPositional(EnemyPositional.Rear, ExecutionersGallowsPvE.Target.Target))
                    return true;
                if (ExecutionersGibbetPvE.CanUse(out act, skipComboCheck: true)
                    && ExecutionersGibbetPvE.Target.Target != null
                    && CanHitPositional(EnemyPositional.Flank, ExecutionersGibbetPvE.Target.Target))
                    return true;
                // Fall back: Gallows first (standard opener starts with Gibbet from Gluttony,
                // which grants Enhanced Gallows, so defaulting to Gallows covers edge cases)
                if (ExecutionersGallowsPvE.CanUse(out act, skipComboCheck: true))
                    return true;
                if (ExecutionersGibbetPvE.CanUse(out act, skipComboCheck: true))
                    return true;
                break;
        }
        act = null;
        return false;
    }

    /// <summary>
    /// Handles Soul Reaver Gibbet/Gallows with positional priority.
    /// Gibbet = Flank, Gallows = Rear. Prefer the Enhanced version,
    /// then try to hit the correct positional, then fall back.
    /// </summary>
    private bool UseSoulReaverCombo(out IAction? act)
    {
        switch ((HasEnhancedGallows, HasEnhancedGibbet))
        {
            case (true, true):
                // Both enhanced: try positionals first
                if (GallowsPvE.CanUse(out act, skipComboCheck: true)
                    && GallowsPvE.Target.Target != null
                    && CanHitPositional(EnemyPositional.Rear, GallowsPvE.Target.Target))
                    return true;
                if (GibbetPvE.CanUse(out act, skipComboCheck: true)
                    && GibbetPvE.Target.Target != null
                    && CanHitPositional(EnemyPositional.Flank, GibbetPvE.Target.Target))
                    return true;
                // Fall back to any
                if (GallowsPvE.CanUse(out act, skipComboCheck: true))
                    return true;
                if (GibbetPvE.CanUse(out act, skipComboCheck: true))
                    return true;
                break;

            case (true, _):
                // Enhanced Gallows: use Gallows (Rear)
                if (GallowsPvE.CanUse(out act, skipComboCheck: true))
                    return true;
                break;

            case (_, true):
                // Enhanced Gibbet: use Gibbet (Flank)
                if (GibbetPvE.CanUse(out act, skipComboCheck: true))
                    return true;
                break;

            case (false, false):
                // No enhancement: try to hit a positional for the bonus
                if (GallowsPvE.CanUse(out act, skipComboCheck: true)
                    && GallowsPvE.Target.Target != null
                    && CanHitPositional(EnemyPositional.Rear, GallowsPvE.Target.Target))
                    return true;
                if (GibbetPvE.CanUse(out act, skipComboCheck: true)
                    && GibbetPvE.Target.Target != null
                    && CanHitPositional(EnemyPositional.Flank, GibbetPvE.Target.Target))
                    return true;
                // Fall back
                if (GallowsPvE.CanUse(out act, skipComboCheck: true))
                    return true;
                if (GibbetPvE.CanUse(out act, skipComboCheck: true))
                    return true;
                break;
        }
        act = null;
        return false;
    }

    #endregion
}
