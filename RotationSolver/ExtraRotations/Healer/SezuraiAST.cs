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
        [Description("Balanced: oGCD first, GCD heals when party HP critical (<40%) or solo healing")]
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
    public float EssentialDignityThird { get; set; } = 0.7f;

    [Range(0, 1, ConfigUnitType.Percent)]
    [RotationConfig(CombatType.PvE, Name = "HP threshold for Essential Dignity 2nd charge")]
    public float EssentialDignitySecond { get; set; } = 0.5f;

    [Range(0, 1, ConfigUnitType.Percent)]
    [RotationConfig(CombatType.PvE, Name = "HP threshold for Essential Dignity last charge (emergency)")]
    public float EssentialDignityLast { get; set; } = 0.3f;

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

    #region Countdown & Opener
    // === AST OPENER (7.4 Balance / Icy Veins) ===
    // Pre-pull: Earthly Star(-4 to -19s) → Fall Malefic precast(-2.1s) → Pot (weave on land)
    // GCD1: Combust III (instant DoT)
    // → Lightspeed (weave, flexible placement)
    // GCD2: Fall Malefic
    // GCD3: Fall Malefic → Divination (weave) + Play The Balance (weave)
    // GCD4: Fall Malefic → Lord of Crowns (weave) + Umbral Draw (weave)
    // GCD5: Fall Malefic → Play The Spear (weave) + Oracle (weave)
    // GCD6-11: Fall Malefic x5-6 (count depends on GCD speed)
    // Last GCD: Combust III (early refresh to snapshot raid buffs)
    //
    // Start with: The Balance, The Arrow, The Spire, Lord of Crowns drawn + Umbral Draw ready
    //
    // === EVEN BURST (120s) ===
    // Divination (6% party buff) + Play Balance + Lord of Crowns + Umbral Draw
    //   + Play Spear + Oracle + Combust refresh to snapshot buffs
    //
    // === ODD BURST (60s) ===
    // Astral Draw off CD — draw Balance + Lord of Crowns but HOLD them for even window
    // No Divination (120s). Continue Fall Malefic spam
    //
    // === FILLER ===
    // Fall Malefic spam. Combust III: maintain ~100% uptime, refresh as last GCD in buffs
    // Hold damage cards from odd draws for Divination windows
    // Oracle: must use while Divination is active (don't let Divining buff expire)
    // Earthly Star on CD (both heal and damage). Lightspeed freely for movement

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

        // Macrocosmos detonation is handled by HealAreaAbility AFTER damage hits.
        // Do NOT detonate here before raidwide — the buff needs to store damage first,
        // then Microcosmos releases the stored healing. Detonating before damage = 0 healing.

        return base.EmergencyAbility(nextGCD, out act);

        static bool CanCastSynastry(IBaseAction actionCheck, IBaseAction synastry, float synastryHp, IAction next)
            => next.IsTheSameTo(false, actionCheck)
               && synastry.Target.Target == actionCheck.Target.Target
               && synastry.Target.Target.GetHealthRatio() < synastryHp;
    }

    #endregion

    #region Defense

    [RotationDesc(ActionID.ExaltationPvE, ActionID.CelestialIntersectionPvE, ActionID.TheArrowPvE, ActionID.TheSpirePvE, ActionID.TheBolePvE, ActionID.TheEwerPvE)]
    protected override bool DefenseSingleAbility(IAction nextGCD, out IAction? act)
    {
        // BMR-aware: if tankbuster is coming, prioritize mit tools for it
        // Per Balance/Icy Veins: Exaltation is THE tankbuster tool (10% mit + 500p delayed heal)
        // Layer with Bole (10% mit card) + Celestial Intersection (400p shield) for big TBs
        bool tbSoon = BmrActive && BmrTankbusterIn is > 0 and <= 6f;

        if (tbSoon)
        {
            // Exaltation: primary TB mit — 10% + delayed heal, use on most impactful hits
            if (ExaltationPvE.CanUse(out act))
                return true;

            // Bole: 10% damage reduction card — stack with Exaltation for heavy TBs
            if (InCombat && TheBolePvE.CanUse(out act))
                return true;

            // Celestial Intersection: 400p shield — layer on top for max effective HP
            if (CelestialIntersectionPvE.CanUse(out act, usedUp: true))
                return true;

            // Spire: 400p barrier — additional shield layer
            if (InCombat && TheSpirePvE.CanUse(out act))
                return true;
        }

        // Without BMR or no imminent TB: use cards to avoid overcapping draws, then Exaltation
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
        // Per Balance/Icy Veins: spread mits across raidwides, don't dump everything on one hit.
        // Mitigation is multiplicative (two 10% = 19%, not 20%), so spreading is more efficient.
        // This method should use at most 1-2 oGCD mits per raidwide.
        //
        // Neutral Sect is NOT here — it's managed in GeneralAbility for Sun Sign timing.
        // Per guides: "delay Sun Sign so Neutral Sect shields cover one mechanic, Sun Sign another"
        //
        // Earthly Star placement is NOT here — it's in AttackAbility (primarily a damage tool).
        // Star DETONATION for healing is in HealAreaAbility and AttackAbility (burst).

        bool rwSoon = BmrActive && BmrRaidwideIn is > 0 and <= 5f;

        // Sun Sign: 10% party mit for 15s — use when available (it's free, Suntouched from Neutral Sect)
        if (SunSignPvE.CanUse(out act))
            return true;

        // Collective Unconscious: tap for 10% mit (30y) + regen (8y), 60s CD.
        // Per Balance: this is MIT — use BEFORE raidwide to reduce incoming damage.
        // BMR: fire 1-5s before raidwide so the mit window covers the damage snapshot.
        // Without BMR: use whenever the framework says to defend.
        if (rwSoon && CollectiveUnconsciousPvE.CanUse(out act))
            return true;

        if (!BmrActive && CollectiveUnconsciousPvE.CanUse(out act))
            return true;

        return base.DefenseAreaAbility(nextGCD, out act);
    }

    #endregion

    #region Healing Abilities

    [RotationDesc(ActionID.EssentialDignityPvE, ActionID.CelestialIntersectionPvE, ActionID.ExaltationPvE, ActionID.TheArrowPvE, ActionID.TheEwerPvE)]
    protected override bool HealSingleAbility(IAction nextGCD, out IAction? act)
    {
        if (MicroPrio && HasMacrocosmos)
            return base.HealSingleAbility(nextGCD, out act);

        // BMR-aware: if a tankbuster is coming SOON, hold single-target heals.
        // Let DefenseSingleAbility handle MIT (Exaltation, Bole, CI shields),
        // then fire heals AFTER the TB hits when the tank actually needs them.
        // Essential Dignity scales inversely with HP — it heals MORE on a low-HP tank post-TB.
        bool tbComingSoon = BmrActive && BmrTankbusterIn is > 1f and <= 8f;
        bool rwComingSoon = BmrActive && BmrRaidwideIn is > 1f and <= 8f;

        if (tbComingSoon || rwComingSoon)
            return base.HealSingleAbility(nextGCD, out act);

        // Essential Dignity: tiered by charge count.
        // Scales inversely with target HP — max 900p at <=30%. Never cap charges.
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

        // Celestial Intersection: 200p heal + 400p shield (2 charges, 30s recharge)
        // Per Balance: "Use one charge regularly to avoid capping, hold one for emergencies"
        // BMR-aware: if TB is imminent, spend both charges for max shield
        bool tbImminent = BmrActive && BmrTankbusterIn is > 0 and <= 4f;
        if (CelestialIntersectionPvE.CanUse(out act, usedUp: tbImminent || CelestialIntersectionPvE.Cooldown.CurrentCharges == 2))
            return true;

        // Exaltation: 10% mitigation (8s) + 500p delayed heal — great for tankbusters
        if (ExaltationPvE.CanUse(out act))
            return true;

        // The Arrow: +10% healing received — apply before co-healer burst heals
        if (InCombat && TheArrowPvE.CanUse(out act))
            return true;

        // The Ewer: 1,000p regen — highest potency single-target oGCD heal
        if (InCombat && TheEwerPvE.CanUse(out act))
            return true;

        return base.HealSingleAbility(nextGCD, out act);
    }

    [RotationDesc(ActionID.CelestialOppositionPvE, ActionID.StellarDetonationPvE, ActionID.HoroscopePvE, ActionID.HoroscopePvE_16558, ActionID.LadyOfCrownsPvE, ActionID.CollectiveUnconsciousPvE)]
    protected override bool HealAreaAbility(IAction nextGCD, out IAction? act)
    {
        // BMR-aware: if a raidwide is coming SOON, don't waste heals — let MIT handle it.
        // Heals should fire AFTER damage, not before. The framework triggers this method
        // when party HP drops, which should be after the raidwide hits.
        bool rwComingSoon = BmrActive && BmrRaidwideIn is > 1f and <= 8f;

        // Earthly Star (charged): 720p heal — highest priority AoE heal
        // This is our best post-raidwide heal. Only detonate when HP is actually low.
        if (HasGiantDominance && StellarDetonationPvE.CanUse(out act))
            return true;

        // Microcosmos detonation: releases stored damage as healing
        // Only fires after damage has been stored (post-raidwide)
        if (MicrocosmosPvE.CanUse(out act))
            return true;

        if (MicroPrio && HasMacrocosmos)
            return base.HealAreaAbility(nextGCD, out act);

        // If raidwide is coming soon, HOLD heals — MIT is already handling it in DefenseAreaAbility.
        // The heals below will fire after the raidwide hits and HP drops.
        if (rwComingSoon)
            return base.HealAreaAbility(nextGCD, out act);

        // Celestial Opposition: 700p total AoE heal, no restrictions
        if (CelestialOppositionPvE.CanUse(out act))
            return true;

        // Collective Unconscious: tap for 10% mitigation + 500p regen (8y range)
        if (CollectiveUnconsciousPvE.CanUse(out act))
            return true;

        // Earthly Star (uncharged): 540p heal — still worth detonating
        if (StellarDetonationPvE.CanUse(out act))
            return true;

        // Horoscope: detonate for free 200p AoE heal (400p if upgraded with Helios)
        // Per Balance: "Use un-upgraded for the free 200p heal. Do NOT cast Helios just to upgrade."
        if (PartyMembersAverHP < HoroscopeHeal && HoroscopePvE_16558.CanUse(out act))
            return true;

        if (PartyMembersAverHP < HoroscopeHeal && HoroscopePvE.CanUse(out act))
            return true;

        // Lady of Crowns: free 400p AoE heal — never waste it
        if (LadyOfCrownsPvE.CanUse(out act))
            return true;

        return base.HealAreaAbility(nextGCD, out act);
    }

    #endregion

    #region General Ability (Cards, Draws, Sun Sign)

    protected override bool GeneralAbility(IAction nextGCD, out IAction? act)
    {
        // === Neutral Sect + Sun Sign management ===
        // Per Balance: "Delay Sun Sign activation so that you can use the Neutral Sect shields
        // to mitigate one thing and Sun Sign to mitigate something else."
        // Suntouched lasts 30s after Neutral Sect — plenty of time to split across mechanics.
        //
        // Strategy: Activate Neutral Sect when convenient (for GCD shield or Sun Sign access).
        // Sun Sign fires here or in DefenseAreaAbility when raidwide is imminent.
        if (StatusHelper.PlayerHasStatus(true, StatusID.Suntouched)
            && SunSignPvE.CanUse(out act, skipAoeCheck: true, skipTTKCheck: true))
        {
            return true;
        }

        // Activate Neutral Sect proactively for Sun Sign access (120s CD)
        // Only when we don't already have Suntouched and we're in combat
        if (InCombat && !StatusHelper.PlayerHasStatus(true, StatusID.Suntouched)
            && NeutralSectPvE.CanUse(out act))
        {
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

            // Star detonation for HEALING is handled by HealAreaAbility AFTER raidwide damage.
            // Do NOT detonate before raidwide — the 720p heal is wasted on full HP.
            // Per Balance: "Place Star before raidwide, detonate AFTER damage for healing value."

            // --- BMR-aware Earthly Star placement ---
            // Per Balance: "Place it 10 seconds before the raidwide so you can detonate after damage"
            // Place star so it matures to Giant Dominance (10s) right before raidwide hits
            if (BmrActive && BmrRaidwideIn is > 10f and <= 20f
                && !HasGiantDominance && !HasEarthlyDominance
                && EarthlyStarPvE.CanUse(out act))
            {
                return true;
            }

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
        // Neutral Sect + Aspected Benefic = massive shield on tank
        // Per Balance: only GCD heal when you have Neutral Sect up (makes it worth the GCD cost)
        // The shield component from Neutral Sect is what makes this worth a GCD.
        if ((NeutralSectPvE.CanUse(out _) || HasNeutralSect || IsLastAbility(false, NeutralSectPvE))
            && AspectedBeneficPvE.CanUse(out act, skipStatusProvideCheck: true))
        {
            return true;
        }

        // Without Neutral Sect: do NOT GCD heal just for a HoT — that's a DPS loss.
        // oGCD tools (Exaltation, Celestial Intersection, Essential Dignity) handle TBs.
        return base.DefenseSingleGCD(out act);
    }

    [RotationDesc(ActionID.MacrocosmosPvE)]
    protected override bool DefenseAreaGCD(out IAction? act)
    {
        // Per Balance: "oGCD abilities are more efficient than GCD spells because you do not
        // have to stop casting damage spells." Only GCD heal/shield when Neutral Sect is up
        // (making the GCD worth the DPS loss) or when Macrocosmos timing is right.

        // Neutral Sect + Helios Conjunction = AoE heal + shield + regen
        // Only worth a GCD because the shield makes it significantly stronger
        if ((NeutralSectPvE.CanUse(out _) || HasNeutralSect || IsLastAbility(false, NeutralSectPvE))
            && HeliosConjunctionPvE.CanUse(out act, skipStatusProvideCheck: true))
        {
            return true;
        }

        // Macrocosmos (180s CD): "Incredibly powerful but effectiveness depends entirely on timing"
        // Per Balance: best for 1-HP mechanics, back-to-back raidwides, DRK Living Dead.
        // BMR-aware: cast 5-8s before raidwide so buff is active when damage hits.
        // Without BMR: fall back to original multi-hit restriction logic.
        if (BmrActive && BmrRaidwideIn is > 2f and <= 8f && MacrocosmosPvE.CanUse(out act))
            return true;

        if (!BmrActive && ((MultiHitRestrict && IsCastingMultiHit) || !MultiHitRestrict))
        {
            if (MacrocosmosPvE.CanUse(out act))
                return true;
        }

        // Do NOT bare-cast Helios Conjunction without Neutral Sect — the regen is not worth
        // losing a Fall Malefic GCD. Use oGCDs (CU, Celestial Opposition, Star) instead.
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

        // BMR-aware: hold GCD heals when TB or RW is coming soon.
        // GCD heals before damage = wasted GCD on full HP target.
        // DefenseSingleAbility/DefenseAreaAbility handle MIT.
        // After damage hits, framework re-triggers and heals fire then.
        // This applies to ALL heal modes (Balanced, DPSFocus, Healbot).
        bool tbComingSoon = BmrActive && BmrTankbusterIn is > 1f and <= 8f;
        bool rwComingSoon = BmrActive && BmrRaidwideIn is > 1f and <= 8f;
        if (tbComingSoon || rwComingSoon)
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

        // BMR-aware: hold AoE GCD heals when raidwide is coming soon.
        // Helios/Aspected Helios before raidwide = GCD wasted on full HP party.
        // MIT handles pre-damage, these GCD heals fire AFTER damage when HP drops.
        // This applies to ALL heal modes (Balanced, DPSFocus, Healbot).
        bool rwComingSoon = BmrActive && BmrRaidwideIn is > 1f and <= 8f;
        if (rwComingSoon)
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
                HealModeStrategy.DPSFocus => false,
                HealModeStrategy.Healbot => true,
                // Balanced: solo healer OR emergency (any party member critically low)
                _ => IsSoloHealer || PartyMembersAverHP < 0.4f,
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
                HealModeStrategy.DPSFocus => false,
                HealModeStrategy.Healbot => true,
                // Balanced: solo healer OR emergency (party HP critically low)
                _ => IsSoloHealer || PartyMembersAverHP < 0.4f,
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
