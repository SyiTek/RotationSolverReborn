namespace RotationSolver.ExtraRotations.Tank;

[Rotation("SezuraiWAR", CombatType.PvE, GameVersion = "7.41",
    Description = "BMR-smart Balance-aligned WAR with timeline-aware mitigation, downtime-aware burst, and Inner Release management.")]
[SourceCode(Path = "main/ExtraRotations/Tank/SezuraiWAR.cs")]
[ExtraRotation]
public sealed class SezuraiWAR : WarriorRotation
{
    #region Config Options

    [Range(0f, 0.25f, ConfigUnitType.Percent)]
    [RotationConfig(CombatType.PvE, Name = "Action Ahead Override (0 = use global setting)")]
    public float ActionAheadOverride { get; set; } = 0f;

    [RotationConfig(CombatType.PvE, Name = "Use Gemdraught of Strength during Inner Release windows")]
    public bool BurstMed { get; set; } = true;

    [RotationConfig(CombatType.PvE, Name = "Use Onslaught as gap closer")]
    public bool UseGapCloser { get; set; } = true;

    [RotationConfig(CombatType.PvE, Name = "Use defensive cooldowns automatically")]
    public bool AutoMitigation { get; set; } = true;

    [Range(0, 1, ConfigUnitType.Percent)]
    [RotationConfig(CombatType.PvE, Name = "Bloodwhetting HP threshold")]
    public float BloodwhettingHpThreshold { get; set; } = 0.7f;

    [Range(0, 1, ConfigUnitType.Percent)]
    [RotationConfig(CombatType.PvE, Name = "Thrill of Battle HP threshold")]
    public float ThrillHpThreshold { get; set; } = 0.65f;

    [Range(0, 1, ConfigUnitType.Percent)]
    [RotationConfig(CombatType.PvE, Name = "Equilibrium HP threshold")]
    public float EquilibriumHpThreshold { get; set; } = 0.5f;

    [Range(0, 1, ConfigUnitType.Percent)]
    [RotationConfig(CombatType.PvE, Name = "Nascent Flash target HP threshold")]
    public float NascentFlashHpThreshold { get; set; } = 0.6f;

    #endregion

    #region Burst State

    /// <summary>
    /// Whether the user has burst enabled in the framework.
    /// </summary>
    private bool CanBurst => MergedStatus.HasFlag(AutoStatus.Burst);

    /// <summary>
    /// True when Inner Release / Inner Strength buff is active, indicating we are
    /// inside the burst window. InnerStrength persists for the full IR duration
    /// even after Fell Cleave stacks are consumed.
    /// </summary>
    private bool InBurstWindow => StatusHelper.PlayerHasStatus(true, StatusID.InnerStrength)
        && !StatusHelper.PlayerWillStatusEnd(0, true, StatusID.InnerStrength);

    /// <summary>
    /// True when Inner Release stacks are actively available for free Fell Cleave usage.
    /// </summary>
    private bool HasIRStacks => InnerReleaseStacks > 0;

    /// <summary>
    /// True when Surging Tempest (10% damage buff) is active.
    /// </summary>
    private static bool HasSurgingTempest => StatusHelper.PlayerHasStatus(true, StatusID.SurgingTempest);

    /// <summary>
    /// True when Surging Tempest will expire within the specified number of GCDs.
    /// Used to determine when to refresh the buff via Storm's Eye.
    /// </summary>
    private static bool SurgingTempestWillEnd(uint gcdCount) =>
        StatusHelper.PlayerWillStatusEndGCD(gcdCount, 0, true, StatusID.SurgingTempest);

    /// <summary>
    /// True when Nascent Chaos is active (from Infuriate), enabling Inner Chaos / Chaotic Cyclone.
    /// </summary>
    private static bool HasNascentChaos => StatusHelper.PlayerHasStatus(true, StatusID.NascentChaos);

    /// <summary>
    /// True when Primal Rend Ready is active (granted by Inner Release).
    /// </summary>
    private static bool HasPrimalRendReady => StatusHelper.PlayerHasStatus(true, StatusID.PrimalRendReady);

    /// <summary>
    /// True when we are medicated (potion active).
    /// </summary>
    private static bool IsMedicated => StatusHelper.PlayerHasStatus(true, StatusID.Medicated);

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
        ImGui.Text($"--- Burst ---");
        ImGui.Text($"CanBurst: {CanBurst}");
        ImGui.Text($"InBurstWindow: {InBurstWindow}");
        ImGui.Text($"HasIRStacks: {HasIRStacks} ({InnerReleaseStacks})");
        ImGui.Text($"IR CD: {(InnerReleasePvE.Cooldown.IsCoolingDown ? $"{InnerReleasePvE.Cooldown.RecastTimeRemain:F1}s" : "Ready")}");
        ImGui.Text($"HasSurgingTempest: {HasSurgingTempest}");
        ImGui.Text($"SurgingTempest <6 GCDs: {SurgingTempestWillEnd(6)}");
        ImGui.Text($"HasNascentChaos: {HasNascentChaos}");
        ImGui.Text($"HasPrimalRendReady: {HasPrimalRendReady}");
        ImGui.Text($"PrimalWrathReady: {PrimalWrathPvEReady}");
        ImGui.Text($"PrimalRuinationReady: {PrimalRuinationPvEReady}");
        ImGui.Text($"InnerChaosPvEReady: {InnerChaosPvEeady}");
        ImGui.Text($"--- Gauge ---");
        ImGui.Text($"BeastGauge: {BeastGauge}");
        ImGui.Text($"OnslaughtCharges: {OnslaughtPvE.Cooldown.CurrentCharges}/{OnslaughtMax}");
        ImGui.Text($"InfuriateCharges: {InfuriatePvE.Cooldown.CurrentCharges}/2");
        ImGui.Text($"--- Status ---");
        ImGui.Text($"IsMedicated: {IsMedicated}");
        ImGui.Text($"InCombat: {InCombat}");
        ImGui.Text($"HP: {Player?.GetHealthRatio():P0}");
        ImGui.Text($"--- Defensive CDs ---");
        ImGui.Text($"Bloodwhetting: {(BloodwhettingPvE.Cooldown.IsCoolingDown ? $"{BloodwhettingPvE.Cooldown.RecastTimeRemain:F1}s" : "Ready")}");
        ImGui.Text($"ShakeItOff: {(ShakeItOffPvE.Cooldown.IsCoolingDown ? $"{ShakeItOffPvE.Cooldown.RecastTimeRemain:F1}s" : "Ready")}");
        ImGui.Text($"Vengeance: {(VengeancePvE.Cooldown.IsCoolingDown ? $"{VengeancePvE.Cooldown.RecastTimeRemain:F1}s" : "Ready")}");
        ImGui.Text($"Rampart: {(RampartPvE.Cooldown.IsCoolingDown ? $"{RampartPvE.Cooldown.RecastTimeRemain:F1}s" : "Ready")}");
        ImGui.Text($"Reprisal: {(ReprisalPvE.Cooldown.IsCoolingDown ? $"{ReprisalPvE.Cooldown.RecastTimeRemain:F1}s" : "Ready")}");
        ImGui.Text($"Holmgang: {(HolmgangPvE.Cooldown.IsCoolingDown ? $"{HolmgangPvE.Cooldown.RecastTimeRemain:F1}s" : "Ready")}");
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

    #region Countdown & Opener
    // === WAR OPENER (7.4 Balance / Icy Veins) ===
    // Pre-pull: Tomahawk(-0.7s) + Infuriate (weave)
    // GCD1: Heavy Swing
    // GCD2: Maim
    // GCD3: Storm's Eye → IR (weave) + Pot (weave)
    // GCD4: Inner Chaos → Upheaval (weave) + Onslaught (weave)
    // GCD5: Primal Rend → Onslaught (weave)
    // GCD6: Primal Ruination → Onslaught (weave)
    // GCD7: Fell Cleave
    // GCD8: Fell Cleave
    // GCD9: Fell Cleave → Primal Wrath (weave) + Infuriate (weave)
    // GCD10: Inner Chaos
    //
    // === EVEN BURST (120s) ===
    // Full burst with pot: 1x Primal Rend + 1x Primal Ruination + 2x Inner Chaos
    //   + 3x Fell Cleave = 7 high-potency GCDs + 1 filler under raid buffs
    // 3 Onslaughts in burst, Upheaval under buffs
    //
    // === ODD BURST (60s) ===
    // IR + 3x Fell Cleave + Primal Wrath + 1x Inner Chaos (smaller window)
    // No pot, fewer raid buffs. 1 Onslaught in filler between windows
    //
    // === FILLER ===
    // Default: Heavy Swing → Maim → Storm's Path (30 gauge per combo)
    // Refresh: Storm's Eye when Surging Tempest < 15s remaining
    // Fell Cleave at 60+ gauge to prevent overcap from Storm's Path (+30)
    // Never Infuriate at 60+ gauge (grants +50, would overcap)

    protected override IAction? CountDownAction(float remainTime)
    {
        // Pot at ~2s before pull for maximum coverage of the burst window
        if (BurstMed && remainTime <= 2f && remainTime > 1f && UseBurstMedicine(out var potAct))
            return potAct;

        // Tomahawk at ~0.7s to land as the boss becomes targetable
        if (remainTime <= 0.7f && TomahawkPvE.CanUse(out var act))
            return act;

        return base.CountDownAction(remainTime);
    }

    #endregion

    #region Movement Abilities

    [RotationDesc(ActionID.OnslaughtPvE)]
    protected override bool MoveForwardAbility(IAction nextGCD, out IAction? act)
    {
        if (UseGapCloser && OnslaughtPvE.CanUse(out act))
            return true;
        return base.MoveForwardAbility(nextGCD, out act);
    }

    [RotationDesc(ActionID.PrimalRendPvE)]
    protected override bool MoveForwardGCD(out IAction? act)
    {
        if (PrimalRendPvE.CanUse(out act, skipAoeCheck: true))
            return true;
        return base.MoveForwardGCD(out act);
    }

    #endregion

    #region Defensive Abilities

    [RotationDesc(ActionID.BloodwhettingPvE, ActionID.DamnationPvE, ActionID.VengeancePvE, ActionID.RampartPvE, ActionID.ReprisalPvE)]
    protected override bool DefenseSingleAbility(IAction nextGCD, out IAction? act)
    {
        act = null;
        if (!AutoMitigation) return false;

        // Don't stack mit during Holmgang when low — invuln handles it
        if (StatusHelper.PlayerHasStatus(true, StatusID.Holmgang_409) && Player?.GetHealthRatio() < 0.3f)
            return false;

        // === BMR-AWARE TANKBUSTER MITIGATION ===
        // Balance: Bloodwhetting first (short CD, always available), then layer ONE heavier CD.
        // Mitigation is multiplicative — spreading across TBs is more efficient than dumping all on one.
        // Don't fire mits if TB is >8s away.
        bool tbSoon = BmrActive && BmrTankbusterIn is > 0 and <= 5f;

        // With BMR active and no TB coming soon, don't waste single-target mits
        if (BmrActive && !tbSoon)
        {
            // Still allow Bloodwhetting for self-healing if HP is low (handled by HealSingleAbility)
            return base.DefenseSingleAbility(nextGCD, out act);
        }

        // --- TB is imminent (BMR path) or non-BMR reactive path ---

        // Bloodwhetting / Raw Intuition: short CD, strong self-heal + shield — always first for TB
        // Balance: "Bloodwhetting is your go-to for every tankbuster"
        if (BloodwhettingPvE.CanUse(out act))
            return true;
        if (!BloodwhettingPvE.Info.EnoughLevelAndQuest() && RawIntuitionPvE.CanUse(out act))
            return true;

        // Don't layer more mit if Bloodwhetting is already covering us
        if (!StatusHelper.PlayerWillStatusEndGCD(0, 0, true, StatusID.Bloodwhetting, StatusID.RawIntuition))
            return false;

        // BMR TB path: layer exactly ONE heavier mit, then stop — save others for the next TB
        if (tbSoon)
        {
            // Reprisal: 10% enemy damage reduction, 60s CD — good for shared TBs too
            if (ReprisalPvE.CanUse(out act, skipAoeCheck: true))
                return true;

            // Damnation/Vengeance: heavy personal mit for big TBs (40%/30%)
            if (DamnationPvE.EnoughLevel && DamnationPvE.CanUse(out act))
                return true;
            if (!DamnationPvE.EnoughLevel && VengeancePvE.CanUse(out act))
                return true;

            // Rampart as fallback if heavier CDs are on CD
            if (RampartPvE.CanUse(out act))
                return true;

            // Only one long CD per TB — don't dump everything
            return base.DefenseSingleAbility(nextGCD, out act);
        }

        // === NON-BMR REACTIVE PATH ===
        // Framework triggered DefenseSingle — stagger CDs in priority order

        if (ReprisalPvE.CanUse(out act, skipAoeCheck: true))
            return true;

        // Damnation (upgraded Vengeance at 92) -> Vengeance: 30-40% damage reduction
        if ((!RampartPvE.Cooldown.IsCoolingDown || RampartPvE.Cooldown.ElapsedAfter(60))
            && !StatusHelper.PlayerHasStatus(true, StatusID.ArmsLength))
        {
            if (DamnationPvE.EnoughLevel && DamnationPvE.CanUse(out act))
                return true;
            if (!DamnationPvE.EnoughLevel && VengeancePvE.CanUse(out act))
                return true;
        }

        // Rampart: 20% damage reduction
        if (((VengeancePvE.Cooldown.IsCoolingDown && VengeancePvE.Cooldown.ElapsedAfter(60)) || !VengeancePvE.EnoughLevel)
            && RampartPvE.CanUse(out act))
            return true;

        return base.DefenseSingleAbility(nextGCD, out act);
    }

    [RotationDesc(ActionID.ShakeItOffPvE, ActionID.ReprisalPvE)]
    protected override bool DefenseAreaAbility(IAction nextGCD, out IAction? act)
    {
        if (!AutoMitigation)
        {
            act = null;
            return false;
        }

        // === BMR-AWARE RAIDWIDE MITIGATION ===
        // Balance: spread mits across raidwides, don't dump everything on one hit.
        // Mitigation is multiplicative (two 10% = 19%, not 20%), so spreading is more efficient.
        // Use 1-2 mits per raidwide max. Don't fire if raidwide is >8s away.
        //
        // Shake It Off: 15% MaxHP shield + 300p HoT. Bonus 2% per buff consumed (Thrill, Damnation, BW).
        // Reprisal: 10% enemy damage reduction — use on SEPARATE raidwides from Shake.
        bool rwSoon = BmrActive && BmrRaidwideIn is > 0 and <= 5f;

        // With BMR active and no raidwide coming soon, don't waste party mits
        if (BmrActive && !rwSoon)
            return base.DefenseAreaAbility(nextGCD, out act);

        // Shake It Off: primary raidwide tool — shield + HoT for the whole party
        // Balance: "grants 15% of Party Member's MaxHP as a Shield"
        // BMR: fire 1-5s before raidwide so the shield covers the damage snapshot
        if (rwSoon && ShakeItOffPvE.CanUse(out act, skipAoeCheck: true))
            return true;

        // Reprisal: use when Shake is on CD for this raidwide, or save for NEXT raidwide
        // Don't stack Reprisal + Shake on the same raidwide — spread them
        if (rwSoon && !StatusHelper.PlayerHasStatus(true, StatusID.ShakeItOff)
            && ReprisalPvE.CanUse(out act, skipAoeCheck: true))
            return true;

        // === NON-BMR FALLBACK ===
        // Without BMR: use whenever the framework says DefenseArea is needed
        if (!BmrActive && ShakeItOffPvE.CanUse(out act, skipAoeCheck: true))
            return true;

        if (!BmrActive && ReprisalPvE.CanUse(out act, skipAoeCheck: true))
            return true;

        return base.DefenseAreaAbility(nextGCD, out act);
    }

    #endregion

    #region Heal Abilities

    [RotationDesc(ActionID.BloodwhettingPvE, ActionID.ThrillOfBattlePvE, ActionID.EquilibriumPvE)]
    protected override bool HealSingleAbility(IAction nextGCD, out IAction? act)
    {
        // Bloodwhetting for self-healing (healing on each weaponskill hit)
        if (Player?.GetHealthRatio() < BloodwhettingHpThreshold && InCombat && NumberOfHostilesInRange > 0)
        {
            if (BloodwhettingPvE.CanUse(out act))
                return true;
            if (!BloodwhettingPvE.Info.EnoughLevelAndQuest() && RawIntuitionPvE.CanUse(out act))
                return true;
        }

        // Thrill of Battle: max HP increase + heal
        if (Player?.GetHealthRatio() < ThrillHpThreshold)
        {
            if (ThrillOfBattlePvE.CanUse(out act))
                return true;
        }

        // Equilibrium: direct heal + HoT
        if (Player?.GetHealthRatio() < EquilibriumHpThreshold
            && !StatusHelper.PlayerHasStatus(true, StatusID.Holmgang_409))
        {
            if (EquilibriumPvE.CanUse(out act))
                return true;
        }

        return base.HealSingleAbility(nextGCD, out act);
    }

    [RotationDesc(ActionID.NascentFlashPvE)]
    protected override bool HealSingleGCD(out IAction? act)
    {
        // Nascent Flash: heal a party member based on your weaponskill hits
        if (NascentFlashPvE.CanUse(out act)
            && InCombat
            && NascentFlashPvE.Target.Target?.GetHealthRatio() < NascentFlashHpThreshold)
            return true;

        return base.HealSingleGCD(out act);
    }

    #endregion

    #region Anti-Knockback

    [RotationDesc(ActionID.ArmsLengthPvE)]
    protected sealed override bool AntiKnockbackAbility(IAction nextGCD, out IAction? act)
    {
        if (ArmsLengthPvE.CanUse(out act))
            return true;
        return base.AntiKnockbackAbility(nextGCD, out act);
    }

    #endregion

    #region Emergency Ability (oGCD - highest priority)

    protected override bool EmergencyAbility(IAction nextGCD, out IAction? act)
    {
        // === HOLMGANG ===
        // Balance: "Warrior's invuln. Prevents most attacks from lowering HP below 1 for 10s."
        // BMR-aware: only Holmgang if TB is actually imminent AND HP is critical.
        // Without BMR: use the framework's health threshold as before.
        bool tbImminent = BmrActive && BmrTankbusterIn is > 0 and <= 3f;
        bool hpCritical = Player?.GetHealthRatio() <= Service.Config.HealthForDyingTanks;

        if (HolmgangPvE.CanUse(out act))
        {
            // BMR path: only invuln if TB is about to hit and we're low
            if (BmrActive && tbImminent && hpCritical)
                return true;

            // Non-BMR path: use framework HP threshold
            if (!BmrActive && hpCritical)
                return true;
        }

        // === MEDICINE ===
        // Balance: pot should cover the full IR window including Primal Rend + Ruination.
        // BMR-aware: don't pot if downtime is imminent (waste of pot duration).
        if (BurstMed && InBurstWindow && InCombat)
        {
            bool downtimeWastesPot = BmrActive && BmrDowntimeIn is > 0 and <= 10f;
            if (!downtimeWastesPot && UseBurstMedicine(out act))
                return true;
        }

        return base.EmergencyAbility(nextGCD, out act);
    }

    #endregion

    #region oGCD Logic (AttackAbility)

    protected override bool AttackAbility(IAction nextGCD, out IAction? act)
    {
        // === EARLY COMBAT GATE ===
        // Don't fire oGCDs on the very first GCD to ensure Surging Tempest
        // gets established before spending resources.
        if (CombatElapsedLessGCD(1))
        {
            act = null;
            return false;
        }

        // === BMR DOWNTIME/VULNERABILITY AWARENESS ===
        bool downtimeSoon = BmrActive && BmrDowntimeIn is > 0 and <= 15f;
        bool downtimeVeryClose = BmrActive && BmrDowntimeIn is > 0 and <= 8f;
        bool vulnWindowSoon = BmrActive && BmrVulnerableIn is > 0 and <= 30f;

        // === BMR: DUMP BURST BEFORE DOWNTIME ===
        // If downtime is very close (<=8s) and IR is available, pop it NOW and burn stacks.
        // Need ~8s to dump all 3 Fell Cleave stacks + Primal Rend + Primal Wrath.
        if (downtimeVeryClose && InCombat && HasHostilesInRange
            && (!SurgingTempestWillEnd(2) || !StormsEyePvE.EnoughLevel))
        {
            if (InnerReleasePvE.CanUse(out act))
                return true;
            if (!InnerReleasePvE.Info.EnoughLevelAndQuest() && BerserkPvE.CanUse(out act))
                return true;
        }

        // === INNER RELEASE (60s CD) ===
        // The core burst cooldown. Grants 3 free Fell Cleave/Decimate stacks,
        // Primal Rend Ready, and extends Surging Tempest by 10s.
        // Balance: "Use Inner Release on cooldown" - ensure Surging Tempest
        // has enough duration so we don't waste IR GCDs refreshing it.
        //
        // BMR: Don't start IR if downtime is <10s (needs ~8s to dump all stacks).
        // BMR: If vulnerability window is within 30s and IR is available, consider holding.
        if (CanBurst && InCombat && HasHostilesInRange)
        {
            // BMR: skip IR if downtime too close (unless we already handled it above)
            bool downtimeTooClose = BmrActive && BmrDowntimeIn is > 0 and < 10f && !downtimeVeryClose;

            // BMR: hold for vulnerability window if it's soon and IR won't come off CD again
            bool holdForVuln = vulnWindowSoon && BmrVulnerableIn > 5f
                && !InnerReleasePvE.Cooldown.WillHaveOneChargeGCD(4);

            if (!downtimeTooClose && !holdForVuln
                && (!SurgingTempestWillEnd(2) || !StormsEyePvE.EnoughLevel))
            {
                if (InnerReleasePvE.CanUse(out act))
                    return true;
                // Low level fallback: Berserk
                if (!InnerReleasePvE.Info.EnoughLevelAndQuest() && BerserkPvE.CanUse(out act))
                    return true;
            }
        }

        // === INFURIATE (2 charges, 60s each) ===
        // Grants Nascent Chaos (enables Inner Chaos, a 580 potency Fell Cleave).
        // Generates 50 Beast Gauge.
        //
        // Balance strategy:
        // - Use 1 charge BEFORE IR to carry Inner Chaos into the burst window.
        // - Use 1 charge AFTER IR stacks are spent for a second Inner Chaos.
        // - Never overcap at 2 charges. Don't use at >50 gauge (would overcap gauge).
        //
        // BMR: dump Infuriate charges before downtime to avoid wasting them
        // During burst (IR active or InnerStrength up): use aggressively
        // Outside burst: use at 3+ GCDs to spare to avoid charge overcap
        if (downtimeSoon && BeastGauge <= 50 && InfuriatePvE.CanUse(out act, usedUp: true))
            return true;

        if (InBurstWindow && (InnerReleaseStacks == 0 || InnerReleaseStacks == 3))
        {
            if (InfuriatePvE.CanUse(out act, usedUp: true))
                return true;
        }

        // Outside burst: use Infuriate with some breathing room to prevent
        // charge overcap while respecting gauge limits (base ActionCheck handles <=50g)
        if (!InBurstWindow && InfuriatePvE.CanUse(out act, gcdCountForAbility: 3))
            return true;

        // Low level: during Berserk, use Infuriate aggressively
        if (!InnerReleasePvE.EnoughLevel && StatusHelper.PlayerHasStatus(true, StatusID.Berserk)
            && InfuriatePvE.CanUse(out act, usedUp: true))
            return true;

        // === PRIMAL WRATH (oGCD follow-up after 3x Fell Cleave/Decimate) ===
        // Unlocked by consuming all 3 Burgeoning Fury stacks.
        // Use immediately when available - it's a high-potency AoE oGCD.
        if (PrimalWrathPvEReady && PrimalWrathPvE.CanUse(out act, skipAoeCheck: true))
            return true;

        // Wait a few GCDs before using Upheaval/Orogeny to ensure
        // Surging Tempest is up (base ModifyUpheavalPvE requires it)
        if (CombatElapsedLessGCD(4))
        {
            act = null;
            return false;
        }

        // === UPHEAVAL / OROGENY (30s CD) ===
        // Balance: "Keep Upheaval on cooldown"
        // Use under Surging Tempest (enforced by base action setting).
        // Orogeny is the AoE version, checked first for multi-target.
        if (OrogenyPvE.CanUse(out act))
            return true;
        if (UpheavalPvE.CanUse(out act))
            return true;

        // === ONSLAUGHT (3 charges, gap closer) ===
        // Balance: "Allow Onslaught to always be recharging" - spend during burst,
        // use to prevent overcap outside burst. Don't move if standing still.
        // During burst: spend extra charges (usedUp: true)
        if (InBurstWindow && !IsMoving
            && !IsLastAction(false, OnslaughtPvE)
            && HasSurgingTempest
            && OnslaughtPvE.CanUse(out act, usedUp: true))
            return true;

        // BMR: dump Onslaught charges before downtime
        if (downtimeSoon && !IsMoving && !IsLastAction(false, OnslaughtPvE)
            && HasSurgingTempest
            && OnslaughtPvE.CanUse(out act, usedUp: true))
            return true;

        // Outside burst: use 1 charge to prevent overcap at max charges
        if (!InBurstWindow && !IsMoving
            && !IsLastAction(false, OnslaughtPvE)
            && HasSurgingTempest
            && OnslaughtPvE.Cooldown.WillHaveXChargesGCD(OnslaughtMax, 1)
            && OnslaughtPvE.CanUse(out act, usedUp: true))
            return true;

        // Normal Onslaught usage: single charge to keep recharging
        if (!IsMoving && !IsLastAction(false, OnslaughtPvE) && HasSurgingTempest
            && OnslaughtPvE.CanUse(out act))
            return true;

        return base.AttackAbility(nextGCD, out act);
    }

    #endregion

    #region General Ability (non-attack oGCDs)

    protected override bool GeneralAbility(IAction nextGCD, out IAction? act)
    {
        // Self-healing with Thrill / Equilibrium outside of defensive triggers
        if (Player?.GetHealthRatio() < ThrillHpThreshold && ThrillOfBattlePvE.CanUse(out act))
            return true;

        if (Player?.GetHealthRatio() < EquilibriumHpThreshold
            && !StatusHelper.PlayerHasStatus(true, StatusID.Holmgang_409)
            && EquilibriumPvE.CanUse(out act))
            return true;

        return base.GeneralAbility(nextGCD, out act);
    }

    #endregion

    #region GCD Logic

    protected override bool GeneralGCD(out IAction? act)
    {
        // Gate: ensure Surging Tempest is active (or not yet unlocked) before
        // spending gauge on powerful GCDs. If it's about to fall off, let the
        // combo section below handle refreshing it first.
        bool hasSurgingTempestSafety = !SurgingTempestWillEnd(3) || !StormsEyePvE.EnoughLevel;

        // === BMR DOWNTIME AWARENESS ===
        bool downtimeVeryClose = BmrActive && BmrDowntimeIn is > 0 and <= 3f;
        bool downtimeSoon = BmrActive && BmrDowntimeIn is > 0 and <= 8f;

        // =====================================================================
        // PRIORITY 1: Primal Ruination (follow-up to Primal Rend)
        // Use immediately - it has a limited-duration buff and high potency.
        // =====================================================================
        if (hasSurgingTempestSafety && PrimalRuinationPvEReady && PrimalRuinationPvE.CanUse(out act))
            return true;

        // =====================================================================
        // PRIORITY 2: Inner Chaos / Chaotic Cyclone (from Infuriate)
        // 580 potency (Inner Chaos) vs 520 (Fell Cleave). Always use before
        // the Nascent Chaos buff expires or before the next Infuriate charge.
        // Checked before IR stacks so we can weave Inner Chaos between
        // Fell Cleaves during the burst window.
        // =====================================================================
        if (hasSurgingTempestSafety)
        {
            if (ChaoticCyclonePvE.CanUse(out act))
                return true;
            if (InnerChaosPvE.CanUse(out act))
                return true;
        }

        // =====================================================================
        // PRIORITY 3: Fell Cleave / Decimate during Inner Release
        // IR grants 3 free uses that are guaranteed Critical Direct Hits.
        // Burn through all stacks quickly, then follow up with Primal Rend.
        // Don't use if Nascent Chaos is active - Inner Chaos takes priority
        // over regular Fell Cleave for higher potency.
        // =====================================================================
        if (hasSurgingTempestSafety && !HasNascentChaos && HasIRStacks)
        {
            if (DecimatePvE.CanUse(out act, skipStatusProvideCheck: true))
                return true;
            if (!DecimatePvE.Info.EnoughLevelAndQuest() && SteelCyclonePvE.CanUse(out act, skipStatusProvideCheck: true))
                return true;
            if (FellCleavePvE.CanUse(out act, skipStatusProvideCheck: true))
                return true;
            if (!FellCleavePvE.Info.EnoughLevelAndQuest() && InnerBeastPvE.CanUse(out act, skipStatusProvideCheck: true))
                return true;
        }

        // =====================================================================
        // PRIORITY 4: Primal Rend (after IR stacks are consumed)
        // High-potency gap closer GCD granted by Inner Release. Use after
        // all 3 Fell Cleave stacks are spent so it doesn't delay them.
        // =====================================================================
        if (hasSurgingTempestSafety && InnerReleaseStacks == 0
            && PrimalRendPvE.CanUse(out act, skipAoeCheck: true))
            return true;

        // =====================================================================
        // BMR: DUMP GAUGE BEFORE DOWNTIME
        // If downtime is approaching (<=8s), spend all Beast Gauge on Fell Cleave
        // / Decimate to avoid wasting resources during the untargetable phase.
        // Skip the Surging Tempest safety — it won't matter during downtime.
        // =====================================================================
        if (downtimeSoon && BeastGauge >= 50)
        {
            if (DecimatePvE.CanUse(out act, skipStatusProvideCheck: true))
                return true;
            if (!DecimatePvE.Info.EnoughLevelAndQuest() && SteelCyclonePvE.CanUse(out act))
                return true;
            if (FellCleavePvE.CanUse(out act, skipStatusProvideCheck: true))
                return true;
            if (!FellCleavePvE.Info.EnoughLevelAndQuest() && InnerBeastPvE.CanUse(out act))
                return true;
        }

        // =====================================================================
        // PRIORITY 5: Fell Cleave / Decimate outside IR (gauge spender)
        // Balance: "Pressing Fell Cleave at 60 or greater gauge can help
        // with avoiding overcapping." Spend gauge during party buffs when
        // possible, and always before overcapping.
        // =====================================================================
        if (hasSurgingTempestSafety)
        {
            // AoE gauge spender
            if (DecimatePvE.CanUse(out act, skipStatusProvideCheck: true))
                return true;
            if (!DecimatePvE.Info.EnoughLevelAndQuest() && SteelCyclonePvE.CanUse(out act))
                return true;

            // Single target gauge spender
            if (FellCleavePvE.CanUse(out act, skipStatusProvideCheck: true))
                return true;
            if (!FellCleavePvE.Info.EnoughLevelAndQuest() && InnerBeastPvE.CanUse(out act))
                return true;
        }

        // =====================================================================
        // BMR: DON'T START NEW COMBOS BEFORE DOWNTIME
        // If downtime is <=3s away, don't start a new combo — it'll drop during
        // the untargetable phase and waste the combo progress. Use Tomahawk or
        // finish current combo instead.
        // =====================================================================
        if (downtimeVeryClose)
        {
            // Still allow finishing an in-progress combo (CanUse checks combo state)
            if (StormsPathPvE.CanUse(out act))
                return true;
            if (StormsEyePvE.CanUse(out act))
                return true;
            if (MaimPvE.CanUse(out act))
                return true;
            // Don't start Heavy Swing — Tomahawk instead for a clean hit
            if (TomahawkPvE.CanUse(out act))
                return true;
            return base.GeneralGCD(out act);
        }

        // =====================================================================
        // AoE COMBO: Overpower -> Mythril Tempest
        // Mythril Tempest extends Surging Tempest by 30s (same as Storm's Eye).
        // =====================================================================
        if (MythrilTempestPvE.CanUse(out act))
            return true;
        if (OverpowerPvE.CanUse(out act))
            return true;

        // =====================================================================
        // SINGLE-TARGET COMBO
        //
        // Two paths diverge from Heavy Swing -> Maim:
        //   Storm's Eye path: grants/refreshes Surging Tempest (10% damage, 30s)
        //   Storm's Path path: generates 20 gauge (vs 10 from Eye)
        //
        // Balance: "Maintain Surging Tempest by refreshing between 7-15 seconds."
        // Inner Release also extends Surging Tempest by 10s, so factor that in.
        //
        // Strategy: Use Storm's Eye when Surging Tempest needs refreshing,
        // otherwise default to Storm's Path for more gauge generation.
        // =====================================================================
        if (StormsEyePvE.CanUse(out act))
            return true;
        if (StormsPathPvE.CanUse(out act))
            return true;
        if (MaimPvE.CanUse(out act))
            return true;
        if (HeavySwingPvE.CanUse(out act))
            return true;

        // =====================================================================
        // RANGED FALLBACK: Tomahawk
        // Use when out of melee range to maintain uptime.
        // =====================================================================
        if (TomahawkPvE.CanUse(out act))
            return true;

        return base.GeneralGCD(out act);
    }

    #endregion
}
