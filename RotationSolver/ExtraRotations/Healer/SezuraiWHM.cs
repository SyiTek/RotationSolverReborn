using System.ComponentModel;

namespace RotationSolver.ExtraRotations.Healer;

[Rotation("SezuraiWHM", CombatType.PvE, GameVersion = "7.41",
    Description = "Balance-aligned WHM with Presence of Mind burst, Lily management, and 3 healing modes.")]
[SourceCode(Path = "main/ExtraRotations/Healer/SezuraiWHM.cs")]
[ExtraRotation]
public sealed class SezuraiWHM : WhiteMageRotation
{
    #region Config Options

    [Range(0f, 0.25f, ConfigUnitType.Percent)]
    [RotationConfig(CombatType.PvE, Name = "Action Ahead Override (0 = use global setting)")]
    public float ActionAheadOverride { get; set; } = 0f;

    [RotationConfig(CombatType.PvE, Name = "Heal Mode (Balanced = normal, DPS Bot = oGCD only, Heal Bot = liberal GCD healing)")]
    public HealModeStrategy HealMode { get; set; } = HealModeStrategy.Balanced;

    public enum HealModeStrategy : byte
    {
        [Description("Normal: GCD heals only if solo healer or emergency")]
        Balanced,

        [Description("DPS Bot: oGCD heals only, Lily only for Misery setup")]
        DPSBot,

        [Description("Heal Bot: liberal GCD healing, safety first")]
        HealBot,
    }

    [RotationConfig(CombatType.PvE, Name = "Use Tincture/Gemdraught during Presence of Mind windows")]
    public bool BurstMed { get; set; } = true;

    [RotationConfig(CombatType.PvE, Name = "Maintain Dia DoT uptime (refresh even while moving)")]
    public bool DiaDotUptime { get; set; } = true;

    [RotationConfig(CombatType.PvE, Name = "Swiftcast restriction: reserve for Raise")]
    public bool SwiftLogic { get; set; } = true;

    [RotationConfig(CombatType.PvE, Name = "Use Lily at max stacks to prevent overcap")]
    public bool LilyOvercapProtection { get; set; } = true;

    [RotationConfig(CombatType.PvE, Name = "Limit Liturgy of the Bell to multihit party stacks")]
    public bool MultiHitRestrict { get; set; } = false;

    [Range(0, 1, ConfigUnitType.Percent)]
    [RotationConfig(CombatType.PvE, Name = "HP threshold for Benediction")]
    public float BenedictionThreshold { get; set; } = 0.3f;

    [Range(0, 1, ConfigUnitType.Percent)]
    [RotationConfig(CombatType.PvE, Name = "HP threshold for Tetragrammaton")]
    public float TetraThreshold { get; set; } = 0.7f;

    [Range(0, 1, ConfigUnitType.Percent)]
    [RotationConfig(CombatType.PvE, Name = "HP threshold for Regen")]
    public float RegenThreshold { get; set; } = 0.3f;

    [Range(0, 1, ConfigUnitType.Percent)]
    [RotationConfig(CombatType.PvE, Name = "Party HP threshold for AoE heals")]
    public float AoeHealThreshold { get; set; } = 0.65f;

    [RotationConfig(CombatType.PvE, Name = "How to manage Thin Air last charge")]
    public ThinAirStrategy ThinAirUsage { get; set; } = ThinAirStrategy.ReserveForRaise;

    public enum ThinAirStrategy : byte
    {
        [Description("Use all charges on expensive spells")]
        UseAll,

        [Description("Reserve last charge for Raise")]
        ReserveForRaise,

        [Description("Reserve last charge for manual use")]
        ReserveManual,
    }

    #endregion

    #region Burst State

    /// <summary>
    /// Framework burst enabled.
    /// </summary>
    private bool CanBurst => MergedStatus.HasFlag(AutoStatus.Burst);

    /// <summary>
    /// True when Presence of Mind buff is active on the player.
    /// </summary>
    private static bool HasPresenceOfMind => StatusHelper.PlayerHasStatus(true, StatusID.PresenceOfMind);

    /// <summary>
    /// True when the player is medicated.
    /// </summary>
    private static bool IsMedicated => StatusHelper.PlayerHasStatus(true, StatusID.Medicated);

    /// <summary>
    /// True when approaching or in the Presence of Mind window.
    /// </summary>
    private bool InBurstWindow => HasPresenceOfMind
        || !PresenceOfMindPvE.EnoughLevel
        || PresenceOfMindPvE.Cooldown.HasOneCharge
        || PresenceOfMindPvE.Cooldown.WillHaveOneCharge(5);

    #endregion

    #region UpdateInfo

    protected override void UpdateInfo()
    {
        DataCenter.RotationActionAheadOverride = ActionAheadOverride > 0f ? ActionAheadOverride : null;
    }

    #endregion

    #region Extra Methods

    private bool IsSoloHealer
    {
        get
        {
            int aliveHealerCount = 0;
            var healers = PartyMembers.GetJobCategory(JobRole.Healer);
            foreach (var h in healers)
            {
                if (!h.IsDead)
                    aliveHealerCount++;
            }
            return aliveHealerCount <= 1;
        }
    }

    /// <summary>
    /// Whether lilies are at or near cap and need to be spent.
    /// </summary>
    private bool LiliesNearCap => Lily == 3 || (Lily == 2 && LilyAfterGCD(2));

    /// <summary>
    /// Whether Misery is ready to fire (3 blood lily stacks).
    /// </summary>
    private static bool MiseryReady => BloodLily == 3;

    public override bool CanHealSingleSpell
    {
        get
        {
            if (!base.CanHealSingleSpell) return false;
            return HealMode switch
            {
                HealModeStrategy.DPSBot => false,
                HealModeStrategy.HealBot => true,
                _ => IsSoloHealer,
            };
        }
    }

    public override bool CanHealAreaSpell
    {
        get
        {
            if (!base.CanHealAreaSpell) return false;
            return HealMode switch
            {
                HealModeStrategy.DPSBot => false,
                HealModeStrategy.HealBot => true,
                _ => IsSoloHealer,
            };
        }
    }

    #endregion

    #region Countdown & Opener
    // === WHM OPENER (7.4 Balance / Icy Veins) ===
    // Pre-pull: Divine Benison(-5s) → Glare III precast(-2.3s)
    // Pull: Pot (weave as Glare III lands)
    // GCD1: Dia (instant DoT)
    // GCD2: Glare III
    // GCD3: Glare III → Presence of Mind (weave)
    // GCD4: Glare IV (Sacred Sight stack 1) → Assize (weave)
    // GCD5: Glare IV (Sacred Sight stack 2)
    // GCD6-11: Glare III x6 under PoM haste
    // GCD12: Glare IV (Sacred Sight stack 3)
    // GCD13: Dia (refresh, snapshots raid buffs)
    //
    // === EVEN BURST (120s) ===
    // Presence of Mind (120s, grants 3x Sacred Sight = Glare IV + 20% haste)
    //   + Assize (40s CD) + Afflatus Misery under raid buffs
    // Refresh Dia as last GCD to snapshot party buffs
    //
    // === ODD BURST (60s) ===
    // Assize on CD (40s, drifts naturally) + Misery if Blood Lily is full
    // No PoM — it is 120s. Continue Glare III spam
    //
    // === FILLER ===
    // Glare III spam (1.5s cast, ~1s weave window per GCD)
    // Dia: maintain 100% uptime, never refresh early
    // Lilies: DPS-neutral (3 Afflatus + Misery = 4 Glare III); use for movement
    // Never overcap lilies at 3 stacks (lost Misery generation)
    // Assize strictly on CD (damage + heal + 500 MP); Lucid at ~80% MP

    protected override IAction? CountDownAction(float remainTime)
    {
        // Tincture at ~2s before pull
        if (BurstMed && remainTime <= 2f && remainTime > 1f && UseBurstMedicine(out var act))
            return act;

        // Precast Glare III to land on pull (use highest level nuke available)
        if (GlareIiiPvE.EnoughLevel && remainTime < GlareIiiPvE.Info.CastTime + CountDownAhead && GlareIiiPvE.CanUse(out act))
            return act;
        if (!GlareIiiPvE.EnoughLevel && GlarePvE.EnoughLevel && remainTime < GlarePvE.Info.CastTime + CountDownAhead && GlarePvE.CanUse(out act))
            return act;
        if (!GlarePvE.EnoughLevel && remainTime < StonePvE.Info.CastTime + CountDownAhead && StonePvE.CanUse(out act))
            return act;

        // Divine Benison on tank prepull
        if (remainTime <= 5 && remainTime > 3 && DivineBenisonPvE.CanUse(out act))
            return act;

        return base.CountDownAction(remainTime);
    }

    #endregion

    #region Emergency Ability

    protected override bool EmergencyAbility(IAction nextGCD, out IAction? act)
    {
        // --- Thin Air before expensive spells ---
        // Priority: Raise always gets Thin Air, then expensive heals
        bool useLastCharge = ThinAirUsage == ThinAirStrategy.UseAll
            || (ThinAirUsage == ThinAirStrategy.ReserveForRaise && (nextGCD == RaisePvE || MergedStatus.HasFlag(AutoStatus.Raise)));

        if (nextGCD is IBaseAction nextAction && IsLastAction() == IsLastGCD())
        {
            if ((nextAction.Info.MPNeed >= 1000 || MergedStatus.HasFlag(AutoStatus.Raise) || nextGCD == RaisePvE)
                && ThinAirPvE.CanUse(out act, usedUp: useLastCharge))
            {
                return true;
            }
        }

        // --- Divine Caress: follow-up to Temperance (use before DivineGrace expires) ---
        if (StatusHelper.PlayerWillStatusEndGCD(2, 0, true, StatusID.DivineGrace)
            && DivineCaressPvE.CanUse(out act))
        {
            return true;
        }

        // --- Medicine before Presence of Mind ---
        if (BurstMed && InCombat && !PresenceOfMindPvE.Cooldown.IsCoolingDown
            && UseBurstMedicine(out act))
        {
            return true;
        }

        // --- Plenary Indulgence before AoE GCD heals ---
        if (nextGCD.IsTheSameTo(true, AfflatusRapturePvE, MedicaPvE, MedicaIiPvE, MedicaIiiPvE, CureIiiPvE)
            && (MergedStatus.HasFlag(AutoStatus.HealAreaSpell) || MergedStatus.HasFlag(AutoStatus.HealSingleSpell)))
        {
            if (PlenaryIndulgencePvE.CanUse(out act))
                return true;
        }

        return base.EmergencyAbility(nextGCD, out act);
    }

    #endregion

    #region Defense

    [RotationDesc(ActionID.TemperancePvE, ActionID.LiturgyOfTheBellPvE)]
    protected override bool DefenseAreaAbility(IAction nextGCD, out IAction? act)
    {
        // BMR-aware: time mit to land before raidwide (1-2 mits max per raidwide)
        // Balance: "Temperance is your main party mitigation tool"
        bool rwSoon = BmrActive && BmrRaidwideIn is > 0 and <= 5f;

        if (rwSoon)
        {
            // Temperance first: 10% party mit + 20% heal boost (best WHM party CD)
            if (TemperancePvE.CanUse(out act))
                return true;

            // Divine Caress follow-up shield from Temperance
            if (DivineCaressPvE.CanUse(out act))
                return true;

            // Liturgy of the Bell: delayed healing on damage taken
            if ((MultiHitRestrict && IsCastingMultiHit) || !MultiHitRestrict)
            {
                if (LiturgyOfTheBellPvE.CanUse(out act, skipAoeCheck: true))
                    return true;
            }

            // Plenary Indulgence: party mitigation
            if (PlenaryIndulgencePvE.CanUse(out act))
                return true;

            // Max 1-2 mits per raidwide — stop here
            return base.DefenseAreaAbility(nextGCD, out act);
        }

        // Non-BMR: stagger cooldowns as before
        if ((TemperancePvE.Cooldown.IsCoolingDown && !TemperancePvE.Cooldown.WillHaveOneCharge(100))
            || (LiturgyOfTheBellPvE.Cooldown.IsCoolingDown && !LiturgyOfTheBellPvE.Cooldown.WillHaveOneCharge(160)))
        {
            return base.DefenseAreaAbility(nextGCD, out act);
        }

        if (PlenaryIndulgencePvE.CanUse(out act))
            return true;

        if (TemperancePvE.CanUse(out act))
            return true;

        if (DivineCaressPvE.CanUse(out act))
            return true;

        if ((MultiHitRestrict && IsCastingMultiHit) || !MultiHitRestrict)
        {
            if (LiturgyOfTheBellPvE.CanUse(out act, skipAoeCheck: true))
                return true;
        }

        return base.DefenseAreaAbility(nextGCD, out act);
    }

    [RotationDesc(ActionID.DivineBenisonPvE, ActionID.AquaveilPvE)]
    protected override bool DefenseSingleAbility(IAction nextGCD, out IAction? act)
    {
        // BMR-aware: when TB is imminent, shield the tank proactively
        bool tbSoon = BmrActive && BmrTankbusterIn is > 0 and <= 6f;

        if (tbSoon)
        {
            // Divine Benison first: 500p shield, 2 charges (low cost, high value)
            if (DivineBenisonPvE.CanUse(out act))
                return true;

            // Aquaveil: 15% damage reduction for 8s
            if (AquaveilPvE.CanUse(out act))
                return true;

            // Max 2 per TB — stop
            return base.DefenseSingleAbility(nextGCD, out act);
        }

        // Non-BMR: stagger cooldowns
        if ((DivineBenisonPvE.Cooldown.IsCoolingDown && !DivineBenisonPvE.Cooldown.WillHaveOneCharge(15))
            || (AquaveilPvE.Cooldown.IsCoolingDown && !AquaveilPvE.Cooldown.WillHaveOneCharge(52)))
        {
            return base.DefenseSingleAbility(nextGCD, out act);
        }

        if (DivineBenisonPvE.CanUse(out act))
            return true;

        if (AquaveilPvE.CanUse(out act))
            return true;

        return base.DefenseSingleAbility(nextGCD, out act);
    }

    #endregion

    #region Healing Abilities (oGCD)

    [RotationDesc(ActionID.BenedictionPvE, ActionID.TetragrammatonPvE, ActionID.DivineBenisonPvE)]
    protected override bool HealSingleAbility(IAction nextGCD, out IAction? act)
    {
        // Benediction: full HP heal, use at low HP threshold
        if (BenedictionPvE.CanUse(out act)
            && BenedictionPvE.Target.Target?.GetHealthRatio() < BenedictionThreshold)
        {
            return true;
        }

        // Don't stack after Benediction
        if (IsLastAction(ActionID.BenedictionPvE))
            return base.HealSingleAbility(nextGCD, out act);

        // Tetragrammaton: 700 potency oGCD heal
        if (TetragrammatonPvE.CanUse(out act, usedUp: true)
            && TetragrammatonPvE.Target.Target?.GetHealthRatio() < TetraThreshold)
        {
            return true;
        }

        // Divine Benison: 500 potency shield (keep on cooldown for tank)
        if (DivineBenisonPvE.CanUse(out act))
            return true;

        // Aquaveil: single target damage reduction
        if (AquaveilPvE.CanUse(out act))
            return true;

        return base.HealSingleAbility(nextGCD, out act);
    }

    [RotationDesc(ActionID.AsylumPvE, ActionID.PlenaryIndulgencePvE)]
    protected override bool HealAreaAbility(IAction nextGCD, out IAction? act)
    {
        // BMR-aware: place Asylum proactively before raidwide for regen + 10% heal boost
        bool rwSoon = BmrActive && BmrRaidwideIn is > 0 and <= 8f;

        if (rwSoon && AsylumPvE.CanUse(out act))
            return true;

        // Asylum: ground AoE regen — always valuable even without BMR
        if (AsylumPvE.CanUse(out act))
            return true;

        // Plenary Indulgence: grants Confession for bonus healing on GCD heals
        if (PlenaryIndulgencePvE.CanUse(out act))
            return true;

        return base.HealAreaAbility(nextGCD, out act);
    }

    #endregion

    #region Attack Ability (oGCD Damage)

    protected override bool AttackAbility(IAction nextGCD, out IAction? act)
    {
        if (!InCombat)
            return base.AttackAbility(nextGCD, out act);

        // --- Presence of Mind (120s personal haste + Sacred Sight stacks) ---
        // WHM's only personal DPS buff. Align with party 2-minute burst windows.
        // Grants 3x Glare IV (640 potency each, instant cast).
        if (CanBurst && PresenceOfMindPvE.CanUse(out act))
            return true;

        // --- Assize (45s CD, 400 potency damage + 400 potency heal + 500 MP) ---
        // NEVER hold Assize. It is damage, healing, AND MP recovery in one oGCD.
        // Balance guide: "Assize should be used on cooldown in pretty much all scenarios."
        if (AssizePvE.CanUse(out act, skipAoeCheck: true))
            return true;

        return base.AttackAbility(nextGCD, out act);
    }

    #endregion

    #region General Ability

    protected override bool GeneralAbility(IAction nextGCD, out IAction? act)
    {
        // Divine Caress: use if available (follow-up to Temperance)
        if (DivineCaressPvE.CanUse(out act))
            return true;

        return base.GeneralAbility(nextGCD, out act);
    }

    #endregion

    #region GCD Healing

    [RotationDesc(ActionID.AfflatusSolacePvE, ActionID.RegenPvE, ActionID.CureIiPvE)]
    protected override bool HealSingleGCD(out IAction? act)
    {
        // Reserve Swiftcast for Raise
        if ((HasSwift || IsLastAction(ActionID.SwiftcastPvE)) && SwiftLogic && MergedStatus.HasFlag(AutoStatus.Raise))
            return base.HealSingleGCD(out act);

        // Afflatus Solace: instant, free, builds Blood Lily. Always top priority.
        if (AfflatusSolacePvE.CanUse(out act))
            return true;

        // Regen: 1500 total potency HoT (only if target not dangerously low)
        if (RegenPvE.CanUse(out act)
            && RegenPvE.Target.Target?.GetHealthRatio() > RegenThreshold)
        {
            return true;
        }

        // Cure II: 800 potency backup when no lilies
        if (CureIiPvE.CanUse(out act))
            return true;

        // Cure: low level fallback
        if (CurePvE.CanUse(out act))
            return true;

        return base.HealSingleGCD(out act);
    }

    [RotationDesc(ActionID.AfflatusRapturePvE, ActionID.MedicaIiiPvE, ActionID.MedicaIiPvE, ActionID.CureIiiPvE)]
    protected override bool HealAreaGCD(out IAction? act)
    {
        // Reserve Swiftcast for Raise
        if ((HasSwift || IsLastAction(ActionID.SwiftcastPvE)) && SwiftLogic && MergedStatus.HasFlag(AutoStatus.Raise))
            return base.HealAreaGCD(out act);

        // Afflatus Rapture: instant, free AoE heal, builds Blood Lily
        if (AfflatusRapturePvE.CanUse(out act))
            return true;

        // Medica III / Medica II: AoE regen (check if regen not already active on majority)
        int hasMedicaRegen = 0;
        int partyCount = 0;
        foreach (var n in PartyMembers)
        {
            partyCount++;
            if (n.HasStatus(true, StatusID.MedicaIi, StatusID.TrueMedicaIi, StatusID.MedicaIii))
                hasMedicaRegen++;
        }

        if (MedicaIiiPvE.EnoughLevel && MedicaIiiPvE.CanUse(out act)
            && hasMedicaRegen < partyCount / 2)
        {
            return true;
        }

        if (!MedicaIiiPvE.EnoughLevel && MedicaIiPvE.EnoughLevel && MedicaIiPvE.CanUse(out act)
            && hasMedicaRegen < partyCount / 2)
        {
            return true;
        }

        // Cure III: high burst AoE heal for stacked party
        if (CureIiiPvE.CanUse(out act))
            return true;

        // Medica: last resort AoE heal
        if (MedicaPvE.CanUse(out act))
            return true;

        return base.HealAreaGCD(out act);
    }

    [RotationDesc(ActionID.RaisePvE)]
    protected override bool RaiseGCD(out IAction? act)
    {
        if (RaisePvE.CanUse(out act))
            return true;

        return base.RaiseGCD(out act);
    }

    #endregion

    #region General GCD (Damage Priority)

    protected override bool GeneralGCD(out IAction? act)
    {
        // --- Raise priority with Thin Air ---
        if (HasThinAir && MergedStatus.HasFlag(AutoStatus.Raise))
            return RaiseGCD(out act);

        // --- Reserve Swiftcast for Raise ---
        if ((HasSwift || IsLastAction(ActionID.SwiftcastPvE)) && SwiftLogic && MergedStatus.HasFlag(AutoStatus.Raise))
            return base.GeneralGCD(out act);

        // === PRIORITY 1: Afflatus Misery (1240 potency, instant) ===
        // Blood Lily at 3 stacks -> fire Misery. Preferably during Presence of Mind / raid buffs.
        // In DPS Bot mode, try to hold for burst if possible, but never let it delay Lily spending.
        if (MiseryReady)
        {
            // In DPS Bot mode, prefer to fire during burst window. Otherwise, fire immediately.
            if (HealMode == HealModeStrategy.DPSBot && !InBurstWindow)
            {
                // Hold Misery for burst, but fire if lilies are capping and we'd waste stacks
                if (LiliesNearCap && AfflatusMiseryPvE.CanUse(out act, skipAoeCheck: true))
                    return true;
            }
            else
            {
                if (AfflatusMiseryPvE.CanUse(out act, skipAoeCheck: true))
                    return true;
            }
        }

        // === PRIORITY 2: Glare IV (Sacred Sight stacks from Presence of Mind) ===
        // 640 potency instant cast, granted by Presence of Mind. Use all 3 stacks.
        if (GlareIvPvE.CanUse(out act))
            return true;

        // === PRIORITY 3: Lily overcap protection ===
        // Spend lilies to prevent overcap. This builds Blood Lily for Misery.
        // In DPS Bot mode, always spend for Misery value.
        // In Balanced/HealBot, also useful for healing.
        if (LilyOvercapProtection && LiliesNearCap && BloodLily < 3)
        {
            if (AfflatusRapturePvE.CanUse(out act, skipAoeCheck: true))
                return true;
            if (AfflatusSolacePvE.CanUse(out act))
                return true;
        }

        // === PRIORITY 4: AoE damage (3+ targets) ===
        if (HolyIiiPvE.EnoughLevel && HolyIiiPvE.CanUse(out act))
            return true;
        if (!HolyIiiPvE.EnoughLevel && HolyPvE.EnoughLevel && HolyPvE.CanUse(out act))
            return true;

        // === PRIORITY 5: Dia DoT maintenance ===
        // Dia: 75 upfront + 750 DoT over 30s = 825 total potency. Must keep up at all times.
        // Framework handles standard refresh timing via IsRestrictedDOT.
        if (DiaPvE.EnoughLevel && DiaPvE.CanUse(out act))
            return true;
        if (!DiaPvE.EnoughLevel && AeroIiPvE.EnoughLevel && AeroIiPvE.CanUse(out act))
            return true;
        if (!AeroIiPvE.EnoughLevel && AeroPvE.EnoughLevel && AeroPvE.CanUse(out act))
            return true;

        // === PRIORITY 6: Glare III (single target filler, 310 potency) ===
        if (GlareIiiPvE.EnoughLevel && GlareIiiPvE.CanUse(out act))
            return true;
        if (!GlareIiiPvE.EnoughLevel && GlarePvE.EnoughLevel && GlarePvE.CanUse(out act))
            return true;
        if (!GlarePvE.EnoughLevel && StoneIvPvE.EnoughLevel && StoneIvPvE.CanUse(out act))
            return true;
        if (!StoneIvPvE.EnoughLevel && StoneIiiPvE.EnoughLevel && StoneIiiPvE.CanUse(out act))
            return true;
        if (!StoneIiiPvE.EnoughLevel && StoneIiPvE.EnoughLevel && StoneIiPvE.CanUse(out act))
            return true;
        if (!StoneIiPvE.EnoughLevel && StonePvE.CanUse(out act))
            return true;

        // === FALLBACK: Lily spend for movement/downtime ===
        // If no valid target for damage, spend lilies to avoid waste
        if (LilyOvercapProtection && LiliesNearCap && BloodLily < 3)
        {
            if (AfflatusRapturePvE.CanUse(out act, skipAoeCheck: true))
                return true;
            if (AfflatusSolacePvE.CanUse(out act))
                return true;
        }

        // === MOVEMENT FALLBACK: Dia even if not needing refresh ===
        // Instant cast DoT for movement uptime
        if (DiaDotUptime && IsMoving)
        {
            if (DiaPvE.EnoughLevel && DiaPvE.CanUse(out act, skipStatusProvideCheck: true))
                return true;
            if (!DiaPvE.EnoughLevel && AeroIiPvE.EnoughLevel && AeroIiPvE.CanUse(out act, skipStatusProvideCheck: true))
                return true;
            if (!AeroIiPvE.EnoughLevel && AeroPvE.EnoughLevel && AeroPvE.CanUse(out act, skipStatusProvideCheck: true))
                return true;
        }

        return base.GeneralGCD(out act);
    }

    #endregion

    #region Debug Display

    public override void DisplayRotationStatus()
    {
        ImGui.Text($"--- Burst ---");
        ImGui.Text($"CanBurst: {CanBurst}");
        ImGui.Text($"InBurstWindow: {InBurstWindow}");
        ImGui.Text($"HasPresenceOfMind: {HasPresenceOfMind}");
        ImGui.Text($"PoM CD: {(PresenceOfMindPvE.Cooldown.IsCoolingDown ? $"{PresenceOfMindPvE.Cooldown.RecastTimeRemain:F1}s" : "Ready")}");
        ImGui.Text($"Medicated: {IsMedicated}");
        ImGui.Text($"--- Lily System ---");
        ImGui.Text($"Lily: {Lily}");
        ImGui.Text($"BloodLily: {BloodLily}");
        ImGui.Text($"LilyTime: {LilyTime:F1}");
        ImGui.Text($"LiliesNearCap: {LiliesNearCap}");
        ImGui.Text($"MiseryReady: {MiseryReady}");
        ImGui.Text($"SacredSightStacks: {SacredSightStacks}");
        ImGui.Text($"--- Cooldowns ---");
        ImGui.Text($"Assize: {(AssizePvE.Cooldown.IsCoolingDown ? $"{AssizePvE.Cooldown.RecastTimeRemain:F1}s" : "Ready")}");
        ImGui.Text($"Benediction: {(BenedictionPvE.Cooldown.IsCoolingDown ? $"{BenedictionPvE.Cooldown.RecastTimeRemain:F1}s" : "Ready")}");
        ImGui.Text($"Tetra: {(TetragrammatonPvE.Cooldown.IsCoolingDown ? $"{TetragrammatonPvE.Cooldown.RecastTimeRemain:F1}s" : "Ready")}");
        ImGui.Text($"Temperance: {(TemperancePvE.Cooldown.IsCoolingDown ? $"{TemperancePvE.Cooldown.RecastTimeRemain:F1}s" : "Ready")}");
        ImGui.Text($"ThinAir: {ThinAirPvE.Cooldown.CurrentCharges}/{ThinAirPvE.Cooldown.MaxCharges}");
        ImGui.Text($"HasThinAir: {HasThinAir}");
        ImGui.Text($"--- Healing ---");
        ImGui.Text($"HealMode: {HealMode}");
        ImGui.Text($"IsSoloHealer: {IsSoloHealer}");
        ImGui.Text($"CanHealSingleSpell: {CanHealSingleSpell}");
        ImGui.Text($"CanHealAreaSpell: {CanHealAreaSpell}");
        ImGui.Text($"PartyHP: {PartyMembersAverHP:P0}");
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
}
