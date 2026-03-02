namespace RotationSolver.ExtraRotations.Melee;

[Rotation("SezuraiRPR", CombatType.PvE, GameVersion = "7.41", Description = "Balance-aligned RPR with double Enshroud burst, Arcane Circle alignment, and gauge optimization.")]
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
    // Shadow of Death: refresh when ≤30s remains (don't clip too early)
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
        if (SecondWindPvE.CanUse(out act))
            return true;
        if (BloodbathPvE.CanUse(out act))
            return true;
        return base.HealSingleAbility(nextGCD, out act);
    }

    [RotationDesc(ActionID.FeintPvE)]
    protected sealed override bool DefenseAreaAbility(IAction nextGCD, out IAction? act)
    {
        // Skip during active combos (Soul Reaver / Enshroud / Executioner)
        if (HasSoulReaver || HasEnshrouded || HasExecutioner)
            return base.DefenseAreaAbility(nextGCD, out act);

        // BMR-aware: Feint + Arcane Crest proactively when raidwide imminent
        // Arcane Crest grants party shield on break — great for raidwides
        bool rwSoon = BmrActive && BmrRaidwideIn is > 0 and <= 5f;

        if (rwSoon)
        {
            if (FeintPvE.CanUse(out act))
                return true;
            if (ArcaneCrestPvE.CanUse(out act))
                return true;
            return base.DefenseAreaAbility(nextGCD, out act);
        }

        // Non-BMR: Feint when framework triggers defense
        if (FeintPvE.CanUse(out act))
            return true;

        return base.DefenseAreaAbility(nextGCD, out act);
    }

    [RotationDesc(ActionID.ArcaneCrestPvE)]
    protected sealed override bool DefenseSingleAbility(IAction nextGCD, out IAction? act)
    {
        if (HasSoulReaver || HasEnshrouded || HasExecutioner)
            return base.DefenseSingleAbility(nextGCD, out act);

        if (ArcaneCrestPvE.CanUse(out act))
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

        // --- Arcane Circle (120s party buff) ---
        // Use when: burst enabled, Death's Design on target, not too early in combat
        if (CanBurst
            && (CurrentTarget?.HasStatus(true, StatusID.DeathsDesign) ?? false)
            && !CombatElapsedLess(3.5f)
            && ArcaneCirclePvE.CanUse(out act, skipAoeCheck: true))
        {
            return true;
        }

        // --- Enshroud ---
        // Priority 1: Ideal Host (free Enshroud from Plentiful Harvest) - always use immediately
        if (HasIdealHost && !HasExecutioner && EnshroudPvE.CanUse(out act))
        {
            return true;
        }

        // Priority 2: During Arcane Circle burst or when AC is coming up soon
        // The base class ActionCheck for Enshroud uses Soul >= 50, but Enshroud costs Shroud gauge.
        // We must check Shroud >= 50 ourselves before calling CanUse.
        if (!HasExecutioner && Shroud >= 50)
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
        if (!HasPerfectioParata && !HasImmortalSacrifice
            && (PlentifulHarvestPvE.EnoughLevel ? !HasBloodsownCircleSelf : true))
        {
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
        // ======================================================================

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

        // ======================================================================
        // 9. HARVEST MOON (ranged GCD from Soulsow)
        // Use when out of melee range for movement, or as a strong filler.
        // ======================================================================
        if (UseHarvestMoonRanged && InCombat && !HasSoulReaver && !HasHostilesInRange
            && HarvestMoonPvE.CanUse(out act, skipAoeCheck: true))
        {
            return true;
        }

        // ======================================================================
        // 10. SOUL SLICE / SOUL SCYTHE (gauge generation, 2 charges)
        // Generates 50 Soul directly. Use to avoid overcapping charges.
        // ======================================================================
        if (SoulScythePvE.CanUse(out act, usedUp: true))
            return true;

        if (SoulSlicePvE.CanUse(out act, usedUp: true))
            return true;

        // ======================================================================
        // 11. COMBO CHAIN (1-2-3 / AoE equivalents)
        // Basic combo to build Soul gauge (10 per hit).
        // ======================================================================

        // AoE combo
        if (NightmareScythePvE.CanUse(out act))
            return true;

        if (SpinningScythePvE.CanUse(out act))
            return true;

        // ST combo (don't break Executioner stacks)
        if (!HasExecutioner)
        {
            if (InfernalSlicePvE.CanUse(out act))
                return true;

            if (WaxingSlicePvE.CanUse(out act))
                return true;

            if (SlicePvE.CanUse(out act))
                return true;
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
