namespace RotationSolver.ExtraRotations.Melee;

[Rotation("SezuraiDRG", CombatType.PvE, GameVersion = "7.41", Description = "Balance-aligned DRG with proper burst ordering, Life Surge targeting, and Geirskogul timing.")]
[SourceCode(Path = "main/ExtraRotations/Melee/SezuraiDRG.cs")]
[ExtraRotation]
public sealed class SezuraiDRG : DragoonRotation
{
    #region Config Options

    [RotationConfig(CombatType.PvE, Name = "Use Doom Spike for damage uptime if out of melee range even if it breaks combo")]
    public bool DoomSpikeWhenever { get; set; } = true;

    [Range(1, 20, ConfigUnitType.Yalms)]
    [RotationConfig(CombatType.PvE, Name = "Max distance from target for Stardiver usage")]
    public float StardiverDistance { get; set; } = 20;

    [Range(1, 20, ConfigUnitType.Yalms)]
    [RotationConfig(CombatType.PvE, Name = "Max distance from target for Dragonfire Dive usage")]
    public float DragonfireDiveDistance { get; set; } = 20;

    [RotationConfig(CombatType.PvE, Name = "Experimental Pot Usage (during Battle Litany windows)")]
    public bool BurstMed { get; set; } = false;

    #endregion

    #region Burst State

    /// <summary>
    /// Whether the user has burst enabled in the framework.
    /// </summary>
    private bool CanBurst => MergedStatus.HasFlag(AutoStatus.Burst);

    /// <summary>
    /// True during even-minute burst windows (Battle Litany active).
    /// Even minutes have: Lance Charge + Battle Litany + Geirskogul + Dragonfire Dive.
    /// </summary>
    private bool InEvenBurst => HasBattleLitany;

    /// <summary>
    /// True when Life of the Dragon is active (15% damage buff from Geirskogul).
    /// </summary>
    private bool InLOTD => LOTDTime > 0;

    #endregion

    #region Status Display

    public override void DisplayRotationStatus()
    {
        ImGui.Text($"CanBurst: {CanBurst}");
        ImGui.Text($"InEvenBurst: {InEvenBurst}");
        ImGui.Text($"InLOTD: {InLOTD}");
        ImGui.Text($"LOTDTime: {LOTDTime:F1}");
        ImGui.Text($"FocusCount: {FocusCount}");
        ImGui.Text($"HasLanceCharge: {HasLanceCharge}");
        ImGui.Text($"HasBattleLitany: {HasBattleLitany}");
        ImGui.Text($"HasPowerSurge: {HasPowerSurge}");
        ImGui.Text($"HasDraconianFire: {HasDraconianFire}");
    }

    #endregion

    #region Additional oGCD Logic

    [RotationDesc(ActionID.WingedGlidePvE)]
    protected override bool MoveForwardAbility(IAction nextGCD, out IAction? act)
    {
        if (IsLastAction(false, StardiverPvE))
            return base.MoveForwardAbility(nextGCD, out act);
        if (WingedGlidePvE.CanUse(out act, skipComboCheck: true))
            return true;
        return base.MoveForwardAbility(nextGCD, out act);
    }

    [RotationDesc(ActionID.ElusiveJumpPvE)]
    protected override bool MoveBackAbility(IAction nextGCD, out IAction? act)
    {
        if (IsLastAction(false, StardiverPvE))
            return base.MoveBackAbility(nextGCD, out act);
        if (ElusiveJumpPvE.CanUse(out act, skipComboCheck: true))
            return true;
        return base.MoveBackAbility(nextGCD, out act);
    }

    [RotationDesc(ActionID.FeintPvE)]
    protected sealed override bool DefenseAreaAbility(IAction nextGCD, out IAction? act)
    {
        if (IsLastAction(false, StardiverPvE))
            return base.DefenseAreaAbility(nextGCD, out act);
        if (FeintPvE.CanUse(out act, skipComboCheck: true))
            return true;
        return base.DefenseAreaAbility(nextGCD, out act);
    }

    [RotationDesc]
    protected override bool HealSingleAbility(IAction nextGCD, out IAction? act)
    {
        if (SecondWindPvE.CanUse(out act))
            return true;
        if (BloodbathPvE.CanUse(out act))
            return true;
        return base.HealSingleAbility(nextGCD, out act);
    }

    [RotationDesc]
    protected sealed override bool AntiKnockbackAbility(IAction nextGCD, out IAction? act)
    {
        if (ArmsLengthPvE.CanUse(out act))
            return true;
        return base.AntiKnockbackAbility(nextGCD, out act);
    }

    [RotationDesc]
    protected sealed override bool InterruptAbility(IAction nextGCD, out IAction? act)
    {
        if (LegSweepPvE.CanUse(out act))
            return true;
        return base.InterruptAbility(nextGCD, out act);
    }

    #endregion

    #region oGCD Logic

    protected override bool EmergencyAbility(IAction nextGCD, out IAction? act)
    {
        // Stardiver: single-weave only (1.5s animation lock), distance check
        // Must be in LOTD. Place early in LOTD window for flexibility.
        if (IsLastAction() == IsLastGCD())
        {
            if (StardiverPvE.CanUse(out act))
            {
                if (StardiverPvE.Target.Target.DistanceToPlayer() <= StardiverDistance)
                    return true;
            }
        }

        // Medicine: use during Battle Litany windows
        if (BurstMed && HasBattleLitany && InCombat && UseBurstMedicine(out act))
            return true;

        return base.EmergencyAbility(nextGCD, out act);
    }

    protected override bool AttackAbility(IAction nextGCD, out IAction? act)
    {
        // Don't weave anything immediately after Stardiver (animation lock)
        if (IsLastAction(false, StardiverPvE))
            return base.AttackAbility(nextGCD, out act);

        // Don't use burst oGCDs without Power Surge active
        if (DisembowelPvE.EnoughLevel && !HasPowerSurge)
            return base.AttackAbility(nextGCD, out act);

        // === BUFF APPLICATION (burst enabled, in combat, hostiles in range) ===
        if (CanBurst && InCombat && HasHostilesInRange)
        {
            // 1. Lance Charge: use on cooldown (60s), gate behind BL timing on even minutes
            // Guide: "use this button as soon as it is available"
            if ((!BattleLitanyPvE.Cooldown.ElapsedAfter(60) || !BattleLitanyPvE.EnoughLevel)
                && LanceChargePvE.CanUse(out act))
            {
                return true;
            }

            // 2. Battle Litany: raid buff, align with 2-minute party bursts
            if (BattleLitanyPvE.CanUse(out act))
                return true;

            // 3. Life Surge: guaranteed crit on next weaponskill
            // Guide: target Heavens' Thrust or Drakesbane (both 460 potency)
            // NOT on Chaotic Spring (DoT doesn't benefit from LS crit)
            // Use both charges during even burst (BL active), hold one for odd
            {
                bool lsOnBigHit = nextGCD.IsTheSameTo(true, HeavensThrustPvE, DrakesbanePvE);
                bool lsOnAoE = nextGCD.IsTheSameTo(true, CoerthanTormentPvE);

                // Low level fallback: use on best available GCD
                bool lsLowLevel =
                    (!DisembowelPvE.EnoughLevel && nextGCD.IsTheSameTo(true, VorpalThrustPvE)) ||
                    (!FullThrustPvE.EnoughLevel && nextGCD.IsTheSameTo(true, VorpalThrustPvE, DisembowelPvE)) ||
                    (!HeavensThrustPvE.EnoughLevel && !DrakesbanePvE.EnoughLevel &&
                     nextGCD.IsTheSameTo(true, FullThrustPvE));

                if ((lsOnBigHit || lsOnAoE || lsLowLevel) && HasLanceCharge)
                {
                    // Even burst: spend both charges. Odd burst: hold one.
                    if (LifeSurgePvE.CanUse(out act, usedUp: InEvenBurst))
                        return true;
                }
            }
        }

        // === GEIRSKOGUL: enters Life of the Dragon (15% damage buff for 20s) ===
        // Guide: "used last to ensure its own potency is buffed by all our personal buffs"
        // Gate behind HasLanceCharge so it fires AFTER Lance Charge is applied.
        // This naturally orders: LC → BL → Geirskogul in the opener.
        if ((HasLanceCharge || !LanceChargePvE.EnoughLevel) && GeirskogulPvE.CanUse(out act))
            return true;

        // === HARD COOLDOWN JUMPS (use on cooldown, don't drift) ===

        // 4. High Jump (30s CD): most frequent oGCD, generates Dive Ready for Mirage Dive
        // Guide: "use it every 30s...it is still important to use them on cooldown"
        if (HighJumpPvE.CanUse(out act))
            return true;

        // Low level: Jump replaces High Jump
        if (!HighJumpPvE.EnoughLevel && LanceChargePvE.Cooldown.IsCoolingDown)
        {
            if (JumpPvE.CanUse(out act))
                return true;
        }

        // 5. Dragonfire Dive (120s CD): use during burst windows (BL or LOTD active)
        if (InEvenBurst || InLOTD || !BattleLitanyPvE.EnoughLevel)
        {
            if (DragonfireDivePvE.CanUse(out act))
            {
                if (DragonfireDivePvE.Target.Target.DistanceToPlayer() <= DragonfireDiveDistance)
                    return true;
            }
        }

        // === FOLLOW-UP ABILITIES & FLEXIBLE oGCDs ===

        // 6. Starcross: follow-up to Stardiver, use immediately
        if (StarcrossPvE.CanUse(out act))
            return true;

        // 7. Rise of the Dragon: follow-up to Dragonfire Dive, use immediately
        if (RiseOfTheDragonPvE.CanUse(out act))
            return true;

        // 8. Nastrond: one use per LOTD window, 720 potency line AoE
        if (NastrondPvE.CanUse(out act))
            return true;

        // 9. Mirage Dive: follow-up to High Jump (15s Dive Ready buff)
        if (MirageDivePvE.CanUse(out act))
            return true;

        // 10. Wyrmwind Thrust: flexible, prefer in buffs but MUST use before overcap
        // Guide: "you have up until the next Raiden Thrust to use WWT in order to not overcap"
        // Use in buffs, during LOTD, or when next GCD would grant a Focus stack (overcap)
        if (HasBattleLitany || HasLanceCharge || InLOTD
            || nextGCD.IsTheSameTo(true, RaidenThrustPvE, DraconianFuryPvE))
        {
            if (WyrmwindThrustPvE.CanUse(out act, usedUp: true))
                return true;
        }

        return base.AttackAbility(nextGCD, out act);
    }

    #endregion

    #region GCD Logic

    protected override bool GeneralGCD(out IAction? act)
    {
        bool doomSpikeRightNow = DoomSpikeWhenever;

        // === AoE Combo (3+ targets) ===
        if (CoerthanTormentPvE.CanUse(out act))
            return true;
        if (SonicThrustPvE.CanUse(out act, skipStatusProvideCheck: true))
            return true;

        // AoE combo starter (Draconian Fury or Doom Spike)
        if (LanceMasteryTrait.EnoughLevel)
        {
            if (HasDraconianFire)
            {
                if (DraconianFuryPvE.CanUse(out act, skipComboCheck: doomSpikeRightNow))
                    return true;
            }
            if (!HasDraconianFire)
            {
                if (DoomSpikePvE.CanUse(out act, skipComboCheck: doomSpikeRightNow))
                    return true;
            }
        }
        if (!LanceMasteryTrait.EnoughLevel)
        {
            if (DoomSpikePvE.CanUse(out act, skipComboCheck: doomSpikeRightNow))
                return true;
        }

        // === Single Target Combo ===

        // Combo finisher: Drakesbane (5th hit, no positional)
        if (DrakesbanePvE.CanUse(out act, skipStatusProvideCheck: true))
            return true;

        // 4th hits: Fang and Claw (flank) / Wheeling Thrust (rear)
        if (FangAndClawPvE.CanUse(out act))
            return true;
        if (WheelingThrustPvE.CanUse(out act))
            return true;

        // 3rd hits: Heavens' Thrust (HT combo) / Chaotic Spring (CS combo, rear + DoT)
        if (LanceMasteryIiTrait.EnoughLevel)
        {
            if (HeavensThrustPvE.CanUse(out act))
                return true;
        }
        if (!LanceMasteryIiTrait.EnoughLevel)
        {
            if (FullThrustPvE.CanUse(out act))
                return true;
        }

        if (LanceMasteryIiTrait.EnoughLevel)
        {
            if (ChaoticSpringPvE.CanUse(out act, skipStatusProvideCheck: true))
                return true;
        }
        if (!LanceMasteryIiTrait.EnoughLevel)
        {
            if (ChaosThrustPvE.CanUse(out act, skipStatusProvideCheck: true))
                return true;
        }

        // 2nd hits: alternation between CS combo (Spiral Blow) and HT combo (Lance Barrage)
        // Spiral Blow grants Power Surge (10% damage, 30s). Use CS path when Power Surge
        // needs refresh (within 6 GCDs). Otherwise use HT path for higher upfront damage.
        // Guide: "alternating the two combos will lead to a 10 GCDs basic rotation"
        if (LanceMasteryIvTrait.EnoughLevel)
        {
            if (SpiralBlowPvE.CanUse(out act, skipStatusProvideCheck: StatusHelper.PlayerWillStatusEndGCD(6, 0, true, StatusID.PowerSurge_2720)))
                return true;
        }
        if (!LanceMasteryIvTrait.EnoughLevel)
        {
            if (DisembowelPvE.CanUse(out act, skipStatusProvideCheck: StatusHelper.PlayerWillStatusEndGCD(6, 0, true, StatusID.PowerSurge_2720)))
                return true;
        }

        if (LanceMasteryIvTrait.EnoughLevel)
        {
            if (LanceBarragePvE.CanUse(out act))
                return true;
        }
        if (!LanceMasteryIvTrait.EnoughLevel)
        {
            if (VorpalThrustPvE.CanUse(out act))
                return true;
        }

        // 1st hit: Raiden Thrust (if Draconian Fire) or True Thrust
        if (LanceMasteryTrait.EnoughLevel)
        {
            if (HasDraconianFire)
            {
                if (RaidenThrustPvE.CanUse(out act))
                    return true;
            }
            if (!HasDraconianFire)
            {
                if (TrueThrustPvE.CanUse(out act))
                    return true;
            }
        }
        if (!LanceMasteryTrait.EnoughLevel)
        {
            if (TrueThrustPvE.CanUse(out act))
                return true;
        }

        // Ranged fallback: Piercing Talon (doesn't break combo)
        if (!IsLastAction(true, WingedGlidePvE) && PiercingTalonPvE.CanUse(out act))
            return true;

        return base.GeneralGCD(out act);
    }

    #endregion
}
