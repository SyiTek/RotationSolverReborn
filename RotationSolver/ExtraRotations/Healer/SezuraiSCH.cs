using System.ComponentModel;

namespace RotationSolver.ExtraRotations.Healer;

[Rotation("SezuraiSCH", CombatType.PvE, GameVersion = "7.41",
    Description = "Balance-aligned SCH with BMR timeline integration, Chain Stratagem burst, Energy Drain optimization, and 3 healing modes.")]
[SourceCode(Path = "main/ExtraRotations/Healer/SezuraiSCH.cs")]
[ExtraRotation]
public sealed class SezuraiSCH : ScholarRotation
{
    #region Config Options

    [Range(0f, 0.25f, ConfigUnitType.Percent)]
    [RotationConfig(CombatType.PvE, Name = "Action Ahead Override (0 = use global setting)")]
    public float ActionAheadOverride { get; set; } = 0f;

    [RotationConfig(CombatType.PvE, Name = "Heal Mode")]
    public HealModeStrategy HealMode { get; set; } = HealModeStrategy.Balanced;

    public enum HealModeStrategy : byte
    {
        [Description("DPS Bot: Energy Drain all Aetherflow, fairy heals only, maximize Broil casts")]
        DPSBot,

        [Description("Balanced: Save Aetherflow for emergencies, fairy + oGCD heals")]
        Balanced,

        [Description("Heal Bot: Liberal Aetherflow healing, GCD heals when needed")]
        HealBot,
    }

    [RotationConfig(CombatType.PvE, Name = "Use Mind Gemdraught/Potion in burst windows")]
    public bool BurstMed { get; set; } = true;

    [RotationConfig(CombatType.PvE, Name = "Swiftcast restriction: reserve for Raise")]
    public bool SwiftLogic { get; set; } = true;

    [Range(0, 3, ConfigUnitType.None)]
    [RotationConfig(CombatType.PvE, Name = "Aetherflow stacks to reserve for healing (DPS Bot ignores this)")]
    public int EnergyDrainThreshold { get; set; } = 1;

    [RotationConfig(CombatType.PvE, Name = "Use Dissipation for extra Aetherflow (DPS mode: on CD, others: only when healing is needed)")]
    public bool UseDissipation { get; set; } = true;

    [Range(0, 1, ConfigUnitType.Percent)]
    [RotationConfig(CombatType.PvE, Name = "HP threshold for Lustrate")]
    public float LustrateThreshold { get; set; } = 0.6f;

    [Range(0, 1, ConfigUnitType.Percent)]
    [RotationConfig(CombatType.PvE, Name = "HP threshold for Excogitation")]
    public float ExcogThreshold { get; set; } = 0.8f;

    [Range(0, 1, ConfigUnitType.Percent)]
    [RotationConfig(CombatType.PvE, Name = "Party HP threshold for Sacred Soil")]
    public float SacredSoilThreshold { get; set; } = 0.75f;

    [Range(0, 1, ConfigUnitType.Percent)]
    [RotationConfig(CombatType.PvE, Name = "Party HP threshold for Indomitability")]
    public float IndomThreshold { get; set; } = 0.7f;

    [Range(0, 1, ConfigUnitType.Percent)]
    [RotationConfig(CombatType.PvE, Name = "Party HP threshold for Fey Blessing")]
    public float FeyBlessingThreshold { get; set; } = 0.8f;

    [Range(0, 1, ConfigUnitType.Percent)]
    [RotationConfig(CombatType.PvE, Name = "Party HP threshold for Whispering Dawn")]
    public float WhisperingDawnThreshold { get; set; } = 0.85f;

    #endregion

    #region Burst State

    /// <summary>
    /// Framework burst enabled.
    /// </summary>
    private bool CanBurst => MergedStatus.HasFlag(AutoStatus.Burst);

    /// <summary>
    /// True when Chain Stratagem debuff is active on a target.
    /// </summary>
    private bool InChainStratagemWindow => ChainStratagemPvE.Cooldown.IsCoolingDown
        && ChainStratagemPvE.Cooldown.JustUsedAfter(15);

    /// <summary>
    /// True when approaching or in the Chain Stratagem window.
    /// </summary>
    private bool InBurstWindow => InChainStratagemWindow
        || !ChainStratagemPvE.EnoughLevel
        || ChainStratagemPvE.Cooldown.HasOneCharge
        || ChainStratagemPvE.Cooldown.WillHaveOneCharge(5);

    /// <summary>
    /// Number of Aetherflow stacks available for Energy Drain based on mode.
    /// </summary>
    private int AetherflowStacksForDPS
    {
        get
        {
            if (HealMode == HealModeStrategy.DPSBot)
                return SCHAetherFlowStacks;

            int reserve = HealMode == HealModeStrategy.HealBot ? 2 : EnergyDrainThreshold;
            int available = SCHAetherFlowStacks - reserve;
            return available > 0 ? available : 0;
        }
    }

    #endregion

    #region UpdateInfo

    protected override void UpdateInfo()
    {
        DataCenter.RotationActionAheadOverride = ActionAheadOverride > 0f ? ActionAheadOverride : null;
    }

    #endregion

    #region Countdown & Opener
    // === SCH OPENER (7.4 Balance / Icy Veins — Aetherflow-first) ===
    // Pre-pull: Summon Eos(-7s) -> Broil IV precast(-2.4s) -> Pot (weave on land)
    // GCD1: Biolysis (instant) -> Aetherflow (weave, grants 3 stacks)
    // GCD2: Broil IV -> Chain Stratagem (weave)
    // GCD3: Broil IV -> Energy Drain (weave)
    // GCD4: Broil IV -> Energy Drain (weave)
    // GCD5: Broil IV -> Energy Drain (weave)
    // GCD6: Broil IV -> Dissipation (weave, +3 more stacks, removes fairy 30s)
    // GCD7: Broil IV -> Baneful Impaction (weave)
    // GCD8: Broil IV -> Energy Drain (weave)
    // GCD9: Broil IV -> Energy Drain (weave)
    // GCD10: Biolysis (early refresh to snapshot buffs) -> Energy Drain (weave)
    //
    // === EVEN BURST (120s) ===
    // Chain Stratagem (10% crit, 120s) + Baneful Impaction + 6x Energy Drain
    //   (3 from Aetherflow + 3 from Dissipation) + Biolysis snapshot refresh
    //
    // === ODD BURST (60s) ===
    // Aetherflow (60s) -> 3x Energy Drain only. No Chain Strat
    // Dump stacks before next Aetherflow to avoid overcapping
    //
    // === FILLER ===
    // Broil IV spam (>90% of GCDs). Biolysis refresh at 30s, skip if target dies in <12s
    // Energy Drain: dump under raid buffs, never waste Aetherflow stacks
    // Movement: Biolysis refresh > Swiftcast+Broil > Ruin II (last resort)

    protected override IAction? CountDownAction(float remainTime)
    {
        // Pre-pull Broil IV cast
        if (remainTime < BroilIvPvE.Info.CastTime + CountDownAhead && BroilIvPvE.CanUse(out var act))
            return act;

        // Lower level fallbacks
        if (!BroilIvPvE.EnoughLevel && remainTime < BroilIiiPvE.Info.CastTime + CountDownAhead && BroilIiiPvE.CanUse(out act))
            return act;
        if (!BroilIiiPvE.EnoughLevel && remainTime < BroilIiPvE.Info.CastTime + CountDownAhead && BroilIiPvE.CanUse(out act))
            return act;
        if (!BroilIiPvE.EnoughLevel && remainTime < BroilPvE.Info.CastTime + CountDownAhead && BroilPvE.CanUse(out act))
            return act;
        if (!BroilPvE.EnoughLevel && remainTime < RuinPvE.Info.CastTime + CountDownAhead && RuinPvE.CanUse(out act))
            return act;

        // Medicine before pull
        if (BurstMed && remainTime < 3 && UseBurstMedicine(out act))
            return act;

        // Summon fairy if needed
        if (remainTime < 7 && SummonEosPvE.CanUse(out act))
            return act;

        return base.CountDownAction(remainTime);
    }

    #endregion

    #region Emergency Ability

    protected override bool EmergencyAbility(IAction nextGCD, out IAction? act)
    {
        if (!InCombat)
            return base.EmergencyAbility(nextGCD, out act);

        // Medicine: use just before Chain Stratagem for burst alignment
        if (BurstMed && ChainStratagemPvE.CanUse(out _) && UseBurstMedicine(out act))
            return true;

        // Recitation before Excogitation or Indomitability for guaranteed crit + free Aetherflow
        // In all modes: Recitation + Excog is the best single-target heal in the game
        if (nextGCD.IsTheSameTo(false, AdloquiumPvE, SuccorPvE))
        {
            if (RecitationPvE.CanUse(out act))
                return true;
        }

        return base.EmergencyAbility(nextGCD, out act);
    }

    #endregion

    #region Defense

    [RotationDesc(ActionID.SacredSoilPvE, ActionID.ExpedientPvE, ActionID.FeyIlluminationPvE, ActionID.SummonSeraphPvE, ActionID.ConsolationPvE)]
    protected override bool DefenseAreaAbility(IAction nextGCD, out IAction? act)
    {
        // === BMR-AWARE PARTY MITIGATION ===
        // Per Balance/Icy Veins: spread mits across raidwides, don't stack everything on one hit.
        // Mitigation is multiplicative (two 10% = 19%, not 20%), so spreading is more efficient.
        // SCH has: Sacred Soil (10% ground mit + HoT), Expedient (10% party mit + sprint),
        //          Fey Illumination (5% magic mit + heal potency), Seraph/Consolation (shields).
        // Use at most 2 mits per raidwide event. Sacred Soil needs placement time (8s window).
        bool rwSoon = BMRActive && BMRRaidwideIn is > 0 and <= 8f;
        bool rwImminent = BMRActive && BMRRaidwideIn is > 0 and <= 5f;
        bool canSpendAetherflowOnHeal = HealMode != HealModeStrategy.DPSBot || !HasAetherflow;
        int mitsUsed = 0;

        if (rwSoon)
        {
            // Sacred Soil: ground 10% mit + HoT — needs to be placed BEFORE damage lands.
            // 8s window gives time for ground placement + party to be inside the bubble.
            // Per Balance: "your most powerful usage of Aetherflow" for healing/mit combined.
            if (canSpendAetherflowOnHeal && SacredSoilPvE.CanUse(out act))
                return true;

            // Seraph + Consolation: pre-shield the party before raidwide.
            // Summon Seraph gives 2 Consolation charges (250p heal + 250 shield each).
            // Per Balance: "especially if there are multiple raidwides within a short interval."
            // Only summon Seraph proactively if Consolation isn't already available.
            if (!ConsolationPvE.Cooldown.HasOneCharge && SummonSeraphPvE.CanUse(out act))
                return true;

            // Consolation: Seraph AoE shield — use one charge for this RW, save one for next.
            // Do NOT usedUp: true here — spread charges across multiple raidwides.
            if (ConsolationPvE.CanUse(out act))
            {
                mitsUsed++;
                return true;
            }

            // Expedient: 10% party mit + sprint for 20s — best SCH party CD.
            // Per Balance: "20s AoE mitigation (10%) and 10s AoE move speed buff."
            // Use when RW is imminent (5s) so the mit window covers the damage snapshot.
            if (rwImminent && ExpedientPvE.CanUse(out act))
            {
                mitsUsed++;
                if (mitsUsed >= 2) return true;
                return true;
            }

            // Fey Illumination: 5% magic mit + 10% heal potency buff.
            // Per Balance: combine with heals after raidwide for amplified recovery.
            // Only if we haven't already stacked 2 mits on this raidwide.
            if (mitsUsed < 2 && FeyIlluminationPvE.CanUse(out act))
                return true;

            // Max 2 mits per raidwide — stop. Let the next RW get its own coverage.
            return base.DefenseAreaAbility(nextGCD, out act);
        }

        // === NON-BMR FALLBACK ===
        // Without BMR timeline data, use mits whenever the framework triggers defense.
        // Expedient is always good — it's the strongest general-purpose party CD.
        if (ExpedientPvE.CanUse(out act))
            return true;

        if (FeyIlluminationPvE.CanUse(out act))
            return true;

        return base.DefenseAreaAbility(nextGCD, out act);
    }

    [RotationDesc(ActionID.ExcogitationPvE, ActionID.ProtractionPvE)]
    protected override bool DefenseSingleAbility(IAction nextGCD, out IAction? act)
    {
        // === BMR-AWARE TANK MITIGATION ===
        // Per Balance: Excogitation is a delayed heal that triggers at <50% HP or on expiry.
        // Place it on the tank BEFORE the tankbuster so it auto-heals after damage.
        // Recitation + Excog = guaranteed crit + free (no Aetherflow cost) — the dream combo.
        // Protraction: 10% max HP increase + healing received buff — stacks with Excog.
        // 8s window: enough time to weave Recitation -> Excogitation -> Protraction.
        bool tbSoon = BMRActive && BMRTankbusterIn is > 0 and <= 8f;
        bool tbImminent = BMRActive && BMRTankbusterIn is > 0 and <= 5f;

        if (tbSoon)
        {
            // Prep Recitation first if TB isn't imminent yet — sets up the free crit Excog.
            // If TB is 5-8s out, we have time to weave Recitation now, then Excog next oGCD.
            if (!tbImminent && !HasRecitation && RecitationPvE.CanUse(out act))
                return true;

            // Excogitation: auto-heal at <50% HP, strongest single-target tool.
            // Recitation + Excog is even better (guaranteed crit + free).
            // Per Balance: "if needed for preemptive tank healing, especially when Holmgang
            // or Living Dead are used."
            if (HasRecitation && ExcogitationPvE.CanUse(out act))
                return true;
            if (ExcogitationPvE.CanUse(out act))
                return true;

            // Protraction: 10% max HP increase + healing received buff.
            // Per Balance: "Most effective on tanks." Stacks multiplicatively with Excog.
            if (ProtractionPvE.CanUse(out act))
                return true;

            // Max 2 per TB — stop
            return base.DefenseSingleAbility(nextGCD, out act);
        }

        // === NON-BMR FALLBACK ===
        // Without BMR data, use Protraction when the framework triggers single defense.
        if (ProtractionPvE.CanUse(out act))
            return true;

        return base.DefenseSingleAbility(nextGCD, out act);
    }

    #endregion

    #region Healing Abilities

    [RotationDesc(ActionID.AetherpactPvE, ActionID.ProtractionPvE, ActionID.ExcogitationPvE, ActionID.LustratePvE)]
    protected override bool HealSingleAbility(IAction nextGCD, out IAction? act)
    {
        // === Free oGCD Heals (no Aetherflow cost) ===

        // Protraction: 10% max HP increase + healing received buff
        if (ProtractionPvE.CanUse(out act))
            return true;

        // Aetherpact (Fey Union): continuous fairy HoT, costs fairy gauge
        if (FairyGauge >= 30 && AetherpactPvE.CanUse(out act))
            return true;

        // === Recitation Combo: guaranteed crit + free Aetherflow ===
        // Recitation + Excogitation is the strongest single target heal combo
        if (HasRecitation && ExcogitationPvE.CanUse(out act))
            return true;

        // === Aetherflow oGCD Heals ===
        // Only spend Aetherflow on heals based on mode

        bool canSpendAetherflowOnHeal = HealMode != HealModeStrategy.DPSBot || !HasAetherflow;

        if (canSpendAetherflowOnHeal)
        {
            // Excogitation: delayed heal (triggers at <50% HP or on expiry)
            if (ExcogitationPvE.CanUse(out act)
                && ExcogitationPvE.Target.Target?.GetHealthRatio() < ExcogThreshold)
                return true;

            // Lustrate: instant 600p heal
            if (LustratePvE.CanUse(out act)
                && LustratePvE.Target.Target?.GetHealthRatio() < LustrateThreshold)
                return true;
        }

        // Setup Recitation for next heal if not already active
        if (!HasRecitation && RecitationPvE.CanUse(out act))
            return true;

        return base.HealSingleAbility(nextGCD, out act);
    }

    [RotationDesc(ActionID.FeyBlessingPvE, ActionID.WhisperingDawnPvE, ActionID.FeyIlluminationPvE,
        ActionID.ConsolationPvE, ActionID.SacredSoilPvE, ActionID.IndomitabilityPvE, ActionID.SeraphismPvE)]
    protected override bool HealAreaAbility(IAction nextGCD, out IAction? act)
    {
        // === BMR-AWARE HEAL TIMING: MIT BEFORE RAIDWIDE, HEAL AFTER DAMAGE ===
        // Per Balance/Icy Veins: the heal priority is free oGCDs > Aetherflow oGCDs > GCD heals.
        // When BMR knows a raidwide is coming, HOLD heals — let DefenseAreaAbility handle mit.
        // Heals should fire AFTER damage, not before. The framework triggers HealAreaAbility
        // when party HP drops, which should be after the raidwide hits.
        //
        // Exception: Sacred Soil has BOTH mit AND HoT, so it goes in DefenseAreaAbility for pre-placement.
        // Exception: Consolation is a shield — it goes in DefenseAreaAbility for pre-shielding.
        bool rwComingSoon = BMRActive && BMRRaidwideIn is > 1f and <= 8f;
        bool canSpendAetherflowOnHeal = HealMode != HealModeStrategy.DPSBot || !HasAetherflow;

        // === Seraphism: emergency healing super mode ===
        // Per Balance: capstone ability — converts GCD heals to instant casts with 20% potency increase.
        // Use in true emergencies or when party HP is critically low.
        if (HealMode == HealModeStrategy.HealBot && SeraphismPvE.CanUse(out act))
            return true;

        if (HealMode != HealModeStrategy.DPSBot && PartyMembersAverHP < 0.5f && SeraphismPvE.CanUse(out act))
            return true;

        // === Consolation: Seraph AoE heal + shield ===
        // Per Balance: 2 charges during Seraph — spread across raidwides for best value.
        // If Seraph is active, use Consolation freely as it's the primary reason we summoned.
        // usedUp: true to spend both charges since Seraph window is short (22s).
        if (ConsolationPvE.CanUse(out act, usedUp: true))
            return true;

        // === BMR HOLD: if raidwide is coming soon, STOP healing — wait for damage to land ===
        // MIT is already running from DefenseAreaAbility. Don't waste heals before the hit.
        // After the raidwide hits, party HP will drop and the framework re-triggers this method.
        if (rwComingSoon)
            return base.HealAreaAbility(nextGCD, out act);

        // === POST-DAMAGE HEALING (raidwide already hit, party HP is low) ===

        // === Free oGCD Heals (no Aetherflow cost) ===

        // Fey Blessing: fairy AoE heal — instant, free, strong.
        if (PartyMembersAverHP < FeyBlessingThreshold && FeyBlessingPvE.CanUse(out act))
            return true;

        // Whispering Dawn: fairy AoE HoT (21s regen) — best sustained AoE heal.
        // Per Balance: "Strong HoT from faerie, typically your primary AoE heal."
        if (PartyMembersAverHP < WhisperingDawnThreshold && WhisperingDawnPvE.CanUse(out act))
            return true;

        // Fey Illumination: 5% magic damage reduction + 10% healing potency buff.
        // Per Balance: combine with post-raidwide heals for amplified recovery.
        if (PartyMembersAverHP < 0.7f && FeyIlluminationPvE.CanUse(out act))
            return true;

        // === Recitation Combo: guaranteed crit + free Aetherflow ===
        // Per Balance: "Recitation + Indomitability is the most common usage in raids
        // for guaranteed critical AoE healing."
        if (HasRecitation && IndomitabilityPvE.CanUse(out act))
            return true;

        // === Aetherflow oGCD Heals ===
        if (canSpendAetherflowOnHeal)
        {
            // Sacred Soil: ground AoE regen + 10% mitigation (best Aetherflow AoE heal).
            // Per Balance: "your most powerful usage of Aetherflow" when party stays in bubble.
            if (PartyMembersAverHP < SacredSoilThreshold && SacredSoilPvE.CanUse(out act))
                return true;

            // Indomitability: instant AoE heal — reliable burst recovery.
            if (PartyMembersAverHP < IndomThreshold && IndomitabilityPvE.CanUse(out act))
                return true;
        }

        // Setup Recitation for next heal if not already active
        if (!HasRecitation && RecitationPvE.CanUse(out act))
            return true;

        return base.HealAreaAbility(nextGCD, out act);
    }

    #endregion

    #region Attack Ability

    protected override bool AttackAbility(IAction nextGCD, out IAction? act)
    {
        if (!InCombat)
            return base.AttackAbility(nextGCD, out act);

        // === CHAIN STRATAGEM (120s raid buff — 10% crit rate on target for 20s) ===
        // This is SCH's most important raid contribution. Align with party burst windows.
        // Per Balance: "Use during the opener, then use on cooldown." Delay only for raid coordination.
        //
        // BMR vuln window: if BMR knows a vulnerability window is coming, hold Chain Strat
        // to align the crit buff with the vuln debuff for maximum party DPS.
        bool vulnSoon = BMRActive && BMRVulnerableIn is > 0 and <= 10f;
        bool holdForVuln = vulnSoon && ChainStratagemPvE.Cooldown.HasOneCharge;

        if (CanBurst && !holdForVuln && ChainStratagemPvE.CanUse(out act))
            return true;

        // If vuln window is imminent (1-3s), fire Chain Strat regardless of burst flag
        // so it lands right as the boss becomes vulnerable.
        if (vulnSoon && BMRVulnerableIn <= 3f && ChainStratagemPvE.CanUse(out act))
            return true;

        // === Baneful Impaction (follow-up to Chain Stratagem) ===
        // Requires Impact Imminent status granted by Chain Stratagem.
        // Use immediately after Chain Strat — applies DoT to all nearby enemies.
        if (BanefulImpactionPvE.CanUse(out act))
            return true;

        // === Aetherflow (60s CD, grants 3 stacks) ===
        // Use on cooldown to prevent stack waste. Must have 0 stacks to use.
        if (AetherflowPvE.CanUse(out act))
            return true;

        // === Dissipation (dismisses fairy for 3 Aetherflow stacks + 20% healing buff) ===
        // DPS Bot: use on CD for extra Energy Drains
        // Balanced: use only when Aetherflow is on CD and stacks are needed
        // Heal Bot: save for emergency healing stacks
        // Per Balance: "Use as a damage cooldown (spending stacks on Energy Drain) or save
        // for emergencies to amplify shield potency with the healing buff."
        if (UseDissipation && DissipationPvE.CanUse(out act))
        {
            if (HealMode == HealModeStrategy.DPSBot)
                return true;

            // In other modes, only Dissipate when Aetherflow is on CD and we need stacks
            if (HealMode == HealModeStrategy.Balanced
                && AetherflowPvE.Cooldown.IsCoolingDown
                && !AetherflowPvE.Cooldown.WillHaveOneCharge(15))
                return true;

            // Heal Bot: only if we truly need healing stacks and have no other options
            if (HealMode == HealModeStrategy.HealBot
                && !HasAetherflow
                && AetherflowPvE.Cooldown.IsCoolingDown
                && !AetherflowPvE.Cooldown.WillHaveOneCharge(30)
                && PartyMembersAverHP < 0.6f)
                return true;
        }

        // === Energy Drain (Aetherflow DPS spender, 100 potency + MP) ===
        // Spend stacks based on mode:
        // DPS Bot: dump all stacks
        // Balanced: dump stacks above reserve threshold
        // Heal Bot: only dump if Aetherflow is about to come off CD and we'd waste stacks
        //
        // BMR downtime: dump all remaining stacks before downtime to avoid waste.
        // Aetherflow stacks don't persist through phase transitions in savage.
        bool downtimeSoon = BMRActive && BMRDowntimeIn is > 0 and <= 10f;

        if (HasAetherflow && EnergyDrainPvE.CanUse(out act))
        {
            if (HealMode == HealModeStrategy.DPSBot)
                return true;

            // BMR: dump ALL stacks before downtime — they'll be wasted otherwise.
            // Override reserve thresholds since there's nothing to heal during downtime.
            if (downtimeSoon)
                return true;

            // Spend stacks above threshold
            if (AetherflowStacksForDPS > 0)
            {
                // Prefer to spend during burst windows for alignment
                if (InBurstWindow)
                    return true;

                // Prevent overcap: spend if Aetherflow is coming off CD soon
                if (AetherflowPvE.Cooldown.IsCoolingDown && AetherflowPvE.Cooldown.WillHaveOneCharge(5))
                    return true;

                // Filler usage outside burst if we have excess stacks
                if (SCHAetherFlowStacks >= 2 && HealMode != HealModeStrategy.HealBot)
                    return true;
            }

            // Overcap prevention for all modes: spend stacks before Aetherflow comes off CD
            if (AetherflowPvE.Cooldown.IsCoolingDown && AetherflowPvE.Cooldown.WillHaveOneCharge(3))
                return true;
        }

        return base.AttackAbility(nextGCD, out act);
    }

    #endregion

    #region General Ability

    protected override bool GeneralAbility(IAction nextGCD, out IAction? act)
    {
        // Summon fairy if not out
        if (SummonEosPvE.CanUse(out act))
            return true;

        return base.GeneralAbility(nextGCD, out act);
    }

    #endregion

    #region GCD Logic

    protected override bool GeneralGCD(out IAction? act)
    {
        if (BMRPyreticActive) { act = null; return false; }
        // Reserve Swiftcast for Raise
        if ((HasSwift || IsLastAction(ActionID.SwiftcastPvE)) && SwiftLogic && MergedStatus.HasFlag(AutoStatus.Raise))
            return base.GeneralGCD(out act);

        // === DoT Snapshot Under Chain Stratagem ===
        // FFXIV DoTs snapshot all damage buffs at application time.
        // Force-refresh Biolysis in the first GCD after Chain Stratagem to get 30s of buffed ticks.
        if (InChainStratagemWindow && ChainStratagemPvE.Cooldown.JustUsedAfter(GCDTime(1)))
        {
            if (BiolysisPvE.EnoughLevel && BiolysisPvE.CanUse(out act, skipStatusProvideCheck: true))
                return true;
            if (!BiolysisPvE.EnoughLevel && BioIiPvE.EnoughLevel && BioIiPvE.CanUse(out act, skipStatusProvideCheck: true))
                return true;
            if (!BioIiPvE.EnoughLevel && BioPvE.EnoughLevel && BioPvE.CanUse(out act, skipStatusProvideCheck: true))
                return true;
        }

        // === AoE Damage (3+ targets) ===
        if (ArtOfWarIiPvE.EnoughLevel && ArtOfWarIiPvE.CanUse(out act))
            return true;
        if (!ArtOfWarIiPvE.EnoughLevel && ArtOfWarPvE.EnoughLevel && ArtOfWarPvE.CanUse(out act))
            return true;

        // === Biolysis / Bio DoT Maintenance ===
        // Maintain as long as target will live 15+ seconds.
        // Framework handles refresh timing via IsRestrictedDOT config.
        if (BiolysisPvE.EnoughLevel && BiolysisPvE.CanUse(out act))
            return true;
        if (!BiolysisPvE.EnoughLevel && BioIiPvE.EnoughLevel && BioIiPvE.CanUse(out act))
            return true;
        if (!BioIiPvE.EnoughLevel && BioPvE.EnoughLevel && BioPvE.CanUse(out act))
            return true;

        // === Broil IV (main damage filler) ===
        // This should be >90% of GCDs in optimized play.
        if (BroilIvPvE.EnoughLevel && BroilIvPvE.CanUse(out act))
            return true;
        if (!BroilIvPvE.EnoughLevel && BroilIiiPvE.EnoughLevel && BroilIiiPvE.CanUse(out act))
            return true;
        if (!BroilIiiPvE.EnoughLevel && BroilIiPvE.EnoughLevel && BroilIiPvE.CanUse(out act))
            return true;
        if (!BroilIiPvE.EnoughLevel && BroilPvE.EnoughLevel && BroilPvE.CanUse(out act))
            return true;

        // === Ruin II (instant cast for movement/weaving) ===
        // Last resort: 25% potency loss vs Broil IV, but allows free movement.
        if (IsMoving && RuinIiPvE.CanUse(out act))
            return true;

        // === Ruin (lowest level fallback) ===
        if (RuinPvE.CanUse(out act))
            return true;

        return base.GeneralGCD(out act);
    }

    [RotationDesc(ActionID.AdloquiumPvE, ActionID.PhysickPvE, ActionID.ManifestationPvE)]
    protected override bool HealSingleGCD(out IAction? act)
    {
        // Reserve Swiftcast for Raise
        if ((HasSwift || IsLastAction(ActionID.SwiftcastPvE)) && SwiftLogic && MergedStatus.HasFlag(AutoStatus.Raise))
            return base.HealSingleGCD(out act);

        // Seraphism mode: Manifestation is instant cast Adloquium
        if (ManifestationReady && ManifestationPvE.CanUse(out act))
            return true;

        // Adloquium: shield heal (use Emergency Tactics for pure heal version)
        if (AdloquiumPvE.CanUse(out act))
            return true;

        // Physick: basic GCD heal (lowest priority)
        if (PhysickPvE.CanUse(out act))
            return true;

        return base.HealSingleGCD(out act);
    }

    [RotationDesc(ActionID.SuccorPvE, ActionID.ConcitationPvE, ActionID.AccessionPvE)]
    protected override bool HealAreaGCD(out IAction? act)
    {
        // Reserve Swiftcast for Raise
        if ((HasSwift || IsLastAction(ActionID.SwiftcastPvE)) && SwiftLogic && MergedStatus.HasFlag(AutoStatus.Raise))
            return base.HealAreaGCD(out act);

        // Seraphism mode: Accession is instant cast Concitation
        if (AccessionReady && AccessionPvE.CanUse(out act))
            return true;

        // Concitation (upgraded Succor at higher levels)
        if (ConcitationPvE.EnoughLevel && ConcitationPvE.CanUse(out act))
            return true;

        // Succor: AoE shield heal
        if (SuccorPvE.CanUse(out act))
            return true;

        return base.HealAreaGCD(out act);
    }

    [RotationDesc(ActionID.ResurrectionPvE)]
    protected override bool RaiseGCD(out IAction? act)
    {
        if (ResurrectionPvE.CanUse(out act))
            return true;

        return base.RaiseGCD(out act);
    }

    protected override bool DefenseAreaGCD(out IAction? act)
    {
        // Succor/Concitation for pre-shielding
        if (AccessionReady && AccessionPvE.CanUse(out act))
            return true;

        if (ConcitationPvE.EnoughLevel && ConcitationPvE.CanUse(out act))
            return true;

        if (SuccorPvE.CanUse(out act))
            return true;

        return base.DefenseAreaGCD(out act);
    }

    protected override bool DefenseSingleGCD(out IAction? act)
    {
        // Adloquium for pre-shielding (+ Deployment Tactics via framework)
        if (ManifestationReady && ManifestationPvE.CanUse(out act))
            return true;

        if (AdloquiumPvE.CanUse(out act))
            return true;

        return base.DefenseSingleGCD(out act);
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

    public override bool CanHealSingleSpell
    {
        get
        {
            if (!base.CanHealSingleSpell) return false;
            return HealMode switch
            {
                HealModeStrategy.DPSBot => false,           // Never GCD heal
                HealModeStrategy.HealBot => true,            // Always GCD heal
                _ => IsSoloHealer,                           // Only if solo healer
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
                HealModeStrategy.DPSBot => false,            // Never GCD heal
                HealModeStrategy.HealBot => true,            // Always GCD heal
                _ => IsSoloHealer,                           // Only if solo healer
            };
        }
    }

    #endregion

    #region Debug Display

    public override void DisplayRotationStatus()
    {
        ImGui.Text($"--- Burst ---");
        ImGui.Text($"CanBurst: {CanBurst}");
        ImGui.Text($"InBurstWindow: {InBurstWindow}");
        ImGui.Text($"InChainStratagemWindow: {InChainStratagemWindow}");
        ImGui.Text($"ChainStratagemCD: {(ChainStratagemPvE.Cooldown.IsCoolingDown ? $"{ChainStratagemPvE.Cooldown.RecastTimeRemain:F1}s" : "Ready")}");
        ImGui.Text($"ImpactImminent: {HasImpactImminent}");
        ImGui.Text($"Medicated: {StatusHelper.PlayerHasStatus(true, StatusID.Medicated)}");

        ImGui.Text($"--- Aetherflow ---");
        ImGui.Text($"AetherflowStacks: {SCHAetherFlowStacks}");
        ImGui.Text($"HasAetherflow: {HasAetherflow}");
        ImGui.Text($"AetherflowCD: {(AetherflowPvE.Cooldown.IsCoolingDown ? $"{AetherflowPvE.Cooldown.RecastTimeRemain:F1}s" : "Ready")}");
        ImGui.Text($"StacksForDPS: {AetherflowStacksForDPS}");
        ImGui.Text($"Dissipation: {HasDissipation}");
        ImGui.Text($"FairyDismissed: {FairyDismissed}");

        ImGui.Text($"--- Fairy ---");
        ImGui.Text($"FairyGauge: {FairyGauge}");
        ImGui.Text($"HasPet: {DataCenter.HasPet()}");
        ImGui.Text($"SeraphTime: {SeraphTime:F1}s");
        ImGui.Text($"ManifestationReady: {ManifestationReady}");
        ImGui.Text($"AccessionReady: {AccessionReady}");

        ImGui.Text($"--- Status ---");
        ImGui.Text($"HasRecitation: {HasRecitation}");
        ImGui.Text($"HasEmergencyTactics: {HasEmergencyTactics}");

        ImGui.Text($"--- Healing ---");
        ImGui.Text($"HealMode: {HealMode}");
        ImGui.Text($"IsSoloHealer: {IsSoloHealer}");
        ImGui.Text($"CanHealSingleSpell: {CanHealSingleSpell}");
        ImGui.Text($"CanHealAreaSpell: {CanHealAreaSpell}");
        ImGui.Text($"PartyHP: {PartyMembersAverHP:P0}");

        ImGui.Text($"--- BMR Timeline ---");
        ImGui.Text($"Active: {BMRActive}{(BMRActive ? $" ({DataCenter.BMRActiveModuleName})" : "")}");
        ImGui.Text($"UseBmrTimeline: {Service.Config.UseBmrTimeline}");
        if (BMRActive)
        {
            ImGui.Text($"-- Final Merged Values --");
            ImGui.Text($"Raidwide In: {(BMRRaidwideIn < 9999f ? $"{BMRRaidwideIn:F1}s" : "None")}");
            ImGui.Text($"Tankbuster In: {(BMRTankbusterIn < 9999f ? $"{BMRTankbusterIn:F1}s" : "None")}");
            ImGui.Text($"Knockback In: {(BMRKnockbackIn < 9999f ? $"{BMRKnockbackIn:F1}s" : "None")}");
            ImGui.Text($"Downtime In: {(BMRDowntimeIn < 9999f ? $"{BMRDowntimeIn:F1}s" : "None")}");
            ImGui.Text($"Vulnerable In: {(BMRVulnerableIn < 9999f ? $"{BMRVulnerableIn:F1}s" : "None")}");
            ImGui.Text($"Damage In: {(BMRDamageIn < 9999f ? $"{BMRDamageIn:F1}s" : "None")}");
            ImGui.Text($"-- IPC Func Binding --");
            ImGui.Text($"TL.RW: {(DataCenter.BMRDebugTimelineRwFunc ? "BOUND" : "NULL")} | TL.TB: {(DataCenter.BMRDebugTimelineTbFunc ? "BOUND" : "NULL")}");
            ImGui.Text($"Hints.RW: {(DataCenter.BMRDebugHintsRwFunc ? "BOUND" : "NULL")} | Hints.TB: {(DataCenter.BMRDebugHintsTbFunc ? "BOUND" : "NULL")}");
            ImGui.Text($"-- Raw Timeline (StateMachine) --");
            ImGui.Text($"TL Raidwide: {(DataCenter.BMRDebugTimelineRaidwide < 9999f ? $"{DataCenter.BMRDebugTimelineRaidwide:F1}s" : "MAX")}");
            ImGui.Text($"TL Tankbuster: {(DataCenter.BMRDebugTimelineTankbuster < 9999f ? $"{DataCenter.BMRDebugTimelineTankbuster:F1}s" : "MAX")}");
            ImGui.Text($"-- Raw Hints (PredictedDamage) --");
            ImGui.Text($"Hints RW: {(DataCenter.BMRDebugHintsRaidwide < 9999f ? $"{DataCenter.BMRDebugHintsRaidwide:F1}s" : "MAX")}");
            ImGui.Text($"Hints TB: {(DataCenter.BMRDebugHintsTankbuster < 9999f ? $"{DataCenter.BMRDebugHintsTankbuster:F1}s" : "MAX")}");
            ImGui.Text($"Generic Dmg: {(DataCenter.BMRDebugGenericDamageIn < 9999f ? $"{DataCenter.BMRDebugGenericDamageIn:F1}s type={DataCenter.BMRDebugGenericDamageType}" : "MAX")}");
            ImGui.Text($"-- State Machine Walk --");
            ImGui.TextWrapped($"{DataCenter.BMRDebugTimelineWalk ?? "N/A"}");
        }
    }

    #endregion
}
