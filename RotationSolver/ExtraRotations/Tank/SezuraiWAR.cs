namespace RotationSolver.ExtraRotations.Tank;

[Rotation("SezuraiWAR", CombatType.PvE, GameVersion = "7.41",
    Description = "Balance-aligned WAR with Inner Release burst, Infuriate management, and Surging Tempest uptime.")]
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
        ImGui.Text($"CanBurst: {CanBurst}");
        ImGui.Text($"InBurstWindow: {InBurstWindow}");
        ImGui.Text($"HasIRStacks: {HasIRStacks} ({InnerReleaseStacks})");
        ImGui.Text($"HasSurgingTempest: {HasSurgingTempest}");
        ImGui.Text($"SurgingTempest <6 GCDs: {SurgingTempestWillEnd(6)}");
        ImGui.Text($"HasNascentChaos: {HasNascentChaos}");
        ImGui.Text($"HasPrimalRendReady: {HasPrimalRendReady}");
        ImGui.Text($"PrimalWrathReady: {PrimalWrathPvEReady}");
        ImGui.Text($"PrimalRuinationReady: {PrimalRuinationPvEReady}");
        ImGui.Text($"InnerChaosPvEReady: {InnerChaosPvEeady}");
        ImGui.Text($"BeastGauge: {BeastGauge}");
        ImGui.Text($"IsMedicated: {IsMedicated}");
        ImGui.Text($"InCombat: {InCombat}");
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

        // Don't stack mit during Holmgang when low
        if (StatusHelper.PlayerHasStatus(true, StatusID.Holmgang_409) && Player?.GetHealthRatio() < 0.3f)
            return false;

        // BMR-aware: when TB is imminent, use Bloodwhetting + ONE longer CD
        // Balance: "Bloodwhetting is your go-to for every tankbuster"
        bool tbSoon = BmrActive && BmrTankbusterIn is > 0 and <= 6f;

        // Bloodwhetting / Raw Intuition: short CD, strong self-heal — always first for TB
        if (BloodwhettingPvE.CanUse(out act))
            return true;
        if (!BloodwhettingPvE.Info.EnoughLevelAndQuest() && RawIntuitionPvE.CanUse(out act))
            return true;

        // Don't layer more mit if Bloodwhetting is already active
        if (!StatusHelper.PlayerWillStatusEndGCD(0, 0, true, StatusID.Bloodwhetting, StatusID.RawIntuition))
            return false;

        // BMR TB path: layer ONE heavier mit for big TBs, then stop
        if (tbSoon)
        {
            if (ReprisalPvE.CanUse(out act, skipAoeCheck: true))
                return true;
            // Only one long CD per TB — don't dump everything
            return base.DefenseSingleAbility(nextGCD, out act);
        }

        // Non-BMR / reactive path: stagger long CDs as before
        if (ReprisalPvE.CanUse(out act, skipAoeCheck: true))
            return true;

        // Damnation (upgraded Vengeance) -> Vengeance: 30% damage reduction
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

        // BMR-aware: time Shake It Off to land before raidwide
        // Balance: "Shake It Off provides a barrier + regen to the party"
        // Use when raidwide is imminent; without BMR use whenever framework triggers
        bool rwSoon = BmrActive && BmrRaidwideIn is > 0 and <= 5f;

        if (rwSoon && ShakeItOffPvE.CanUse(out act, skipAoeCheck: true))
            return true;

        // Without BMR: use whenever the framework says DefenseArea is needed
        if (!BmrActive && ShakeItOffPvE.CanUse(out act, skipAoeCheck: true))
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
        // Holmgang: invulnerability when about to die
        if (HolmgangPvE.CanUse(out act) && Player?.GetHealthRatio() <= Service.Config.HealthForDyingTanks)
            return true;

        // Medicine: use during Inner Release burst windows
        // Balance: pot should cover the full IR window including Primal Rend + Ruination
        if (BurstMed && InBurstWindow && InCombat && UseBurstMedicine(out act))
            return true;

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

        // === INNER RELEASE (60s CD) ===
        // The core burst cooldown. Grants 3 free Fell Cleave/Decimate stacks,
        // Primal Rend Ready, and extends Surging Tempest by 10s.
        // Balance: "Use Inner Release on cooldown" - ensure Surging Tempest
        // has enough duration so we don't waste IR GCDs refreshing it.
        if (CanBurst && InCombat && HasHostilesInRange)
        {
            if (!SurgingTempestWillEnd(2) || !StormsEyePvE.EnoughLevel)
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
        // During burst (IR active or InnerStrength up): use aggressively
        // Outside burst: use at 3+ GCDs to spare to avoid charge overcap
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
