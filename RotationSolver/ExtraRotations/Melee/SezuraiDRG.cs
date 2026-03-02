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
    public bool BurstMed { get; set; } = true;

    [Range(0f, 0.25f, ConfigUnitType.Percent)]
    [RotationConfig(CombatType.PvE, Name = "Action Ahead Override (0 = use global setting)")]
    public float ActionAheadOverride { get; set; } = 0f;

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

    #region UpdateInfo

    protected override void UpdateInfo()
    {
        DataCenter.RotationActionAheadOverride = ActionAheadOverride > 0f ? ActionAheadOverride : null;
    }

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
        ImGui.Text($"--- BMR Timeline ---");
        ImGui.Text($"Active: {BmrActive}{(BmrActive ? $" ({DataCenter.BmrActiveModuleName})" : "")}");
        if (BmrActive)
        {
            ImGui.Text($"Raidwide In: {(BmrRaidwideIn < 9999f ? $"{BmrRaidwideIn:F1}s" : "None")}");
            ImGui.Text($"Knockback In: {(BmrKnockbackIn < 9999f ? $"{BmrKnockbackIn:F1}s" : "None")}");
            ImGui.Text($"Downtime In: {(BmrDowntimeIn < 9999f ? $"{BmrDowntimeIn:F1}s" : "None")}");
            ImGui.Text($"Vulnerable In: {(BmrVulnerableIn < 9999f ? $"{BmrVulnerableIn:F1}s" : "None")}");
        }
    }

    #endregion

    #region Countdown & Opener
    // === DRG OPENER (7.4 Balance) ===
    // Pre-pull: True North(-5s) → Pot(-2s)
    // GCD1: True Thrust → Lance Charge (weave) → Battle Litany (weave)
    // GCD2: Spiral Blow (grants Power Surge) → Life Surge (weave)
    // GCD3: Chaotic Spring (rear) → Geirskogul (weave, enters LotD) → High Jump (weave)
    // GCD4: Wheeling Thrust → Dragonfire Dive (weave) → Nastrond (weave)
    // GCD5: Fang and Claw → Starcross (weave) → Rise of the Dragon (weave)
    // GCD6: Raiden Thrust (proc from combo finisher) → Wyrmwind Thrust (weave) → Mirage Dive (weave)
    // Continue with Lance Barrage → Heavens' Thrust combo
    //
    // === EVEN BURST (120s) ===
    // Lance Charge + Battle Litany + Dragonfire Dive + Starcross + Rise of the Dragon
    // Full LotD phase: Nastrond x3 + Stardiver under raid buffs
    // Life Surge on Heavens' Thrust (highest potency single-target GCD)
    //
    // === ODD BURST (60s) ===
    // Lance Charge only — Battle Litany is 120s
    // Geirskogul → LotD → Nastrond chain, hold Dragonfire Dive for even
    //
    // === FILLER / SUSTAIN ===
    // 10-GCD repeating loop: True Thrust → Spiral Blow → Chaotic Spring →
    //   Wheeling Thrust → Fang and Claw → Raiden Thrust → Lance Barrage →
    //   Heavens' Thrust → Fang and Claw → Wheeling Thrust
    // Use Wyrmwind Thrust before next Raiden Thrust to avoid overcap (2 stacks max)
    // Life Surge on Heavens' Thrust or Chaotic Spring (highest potency GCDs)
    // Keep Power Surge buff active (refreshed by Spiral Blow)

    protected override IAction? CountDownAction(float remainTime)
    {
        // Pre-pull pot at ~2s
        if (BurstMed && remainTime <= 2f && remainTime > 1f && UseBurstMedicine(out var act))
            return act;

        // True North for positional safety
        if (remainTime <= 5f && remainTime > 2f && TrueNorthPvE.CanUse(out act))
            return act;

        return base.CountDownAction(remainTime);
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
        // Skip right after Stardiver (animation lock)
        if (IsLastAction(false, StardiverPvE))
            return base.DefenseAreaAbility(nextGCD, out act);

        // BMR-aware: Feint proactively when raidwide imminent
        bool rwSoon = BmrActive && BmrRaidwideIn is > 0 and <= 5f;

        if ((rwSoon || !BmrActive) && FeintPvE.CanUse(out act, skipComboCheck: true))
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
            // 1. Lance Charge: use on cooldown (60s)
            // Guide: "always press this button as soon as it is available"
            // LC is checked first so it fires before BL, matching the standard opener
            // (GCD2 → LC, GCD3 → BL + Geirskogul)
            if (LanceChargePvE.CanUse(out act))
                return true;

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

                // Filler Life Surge: use on HT/Drakesbane outside Lance Charge
                // Balance FAQ: "should almost never reach a full 2 stacks"
                // Prevents charge drift by spending during filler on high-potency GCDs
                if ((lsOnBigHit || lsOnAoE) && !HasLanceCharge)
                {
                    if (LifeSurgePvE.CanUse(out act))
                        return true;
                }
            }
        }

        // Life Surge overcap prevention: if at max charges, use on next HT/Drakesbane
        // even outside buff windows. Guide: "should almost never reach a full 2 stacks"
        {
            bool lsOvercapHit = nextGCD.IsTheSameTo(true, HeavensThrustPvE, DrakesbanePvE, CoerthanTormentPvE);
            if (!lsOvercapHit && !HeavensThrustPvE.EnoughLevel && !DrakesbanePvE.EnoughLevel)
                lsOvercapHit = nextGCD.IsTheSameTo(true, FullThrustPvE);

            if (lsOvercapHit && LifeSurgePvE.Cooldown.CurrentCharges >= 2)
            {
                if (LifeSurgePvE.CanUse(out act))
                    return true;
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
        // Balance: hard cooldown, use on CD to prevent drift
        if (InEvenBurst || InLOTD || !BattleLitanyPvE.EnoughLevel)
        {
            if (DragonfireDivePvE.CanUse(out act))
            {
                if (DragonfireDivePvE.Target.Target.DistanceToPlayer() <= DragonfireDiveDistance)
                    return true;
            }
        }

        // DfD drift prevention: if available but burst conditions missed it, use before it drifts further
        if (DragonfireDivePvE.Cooldown.HasOneCharge && !BattleLitanyPvE.Cooldown.WillHaveOneCharge(15))
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
        // Balance: "you have up until the next Raiden Thrust to use WWT in order to not overcap"
        // Use in buffs, during LOTD, at 2 Focus stacks (next RT would overcap), or when next GCD grants Focus
        if (HasBattleLitany || HasLanceCharge || InLOTD
            || FocusCount >= 2
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
