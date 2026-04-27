using System.ComponentModel;

namespace RotationSolver.ExtraRotations.Healer;

[Rotation("SezuraiSGE", CombatType.PvE, GameVersion = "7.41",
    Description = "Balance-aligned SGE with Phlegma burst, Addersgall management, BMR timeline integration, and 3 healing modes.")]
[SourceCode(Path = "main/ExtraRotations/Healer/SezuraiSGE.cs")]
[ExtraRotation]
public sealed class SezuraiSGE : SageRotation
{
    #region Config Options

    [RotationConfig(CombatType.PvE, Name = "Heal Mode (Balanced = normal, DPS Bot = oGCD only, Heal Bot = GCD heals freely)")]
    public HealModeStrategy HealMode { get; set; } = HealModeStrategy.Balanced;

    public enum HealModeStrategy : byte
    {
        [Description("DPS Bot: Kardia + oGCD heals only, maximize Dosis casts")]
        DPSBot,

        [Description("Balanced: Addersgall + oGCD heals, GCD shields when needed")]
        Balanced,

        [Description("Heal Bot: liberal Eukrasian heals, safety first")]
        HealBot,
    }

    [RotationConfig(CombatType.PvE, Name = "Experimental Pot Usage (during burst windows)")]
    public bool BurstMed { get; set; } = true;

    [Range(0, 2, ConfigUnitType.None)]
    [RotationConfig(CombatType.PvE, Name = "Addersgall charges to reserve for healing (0 = spend freely)")]
    public int AddersgallReserve { get; set; } = 0;

    [RotationConfig(CombatType.PvE, Name = "Swiftcast restriction: reserve for Raise")]
    public bool SwiftLogic { get; set; } = true;

    [RotationConfig(CombatType.PvE, Name = "Pool Phlegma charges for raid buff windows")]
    public bool PhlegmaHoldForBurst { get; set; } = true;

    [Range(0, 1, ConfigUnitType.Percent)]
    [RotationConfig(CombatType.PvE, Name = "HP threshold for Taurochole")]
    public float TaurocholHP { get; set; } = 0.65f;

    [Range(0, 1, ConfigUnitType.Percent)]
    [RotationConfig(CombatType.PvE, Name = "HP threshold for Druochole")]
    public float DruocholHP { get; set; } = 0.55f;

    [Range(0, 1, ConfigUnitType.Percent)]
    [RotationConfig(CombatType.PvE, Name = "Party HP threshold for Ixochole")]
    public float IxocholHP { get; set; } = 0.65f;

    [Range(0, 1, ConfigUnitType.Percent)]
    [RotationConfig(CombatType.PvE, Name = "Party HP threshold for Kerachole (mit + regen)")]
    public float KeracholHP { get; set; } = 0.80f;

    [Range(0, 1, ConfigUnitType.Percent)]
    [RotationConfig(CombatType.PvE, Name = "Party HP threshold for Pneuma emergency heal")]
    public float PneumaHealHP { get; set; } = 0.55f;

    [Range(0f, 0.25f, ConfigUnitType.Percent)]
    [RotationConfig(CombatType.PvE, Name = "Action Ahead Override (0 = use global setting)")]
    public float ActionAheadOverride { get; set; } = 0f;

    [RotationConfig(CombatType.PvE, Name = "[BMR] Hold Phlegma charges for pre-downtime dump")]
    public bool PhlegmaDumpBeforeDowntime { get; set; } = true;

    [RotationConfig(CombatType.PvE, Name = "[BMR] Hold Philosophia for vuln windows")]
    public bool PhilosophiaForVuln { get; set; } = true;

    [RotationConfig(CombatType.PvE, Name = "[BMR] Use Panhaima proactively for multi-hit raidwides (10-15s lead)")]
    public bool PanhaimaMultiHit { get; set; } = true;

    #endregion

    #region Burst State

    /// <summary>
    /// Whether the user has burst enabled in the framework.
    /// </summary>
    private bool CanBurst => MergedStatus.HasFlag(AutoStatus.Burst);

    /// <summary>
    /// True when raid buffs are active. Uses framework's party-composition-aware
    /// HasBuffs system instead of manual status checks -- automatically adapts to
    /// whatever jobs are in the party.
    /// </summary>
    private static bool InRaidBuffs => HasBuffs;

    /// <summary>
    /// Whether Addersgall can be spent freely (above the reserve threshold).
    /// </summary>
    private bool CanSpendAddersgall => Addersgall > AddersgallReserve;

    #endregion

    #region Eukrasia Helpers

    /// <summary>
    /// Tracks which Eukrasian action we intend to cast after pressing Eukrasia.
    /// Needed because Eukrasia toggles a buff, and the actual spell is cast on
    /// the NEXT GCD. Without tracking, the rotation loses context between the
    /// Eukrasia press and the follow-up spell.
    /// </summary>
    private IBaseAction? _eukrasiaAim;

    /// <summary>
    /// Attempts to cast a two-step Eukrasian spell. First press: casts Eukrasia.
    /// Second press: casts the actual Eukrasian spell.
    /// </summary>
    private bool TryEukrasianAction(IBaseAction eukrasianSpell, out IAction? act, bool skipStatusProvideCheck = false)
    {
        act = null;

        // If we already have Eukrasia active and this is what we aimed for, cast the spell
        if (HasEukrasia && (_eukrasiaAim == null || _eukrasiaAim == eukrasianSpell))
        {
            if (eukrasianSpell.CanUse(out act, skipStatusProvideCheck: skipStatusProvideCheck))
            {
                _eukrasiaAim = null;
                return true;
            }
            // Eukrasia active but spell can't be used - clear aim to avoid getting stuck
            _eukrasiaAim = null;
            return false;
        }

        // If Eukrasia is not active and no other aim is set, activate Eukrasia
        if (!HasEukrasia && _eukrasiaAim == null)
        {
            // Verify the spell would actually be usable before committing to Eukrasia
            if (eukrasianSpell.CanUse(out _, skipStatusProvideCheck: skipStatusProvideCheck))
            {
                if (EukrasiaPvE.CanUse(out act))
                {
                    _eukrasiaAim = eukrasianSpell;
                    return true;
                }
            }
        }

        return false;
    }

    #endregion

    #region UpdateInfo

    protected override void UpdateInfo()
    {
        DataCenter.RotationActionAheadOverride = ActionAheadOverride > 0f ? ActionAheadOverride : null;

        // Clear stale Eukrasia aim if Eukrasia has expired (e.g. timed out)
        if (_eukrasiaAim != null && !HasEukrasia && !IsLastAction(ActionID.EukrasiaPvE))
        {
            _eukrasiaAim = null;
        }
    }

    #endregion

    #region Countdown & Opener
    // === SGE OPENER (7.4 Balance / Icy Veins) ===
    // Pre-pull: Eukrasia(-1.5s)
    // GCD1: Eukrasian Dosis III (instant DoT) -> Pot (weave)
    // GCD2: Dosis III
    // GCD3: Dosis III (waiting for raid buffs)
    // GCD4: Dosis III
    // GCD5: Phlegma III (instant) -> Psyche (weave, 600p oGCD)
    // GCD6: Phlegma III (instant, 2nd charge)
    // GCD7-9: Dosis III x3
    // GCD10: Eukrasian Dosis III (refresh, snapshots raid buffs)
    //
    // === EVEN BURST (120s) ===
    // 2x Phlegma III + Psyche + E.Dosis refresh under party raid buffs
    // Dump both Phlegma charges inside the window
    //
    // === ODD BURST (60s) ===
    // Psyche on CD (60s). 1 Phlegma charge may be available
    // Do not hold Psyche or it may drift out of even windows
    //
    // === FILLER ===
    // Dosis III spam. E.Dosis: maintain 100% uptime, refresh as last GCD in buffs
    // Phlegma: 2 charges, 45s each. Use 1 between bursts to avoid overcap
    // Toxikon: damage-neutral with Dosis, use for movement (don't charge manually)
    // Kardia on tank for free passive healing. Lucid at ~8000 MP

    protected override IAction? CountDownAction(float remainTime)
    {
        // Pre-pull Eukrasia so first GCD is instant Eukrasian Dosis
        if (remainTime < 1.5f + CountDownAhead && !HasEukrasia && EukrasiaPvE.CanUse(out var act))
            return act;

        // Medicine ~3s before pull
        if (remainTime < 3 && BurstMed && UseBurstMedicine(out act))
            return act;

        return base.CountDownAction(remainTime);
    }

    #endregion

    #region Emergency Ability

    protected override bool EmergencyAbility(IAction nextGCD, out IAction? act)
    {
        if (!InCombat)
            return base.EmergencyAbility(nextGCD, out act);

        // Medicine during burst (align with Phlegma + Psyche window)
        if (BurstMed && CanBurst && InRaidBuffs && UseBurstMedicine(out act))
            return true;

        // Zoe before Pneuma for boosted heal (Pneuma + Zoe = massive AoE heal)
        if (nextGCD.IsTheSameTo(false, PneumaPvE))
        {
            if (ZoePvE.CanUse(out act))
                return true;
        }

        // Krasis before big single-target healing GCDs
        if (nextGCD.IsTheSameTo(false, EukrasianDiagnosisPvE, DiagnosisPvE))
        {
            if (KrasisPvE.CanUse(out act))
                return true;
        }

        return base.EmergencyAbility(nextGCD, out act);
    }

    #endregion

    #region Defense

    [RotationDesc(ActionID.KeracholePvE, ActionID.HolosPvE, ActionID.PanhaimaPvE, ActionID.PhysisIiPvE)]
    protected override bool DefenseAreaAbility(IAction nextGCD, out IAction? act)
    {
        // === BMR-AWARE PARTY MITIGATION ===
        // Per Balance: "Kerachole is your bread-and-butter party mit - 10% + regen"
        // Strategy: Spread mits across raidwides. Max 1-2 per raidwide.
        // Kerachole + Holos for big hits. Panhaima for multi-hit mechanics (10-15s lead).
        // Physis II for regen + 10% healing buff (amplifies post-RW recovery).
        //
        // Mitigation is multiplicative (two 10% = 19%, not 20%), so spreading is more
        // efficient total damage reduction over the fight.

        bool rwSoon = BMRActive && BMRRaidwideIn is > 0 and <= 5f;
        bool rwMedium = BMRActive && BMRRaidwideIn is > 5f and <= 15f;

        if (rwSoon)
        {
            // === Imminent raidwide (0-5s) ===
            // Kerachole first: 10% mit + regen, best value (costs 1 Addersgall)
            if (CanSpendAddersgall && KeracholePvE.CanUse(out act))
                return true;

            // Holos: AoE shield + 10% mit, no Addersgall cost
            if (HolosPvE.CanUse(out act))
                return true;

            // Physis II: AoE regen + 10% healing received buff — preps post-RW recovery
            if (PhysisIiPvE.CanUse(out act))
                return true;

            if (!PhysisIiPvE.EnoughLevel && PhysisPvE.CanUse(out act))
                return true;

            // Max 1-2 mits per raidwide — don't stack Panhaima here unless multi-hit
            return base.DefenseAreaAbility(nextGCD, out act);
        }

        if (rwMedium)
        {
            // === Raidwide in 5-15s ===
            // Panhaima: multi-layer shields, best placed 10-15s before multi-hit mechanics
            // Each shield pops on a separate hit, giving 600-1000+ total potency
            if (PanhaimaMultiHit && PanhaimaPvE.CanUse(out act))
                return true;

            // If Kerachole is available and RW is ~5-8s out, still good to pre-apply
            // (15s duration means it will cover the hit)
            if (BMRRaidwideIn <= 8f && CanSpendAddersgall && KeracholePvE.CanUse(out act))
                return true;

            return base.DefenseAreaAbility(nextGCD, out act);
        }

        // === Non-BMR fallback: use whenever framework triggers defense ===
        if (CanSpendAddersgall && KeracholePvE.CanUse(out act))
            return true;

        if (HolosPvE.CanUse(out act))
            return true;

        if (PanhaimaPvE.CanUse(out act))
            return true;

        return base.DefenseAreaAbility(nextGCD, out act);
    }

    [RotationDesc(ActionID.TaurocholePvE, ActionID.HaimaPvE, ActionID.KrasisPvE)]
    protected override bool DefenseSingleAbility(IAction nextGCD, out IAction? act)
    {
        // === BMR-AWARE TANK MITIGATION ===
        // Per Balance: Taurochole = strongest single-target tool (700p heal + 10% mit)
        // Haima = multi-layer shield for heavy TBs (1800p total with auto-attacks)
        // Krasis = 20% healing received buff, snapshot before Taurochole for extra value
        //
        // Strategy: Spread across TBs. Max 2 tools per TB.

        bool tbSoon = BMRActive && BMRTankbusterIn is > 0 and <= 5f;
        bool tbMedium = BMRActive && BMRTankbusterIn is > 5f and <= 10f;

        if (tbSoon)
        {
            // === Imminent TB (0-5s) ===
            // Krasis first: 20% healing buff on tank amplifies Taurochole + Kardia
            if (KrasisPvE.CanUse(out act))
                return true;

            // Taurochole: heal + 10% mit (best single-target Addersgall tool)
            if (CanSpendAddersgall && TaurocholePvE.CanUse(out act))
                return true;

            // Haima: multi-layer shield for heavy TBs
            if (HaimaPvE.CanUse(out act))
                return true;

            // Max 2 per TB — stop
            return base.DefenseSingleAbility(nextGCD, out act);
        }

        if (tbMedium)
        {
            // === TB in 5-10s ===
            // Pre-place Haima (shields stack over time via auto-attacks)
            if (HaimaPvE.CanUse(out act))
                return true;

            return base.DefenseSingleAbility(nextGCD, out act);
        }

        // === Non-BMR fallback: use when framework triggers defense ===
        if (HaimaPvE.CanUse(out act))
            return true;

        if (CanSpendAddersgall && TaurocholePvE.CanUse(out act))
            return true;

        return base.DefenseSingleAbility(nextGCD, out act);
    }

    #endregion

    #region Healing Abilities

    [RotationDesc(ActionID.TaurocholePvE, ActionID.DruocholePvE, ActionID.SoteriaPvE, ActionID.HaimaPvE, ActionID.KrasisPvE)]
    protected override bool HealSingleAbility(IAction nextGCD, out IAction? act)
    {
        // BMR-aware: if a tankbuster is coming soon, don't waste Taurochole on chip damage.
        // Save it for the TB where the 10% mit matters.
        bool tbComingSoon = BMRActive && BMRTankbusterIn is > 1f and <= 8f;

        // Soteria: boosts Kardia heals, free, no Addersgall cost
        // Use when a tank needs extra passive healing
        if (SoteriaPvE.CanUse(out act))
            return true;

        // Taurochole: strongest single-target Addersgall heal + mit
        // BMR: hold for TB if one is coming soon — the mit component is wasted otherwise
        if (!tbComingSoon && CanSpendAddersgall && TaurocholePvE.CanUse(out act)
            && TaurocholePvE.Target.Target?.GetHealthRatio() < TaurocholHP)
            return true;

        // Druochole: basic Addersgall single heal, essentially no CD
        if (CanSpendAddersgall && DruocholePvE.CanUse(out act)
            && DruocholePvE.Target.Target?.GetHealthRatio() < DruocholHP)
            return true;

        // Haima: multi-layer shield on low HP target
        // BMR: hold for TB if one is coming soon
        if (!tbComingSoon && HaimaPvE.CanUse(out act))
            return true;

        // Krasis: boost healing received by 20% on target
        if (KrasisPvE.CanUse(out act))
            return true;

        return base.HealSingleAbility(nextGCD, out act);
    }

    [RotationDesc(ActionID.KeracholePvE, ActionID.IxocholePvE, ActionID.HolosPvE, ActionID.PhysisIiPvE, ActionID.PanhaimaPvE, ActionID.PepsisPvE, ActionID.PhilosophiaPvE)]
    protected override bool HealAreaAbility(IAction nextGCD, out IAction? act)
    {
        // === BMR-AWARE AREA HEALING ===
        // CRITICAL RULE: MIT BEFORE raidwide, HEAL AFTER damage.
        // This method fires when party HP drops (i.e. AFTER damage).
        // If a raidwide is STILL coming soon, hold heals — MIT is handling it in DefenseAreaAbility.
        // After the RW hits and HP drops, these heals fire to recover the party.
        //
        // Per Balance: "Kerachole for mit+regen, Physis for regen+heal buff,
        // Ixochole for immediate burst, Pepsis to convert shields, Philosophia for sustained"

        bool rwComingSoon = BMRActive && BMRRaidwideIn is > 1f and <= 8f;

        // === Pepsis: convert existing shields to heals (best used POST-raidwide) ===
        // If we applied E.Prognosis shield before the RW and it's still up, Pepsis converts
        // the remaining shield value into a heal. Best value right after RW lands.
        // Pepsis requires EukrasianPrognosis status to be active on the party.
        if (!rwComingSoon && PepsisPvE.CanUse(out act))
            return true;

        // === Philosophia: Dawntrail ability, party heal + GCD healing buff ===
        // BMR: if vuln window coming, hold Philosophia so the healing buff covers the
        // vuln phase where extra healing matters most.
        if (PhilosophiaForVuln && BMRActive && BMRVulnerableIn is > 0 and <= 20f)
        {
            // Hold Philosophia for the vuln window — skip it here
        }
        else if (PhilosophiaPvE.CanUse(out act))
        {
            return true;
        }

        // === Hold heals if raidwide is imminent ===
        // MIT handles the incoming damage. These heals would be wasted on pre-damage HP.
        // After the RW hits, the framework will call this method again with low party HP.
        if (rwComingSoon)
            return base.HealAreaAbility(nextGCD, out act);

        // === Post-raidwide healing priority (oGCD > GCD) ===

        // Kerachole: AoE regen + 10% mit (best value Addersgall spend)
        // If party took damage, the regen will heal them up over 15s
        if (CanSpendAddersgall && PartyMembersAverHP < KeracholHP && KeracholePvE.CanUse(out act))
            return true;

        // Ixochole: immediate AoE heal — burst recovery when party is low
        if (CanSpendAddersgall && PartyMembersAverHP < IxocholHP && IxocholePvE.CanUse(out act))
            return true;

        // Physis II: AoE regen + 10% healing buff (amplifies other heals in the window)
        if (PhysisIiPvE.CanUse(out act))
            return true;

        if (!PhysisIiPvE.EnoughLevel && PhysisPvE.CanUse(out act))
            return true;

        // Holos: AoE shield + mit (no Addersgall cost)
        if (HolosPvE.CanUse(out act))
            return true;

        // Panhaima: multi-layer shields (useful for follow-up damage)
        if (PanhaimaPvE.CanUse(out act))
            return true;

        return base.HealAreaAbility(nextGCD, out act);
    }

    #endregion

    #region General Ability

    protected override bool GeneralAbility(IAction nextGCD, out IAction? act)
    {
        // Kardia: apply to a party member if nobody has Kardion
        if (KardiaPvE.CanUse(out act))
            return true;

        // Rhizomata: grants 1 Addersgall charge
        // Use when below max charges and not about to naturally gain one
        // BMR: more aggressive usage before raidwide/TB if we need charges for Kerachole/Taurochole
        bool needChargesForMit = BMRActive
            && ((BMRRaidwideIn is > 0 and <= 10f) || (BMRTankbusterIn is > 0 and <= 10f))
            && Addersgall == 0;

        if (needChargesForMit && RhizomataPvE.CanUse(out act))
            return true;

        if (Addersgall <= 1 && !AddersgallEndAfter(3) && RhizomataPvE.CanUse(out act))
            return true;

        return base.GeneralAbility(nextGCD, out act);
    }

    #endregion

    #region Attack Ability

    protected override bool AttackAbility(IAction nextGCD, out IAction? act)
    {
        if (!InCombat || !HasHostilesInRange)
            return base.AttackAbility(nextGCD, out act);

        // === BMR: Phlegma dump before downtime ===
        // If boss is about to become untargetable, spend all Phlegma charges now.
        // Charges ticking during downtime = wasted damage. Better to front-load them.
        bool downtimeApproaching = PhlegmaDumpBeforeDowntime
            && BMRActive && BMRDowntimeIn is > 0 and <= 8f;

        // === Psyche (Dawntrail oGCD, 60s CD) ===
        // Use on cooldown. SGE has no personal raid buff, so Psyche should not
        // drift. It naturally aligns with 120s windows every other use.
        // BMR: hold briefly if downtime is imminent (Psyche is wasted on invuln boss)
        if (downtimeApproaching && BMRDowntimeIn <= 3f)
        {
            // Boss going away in <3s — hold Psyche
        }
        else if (PsychePvE.CanUse(out act))
        {
            return true;
        }

        // === Rhizomata: gain Addersgall charge ===
        // Use when we need charges for healing (empty gauge)
        if (Addersgall == 0 && RhizomataPvE.CanUse(out act))
            return true;

        // === Soteria: boost Kardia healing, free oGCD ===
        // Use on cooldown if Kardia is active (DPS Bot mode benefits greatly)
        if (HealMode == HealModeStrategy.DPSBot && SoteriaPvE.CanUse(out act))
            return true;

        return base.AttackAbility(nextGCD, out act);
    }

    #endregion

    #region GCD Healing

    [RotationDesc(ActionID.EukrasianDiagnosisPvE, ActionID.DiagnosisPvE)]
    protected override bool HealSingleGCD(out IAction? act)
    {
        // Reserve Swiftcast for Raise
        if ((HasSwift || IsLastAction(ActionID.SwiftcastPvE)) && SwiftLogic && MergedStatus.HasFlag(AutoStatus.Raise))
            return base.HealSingleGCD(out act);

        // Eukrasian Diagnosis: shield + heal (generates Addersting on break)
        if (TryEukrasianAction(EukrasianDiagnosisPvE, out act))
            return true;

        // Diagnosis: basic heal fallback
        if (DiagnosisPvE.CanUse(out act))
            return true;

        return base.HealSingleGCD(out act);
    }

    [RotationDesc(ActionID.EukrasianPrognosisIiPvE, ActionID.EukrasianPrognosisPvE, ActionID.PrognosisPvE)]
    protected override bool HealAreaGCD(out IAction? act)
    {
        // Reserve Swiftcast for Raise
        if ((HasSwift || IsLastAction(ActionID.SwiftcastPvE)) && SwiftLogic && MergedStatus.HasFlag(AutoStatus.Raise))
            return base.HealAreaGCD(out act);

        // Pneuma: line AoE damage + heal (600 potency heal)
        // Use as emergency AoE heal when party is low
        if (PartyMembersAverHP < PneumaHealHP && PneumaPvE.CanUse(out act))
            return true;

        // Eukrasian Prognosis II (upgraded) or Eukrasian Prognosis: AoE shield
        if (EukrasianPrognosisIiPvE.EnoughLevel)
        {
            if (TryEukrasianAction(EukrasianPrognosisIiPvE, out act))
                return true;
        }
        else
        {
            if (TryEukrasianAction(EukrasianPrognosisPvE, out act))
                return true;
        }

        // Prognosis: basic AoE heal
        if (PrognosisPvE.CanUse(out act))
            return true;

        return base.HealAreaGCD(out act);
    }

    [RotationDesc(ActionID.EgeiroPvE)]
    protected override bool RaiseGCD(out IAction? act)
    {
        if (EgeiroPvE.CanUse(out act))
            return true;

        return base.RaiseGCD(out act);
    }

    #endregion

    #region Defense GCDs

    protected override bool DefenseSingleGCD(out IAction? act)
    {
        // Eukrasian Diagnosis shield for incoming single-target damage
        if (TryEukrasianAction(EukrasianDiagnosisPvE, out act))
            return true;

        return base.DefenseSingleGCD(out act);
    }

    [RotationDesc(ActionID.EukrasianPrognosisIiPvE, ActionID.EukrasianPrognosisPvE)]
    protected override bool DefenseAreaGCD(out IAction? act)
    {
        // Eukrasian Prognosis: AoE shield before raidwide
        if (EukrasianPrognosisIiPvE.EnoughLevel)
        {
            if (TryEukrasianAction(EukrasianPrognosisIiPvE, out act))
                return true;
        }
        else
        {
            if (TryEukrasianAction(EukrasianPrognosisPvE, out act))
                return true;
        }

        return base.DefenseAreaGCD(out act);
    }

    #endregion

    #region GCD Logic

    protected override bool GeneralGCD(out IAction? act)
    {
        // Reserve Swiftcast for Raise
        if ((HasSwift || IsLastAction(ActionID.SwiftcastPvE)) && SwiftLogic && MergedStatus.HasFlag(AutoStatus.Raise))
            return base.GeneralGCD(out act);

        // If Eukrasia is active, we need to follow through with the aimed action
        if (HasEukrasia)
        {
            // Complete the pending Eukrasian action
            if (_eukrasiaAim != null)
            {
                if (_eukrasiaAim.CanUse(out act, skipStatusProvideCheck: true))
                {
                    _eukrasiaAim = null;
                    return true;
                }
                // Aim is stale / can't use - clear and fall through
                _eukrasiaAim = null;
            }

            // Eukrasia active but no aim set - use best available Eukrasian spell
            // This handles edge cases like pre-pull Eukrasia
            if (EukrasianDosisIiiPvE.EnoughLevel && EukrasianDosisIiiPvE.CanUse(out act))
                return true;
            if (EukrasianDosisIiPvE.EnoughLevel && EukrasianDosisIiPvE.CanUse(out act))
                return true;
            if (EukrasianDosisPvE.EnoughLevel && EukrasianDosisPvE.CanUse(out act))
                return true;
            if (EukrasianDyskrasiaPvE.CanUse(out act))
                return true;
            // Anti-brick: if nothing else works, dump Eukrasia on a prognosis
            if (EukrasianPrognosisIiPvE.EnoughLevel && EukrasianPrognosisIiPvE.CanUse(out act, skipStatusProvideCheck: true))
                return true;
            if (EukrasianPrognosisPvE.CanUse(out act, skipStatusProvideCheck: true))
                return true;
        }

        // === BMR: Skip DoT refresh if downtime is imminent ===
        // If the boss is going untargetable in <5s, don't waste a GCD applying a 30s DoT.
        // Use Phlegma/Toxikon/Dosis instead for immediate damage.
        bool downtimeImminent = BMRActive && BMRDowntimeIn is > 0 and <= 5f;

        // === Eukrasian Dosis DoT Snapshot ===
        // If raid buffs just went up, force-refresh DoT to snapshot buffed ticks.
        // DoTs in FFXIV snapshot all buffs at application time, so refreshing early
        // during raid buffs gives 30s of buffed ticks.
        // BMR: skip snapshot if downtime imminent (DoT wasted on untargetable boss)
        if (!downtimeImminent && InRaidBuffs && CanBurst)
        {
            if (EukrasianDosisIiiPvE.EnoughLevel)
            {
                if (TryEukrasianAction(EukrasianDosisIiiPvE, out act, skipStatusProvideCheck: true))
                    return true;
            }
            else if (EukrasianDosisIiPvE.EnoughLevel)
            {
                if (TryEukrasianAction(EukrasianDosisIiPvE, out act, skipStatusProvideCheck: true))
                    return true;
            }
            else if (EukrasianDosisPvE.EnoughLevel)
            {
                if (TryEukrasianAction(EukrasianDosisPvE, out act, skipStatusProvideCheck: true))
                    return true;
            }
        }

        // === AoE Damage (3+ targets) ===

        // Eukrasian Dyskrasia: AoE DoT (3+ targets, does not stack with Eukrasian Dosis)
        // BMR: skip if downtime imminent (DoT wasted)
        if (!downtimeImminent && TryEukrasianAction(EukrasianDyskrasiaPvE, out act))
            return true;

        // === BMR: Phlegma dump before downtime ===
        // If boss is about to become untargetable, dump all Phlegma charges NOW.
        // Charges regenerating during downtime = pure waste. Front-load them.
        if (downtimeImminent && PhlegmaDumpBeforeDowntime)
        {
            if (PhlegmaIiiPvE.EnoughLevel && PhlegmaIiiPvE.CanUse(out act, usedUp: true))
                return true;
            if (PhlegmaIiPvE.EnoughLevel && PhlegmaIiPvE.CanUse(out act, usedUp: true))
                return true;
            if (PhlegmaPvE.EnoughLevel && PhlegmaPvE.CanUse(out act, usedUp: true))
                return true;
        }

        // Phlegma III: 2 charges, 45s each.
        // Pool for raid buff windows when configured, but don't overcap charges.
        // usedUp: true during raid buffs = spend both charges in burst.
        // usedUp: false normally = only use if about to overcap.
        {
            bool useUp = InRaidBuffs || !PhlegmaHoldForBurst;
            if (PhlegmaIiiPvE.EnoughLevel)
            {
                if (PhlegmaIiiPvE.CanUse(out act, usedUp: useUp))
                    return true;
            }
            else if (PhlegmaIiPvE.EnoughLevel)
            {
                if (PhlegmaIiPvE.CanUse(out act, usedUp: useUp))
                    return true;
            }
            else if (PhlegmaPvE.EnoughLevel)
            {
                if (PhlegmaPvE.CanUse(out act, usedUp: useUp))
                    return true;
            }
        }

        // Dyskrasia II / Dyskrasia: AoE damage filler (3+ targets)
        if (DyskrasiaIiPvE.EnoughLevel && DyskrasiaIiPvE.CanUse(out act))
            return true;
        if (!DyskrasiaIiPvE.EnoughLevel && DyskrasiaPvE.EnoughLevel && DyskrasiaPvE.CanUse(out act))
            return true;

        // === Pneuma (Line AoE + Heal) ===
        // Pneuma is damage-neutral with Dosis (same potency) but also heals the party.
        // Use freely as a DPS GCD - it's free healing while doing damage.
        if (PneumaPvE.CanUse(out act))
            return true;

        // === DoT Maintenance ===
        // Eukrasian Dosis: apply/refresh DoT (framework handles refresh timing via IsRestrictedDOT)
        // BMR: skip refresh if downtime imminent (DoT ticks wasted on untargetable boss)
        if (!downtimeImminent)
        {
            if (EukrasianDosisIiiPvE.EnoughLevel)
            {
                if (TryEukrasianAction(EukrasianDosisIiiPvE, out act))
                    return true;
            }
            else if (EukrasianDosisIiPvE.EnoughLevel)
            {
                if (TryEukrasianAction(EukrasianDosisIiPvE, out act))
                    return true;
            }
            else if (EukrasianDosisPvE.EnoughLevel)
            {
                if (TryEukrasianAction(EukrasianDosisPvE, out act))
                    return true;
            }
        }

        // === Movement GCDs ===
        // Toxikon II: instant cast, consumes Addersting (from broken shields)
        // Use when moving and have stacks - better than losing a GCD
        if (IsMoving && Addersting > 0)
        {
            if (ToxikonIiPvE.EnoughLevel && ToxikonIiPvE.CanUse(out act))
                return true;
            if (!ToxikonIiPvE.EnoughLevel && ToxikonPvE.EnoughLevel && ToxikonPvE.CanUse(out act))
                return true;
        }

        // === Single-Target Filler ===
        // Dosis III: main damage GCD (1.5s cast time, easy to slidecast)
        if (DosisIiiPvE.EnoughLevel && DosisIiiPvE.CanUse(out act))
            return true;
        if (!DosisIiiPvE.EnoughLevel && DosisIiPvE.EnoughLevel && DosisIiPvE.CanUse(out act))
            return true;
        if (!DosisIiPvE.EnoughLevel && DosisPvE.CanUse(out act))
            return true;

        // Toxikon as filler when nothing else available (instant, still better than nothing)
        if (ToxikonIiPvE.EnoughLevel && ToxikonIiPvE.CanUse(out act))
            return true;
        if (!ToxikonIiPvE.EnoughLevel && ToxikonPvE.EnoughLevel && ToxikonPvE.CanUse(out act))
            return true;

        return base.GeneralGCD(out act);
    }

    #endregion

    #region Heal Mode Logic

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
    /// Controls whether the framework will attempt single-target GCD heals.
    /// DPS Bot: never GCD heal (Kardia + oGCDs only).
    /// Balanced: GCD heal only if solo healer.
    /// Heal Bot: always GCD heal.
    /// </summary>
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

    /// <summary>
    /// Controls whether the framework will attempt AoE GCD heals.
    /// DPS Bot: never GCD heal.
    /// Balanced: GCD heal only if solo healer.
    /// Heal Bot: always GCD heal.
    /// </summary>
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

    #region Movement

    [RotationDesc(ActionID.IcarusPvE)]
    protected override bool MoveForwardAbility(IAction nextGCD, out IAction? act)
    {
        if (IcarusPvE.CanUse(out act))
            return true;
        return base.MoveForwardAbility(nextGCD, out act);
    }

    #endregion

    #region Debug Display

    public override void DisplayRotationStatus()
    {
        ImGui.Text($"--- Burst ---");
        ImGui.Text($"CanBurst: {CanBurst}");
        ImGui.Text($"InRaidBuffs: {InRaidBuffs}");
        ImGui.Text($"Medicated: {StatusHelper.PlayerHasStatus(true, StatusID.Medicated)}");

        ImGui.Text($"--- Gauge ---");
        ImGui.Text($"Addersgall: {Addersgall} / 3 (reserve: {AddersgallReserve})");
        ImGui.Text($"CanSpendAddersgall: {CanSpendAddersgall}");
        ImGui.Text($"AddersgallTime: {AddersgallTime:F1}s");
        ImGui.Text($"Addersting: {Addersting}");
        ImGui.Text($"Eukrasia: {HasEukrasia}");
        ImGui.Text($"EukrasiaAim: {_eukrasiaAim?.Info.Name ?? "None"}");
        ImGui.Text($"Kardia: {HasKardia}");

        ImGui.Text($"--- Cooldowns ---");
        ImGui.Text($"Phlegma: {(PhlegmaIiiPvE.EnoughLevel ? $"{PhlegmaIiiPvE.Cooldown.CurrentCharges}/{PhlegmaIiiPvE.Cooldown.MaxCharges}" : "N/A")}");
        ImGui.Text($"Psyche: {(PsychePvE.Cooldown.IsCoolingDown ? $"{PsychePvE.Cooldown.RecastTimeRemain:F1}s" : "Ready")}");
        ImGui.Text($"Pneuma: {(PneumaPvE.Cooldown.IsCoolingDown ? $"{PneumaPvE.Cooldown.RecastTimeRemain:F1}s" : "Ready")}");
        ImGui.Text($"Rhizomata: {(RhizomataPvE.Cooldown.IsCoolingDown ? $"{RhizomataPvE.Cooldown.RecastTimeRemain:F1}s" : "Ready")}");
        ImGui.Text($"Philosophia: {(PhilosophiaPvE.Cooldown.IsCoolingDown ? $"{PhilosophiaPvE.Cooldown.RecastTimeRemain:F1}s" : "Ready")}");

        ImGui.Text($"--- Healing ---");
        ImGui.Text($"HealMode: {HealMode}");
        ImGui.Text($"IsSoloHealer: {IsSoloHealer}");
        ImGui.Text($"CanHealSingleSpell: {CanHealSingleSpell}");
        ImGui.Text($"CanHealAreaSpell: {CanHealAreaSpell}");
        ImGui.Text($"PartyHP: {PartyMembersAverHP:P0}");

        ImGui.Text($"--- Defensive CDs ---");
        ImGui.Text($"Kerachole: {(KeracholePvE.Cooldown.IsCoolingDown ? $"{KeracholePvE.Cooldown.RecastTimeRemain:F1}s" : "Ready")}");
        ImGui.Text($"Holos: {(HolosPvE.Cooldown.IsCoolingDown ? $"{HolosPvE.Cooldown.RecastTimeRemain:F1}s" : "Ready")}");
        ImGui.Text($"Panhaima: {(PanhaimaPvE.Cooldown.IsCoolingDown ? $"{PanhaimaPvE.Cooldown.RecastTimeRemain:F1}s" : "Ready")}");
        ImGui.Text($"Haima: {(HaimaPvE.Cooldown.IsCoolingDown ? $"{HaimaPvE.Cooldown.RecastTimeRemain:F1}s" : "Ready")}");
        ImGui.Text($"Taurochole: {(TaurocholePvE.Cooldown.IsCoolingDown ? $"{TaurocholePvE.Cooldown.RecastTimeRemain:F1}s" : "Ready")}");

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
