namespace RotationSolver.ExtraRotations.Ranged;

[Rotation("SezuraiDNC", CombatType.PvE, GameVersion = "7.41",
    Description = "Balance-aligned DNC with Technical Step burst, Esprit management, and proc optimization.")]
[SourceCode(Path = "main/ExtraRotations/Ranged/SezuraiDNC.cs")]
[ExtraRotation]
public sealed class SezuraiDNC : DancerRotation
{
    #region Config Options

    [Range(0f, 0.25f, ConfigUnitType.Percent)]
    [RotationConfig(CombatType.PvE, Name = "Action Ahead Override (0 = use global setting)")]
    public float ActionAheadOverride { get; set; } = 0f;

    [RotationConfig(CombatType.PvE, Name = "Auto Pot Usage (Gemdraught during Technical burst)")]
    public bool BurstMed { get; set; } = true;

    [Range(50, 80, ConfigUnitType.None)]
    [RotationConfig(CombatType.PvE, Name = "Esprit threshold to spend outside burst (50-80)")]
    public int EspritThreshold { get; set; } = 70;

    [RotationConfig(CombatType.PvE, Name = "Hold Technical Step if no targets in range (may drift)")]
    public bool HoldTechForTargets { get; set; } = true;

    [RotationConfig(CombatType.PvE, Name = "Prevent defense abilities during burst")]
    public bool NoDefenseInBurst { get; set; } = true;

    [Range(3, 4, ConfigUnitType.None, 1)]
    [RotationConfig(CombatType.PvE, Name = "Feather stacks to pool before spending (3 or 4)")]
    public int FeatherThreshold { get; set; } = 3;

    #endregion

    #region Burst State

    /// <summary>
    /// Framework burst enabled.
    /// </summary>
    private bool CanBurst => MergedStatus.HasFlag(AutoStatus.Burst);

    /// <summary>
    /// True during the Technical Finish + Devilment burst window (120s cycle).
    /// </summary>
    private bool InBurstWindow => HasDevilment && HasTechnicalFinish;

    /// <summary>
    /// Tillana is available (FlourishingFinish status from Technical Finish).
    /// Using TillanaPvEReady which checks the adjusted action ID.
    /// </summary>
    private static bool HasTillana => TillanaPvEReady;

    /// <summary>
    /// Finishing Move is available (from Flourish granting FinishingMoveReady).
    /// </summary>
    private static bool HasFinishingMove =>
        StatusHelper.PlayerHasStatus(true, StatusID.FinishingMoveReady);

    /// <summary>
    /// Dance of the Dawn is available (from Technical Finish granting DanceOfTheDawnReady).
    /// </summary>
    private static bool HasDanceOfTheDawn =>
        StatusHelper.PlayerHasStatus(true, StatusID.DanceOfTheDawnReady);

    /// <summary>
    /// Any combo proc is active (Silken or Flourishing Symmetry/Flow).
    /// </summary>
    private static bool HasAnyProc =>
        HasSilkenSymmetry || HasSilkenFlow || HasFlourishingSymmetry || HasFlourishingFlow;

    /// <summary>
    /// Dance targets are within 15y (Technical/Standard Finish AoE range).
    /// </summary>
    private static bool AreDanceTargetsInRange =>
        AllHostileTargets.Any(t => t.DistanceToPlayer() <= 15);

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
        ImGui.Text("--- Burst ---");
        ImGui.Text($"CanBurst: {CanBurst}");
        ImGui.Text($"InBurstWindow: {InBurstWindow}");
        ImGui.Text($"HasDevilment: {HasDevilment}");
        ImGui.Text($"HasTechnicalFinish: {HasTechnicalFinish}");
        ImGui.Text($"Medicated: {StatusHelper.PlayerHasStatus(true, StatusID.Medicated)}");
        ImGui.Text("--- Gauge ---");
        ImGui.Text($"Esprit: {Esprit}");
        ImGui.Text($"Feathers: {Feathers}");
        ImGui.Text($"IsDancing: {IsDancing}");
        ImGui.Text($"CompletedSteps: {CompletedSteps}");
        ImGui.Text("--- Procs ---");
        ImGui.Text($"HasTillana: {HasTillana}");
        ImGui.Text($"HasFinishingMove: {HasFinishingMove}");
        ImGui.Text($"HasLastDance: {HasLastDance}");
        ImGui.Text($"HasDanceOfTheDawn: {HasDanceOfTheDawn}");
        ImGui.Text($"HasFlourishingStarfall: {HasFlourishingStarfall}");
        ImGui.Text($"HasThreefoldFanDance: {HasThreefoldFanDance}");
        ImGui.Text($"HasFourfoldFanDance: {HasFourfoldFanDance}");
        ImGui.Text($"HasAnyProc: {HasAnyProc}");
        ImGui.Text("--- Steps ---");
        ImGui.Text($"HasStandardFinish: {HasStandardFinish}");
        ImGui.Text($"HasStandardStep: {HasStandardStep}");
        ImGui.Text($"HasTechnicalStep: {HasTechnicalStep}");
        ImGui.Text($"HasClosedPosition: {HasClosedPosition}");
        ImGui.Text("--- CDs ---");
        ImGui.Text($"TechStep: {(TechnicalStepPvE.Cooldown.IsCoolingDown ? $"{TechnicalStepPvE.Cooldown.RecastTimeRemain:F1}s" : "Ready")}");
        ImGui.Text($"StdStep: {(StandardStepPvE.Cooldown.IsCoolingDown ? $"{StandardStepPvE.Cooldown.RecastTimeRemain:F1}s" : "Ready")}");
        ImGui.Text($"Devilment: {(DevilmentPvE.Cooldown.IsCoolingDown ? $"{DevilmentPvE.Cooldown.RecastTimeRemain:F1}s" : "Ready")}");
        ImGui.Text($"Flourish: {(FlourishPvE.Cooldown.IsCoolingDown ? $"{FlourishPvE.Cooldown.RecastTimeRemain:F1}s" : "Ready")}");
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

    #region Countdown & Opener
    // === DNC OPENER (7.4 Balance) ===
    // Pre-pull: Closed Position → Standard Step(-15.5s) → dance steps → Pot(-1.5s) → Standard Finish(-0.5s)
    // GCD1: Technical Step → dance steps → Technical Finish
    // → Devilment (weave) → Tillana → Flourish (weave)
    // → Dance of the Dawn → Fan Dance IV (weave)
    // → Last Dance → Fan Dance III (weave) → Starfall Dance
    // → Finishing Move → Saber Dance (Esprit dump) → proc GCDs under buffs
    //
    // === BURST WINDOWS (120s cycle) ===
    // Technical Finish (5% party buff) + Devilment (personal crit/DH)
    // Tillana → DotD → Last Dance → Starfall → Finishing Move → Saber spam
    // Flourish → Fan Dance IV + Fan Dance III + feather dump under full buffs
    // All major CDs are 120s — every burst window is identical
    //
    // === FILLER / SUSTAIN ===
    // Standard Step: refresh on CD (60s), provides Standard Finish buff
    // Feather management: spend at 3-4 feathers to avoid overcap (4 max)
    // Esprit gauge: Saber Dance at 50+ Esprit, pool for burst if close
    // Proc priority: use Fountainfall/Reverse Cascade procs ASAP (don't lose them)
    // Fan Dance III: use on proc from Fan Dance I/II (don't hold)
    // Combo: Cascade → Fountain (build feathers + Esprit)

    protected override IAction? CountDownAction(float remainTime)
    {
        // Dance Partner setup
        if (TryUseClosedPosition(out var act))
            return act;

        // Standard Step at ~15.5s prepull (2 dance steps + hold finish until ~0.5s)
        if (remainTime <= 15.5f && StandardStepPvE.CanUse(out act, skipAoeCheck: true))
            return act;

        // Execute dance steps during countdown
        if (ExecuteStepGCD(out act))
            return act;

        // Finish Standard Step at ~0.5s before pull
        if (remainTime <= 0.5f && DoubleStandardFinishPvE.CanUse(out act, skipAoeCheck: true))
            return act;

        // Medicine at ~1.5s before pull (during Technical burst opener)
        if (BurstMed && remainTime <= 1.5f && UseBurstMedicine(out act))
            return act;

        return base.CountDownAction(remainTime);
    }

    #endregion

    #region Emergency Ability

    protected override bool EmergencyAbility(IAction nextGCD, out IAction? act)
    {
        act = null;

        // Dance Partner management
        if (TryUseClosedPosition(out act))
            return true;

        // Swap out dead/weakened dance partner
        if (TrySwapDancePartner(out act))
            return true;

        // === DEVILMENT ===
        // Must fire immediately after Technical Finish to align with raid buffs.
        // Pre-Tech level: use after Standard Finish.
        if (!IsDancing && DevilmentPvE.EnoughLevel && !DevilmentPvE.Cooldown.IsCoolingDown)
        {
            if (HasTechnicalFinish || IsLastGCD(true, QuadrupleTechnicalFinishPvE))
            {
                act = DevilmentPvE;
                return true;
            }

            // Pre-Technical Step level: use after Standard Finish
            if (!TechnicalStepPvE.EnoughLevel
                && (HasStandardFinish || IsLastGCD(true, DoubleStandardFinishPvE)))
            {
                act = DevilmentPvE;
                return true;
            }
        }

        // === MEDICINE ===
        // Use during Technical Finish burst or when Technical Step is about to come off CD
        if (BurstMed && InCombat)
        {
            if (HasTechnicalFinish && HasDevilment
                && !StatusHelper.PlayerHasStatus(true, StatusID.Medicated)
                && UseBurstMedicine(out act))
                return true;

            // Also pot just before Technical Step becomes available
            if (TechnicalStepPvE.EnoughLevel
                && TechnicalStepPvE.Cooldown.WillHaveOneCharge(5)
                && !StatusHelper.PlayerHasStatus(true, StatusID.Medicated)
                && UseBurstMedicine(out act))
                return true;
        }

        // Don't weave other emergency abilities while dancing or about to dance
        if (IsDancing)
            return base.EmergencyAbility(nextGCD, out act);

        return base.EmergencyAbility(nextGCD, out act);
    }

    #endregion

    #region Defense

    [RotationDesc(ActionID.ShieldSambaPvE)]
    protected override bool DefenseAreaAbility(IAction nextGCD, out IAction? act)
    {
        // BMR-aware: Shield Samba when raidwide imminent (override burst-skip)
        bool rwSoon = BmrActive && BmrRaidwideIn is > 0 and <= 5f;

        if (rwSoon)
        {
            if (ShieldSambaPvE.CanUse(out act))
                return true;
            return base.DefenseAreaAbility(nextGCD, out act);
        }

        // Non-BMR: skip during burst if configured
        if (NoDefenseInBurst && InBurstWindow)
            return base.DefenseAreaAbility(nextGCD, out act);

        if (ShieldSambaPvE.CanUse(out act))
            return true;

        return base.DefenseAreaAbility(nextGCD, out act);
    }

    [RotationDesc(ActionID.CuringWaltzPvE, ActionID.ImprovisationPvE)]
    protected override bool HealAreaAbility(IAction nextGCD, out IAction? act)
    {
        // Don't heal during dances
        if (IsDancing)
            return base.HealAreaAbility(nextGCD, out act);

        if (CuringWaltzPvE.CanUse(out act))
            return true;

        if (ImprovisationPvE.CanUse(out act))
            return true;

        return base.HealAreaAbility(nextGCD, out act);
    }

    [RotationDesc(ActionID.EnAvantPvE)]
    protected override bool MoveForwardAbility(IAction nextGCD, out IAction? act)
    {
        if (EnAvantPvE.CanUse(out act, usedUp: true))
            return true;
        return base.MoveForwardAbility(nextGCD, out act);
    }

    #endregion

    #region Attack Ability (oGCD)

    protected override bool AttackAbility(IAction nextGCD, out IAction? act)
    {
        act = null;

        // Never weave while dancing
        if (IsDancing)
            return base.AttackAbility(nextGCD, out act);

        // === FLOURISH (60s) ===
        // Grants: Flourishing Symmetry, Flourishing Flow, Threefold Fan Dance,
        //         Fourfold Fan Dance, Finishing Move Ready.
        // Use during burst (Devilment + Technical Finish) ideally.
        // On odd minutes (no Technical), use on CD to avoid drift.
        // Never use when Threefold Fan Dance is already active (would waste proc).
        if (FlourishPvE.EnoughLevel && InCombat && !HasThreefoldFanDance)
        {
            if (InBurstWindow)
            {
                if (FlourishPvE.CanUse(out act))
                    return true;
            }
            else if (!TechnicalStepPvE.EnoughLevel
                || (TechnicalStepPvE.Cooldown.IsCoolingDown
                    && !TechnicalStepPvE.Cooldown.WillHaveOneCharge(15)))
            {
                // Odd-minute Flourish: use freely when Tech is far away
                if (FlourishPvE.CanUse(out act))
                    return true;
            }
        }

        // === FAN DANCE IV (oGCD, from Flourish) ===
        // Fourfold Fan Dance proc - high potency, use ASAP
        if (HasFourfoldFanDance && FanDanceIvPvE.CanUse(out act, skipAoeCheck: true))
            return true;

        // === FAN DANCE III (oGCD, proc from Fan Dance I/II) ===
        // Threefold Fan Dance proc - use ASAP to avoid wasting
        if (HasThreefoldFanDance && FanDanceIiiPvE.CanUse(out act, skipAoeCheck: true))
            return true;

        // === FAN DANCE I/II (Feather spending) ===
        // During burst: spend all feathers (they deal more under Devilment/Technical)
        // Outside burst: spend at 3-4 feathers when procs are active (prevent overcap)
        //   Reverse Cascade / Fountainfall have 50% chance to grant feathers
        //   Flourish will grant Threefold, so dump before Flourish if 4 feathers
        {
            bool shouldSpendFeathers = false;

            // During burst: spend feathers freely
            if (InBurstWindow)
                shouldSpendFeathers = true;

            // At or above feather threshold with procs active: spend to prevent overcap
            if (Feathers >= FeatherThreshold && HasAnyProc)
                shouldSpendFeathers = true;

            // Near threshold and Flourish coming soon: dump to make room
            if (Feathers >= FeatherThreshold && FlourishPvE.EnoughLevel
                && FlourishPvE.Cooldown.WillHaveOneCharge(4))
                shouldSpendFeathers = true;

            // At max feathers: always spend regardless
            if (Feathers >= 4)
                shouldSpendFeathers = true;

            if (shouldSpendFeathers && Feathers > 0)
            {
                // AoE: Fan Dance II (3+ targets)
                if (FanDanceIiPvE.CanUse(out act))
                    return true;
                // ST: Fan Dance I
                if (FanDancePvE.CanUse(out act))
                    return true;
            }
        }

        return base.AttackAbility(nextGCD, out act);
    }

    #endregion

    #region GCD Logic

    protected override bool GeneralGCD(out IAction? act)
    {
        // === DANCE COMPLETION ===
        // If we are mid-dance, finish the dance steps and then the Finish.
        // NEVER break a dance once started.
        if (IsDancing)
        {
            // Execute dance steps (Emboite/Entrechat/Jete/Pirouette)
            if (ExecuteStepGCD(out act))
                return true;

            // Finish the dance immediately when steps are complete.
            // finishNow: true prevents a stall where CanUse fails for a framework
            // reason but the status isn't expiring yet — without this the rotation
            // would return null and the character would stand idle forever.
            if (DanceFinishGCD(out act, finishNow: true))
                return true;

            // Safety fallback: if we're somehow stuck in dance with no step or finish
            // resolving, try the raw finish actions directly to break the stall.
            if (HasStandardStep && CompletedSteps == 2)
            {
                act = DoubleStandardFinishPvE;
                return true;
            }
            if (HasTechnicalStep && CompletedSteps == 4)
            {
                act = QuadrupleTechnicalFinishPvE;
                return true;
            }

            act = null;
            return false;
        }

        // === TECHNICAL STEP (120s, main raid buff + DPS burst) ===
        // Must have Standard Finish active first (base ActionCheck enforces this).
        // Technical Step starts 4-step dance -> Technical Finish (raid-wide 5% damage for 20s).
        if (CanBurst && InCombat && TechnicalStepPvE.EnoughLevel)
        {
            if (HoldTechForTargets && !AreDanceTargetsInRange)
            {
                // Hold Tech if no targets in range (user config)
            }
            else if (TechnicalStepPvE.CanUse(out act, skipAoeCheck: true))
            {
                return true;
            }
        }

        // === TILLANA (follow-up to Technical Finish) ===
        // Grants 50 Esprit. Use when Esprit <= 50 to avoid overcap.
        // The base ActionCheck already gates Esprit <= 50 and TillanaPvEReady.
        // During burst, delay Tillana if we have enough Esprit for Saber Dance / Dance of the Dawn.
        if (HasTillana && TryUseTillana(out act))
            return true;

        // === DANCE OF THE DAWN (from Technical Finish, costs 50 Esprit) ===
        // Highest potency GCD (1000p). Use early in burst after Tillana.
        // Requires Esprit >= 50 and DanceOfTheDawnReady status.
        if (HasDanceOfTheDawn && Esprit >= 50)
        {
            if (InBurstWindow && DanceOfTheDawnPvE.CanUse(out act, skipAoeCheck: true))
                return true;

            // Outside burst: use before status expires
            if (StatusHelper.PlayerWillStatusEnd(5, true, StatusID.DanceOfTheDawnReady)
                && DanceOfTheDawnPvE.CanUse(out act, skipAoeCheck: true))
                return true;
        }

        // === LAST DANCE (from Standard Finish / Finishing Move) ===
        // 30s duration. Use during burst after DotD, or before it expires, or as filler.
        // Must be used before Finishing Move (FM also grants Last Dance, would overwrite).
        if (HasLastDance)
        {
            bool ldUrgent = StatusHelper.PlayerWillStatusEnd(5, true, StatusID.LastDanceReady);
            bool ldInBurst = InBurstWindow;
            bool ldFiller = !InBurstWindow && Esprit < EspritThreshold
                && (!TechnicalStepPvE.EnoughLevel || !TechnicalStepPvE.Cooldown.WillHaveOneCharge(15));

            if ((ldUrgent || ldInBurst || ldFiller) && LastDancePvE.CanUse(out act, skipAoeCheck: true))
                return true;
        }

        // === STARFALL DANCE (from Devilment, Flourishing Starfall) ===
        // 600p guaranteed crit+DH under Devilment. 20s duration.
        // Use after DotD and Last Dance during burst.
        if (HasFlourishingStarfall)
        {
            bool starfallUrgent = StatusHelper.PlayerWillStatusEnd(7, true, StatusID.FlourishingStarfall);
            bool starfallInBurst = InBurstWindow && !HasTillana;

            if ((starfallUrgent || starfallInBurst) && StarfallDancePvE.CanUse(out act, skipAoeCheck: true))
                return true;
        }

        // === FINISHING MOVE (from Flourish, replaces Standard Step button) ===
        // Acts like a free Standard Finish without dancing. Grants Last Dance Ready.
        // Use during burst after Last Dance is consumed (to avoid overwriting the proc).
        // Outside burst, use when available but don't hold too long.
        if (HasFinishingMove && !HasLastDance)
        {
            bool fmUrgent = StatusHelper.PlayerWillStatusEnd(5, true, StatusID.FinishingMoveReady);

            if ((InBurstWindow || fmUrgent) && FinishingMovePvE.CanUse(out act, skipAoeCheck: true))
                return true;
        }

        // === SABER DANCE (Esprit spender, 50 cost) ===
        // During burst: spend at 50+ Esprit (everything hits harder)
        // Outside burst: spend at threshold (config, default 70) to avoid overcap
        // Always spend at 80+ to prevent overcap regardless of context
        if (Esprit >= 50 && !HasDanceOfTheDawn)
        {
            bool spendEsprit = false;

            if (InBurstWindow)
                spendEsprit = true;
            else if (Esprit >= 80)
                spendEsprit = true; // Hard overcap protection
            else if (Esprit >= EspritThreshold)
                spendEsprit = true;
            else if (StatusHelper.PlayerHasStatus(true, StatusID.Medicated) && Esprit >= 50)
                spendEsprit = true; // Always spend under pot

            if (spendEsprit && SaberDancePvE.CanUse(out act, skipAoeCheck: true))
                return true;
        }

        // === STANDARD STEP (30s buff cycle) ===
        // Refreshes Standard Finish buff (5% partner damage + Esprit generation).
        // Don't start Standard Step if Technical Step is about to come off CD (within 5s).
        // After Flourish: Finishing Move replaces Standard Step (handled above).
        if (!HasFinishingMove && !HasLastDance)
        {
            if (TryUseStandardStep(out act))
                return true;
        }

        // Finishing Move outside burst (non-urgent, but should still use)
        if (HasFinishingMove && FinishingMovePvE.CanUse(out act, skipAoeCheck: true))
            return true;

        // Last Dance filler (any remaining Last Dance we haven't used yet)
        if (HasLastDance && LastDancePvE.CanUse(out act, skipAoeCheck: true))
            return true;

        // Starfall Dance outside burst (don't let it expire)
        if (HasFlourishingStarfall && StarfallDancePvE.CanUse(out act, skipAoeCheck: true))
            return true;

        // Saber Dance overcap protection at 80+
        if (Esprit >= 80 && SaberDancePvE.CanUse(out act, skipAoeCheck: true))
            return true;

        // === PROC GCDs ===
        // Proc priority: Fountainfall / Bloodshower > Reverse Cascade / Rising Windmill
        // (Flourishing versions share priority with Silken versions)
        // These generate Feathers (50% chance) and 10 Esprit each.

        // AoE procs (3+ targets handled by CanUse AoeCount)
        if (BloodshowerPvE.CanUse(out act))
            return true;
        if (FountainfallPvE.CanUse(out act))
            return true;
        if (RisingWindmillPvE.CanUse(out act))
            return true;
        if (ReverseCascadePvE.CanUse(out act))
            return true;

        // === BASIC COMBO ===
        // Cascade -> Fountain (ST), Windmill -> Bladeshower (AoE 3+)
        // These generate 5 Esprit each and have a chance to proc Silken Symmetry/Flow.
        if (BladeshowerPvE.CanUse(out act))
            return true;
        if (FountainPvE.CanUse(out act))
            return true;
        if (WindmillPvE.CanUse(out act))
            return true;
        if (CascadePvE.CanUse(out act))
            return true;

        return base.GeneralGCD(out act);
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Tillana usage logic. Tillana grants 50 Esprit and is gated at Esprit &lt;= 50
    /// by the base ActionCheck. During burst, we want to delay Tillana until we've
    /// spent Esprit via Saber Dance / Dance of the Dawn to avoid overcapping the +50.
    /// </summary>
    private bool TryUseTillana(out IAction? act)
    {
        act = null;

        // Urgent: status about to expire
        if (StatusHelper.PlayerWillStatusEnd(3, true, StatusID.FlourishingFinish))
            return TillanaPvE.CanUse(out act, skipAoeCheck: true);

        // During burst: delay if we have Esprit to spend first
        if (InBurstWindow && Esprit >= 50)
            return false;

        // Normal usage: Esprit is low enough (base check handles <= 50)
        return TillanaPvE.CanUse(out act, skipAoeCheck: true);
    }

    /// <summary>
    /// Standard Step usage with proper hold logic.
    /// Don't start if Technical Step is imminent (within 5s).
    /// Don't start if Standard Finish buff is still healthy.
    /// </summary>
    private bool TryUseStandardStep(out IAction? act)
    {
        act = null;

        if (!StandardStepPvE.EnoughLevel)
            return false;

        // Don't start Standard Step if Technical Step is coming within 5s
        if (TechnicalStepPvE.EnoughLevel
            && InCombat
            && HasStandardFinish
            && TechnicalStepPvE.Cooldown.WillHaveOneCharge(5))
            return false;

        // Standard Finish about to drop: refresh urgently
        if (StatusHelper.PlayerWillStatusEnd(5, true, StatusID.StandardFinish))
        {
            if (StandardStepPvE.CanUse(out act, skipAoeCheck: true))
                return true;
        }

        // Normal usage: use when available
        if (StandardStepPvE.CanUse(out act, skipAoeCheck: true))
            return true;

        return false;
    }

    /// <summary>
    /// Apply Closed Position to a dance partner if we don't have one.
    /// </summary>
    private bool TryUseClosedPosition(out IAction? act)
    {
        act = null;

        if (HasClosedPosition || !PartyMembers.Any() || !ClosedPositionPvE.IsEnabled)
            return false;

        return ClosedPositionPvE.CanUse(out act);
    }

    /// <summary>
    /// Swap dance partner if current one is dead/weakened and a step is not imminent.
    /// </summary>
    private bool TrySwapDancePartner(out IAction? act)
    {
        act = null;

        if (!HasClosedPosition || CurrentDancePartner == null)
            return false;

        bool partnerBad = CurrentDancePartner.IsDead
            || CurrentDancePartner.HasStatus(false,
                StatusID.Weakness, StatusID.BrinkOfDeath,
                StatusID.DamageDown, StatusID.DamageDown_2911);

        if (!partnerBad)
            return false;

        // Don't swap right before a step
        if (StandardStepPvE.Cooldown.WillHaveOneCharge(3)
            || TechnicalStepPvE.Cooldown.WillHaveOneCharge(3))
            return false;

        return EndingPvE.CanUse(out act);
    }

    #endregion
}
