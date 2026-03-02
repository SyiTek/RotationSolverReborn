namespace RotationSolver.ExtraRotations.Tank;

[Rotation("SezuraiGNB", CombatType.PvE, GameVersion = "7.41",
    Description = "Balance-aligned GNB with No Mercy burst, Gnashing Fang combo, Reign combo, and cartridge optimization.")]
[SourceCode(Path = "main/ExtraRotations/Tank/SezuraiGNB.cs")]
[ExtraRotation]
public sealed class SezuraiGNB : GunbreakerRotation
{
    #region Config Options

    [Range(0f, 0.25f, ConfigUnitType.Percent)]
    [RotationConfig(CombatType.PvE, Name = "Action Ahead Override (0 = use global setting)")]
    public float ActionAheadOverride { get; set; } = 0f;

    [RotationConfig(CombatType.PvE, Name = "Experimental Pot Usage (during No Mercy windows)")]
    public bool BurstMed { get; set; } = true;

    [RotationConfig(CombatType.PvE, Name = "Auto Mitigation (use defensive CDs automatically)")]
    public bool AutoMitigation { get; set; } = true;

    [RotationConfig(CombatType.PvE, Name = "Use Trajectory as gap closer")]
    public bool UseGapCloser { get; set; } = true;

    #endregion

    #region Burst State

    /// <summary>
    /// Whether the user has burst enabled in the framework.
    /// </summary>
    private bool CanBurst => MergedStatus.HasFlag(AutoStatus.Burst);

    /// <summary>
    /// True when we are inside a No Mercy damage window or No Mercy is imminent.
    /// No Mercy is a 20s buff on a 60s cooldown.
    /// </summary>
    private bool InBurstWindow => HasNoMercy;

    /// <summary>
    /// No Mercy cooldown has less than a specified number of seconds remaining.
    /// Used to hold cartridge spenders for the upcoming burst.
    /// </summary>
    private bool NoMercySoon => NoMercyPvE.Cooldown.WillHaveOneCharge(5);

    /// <summary>
    /// True when we are in the middle of a Gnashing Fang or Reign combo and must finish it.
    /// </summary>
    private bool InLockedCombo => InGnashingFang || InReignCombo;

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
        ImGui.Text("=== SezuraiGNB Debug ===");
        ImGui.Separator();

        // Burst state
        ImGui.Text($"CanBurst: {CanBurst}");
        ImGui.Text($"InBurstWindow (NoMercy): {InBurstWindow}");
        ImGui.Text($"NoMercySoon: {NoMercySoon}");

        ImGui.Separator();

        // Cartridge state
        ImGui.Text($"Ammo: {Ammo}");
        ImGui.Text($"MaxAmmo: {MaxAmmo()}");
        ImGui.Text($"NormalMaxAmmo: {NormalMaxAmmo()}");
        ImGui.Text($"IsAmmoCapped: {IsAmmoCapped}");
        ImGui.Text($"HasBloodfest: {HasBloodfest}");

        ImGui.Separator();

        // Combo state
        ImGui.Text($"AmmoComboStep: {AmmoComboStep}");
        ImGui.Text($"InGnashingFang: {InGnashingFang}");
        ImGui.Text($"InReignCombo: {InReignCombo}");
        ImGui.Text($"InLockedCombo: {InLockedCombo}");

        ImGui.Separator();

        // Ready checks
        ImGui.Text($"HasNoMercy: {HasNoMercy}");
        ImGui.Text($"HasReadyToBreak: {HasReadyToBreak}");
        ImGui.Text($"HasReadyToReign: {HasReadyToReign}");
        ImGui.Text($"HasReadyToRip: {HasReadyToRip}");
        ImGui.Text($"HasReadyToTear: {HasReadyToTear}");
        ImGui.Text($"HasReadyToGouge: {HasReadyToGouge}");
        ImGui.Text($"HasReadyToBlast: {HasReadyToBlast}");
        ImGui.Text($"HasReadyToRaze: {HasReadyToRaze}");

        ImGui.Separator();

        // Cooldowns
        ImGui.Text($"NoMercy CD: {NoMercyPvE.Cooldown.RecastTimeRemainOneCharge:F1}s");
        ImGui.Text($"Bloodfest CD: {BloodfestPvE.Cooldown.RecastTimeRemainOneCharge:F1}s");
        ImGui.Text($"GnashingFang Charges: {GnashingFangPvE.Cooldown.CurrentCharges}");
        ImGui.Text($"DoubleDown CD: {DoubleDownPvE.Cooldown.RecastTimeRemainOneCharge:F1}s");
        ImGui.Text($"BlastingZone CD: {BlastingZonePvE.Cooldown.RecastTimeRemainOneCharge:F1}s");
        ImGui.Text($"--- BMR Timeline ---");
        ImGui.Text($"Active: {BmrActive}{(BmrActive ? $" ({DataCenter.BmrActiveModuleName})" : "")}");
        if (BmrActive)
        {
            ImGui.Text($"Raidwide In: {(BmrRaidwideIn < 9999f ? $"{BmrRaidwideIn:F1}s" : "None")}");
            ImGui.Text($"Tankbuster In: {(BmrTankbusterIn < 9999f ? $"{BmrTankbusterIn:F1}s" : "None")}");
            ImGui.Text($"Knockback In: {(BmrKnockbackIn < 9999f ? $"{BmrKnockbackIn:F1}s" : "None")}");
            ImGui.Text($"Downtime In: {(BmrDowntimeIn < 9999f ? $"{BmrDowntimeIn:F1}s" : "None")}");
        }
    }

    #endregion

    #region Countdown & Opener
    // === GNB OPENER (7.4 Balance / Icy Veins) ===
    // Pre-pull: Lightning Shot
    // GCD1: Keen Edge
    // GCD2: Brutal Shell
    // GCD3: Solid Barrel → Pot (weave)
    // GCD4: Keen Edge → No Mercy (early weave at 2.50 GCD)
    // GCD5: Gnashing Fang → Bloodfest (weave) + Jugular Rip (weave)
    // GCD6: Double Down → Bow Shock (weave) + Blasting Zone (weave)
    // GCD7: Sonic Break
    // GCD8: Savage Claw → Abdomen Tear (weave)
    // GCD9: Wicked Talon → Eye Gouge (weave)
    // GCD10: Reign of Beasts
    // GCD11: Noble Blood
    // GCD12: Lion Heart
    //
    // === BURST WINDOWS (60s cycle — every window is the same in 7.4) ===
    // Bloodfest is 60s (changed in 7.4), so every NM window has Reign combo:
    //   NM → GF combo + DD + Sonic Break + Reign combo (9 GCDs under NM)
    //   Weave Bow Shock + Blasting Zone + Continuations inside NM
    //
    // === FILLER ===
    // Keen Edge → Brutal Shell → Solid Barrel (generates 1 cartridge)
    // GF combo once outside NM (don't let both charges cap)
    // Blasting Zone on CD (one in burst, one in filler)
    // Burst Strike only to prevent cartridge overcap (gauge full + SB next)

    protected override IAction? CountDownAction(float remainTime)
    {
        // Pot at ~2s before pull (medicine animation is ~1s)
        if (BurstMed && remainTime <= 2f && remainTime > 1f && UseBurstMedicine(out var act))
            return act;

        // Lightning Shot at 0.7s for ranged pull
        if (remainTime <= 0.7f && LightningShotPvE.CanUse(out act))
            return act;

        return base.CountDownAction(remainTime);
    }

    #endregion

    #region Additional oGCD Logic

    [RotationDesc(ActionID.TrajectoryPvE)]
    protected override bool MoveForwardAbility(IAction nextGCD, out IAction? act)
    {
        if (UseGapCloser && TrajectoryPvE.CanUse(out act, skipComboCheck: true))
            return true;
        return base.MoveForwardAbility(nextGCD, out act);
    }

    [RotationDesc(ActionID.ArmsLengthPvE)]
    protected sealed override bool AntiKnockbackAbility(IAction nextGCD, out IAction? act)
    {
        if (ArmsLengthPvE.CanUse(out act))
            return true;
        return base.AntiKnockbackAbility(nextGCD, out act);
    }

    [RotationDesc(ActionID.HeartOfLightPvE, ActionID.ReprisalPvE)]
    protected override bool DefenseAreaAbility(IAction nextGCD, out IAction? act)
    {
        if (!AutoMitigation)
            return base.DefenseAreaAbility(nextGCD, out act);

        // BMR-aware: time Heart of Light to raidwide — covers both magic (10%) and phys (5%)
        bool rwSoon = BmrActive && BmrRaidwideIn is > 0 and <= 5f;

        if (rwSoon)
        {
            if (HeartOfLightPvE.CanUse(out act, skipAoeCheck: true))
                return true;
            // Don't stack Reprisal on same raidwide — save for next one
            return base.DefenseAreaAbility(nextGCD, out act);
        }

        // Without BMR: skip if NM burst is imminent (avoid clipping oGCD slots)
        if (!BmrActive && NoMercySoon && !HasNoMercy)
            return base.DefenseAreaAbility(nextGCD, out act);

        if (HeartOfLightPvE.CanUse(out act, skipAoeCheck: true))
            return true;

        if (ReprisalPvE.CanUse(out act, skipAoeCheck: true))
            return true;

        return base.DefenseAreaAbility(nextGCD, out act);
    }

    [RotationDesc(ActionID.HeartOfCorundumPvE, ActionID.GreatNebulaPvE, ActionID.NebulaPvE,
        ActionID.CamouflagePvE, ActionID.RampartPvE, ActionID.ReprisalPvE)]
    protected override bool DefenseSingleAbility(IAction nextGCD, out IAction? act)
    {
        if (!AutoMitigation)
            return base.DefenseSingleAbility(nextGCD, out act);

        // BMR-aware: when TB is imminent, Heart of Corundum + ONE longer CD
        // Balance: "Heart of Corundum is your primary short-CD mitigation (~28% for first 4s)"
        bool tbSoon = BmrActive && BmrTankbusterIn is > 0 and <= 6f;

        // Heart of Corundum / Heart of Stone - short CD, always first
        if (HeartOfCorundumPvE.CanUse(out act))
            return true;
        if (!HeartOfCorundumPvE.EnoughLevel && HeartOfStonePvE.CanUse(out act))
            return true;

        if (tbSoon)
        {
            // Layer ONE longer CD for big TBs
            if (CamouflagePvE.CanUse(out act))
                return true;
            // Don't dump all long CDs — 2 mits per TB is enough
            return base.DefenseSingleAbility(nextGCD, out act);
        }

        // Non-BMR / reactive path: stagger long CDs
        if (CamouflagePvE.CanUse(out act))
            return true;

        // Great Nebula / Nebula - 30% mit, stagger with Rampart
        if ((!RampartPvE.Cooldown.IsCoolingDown || RampartPvE.Cooldown.ElapsedAfter(60))
            && GreatNebulaPvE.CanUse(out act))
            return true;

        if (!GreatNebulaPvE.EnoughLevel
            && (!RampartPvE.Cooldown.IsCoolingDown || RampartPvE.Cooldown.ElapsedAfter(60))
            && NebulaPvE.CanUse(out act))
            return true;

        // Rampart - 20% mit, stagger with Nebula
        if (GreatNebulaPvE.EnoughLevel)
        {
            if (GreatNebulaPvE.Cooldown.IsCoolingDown && GreatNebulaPvE.Cooldown.ElapsedAfter(60)
                && RampartPvE.CanUse(out act))
                return true;
        }
        else if (NebulaPvE.Cooldown.IsCoolingDown && NebulaPvE.Cooldown.ElapsedAfter(60)
            && RampartPvE.CanUse(out act))
            return true;

        if (ReprisalPvE.CanUse(out act))
            return true;

        return base.DefenseSingleAbility(nextGCD, out act);
    }

    [RotationDesc(ActionID.AuroraPvE)]
    protected override bool HealSingleAbility(IAction nextGCD, out IAction? act)
    {
        if (AuroraPvE.CanUse(out act))
            return true;
        return base.HealSingleAbility(nextGCD, out act);
    }

    #endregion

    #region oGCD Logic

    protected override bool EmergencyAbility(IAction nextGCD, out IAction? act)
    {
        // === CONTINUATION: absolute highest priority ===
        // These MUST fire immediately after their trigger GCD.
        // Placed in Emergency so they are checked before any other oGCD.
        // Each Continuation ability is gated by its Ready status from the base class.

        // Gnashing Fang combo continuations
        if (JugularRipPvE.CanUse(out act))
            return true;

        if (AbdomenTearPvE.CanUse(out act))
            return true;

        if (EyeGougePvE.CanUse(out act))
            return true;

        // Burst Strike continuation
        if (HypervelocityPvE.CanUse(out act))
            return true;

        // Fated Circle continuation
        if (FatedBrandPvE.CanUse(out act))
            return true;

        // === MEDICINE ===
        // Use during No Mercy for maximum value. No Mercy is a 20% damage buff.
        if (BurstMed && InBurstWindow && InCombat && UseBurstMedicine(out act))
            return true;

        return base.EmergencyAbility(nextGCD, out act);
    }

    protected override bool AttackAbility(IAction nextGCD, out IAction? act)
    {
        // === NO MERCY: 20% damage buff, 20s duration, 60s cooldown ===
        // In 7.4, every NM window has Bloodfest available.
        // Use NM when we have Bloodfest buff active (meaning Bloodfest was just used)
        // or at the standard timings.

        // At high level with Reign of Beasts: use NM when Bloodfest buff is active
        // (the standard 7.4 flow is: build to 3 cartridges -> Bloodfest -> NM)
        if (CanBurst && InCombat && HasHostilesInRange)
        {
            // High level: NM with Bloodfest buff for full 9-GCD window
            if (ReignOfBeastsPvE.EnoughLevel && HasBloodfest && NoMercyPvE.CanUse(out act))
                return true;

            // Mid level: NM before Gnashing Fang
            if (!ReignOfBeastsPvE.EnoughLevel && GnashingFangPvE.EnoughLevel
                && nextGCD.IsTheSameTo(false, (ActionID)GnashingFangPvE.ID)
                && NoMercyPvE.CanUse(out act))
                return true;

            // Low level fallbacks
            if (!GnashingFangPvE.EnoughLevel && BurstStrikePvE.EnoughLevel
                && nextGCD.IsTheSameTo(false, (ActionID)BurstStrikePvE.ID)
                && NoMercyPvE.CanUse(out act))
                return true;

            if (!BurstStrikePvE.EnoughLevel && SolidBarrelPvE.EnoughLevel
                && nextGCD.IsTheSameTo(false, (ActionID)SolidBarrelPvE.ID)
                && NoMercyPvE.CanUse(out act))
                return true;

            if (!SolidBarrelPvE.EnoughLevel
                && NoMercyPvE.CanUse(out act))
                return true;

            // AoE: NM before Double Down or Fated Circle
            if (DemonSlicePvE.CanUse(out _))
            {
                if (DoubleDownPvE.EnoughLevel
                    && nextGCD.IsTheSameTo(false, (ActionID)DoubleDownPvE.ID)
                    && NoMercyPvE.CanUse(out act))
                    return true;

                if (!DoubleDownPvE.EnoughLevel
                    && nextGCD.IsTheSameTo(false, (ActionID)FatedCirclePvE.ID)
                    && NoMercyPvE.CanUse(out act))
                    return true;
            }
        }

        // === BLOODFEST: 60s CD, grants 3 cartridges + Ready to Reign ===
        // In 7.4, Bloodfest is 60s (same as NM). Use after NM in burst windows.
        // The Bloodfest buff raises max cartridges to 6 for 30s, preventing overcap.
        if (BloodfestPvE.CanUse(out act))
            return true;

        // === BOW SHOCK: 60s CD, AoE damage + DoT, use inside No Mercy ===
        if (HasNoMercy && BowShockPvE.CanUse(out act, skipAoeCheck: true))
            return true;

        // Low level: Bow Shock without NM gating if Sonic Break isn't unlocked
        if (!SonicBreakPvE.EnoughLevel && BowShockPvE.CanUse(out act, skipAoeCheck: true))
            return true;

        // === BLASTING ZONE / DANGER ZONE: 30s CD oGCD ===
        // Prefer inside No Mercy, but don't let it drift.
        if (BlastingZonePvE.EnoughLevel)
        {
            // Inside NM: use after Double Down for proper sequencing
            if (HasNoMercy && BlastingZonePvE.CanUse(out act))
                return true;

            // Outside NM: use if it won't be ready for next NM window
            if (!HasNoMercy && !NoMercyPvE.Cooldown.WillHaveOneCharge(15)
                && BlastingZonePvE.CanUse(out act))
                return true;
        }

        // Danger Zone: lower level version
        if (!BlastingZonePvE.EnoughLevel
            && (HasNoMercy || !NoMercyPvE.Cooldown.WillHaveOneCharge(15))
            && DangerZonePvE.CanUse(out act))
            return true;

        return base.AttackAbility(nextGCD, out act);
    }

    #endregion

    #region GCD Logic

    protected override bool GeneralGCD(out IAction? act)
    {
        // =====================================================
        // PRIORITY 0: Overcap prevention before NM window
        // If ammo is capped and we'd waste a cartridge from Solid Barrel,
        // spend one now even outside burst.
        // =====================================================
        if (IsAmmoCapped && AmmoComboStep == 0 && !InLockedCombo
            && NoMercyPvE.Cooldown.WillHaveOneChargeGCD(1) && BloodfestPvE.EnoughLevel)
        {
            if (BurstStrikePvE.CanUse(out act))
                return true;
        }

        // =====================================================
        // PRIORITY 1: Finish locked combos (MUST always complete)
        // Gnashing Fang combo: GF -> Savage Claw -> Wicked Talon
        // Reign combo: Reign of Beasts -> Noble Blood -> Lion Heart
        // =====================================================

        // Gnashing Fang combo finishers
        if (WickedTalonPvE.CanUse(out act, skipComboCheck: true))
            return true;

        if (SavageClawPvE.CanUse(out act, skipComboCheck: true))
            return true;

        // Reign combo finishers
        if (LionHeartPvE.CanUse(out act, skipComboCheck: true))
            return true;

        if (NobleBloodPvE.CanUse(out act, skipComboCheck: true))
            return true;

        // =====================================================
        // PRIORITY 2: No Mercy burst window GCDs
        // The 7.4 NM window packs 9 GCDs (at 2.40-2.47 speed):
        //   Double Down, Gnashing Fang combo (3), Reign combo (3),
        //   Sonic Break, Burst Strike
        // Priority order when facing potential GCD loss:
        //   Double Down > Reign combo > Sonic Break > Gnashing Fang
        // =====================================================

        if (!InLockedCombo)
        {
            // --- Inside No Mercy burst ---
            if (HasNoMercy)
            {
                // Double Down: costs 2 cartridges, highest potency GCD, always first in NM
                if (DoubleDownPvE.CanUse(out act))
                    return true;

                // Gnashing Fang: starts the 3-hit combo (costs 1 cartridge)
                // Use when available and not in another combo
                if (AmmoComboStep == 0 && GnashingFangPvE.CanUse(out act, skipComboCheck: true))
                    return true;

                // Sonic Break: strong DoT, use inside NM
                if (SonicBreakPvE.CanUse(out act))
                    return true;

                // Reign of Beasts: starts the 3-hit combo, requires Ready to Reign from Bloodfest
                if (AmmoComboStep == 0 && ReignOfBeastsPvE.CanUse(out act, skipComboCheck: true))
                    return true;

                // Burst Strike: 1-cartridge spender to fill remaining NM GCDs
                // Also fires when GF is on cooldown and we have spare cartridges
                if (AmmoComboStep == 0 && !GnashingFangPvE.Cooldown.WillHaveOneCharge(1)
                    && BurstStrikePvE.CanUse(out act))
                    return true;
            }

            // --- Outside No Mercy: spend cartridges / use abilities ---

            // Gnashing Fang: 2 charges in 7.4. Use on cooldown, spend charges in NM.
            // Outside NM, use if we have 2 charges (prevent overcap) or NM won't be up soon.
            if (AmmoComboStep == 0 && GnashingFangPvE.CanUse(out act, skipComboCheck: true,
                usedUp: HasNoMercy || GnashingFangPvE.Cooldown.WillHaveXChargesGCD(2, 1)))
                return true;

            // Sonic Break: use if Ready to Break will expire (outside NM safety net)
            if (HasReadyToBreak && !NoMercyPvE.Cooldown.WillHaveOneCharge(10)
                && SonicBreakPvE.CanUse(out act))
                return true;

            // Reign of Beasts: use if Ready to Reign and NM is far away
            if (HasReadyToReign && AmmoComboStep == 0
                && !NoMercyPvE.Cooldown.WillHaveOneCharge(10)
                && ReignOfBeastsPvE.CanUse(out act, skipComboCheck: true))
                return true;

            // Burst Strike: overcap prevention
            // Spend cartridges if about to overcap from the basic combo
            if (BurstStrikePvE.CanUse(out act))
            {
                // Overcap from Bloodfest buff: spend extra cartridges beyond normal max
                if (Ammo > 3 && OvercappedAmmo() > 0
                    && StatusHelper.PlayerWillStatusEndGCD(OvercappedAmmo(), 0, true, StatusID.Bloodfest))
                    return true;

                // About to overcap from Solid Barrel (last combo was Brutal Shell at max ammo)
                if (IsLastComboAction((ActionID)BrutalShellPvE.ID) && IsAmmoCapped)
                    return true;

                // Bloodfest coming soon and we need room
                if (IsLastComboAction((ActionID)BrutalShellPvE.ID)
                    && BloodfestPvE.Cooldown.WillHaveOneCharge(6) && Ammo <= 2
                    && !NoMercyPvE.Cooldown.WillHaveOneCharge(10) && BloodfestPvE.EnoughLevel)
                    return true;

                // Capped inside NM (already covered above, but safety)
                if (IsAmmoCapped && HasNoMercy)
                    return true;
            }

            // =====================================================
            // PRIORITY 3: AoE Rotation (3+ targets)
            // Fated Circle > Demon Slaughter combo
            // =====================================================

            // Fated Circle: AoE cartridge spender during NM or overcap
            if ((HasNoMercy || IsAmmoCapped) && FatedCirclePvE.CanUse(out act))
                return true;

            // AoE combo
            if (DemonSlaughterPvE.CanUse(out act))
                return true;

            if (DemonSlicePvE.CanUse(out act))
                return true;

            // =====================================================
            // PRIORITY 4: Single Target Combo
            // Keen Edge -> Brutal Shell -> Solid Barrel
            // Generates 1 cartridge on Solid Barrel
            // =====================================================

            if (SolidBarrelPvE.CanUse(out act))
                return true;

            if (BrutalShellPvE.CanUse(out act))
                return true;

            if (KeenEdgePvE.CanUse(out act))
                return true;
        }

        // =====================================================
        // PRIORITY 5: Ranged fallback
        // =====================================================
        if (LightningShotPvE.CanUse(out act))
            return true;

        return base.GeneralGCD(out act);
    }

    #endregion
}
