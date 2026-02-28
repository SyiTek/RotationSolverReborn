using System.ComponentModel;

namespace RotationSolver.ExtraRotations.Healer;

[Rotation("SezuraiAST", CombatType.PvE, GameVersion = "7.41",
    Description = "Balance-aligned AST with proper Divination timing, burst-aligned Oracle/Lord, and configurable healing.")]
[SourceCode(Path = "main/ExtraRotations/Healer/SezuraiAST.cs")]
[ExtraRotation]
public sealed class SezuraiAST : AstrologianRotation
{
    #region Config Options

    [RotationConfig(CombatType.PvE, Name = "Limit Macrocosmos to multihit party stacks")]
    public bool MultiHitRestrict { get; set; } = false;

    [RotationConfig(CombatType.PvE, Name = "Swiftcast restriction: reserve for Raise")]
    public bool SwiftLogic { get; set; } = true;

    [RotationConfig(CombatType.PvE, Name = "Use both Lightspeed charges while moving")]
    public bool LightspeedMove { get; set; } = true;

    [RotationConfig(CombatType.PvE, Name = "Heal Mode (Balanced = normal, DPS = oGCD heals only, Healbot = always GCD heal)")]
    public HealModeStrategy HealMode { get; set; } = HealModeStrategy.Balanced;

    public enum HealModeStrategy : byte
    {
        [Description("Normal: GCD heals only if solo healer")]
        Balanced,

        [Description("DPS Focus: oGCD heals only, never GCD heal")]
        DPSFocus,

        [Description("Healbot: always use GCD heals even with co-healer")]
        Healbot,
    }

    [RotationConfig(CombatType.PvE, Name = "Prioritize Microcosmos over all other healing when available")]
    public bool MicroPrio { get; set; } = false;

    [RotationConfig(CombatType.PvE, Name = "Simple Lord of Crowns (use under Divination only)")]
    public bool SimpleLord { get; set; } = false;

    [RotationConfig(CombatType.PvE, Name = "Detonate Earthly Star when you have Giant Dominance")]
    public bool StellarNow { get; set; } = false;

    [RotationConfig(CombatType.PvE, Name = "Use Earthly Star as an attack while moving")]
    public bool StarMove { get; set; } = true;

    [Range(4, 20, ConfigUnitType.Seconds)]
    [RotationConfig(CombatType.PvE, Name = "Use Earthly Star during countdown timer")]
    public float UseEarthlyStarTime { get; set; } = 4;

    [Range(0, 1, ConfigUnitType.Percent)]
    [RotationConfig(CombatType.PvE, Name = "HP threshold for Aspected Benefic")]
    public float AspectedBeneficHeal { get; set; } = 0.4f;

    [Range(0, 1, ConfigUnitType.Percent)]
    [RotationConfig(CombatType.PvE, Name = "HP threshold for Synastry")]
    public float SynastryHeal { get; set; } = 0.5f;

    [Range(0, 1, ConfigUnitType.Percent)]
    [RotationConfig(CombatType.PvE, Name = "Party HP threshold for Horoscope")]
    public float HoroscopeHeal { get; set; } = 0.5f;

    [Range(0, 1, ConfigUnitType.Percent)]
    [RotationConfig(CombatType.PvE, Name = "Party HP threshold for Lady of Crowns")]
    public float LadyOfHeals { get; set; } = 0.8f;

    [Range(0, 1, ConfigUnitType.Percent)]
    [RotationConfig(CombatType.PvE, Name = "HP threshold for Essential Dignity 3rd charge")]
    public float EssentialDignityThird { get; set; } = 0.8f;

    [Range(0, 1, ConfigUnitType.Percent)]
    [RotationConfig(CombatType.PvE, Name = "HP threshold for Essential Dignity 2nd charge")]
    public float EssentialDignitySecond { get; set; } = 0.7f;

    [Range(0, 1, ConfigUnitType.Percent)]
    [RotationConfig(CombatType.PvE, Name = "HP threshold for Essential Dignity last charge")]
    public float EssentialDignityLast { get; set; } = 0.6f;

    [Range(0f, 0.25f, ConfigUnitType.Percent)]
    [RotationConfig(CombatType.PvE, Name = "Action Ahead Override (0 = use global setting)")]
    public float ActionAheadOverride { get; set; } = 0f;

    [RotationConfig(CombatType.PvE, Name = "Essential Dignity priority over GCD heals")]
    public EssentialPrioStrategy EssentialPrio2 { get; set; } = EssentialPrioStrategy.UseGCDs;

    public enum EssentialPrioStrategy : byte
    {
        [Description("Ignore setting")]
        UseGCDs,

        [Description("When capped")]
        CappedCharges,

        [Description("Any charges")]
        AnyCharges,
    }

    #endregion

    #region Burst State

    /// <summary>
    /// Framework burst enabled. Replaces IsBurst (always true when AutoBurst on).
    /// </summary>
    private bool CanBurst => MergedStatus.HasFlag(AutoStatus.Burst);

    /// <summary>
    /// True when Divination buff is active on the party.
    /// </summary>
    private static bool InBurstStatus => HasDivination;

    /// <summary>
    /// True when approaching or in the Divination window.
    /// </summary>
    private bool InBurstWindow => HasDivination
        || !DivinationPvE.EnoughLevel
        || DivinationPvE.Cooldown.HasOneCharge
        || DivinationPvE.Cooldown.WillHaveOneCharge(5);

    #endregion

    #region UpdateInfo

    protected override void UpdateInfo()
    {
        DataCenter.RotationActionAheadOverride = ActionAheadOverride > 0f ? ActionAheadOverride : null;
    }

    #endregion

    #region Countdown

    protected override IAction? CountDownAction(float remainTime)
    {
        if (remainTime < MaleficPvE.Info.CastTime + CountDownAhead && MaleficPvE.CanUse(out var act))
            return act;

        if (remainTime < 3 && UseBurstMedicine(out act))
            return act;

        if (remainTime < UseEarthlyStarTime && EarthlyStarPvE.CanUse(out act, skipTTKCheck: true))
            return act;

        if (remainTime < 30 && AstralDrawPvE.CanUse(out act))
            return act;

        return base.CountDownAction(remainTime);
    }

    #endregion

    #region Emergency Ability

    protected override bool EmergencyAbility(IAction nextGCD, out IAction? act)
    {
        if (MicroPrio && HasMacrocosmos)
            return base.EmergencyAbility(nextGCD, out act);

        if (!InCombat)
            return base.EmergencyAbility(nextGCD, out act);

        // Oracle: use when Divining status is available
        // Fires under Divination by design (Divination grants Divining)
        if (OraclePvE.CanUse(out act))
            return true;

        // Neutral Sect before AoE heal GCD
        if (nextGCD.IsTheSameTo(false, HeliosConjunctionPvE, AspectedHeliosPvE))
        {
            if (NeutralSectPvE.CanUse(out act))
                return true;
        }

        // Horoscope before AoE heal GCD
        if (nextGCD.IsTheSameTo(false, HeliosConjunctionPvE, HeliosPvE))
        {
            if (PartyMembersAverHP < HoroscopeHeal && HoroscopePvE.CanUse(out act))
                return true;
        }

        // Synastry on single target heal
        if (SynastryPvE.CanUse(out act))
        {
            if (CanCastSynastry(AspectedBeneficPvE, SynastryPvE, SynastryHeal, nextGCD)
                || CanCastSynastry(BeneficIiPvE, SynastryPvE, SynastryHeal, nextGCD)
                || CanCastSynastry(BeneficPvE, SynastryPvE, SynastryHeal, nextGCD))
            {
                return true;
            }
        }

        // Medicine before Divination
        if (DivinationPvE.CanUse(out _) && UseBurstMedicine(out act))
            return true;

        // Detonate Giant Dominance Earthly Star on config
        if (StellarNow && HasGiantDominance && StellarDetonationPvE.CanUse(out act))
            return true;

        return base.EmergencyAbility(nextGCD, out act);

        static bool CanCastSynastry(IBaseAction actionCheck, IBaseAction synastry, float synastryHp, IAction next)
            => next.IsTheSameTo(false, actionCheck)
               && synastry.Target.Target == actionCheck.Target.Target
               && synastry.Target.Target.GetHealthRatio() < synastryHp;
    }

    #endregion

    #region Defense

    [RotationDesc(ActionID.ExaltationPvE, ActionID.TheArrowPvE, ActionID.TheSpirePvE, ActionID.TheBolePvE, ActionID.TheEwerPvE)]
    protected override bool DefenseSingleAbility(IAction nextGCD, out IAction? act)
    {
        if (InCombat && TheSpirePvE.CanUse(out act))
            return true;

        if (InCombat && TheBolePvE.CanUse(out act))
            return true;

        if (ExaltationPvE.CanUse(out act))
            return true;

        return base.DefenseSingleAbility(nextGCD, out act);
    }

    [RotationDesc(ActionID.CollectiveUnconsciousPvE, ActionID.SunSignPvE)]
    protected override bool DefenseAreaAbility(IAction nextGCD, out IAction? act)
    {
        if (SunSignPvE.CanUse(out act))
            return true;

        if (EarthlyStarPvE.CanUse(out act))
            return true;

        if ((MacrocosmosPvE.Cooldown.IsCoolingDown && !MacrocosmosPvE.Cooldown.WillHaveOneCharge(150))
            || (CollectiveUnconsciousPvE.Cooldown.IsCoolingDown && !CollectiveUnconsciousPvE.Cooldown.WillHaveOneCharge(40)))
        {
            return base.DefenseAreaAbility(nextGCD, out act);
        }

        if (CollectiveUnconsciousPvE.CanUse(out act))
            return true;

        return base.DefenseAreaAbility(nextGCD, out act);
    }

    #endregion

    #region Healing Abilities

    [RotationDesc(ActionID.TheArrowPvE, ActionID.TheEwerPvE, ActionID.EssentialDignityPvE, ActionID.CelestialIntersectionPvE)]
    protected override bool HealSingleAbility(IAction nextGCD, out IAction? act)
    {
        if (MicroPrio && HasMacrocosmos)
            return base.HealSingleAbility(nextGCD, out act);

        if (InCombat && TheArrowPvE.CanUse(out act))
            return true;

        if (InCombat && TheEwerPvE.CanUse(out act))
            return true;

        // Essential Dignity: tiered by charge count
        if (EssentialDignityPvE.Cooldown.CurrentCharges == 3
            && EssentialDignityPvE.CanUse(out act, usedUp: true)
            && EssentialDignityPvE.Target.Target.GetHealthRatio() < EssentialDignityThird)
            return true;

        if (EssentialDignityPvE.Cooldown.CurrentCharges == 2
            && EssentialDignityPvE.CanUse(out act, usedUp: true)
            && EssentialDignityPvE.Target.Target.GetHealthRatio() < EssentialDignitySecond)
            return true;

        if (EssentialDignityPvE.Cooldown.CurrentCharges == 1
            && EssentialDignityPvE.CanUse(out act, usedUp: true)
            && EssentialDignityPvE.Target.Target.GetHealthRatio() < EssentialDignityLast)
            return true;

        if (CelestialIntersectionPvE.CanUse(out act, usedUp: true))
            return true;

        return base.HealSingleAbility(nextGCD, out act);
    }

    [RotationDesc(ActionID.CelestialOppositionPvE, ActionID.StellarDetonationPvE, ActionID.HoroscopePvE, ActionID.HoroscopePvE_16558, ActionID.LadyOfCrownsPvE)]
    protected override bool HealAreaAbility(IAction nextGCD, out IAction? act)
    {
        if (HasGiantDominance && StellarDetonationPvE.CanUse(out act))
            return true;

        if (MicrocosmosPvE.CanUse(out act))
            return true;

        if (MicroPrio && HasMacrocosmos)
            return base.HealAreaAbility(nextGCD, out act);

        if (CelestialOppositionPvE.CanUse(out act))
            return true;

        if (StellarDetonationPvE.CanUse(out act))
            return true;

        if (PartyMembersAverHP < HoroscopeHeal && HoroscopePvE_16558.CanUse(out act))
            return true;

        if (PartyMembersAverHP < HoroscopeHeal && HoroscopePvE.CanUse(out act))
            return true;

        if (LadyOfCrownsPvE.CanUse(out act))
            return true;

        return base.HealAreaAbility(nextGCD, out act);
    }

    #endregion

    #region General Ability (Cards, Draws, Sun Sign)

    protected override bool GeneralAbility(IAction nextGCD, out IAction? act)
    {
        // Sun Sign: use before Suntouched expires
        if (StatusHelper.PlayerHasStatus(true, StatusID.Suntouched)
            && StatusHelper.PlayerWillStatusEndGCD(3, 0, true, StatusID.Suntouched))
        {
            if (SunSignPvE.CanUse(out act, skipAoeCheck: true, skipTTKCheck: true))
                return true;
        }

        // Lady of Crowns: heal if party HP low or draw about to overcap
        if (PartyMembersAverHP < LadyOfHeals && LadyOfCrownsPvE.CanUse(out act))
            return true;

        if (AstralDrawPvE.Cooldown.WillHaveOneCharge(3) && LadyOfCrownsPvE.CanUse(out act))
            return true;

        // Dump defensive cards before draws overcap
        if (AstralDrawPvE.Cooldown.WillHaveOneCharge(3) && InCombat && TheEwerPvE.CanUse(out act))
            return true;

        if (AstralDrawPvE.Cooldown.WillHaveOneCharge(3) && InCombat && TheBolePvE.CanUse(out act))
            return true;

        if (UmbralDrawPvE.Cooldown.WillHaveOneCharge(3) && InCombat && TheArrowPvE.CanUse(out act))
            return true;

        if (UmbralDrawPvE.Cooldown.WillHaveOneCharge(3) && InCombat && TheSpirePvE.CanUse(out act))
            return true;

        // Draw new cards
        if (AstralDrawPvE.CanUse(out act))
            return true;

        if (UmbralDrawPvE.CanUse(out act))
            return true;

        // DPS cards: Balance/Spear - hold for Divination window if within ~66s
        // Use immediately if: Divination active, too far from next Div, or Div not learned
        if ((HasDivination || !DivinationPvE.Cooldown.WillHaveOneCharge(66) || !DivinationPvE.EnoughLevel)
            && InCombat && TheBalancePvE.CanUse(out act))
        {
            return true;
        }

        if ((HasDivination || !DivinationPvE.Cooldown.WillHaveOneCharge(66) || !DivinationPvE.EnoughLevel)
            && InCombat && TheSpearPvE.CanUse(out act))
        {
            return true;
        }

        return base.GeneralAbility(nextGCD, out act);
    }

    #endregion

    #region Attack Ability

    protected override bool AttackAbility(IAction nextGCD, out IAction? act)
    {
        // Simple Lord: only under Divination
        if (SimpleLord && InCombat && HasDivination && LordOfCrownsPvE.CanUse(out act))
            return true;

        // --- Divination (120s party buff) ---
        // Fixed: uses CanBurst instead of IsBurst (which was always true)
        if (CanBurst && !IsMoving && InCombat && DivinationPvE.CanUse(out act))
            return true;

        // --- Astral Draw ---
        // Spend charges more aggressively during burst
        if (AstralDrawPvE.CanUse(out act, usedUp: CanBurst))
            return true;

        // --- Lightspeed ---
        // Pre-burst: activate before Divination for instant casts during buff window
        // Movement: use charges for uptime
        if (!HasLightspeed && InCombat
            && (InBurstStatus
                || DivinationPvE.Cooldown.WillHaveOneCharge(5)
                || HasDivination)
            && LightspeedPvE.CanUse(out act, usedUp: true))
        {
            return true;
        }

        if (!HasLightspeed && IsMoving && InCombat && LightspeedPvE.CanUse(out act, usedUp: LightspeedMove))
            return true;

        if (InCombat)
        {
            // --- Detonate Giant Dominance Star during burst for damage ---
            // Ensures detonation fires during Divination even if no healing trigger
            if (HasDivination && HasGiantDominance && StellarDetonationPvE.CanUse(out act))
                return true;

            // --- Burst-aligned Earthly Star placement ---
            // Place Star 10-20s before Divination so it matures to Giant Dominance during burst
            if (!HasGiantDominance && !HasEarthlyDominance
                && DivinationPvE.EnoughLevel
                && DivinationPvE.Cooldown.IsCoolingDown
                && !DivinationPvE.Cooldown.HasOneCharge
                && DivinationPvE.Cooldown.RecastTimeRemain <= 20
                && DivinationPvE.Cooldown.RecastTimeRemain > 5
                && EarthlyStarPvE.CanUse(out act))
            {
                return true;
            }

            // --- Normal Earthly Star placement (filler usage when burst is far away) ---
            if (((!StarMove && !IsMoving) || StarMove)
                && !HasGiantDominance && !HasEarthlyDominance
                && EarthlyStarPvE.CanUse(out act))
            {
                return true;
            }

            // --- Lord of Crowns ---
            // Hold for Divination window if it's coming relatively soon
            if (!SimpleLord
                && (HasDivination
                    || !DivinationPvE.Cooldown.WillHaveOneCharge(45)
                    || !DivinationPvE.EnoughLevel
                    || UmbralDrawPvE.Cooldown.WillHaveOneCharge(3))
                && LordOfCrownsPvE.CanUse(out act))
            {
                return true;
            }
        }

        return base.AttackAbility(nextGCD, out act);
    }

    #endregion

    #region GCD Logic

    protected override bool DefenseSingleGCD(out IAction? act)
    {
        if ((MacrocosmosPvE.Cooldown.IsCoolingDown && !MacrocosmosPvE.Cooldown.WillHaveOneCharge(150))
            || (CollectiveUnconsciousPvE.Cooldown.IsCoolingDown && !CollectiveUnconsciousPvE.Cooldown.WillHaveOneCharge(40)))
        {
            return base.DefenseAreaGCD(out act);
        }

        if ((NeutralSectPvE.CanUse(out _) || HasNeutralSect || IsLastAbility(false, NeutralSectPvE))
            && AspectedBeneficPvE.CanUse(out act, skipStatusProvideCheck: true))
        {
            return true;
        }

        return base.DefenseAreaGCD(out act);
    }

    [RotationDesc(ActionID.MacrocosmosPvE)]
    protected override bool DefenseAreaGCD(out IAction? act)
    {
        if ((MacrocosmosPvE.Cooldown.IsCoolingDown && !MacrocosmosPvE.Cooldown.WillHaveOneCharge(150))
            || (CollectiveUnconsciousPvE.Cooldown.IsCoolingDown && !CollectiveUnconsciousPvE.Cooldown.WillHaveOneCharge(40)))
        {
            return base.DefenseAreaGCD(out act);
        }

        if ((NeutralSectPvE.CanUse(out _) || HasNeutralSect || IsLastAbility(false, NeutralSectPvE))
            && HeliosConjunctionPvE.CanUse(out act, skipStatusProvideCheck: true))
        {
            return true;
        }

        if ((MultiHitRestrict && IsCastingMultiHit) || !MultiHitRestrict)
        {
            if (MacrocosmosPvE.CanUse(out act))
                return true;
        }

        return base.DefenseAreaGCD(out act);
    }

    [RotationDesc(ActionID.AspectedBeneficPvE, ActionID.BeneficIiPvE, ActionID.BeneficPvE)]
    protected override bool HealSingleGCD(out IAction? act)
    {
        // Reserve Swiftcast for Raise
        if ((HasSwift || IsLastAction(ActionID.SwiftcastPvE)) && SwiftLogic && MergedStatus.HasFlag(AutoStatus.Raise))
            return base.HealSingleGCD(out act);

        if (MicroPrio && HasMacrocosmos)
            return base.HealSingleGCD(out act);

        // Defer to Essential Dignity oGCD if configured
        var shouldUseEssentialDignity =
            (EssentialPrio2 == EssentialPrioStrategy.AnyCharges && EssentialDignityPvE.EnoughLevel
             && EssentialDignityPvE.Cooldown.CurrentCharges > 0)
            || (EssentialPrio2 == EssentialPrioStrategy.CappedCharges && EssentialDignityPvE.EnoughLevel
                && EssentialDignityPvE.Cooldown.CurrentCharges == EssentialDignityPvE.Cooldown.MaxCharges);

        if (shouldUseEssentialDignity)
            return base.HealSingleGCD(out act);

        if (AspectedBeneficPvE.CanUse(out act)
            && (IsMoving || AspectedBeneficPvE.Target.Target?.GetHealthRatio() < AspectedBeneficHeal))
            return true;

        if (BeneficIiPvE.CanUse(out act))
            return true;

        if (BeneficPvE.CanUse(out act))
            return true;

        return base.HealSingleGCD(out act);
    }

    [RotationDesc(ActionID.AspectedHeliosPvE, ActionID.HeliosPvE, ActionID.HeliosConjunctionPvE)]
    protected override bool HealAreaGCD(out IAction? act)
    {
        if ((HasSwift || IsLastAction(ActionID.SwiftcastPvE)) && SwiftLogic && MergedStatus.HasFlag(AutoStatus.Raise))
            return base.HealAreaGCD(out act);

        if (MicroPrio && HasMacrocosmos)
            return base.HealAreaGCD(out act);

        if (HeliosConjunctionPvE.EnoughLevel && HeliosConjunctionPvE.CanUse(out act))
            return true;

        if (!HeliosConjunctionPvE.EnoughLevel && AspectedHeliosPvE.CanUse(out act))
            return true;

        if (HeliosPvE.CanUse(out act))
            return true;

        return base.HealAreaGCD(out act);
    }

    [RotationDesc(ActionID.AscendPvE)]
    protected override bool RaiseGCD(out IAction? act)
    {
        if (AscendPvE.CanUse(out act))
            return true;

        return base.RaiseGCD(out act);
    }

    protected override bool GeneralGCD(out IAction? act)
    {
        // Reserve Swiftcast for Raise
        if ((HasSwift || IsLastAction(ActionID.SwiftcastPvE)) && SwiftLogic && MergedStatus.HasFlag(AutoStatus.Raise))
            return base.GeneralGCD(out act);

        // === Combust DoT Snapshot ===
        // FFXIV DoTs snapshot all damage buffs at application time.
        // Force-refresh Combust in the first GCD window after Divination to get 30s of buffed ticks.
        if (HasDivination && DivinationPvE.Cooldown.IsCoolingDown
            && DivinationPvE.Cooldown.JustUsedAfter(GCDTime(1)))
        {
            if (CombustIiiPvE.EnoughLevel && CombustIiiPvE.CanUse(out act, skipStatusProvideCheck: true))
                return true;
            if (!CombustIiiPvE.EnoughLevel && CombustIiPvE.EnoughLevel && CombustIiPvE.CanUse(out act, skipStatusProvideCheck: true))
                return true;
            if (!CombustIiPvE.EnoughLevel && CombustPvE.EnoughLevel && CombustPvE.CanUse(out act, skipStatusProvideCheck: true))
                return true;
        }

        // AoE damage
        if (GravityIiPvE.EnoughLevel && GravityIiPvE.CanUse(out act))
            return true;
        if (!GravityIiPvE.EnoughLevel && GravityPvE.EnoughLevel && GravityPvE.CanUse(out act))
            return true;

        // Combust DoT (normal refresh — framework handles timing)
        if (CombustIiiPvE.EnoughLevel && CombustIiiPvE.CanUse(out act))
            return true;
        if (!CombustIiiPvE.EnoughLevel && CombustIiPvE.EnoughLevel && CombustIiPvE.CanUse(out act))
            return true;
        if (!CombustIiPvE.EnoughLevel && CombustPvE.EnoughLevel && CombustPvE.CanUse(out act))
            return true;

        // ST damage filler
        if (FallMaleficPvE.EnoughLevel && FallMaleficPvE.CanUse(out act))
            return true;
        if (!FallMaleficPvE.EnoughLevel && MaleficIvPvE.EnoughLevel && MaleficIvPvE.CanUse(out act))
            return true;
        if (!MaleficIvPvE.EnoughLevel && MaleficIiiPvE.EnoughLevel && MaleficIiiPvE.CanUse(out act))
            return true;
        if (!MaleficIiiPvE.EnoughLevel && MaleficIiPvE.EnoughLevel && MaleficIiPvE.CanUse(out act))
            return true;
        if (!MaleficIiPvE.EnoughLevel && MaleficPvE.CanUse(out act))
            return true;

        return base.GeneralGCD(out act);
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
                HealModeStrategy.DPSFocus => false,              // Never GCD heal
                HealModeStrategy.Healbot => true,                 // Always GCD heal
                _ => IsSoloHealer,                                // Only if solo healer
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
                HealModeStrategy.DPSFocus => false,              // Never GCD heal
                HealModeStrategy.Healbot => true,                 // Always GCD heal
                _ => IsSoloHealer,                                // Only if solo healer
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
        ImGui.Text($"HasDivination: {HasDivination}");
        ImGui.Text($"DivinationCD: {(DivinationPvE.Cooldown.IsCoolingDown ? $"{DivinationPvE.Cooldown.RecastTimeRemain:F1}s" : "Ready")}");
        ImGui.Text($"Medicated: {StatusHelper.PlayerHasStatus(true, StatusID.Medicated)}");
        ImGui.Text($"--- Cards ---");
        ImGui.Text($"DPS: Balance={HasBalance} Spear={HasSpear}");
        ImGui.Text($"Heal: Arrow={HasArrow} Ewer={HasEwer}");
        ImGui.Text($"Def: Bole={HasBole} Spire={HasSpire}");
        ImGui.Text($"Crown: Lord={HasLord} Lady={HasLady}");
        ImGui.Text($"AstralDraw: {(AstralDrawPvE.Cooldown.IsCoolingDown ? $"{AstralDrawPvE.Cooldown.RecastTimeRemain:F1}s" : "Ready")}");
        ImGui.Text($"UmbralDraw: {(UmbralDrawPvE.Cooldown.IsCoolingDown ? $"{UmbralDrawPvE.Cooldown.RecastTimeRemain:F1}s" : "Ready")}");
        ImGui.Text($"--- Star/Buffs ---");
        ImGui.Text($"GiantDominance: {HasGiantDominance} | EarthlyDominance: {HasEarthlyDominance}");
        ImGui.Text($"Suntouched: {StatusHelper.PlayerStatusTime(true, StatusID.Suntouched):F1}s");
        ImGui.Text($"Lightspeed: {HasLightspeed}");
        ImGui.Text($"--- Healing ---");
        ImGui.Text($"HealMode: {HealMode}");
        ImGui.Text($"IsSoloHealer: {IsSoloHealer}");
        ImGui.Text($"CanHealSingleSpell: {CanHealSingleSpell}");
        ImGui.Text($"CanHealAreaSpell: {CanHealAreaSpell}");
        ImGui.Text($"PartyHP: {PartyMembersAverHP:P0}");
    }

    #endregion
}
