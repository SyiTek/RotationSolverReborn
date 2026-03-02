using System.ComponentModel;

namespace RotationSolver.ExtraRotations.Healer;

[Rotation("SezuraiWHM", CombatType.PvE, GameVersion = "7.41",
    Description = "Balance-aligned WHM with BMR timeline integration, Presence of Mind burst, Lily management, and 3 healing modes.")]
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
    // Pre-pull: Divine Benison(-5s) -> Glare III precast(-2.3s)
    // Pull: Pot (weave as Glare III lands)
    // GCD1: Dia (instant DoT)
    // GCD2: Glare III
    // GCD3: Glare III -> Presence of Mind (weave)
    // GCD4: Glare IV (Sacred Sight stack 1) -> Assize (weave)
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
    // No PoM -- it is 120s. Continue Glare III spam
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
        // Per Balance 7.4: Plenary grants Confession (200p bonus per AoE GCD heal)
        // Non-consumed buff = multiple procs over 10s. Fire before planned GCD heal sequence.
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

    [RotationDesc(ActionID.TemperancePvE, ActionID.LiturgyOfTheBellPvE, ActionID.PlenaryIndulgencePvE)]
    protected override bool DefenseAreaAbility(IAction nextGCD, out IAction? act)
    {
        // === BMR-AWARE MITIGATION ===
        // Core principle: MIT BEFORE raidwide, HEAL AFTER damage.
        // Per Balance: "Temperance is your main party mitigation tool" (10% mit + 20% heal boost)
        // Per Balance 7.4: Plenary Indulgence now grants damage mitigation (AoE increased to 30y)
        // Spread mits across raidwides -- multiplicative stacking means spreading > dumping.
        // Max 1-2 mits per raidwide to preserve CDs for the next mechanic.
        //
        // Liturgy of the Bell: 5 stacks, each pops for 400p heal when WHM takes damage.
        // Must be PLACED BEFORE raidwide (needs ~3-8s lead time). Total 2000p if all 5 pop.
        // 180s CD so plan usage around major mechanics.
        //
        // Temperance: 10% party mit (refreshes while in range) + 20% GCD heal buff. 120s CD.
        // Best used for sustained heavy damage phases. The mit is the primary value in savage.
        //
        // Plenary Indulgence: 60s CD. In 7.4 also provides mitigation.
        // Lighter CD -- use more liberally on lesser raidwides where Temperance is overkill.

        bool rwSoon = BmrActive && BmrRaidwideIn is > 0 and <= 5f;
        bool rwMedium = BmrActive && BmrRaidwideIn is > 5f and <= 8f;

        if (rwSoon)
        {
            // Temperance first: highest-value WHM party CD (10% mit for full duration)
            if (TemperancePvE.CanUse(out act))
                return true;

            // Divine Caress: follow-up shield from Temperance (requires DivineGrace status)
            if (DivineCaressPvE.CanUse(out act))
                return true;

            // Plenary Indulgence: party mitigation + prepares Confession for post-RW GCD heals
            if (PlenaryIndulgencePvE.CanUse(out act))
                return true;

            // Max 1-2 mits per raidwide -- Bell goes in the medium window below
            return base.DefenseAreaAbility(nextGCD, out act);
        }

        if (rwMedium)
        {
            // Liturgy of the Bell: needs to be placed 3-8s before damage so bells pop on hits
            // Per Balance: "5 stacks, each damage instance heals all allies within 20y for 400p"
            if ((MultiHitRestrict && IsCastingMultiHit) || !MultiHitRestrict)
            {
                if (LiturgyOfTheBellPvE.CanUse(out act, skipAoeCheck: true))
                    return true;
            }

            // If no Bell available and Temperance is up, use it with the longer lead time
            if (TemperancePvE.CanUse(out act))
                return true;

            return base.DefenseAreaAbility(nextGCD, out act);
        }

        // === NON-BMR FALLBACK ===
        // Without BMR timeline data, stagger cooldowns to avoid dumping everything at once.
        // Only use if the CDs are reasonably available (not deep into cooldown).
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
        // === BMR-AWARE TANKBUSTER MITIGATION ===
        // Core principle: shield/mit BEFORE tankbuster, heal AFTER damage hits.
        // Per Balance: "Divine Benison is a 500p shield on 30s CD with 2 charges at 88.
        //   Avoid holding charges unnecessarily. Shields have application delays."
        // Per Balance: "Aquaveil is 15% mitigation for 8s, stacks with other mit."
        //
        // Strategy: Benison first (cheap, 2 charges, covers auto-attacks too),
        // then Aquaveil for heavy TBs. Max 2 mits per TB to preserve for next mechanic.
        bool tbSoon = BmrActive && BmrTankbusterIn is > 0 and <= 6f;

        if (tbSoon)
        {
            // Divine Benison first: 500p shield, 2 charges (low cost, high value)
            if (DivineBenisonPvE.CanUse(out act))
                return true;

            // Aquaveil: 15% damage reduction for 8s -- stack with Benison for heavy TBs
            if (AquaveilPvE.CanUse(out act))
                return true;

            // Max 2 per TB -- stop
            return base.DefenseSingleAbility(nextGCD, out act);
        }

        // === NON-BMR FALLBACK ===
        // Stagger cooldowns -- don't use both if neither is really needed
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
        // === BMR-AWARE SINGLE HEAL LOGIC ===
        // Core principle: heal AFTER damage, not before. Healing at full HP = 100% overheal.
        // If a TB is coming in 1-4s, hold single-target heals -- the damage hasn't hit yet.
        // DefenseSingleAbility already applied Benison/Aquaveil MIT; heals fire post-damage.
        bool tbComingSoon = BmrActive && BmrTankbusterIn is > 1f and <= 4f;

        if (tbComingSoon)
        {
            // Only Benediction breaks through the hold -- if tank is critically low from
            // auto-attacks BEFORE the TB, they need emergency healing regardless
            if (BenedictionPvE.CanUse(out act)
                && BenedictionPvE.Target.Target?.GetHealthRatio() < BenedictionThreshold)
            {
                return true;
            }
            return base.HealSingleAbility(nextGCD, out act);
        }

        // === POST-DAMAGE HEALING (normal priority) ===
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
        // Per Balance: "Avoid holding charges unnecessarily"
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
        // === BMR-AWARE AREA HEAL LOGIC ===
        // Core principle: MIT before raidwide, HEAL after damage.
        // This method fires when party HP drops (i.e., AFTER raidwide hits) or proactively.
        //
        // rwComingSoon guard: if a raidwide is 1-8s away, HOLD area heals.
        // Healing before damage = wasted on full HP. MIT is already handled by DefenseAreaAbility.
        // The heals below will fire naturally after the raidwide hits and HP drops.
        //
        // EXCEPTION: Asylum placement -- it is BOTH mit (10% heal received buff at L78)
        // AND healing (regen ticks). Place it BEFORE raidwide so the regen starts ticking
        // immediately after damage, and the 10% heal buff amplifies post-RW heals.
        bool rwComingSoon = BmrActive && BmrRaidwideIn is > 1f and <= 8f;

        // Asylum BEFORE raidwide: ground regen + 10% healing received buff (at L78+)
        // Per Balance: "980p total across 8 buffed ticks + instant tick. 90s CD."
        // Place it 2-8s before RW so regen ticks start healing immediately post-damage.
        // The 10% heal buff also amplifies subsequent GCD heals (stacks with Temperance 20%).
        if (rwComingSoon && AsylumPvE.CanUse(out act))
            return true;

        // Hold all other heals if raidwide is imminent -- damage hasn't hit yet
        if (rwComingSoon)
            return base.HealAreaAbility(nextGCD, out act);

        // === POST-DAMAGE HEALING (normal priority) ===
        // This section fires after raidwide damage has hit (party HP is low).

        // Asylum: ground AoE regen -- always valuable even without BMR
        if (AsylumPvE.CanUse(out act))
            return true;

        // Plenary Indulgence: grants Confession for 200p bonus on GCD heals
        // Per Balance: "Non-consumed buff allows multiple procs over 10s duration."
        // In post-damage context, prepares the next GCD heal to be significantly stronger.
        // Already used for MIT in DefenseAreaAbility if BMR-aware, so only fire here
        // if it wasn't consumed pre-RW (60s CD, should be available frequently).
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

        // === BMR-AWARE BURST MANAGEMENT ===
        // Per Balance: PoM is WHM's only personal DPS buff (120s).
        // Align with party 2-minute burst windows. Grants 3x Glare IV (640p each, instant).
        //
        // BMR downtime awareness: if downtime is imminent, accelerate burst usage.
        // Don't waste PoM on the last 5s before boss jumps.
        bool downtimeSoon = BmrActive && BmrDowntimeIn is > 0 and <= 15f;

        // --- Presence of Mind (120s personal haste + Sacred Sight stacks) ---
        // Use during burst, or accelerate if downtime is coming soon
        if (CanBurst && PresenceOfMindPvE.CanUse(out act))
            return true;

        // If downtime is coming and PoM is available, use it now to get value before boss jumps
        if (downtimeSoon && BmrDowntimeIn > 5f && PresenceOfMindPvE.CanUse(out act))
            return true;

        // --- Assize (45s CD, 400 potency damage + 400 potency heal + 500 MP) ---
        // NEVER hold Assize. It is damage, healing, AND MP recovery in one oGCD.
        // Per Balance: "Assize should be used on cooldown in pretty much all scenarios."
        // BMR note: if downtime is imminent (<3s), hold Assize -- it would miss the target.
        bool downtimeImminent = BmrActive && BmrDowntimeIn is > 0 and <= 3f;
        if (!downtimeImminent && AssizePvE.CanUse(out act, skipAoeCheck: true))
            return true;

        return base.AttackAbility(nextGCD, out act);
    }

    #endregion

    #region General Ability

    protected override bool GeneralAbility(IAction nextGCD, out IAction? act)
    {
        // Divine Caress: use if available (follow-up to Temperance)
        // Per Balance: grants a party shield. Never let DivineGrace expire without using this.
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
        ImGui.Text($"Asylum: {(AsylumPvE.Cooldown.IsCoolingDown ? $"{AsylumPvE.Cooldown.RecastTimeRemain:F1}s" : "Ready")}");
        ImGui.Text($"Bell: {(LiturgyOfTheBellPvE.Cooldown.IsCoolingDown ? $"{LiturgyOfTheBellPvE.Cooldown.RecastTimeRemain:F1}s" : "Ready")}");
        ImGui.Text($"Plenary: {(PlenaryIndulgencePvE.Cooldown.IsCoolingDown ? $"{PlenaryIndulgencePvE.Cooldown.RecastTimeRemain:F1}s" : "Ready")}");
        ImGui.Text($"Aquaveil: {(AquaveilPvE.Cooldown.IsCoolingDown ? $"{AquaveilPvE.Cooldown.RecastTimeRemain:F1}s" : "Ready")}");
        ImGui.Text($"Benison: {DivineBenisonPvE.Cooldown.CurrentCharges}/{DivineBenisonPvE.Cooldown.MaxCharges}");
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
        ImGui.Text($"UseBmrTimeline: {Service.Config.UseBmrTimeline}");
        if (BmrActive)
        {
            ImGui.Text($"-- Final Merged Values --");
            ImGui.Text($"Raidwide In: {(BmrRaidwideIn < 9999f ? $"{BmrRaidwideIn:F1}s" : "None")}");
            ImGui.Text($"Tankbuster In: {(BmrTankbusterIn < 9999f ? $"{BmrTankbusterIn:F1}s" : "None")}");
            ImGui.Text($"Knockback In: {(BmrKnockbackIn < 9999f ? $"{BmrKnockbackIn:F1}s" : "None")}");
            ImGui.Text($"Downtime In: {(BmrDowntimeIn < 9999f ? $"{BmrDowntimeIn:F1}s" : "None")}");
            ImGui.Text($"Vulnerable In: {(BmrVulnerableIn < 9999f ? $"{BmrVulnerableIn:F1}s" : "None")}");
            ImGui.Text($"DamageIn: {(BmrDamageIn < 9999f ? $"{BmrDamageIn:F1}s" : "None")}");
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
}
