using System.ComponentModel;

namespace RotationSolver.ExtraRotations.Magical;

[Rotation("SezuraiSMN", CombatType.PvE, GameVersion = "7.41",
    Description = "Balance-aligned SMN with Searing Light burst, primal optimization, and demi-summon management.")]
[SourceCode(Path = "main/ExtraRotations/Magical/SezuraiSMN.cs")]
[ExtraRotation]
public sealed class SezuraiSMN : SummonerRotation
{
    #region Config Options

    public enum PrimalOrderType : byte
    {
        [Description("Titan > Garuda > Ifrit (Standard)")] TitanGarudaIfrit,
        [Description("Titan > Ifrit > Garuda")] TitanIfritGaruda,
        [Description("Garuda > Titan > Ifrit")] GarudaTitanIfrit,
        [Description("Ifrit > Titan > Garuda")] IfritTitanGaruda,
    }

    [Range(0f, 0.25f, ConfigUnitType.Percent)]
    [RotationConfig(CombatType.PvE, Name = "Action Ahead Override (0 = use global setting)")]
    public float ActionAheadOverride { get; set; } = 0f;

    [RotationConfig(CombatType.PvE, Name = "Auto Pot Usage (Gemdraught during Searing Light burst windows)")]
    public bool BurstMed { get; set; } = true;

    [RotationConfig(CombatType.PvE, Name = "Primal summon order")]
    public PrimalOrderType PrimalOrder { get; set; } = PrimalOrderType.TitanGarudaIfrit;

    [RotationConfig(CombatType.PvE, Name = "Use Crimson Cyclone at any range (ignores distance setting below)")]
    public bool CrimsonCycloneAnywhere { get; set; } = true;

    [Range(1, 20, ConfigUnitType.Yalms)]
    [RotationConfig(CombatType.PvE, Name = "Max distance from target for Crimson Cyclone usage")]
    public float CrimsonCycloneDistance { get; set; } = 5f;

    [RotationConfig(CombatType.PvE, Name = "Use Crimson Cyclone while moving (gap closer)")]
    public bool CrimsonCycloneMoving { get; set; } = false;

    [RotationConfig(CombatType.PvE, Name = "Use Swiftcast on Slipstream (Garuda hardcast)")]
    public bool SwiftcastSlipstream { get; set; } = false;

    [RotationConfig(CombatType.PvE, Name = "Use Swiftcast on Resurrection")]
    public bool SwiftcastRaise { get; set; } = true;

    #endregion

    #region Status Helpers

    /// <summary>
    /// Whether the user has burst enabled in the framework.
    /// </summary>
    private bool CanBurst => MergedStatus.HasFlag(AutoStatus.Burst);

    /// <summary>
    /// True during any demi-summon phase (Bahamut, Phoenix, or Solar Bahamut).
    /// </summary>
    private bool InDemiSummon => InBahamut || InPhoenix || InSolarBahamut;

    /// <summary>
    /// True when no primal attunement or favor is active and no primals remain.
    /// Used to detect filler / transition states.
    /// </summary>
    private bool InFillerPhase => !InDemiSummon && !InIfrit && !InGaruda && !InTitan
        && !HasIfritFavor && !HasGarudaFavor && !HasTitanFavor && !HasCrimsonStrike;

    /// <summary>
    /// Any primal gem is available for summoning.
    /// </summary>
    private static bool AnyPrimalReady => IsIfritReady || IsGarudaReady || IsTitanReady;

    // Status helpers
    private static bool HasFurtherRuin => StatusHelper.PlayerHasStatus(true, StatusID.FurtherRuin_2701);
    private static bool HasCrimsonStrike => StatusHelper.PlayerHasStatus(true, StatusID.CrimsonStrikeReady_4403);
    private static bool HasIfritFavor => StatusHelper.PlayerHasStatus(true, StatusID.IfritsFavor);
    private static bool HasGarudaFavor => StatusHelper.PlayerHasStatus(true, StatusID.GarudasFavor);
    private static bool HasTitanFavor => StatusHelper.PlayerHasStatus(true, StatusID.TitansFavor);
    private static bool HasRadiantAegisStatus => StatusHelper.PlayerHasStatus(true, StatusID.RadiantAegis);

    /// <summary>
    /// Remaining GCDs inside the current demi-summon window.
    /// Demi-summons last ~15s; each GCD is roughly one weapon cycle.
    /// </summary>
    private int DemiGCDsRemaining
    {
        get
        {
            if (!InDemiSummon) return 0;
            float raw = SummonTime;
            if (raw <= 0) return 0;
            float gcdLen = RuinPvE.Cooldown.RecastTime;
            if (gcdLen <= 0) gcdLen = 2.5f;
            return (int)(raw / gcdLen) + 1;
        }
    }

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
        ImGui.Text("=== Sezurai SMN Debug ===");
        ImGui.Text($"CanBurst: {CanBurst}");
        ImGui.Text($"InDemiSummon: {InDemiSummon}");
        ImGui.Text($"  InBahamut: {InBahamut}");
        ImGui.Text($"  InPhoenix: {InPhoenix}");
        ImGui.Text($"  InSolarBahamut: {InSolarBahamut}");
        ImGui.Text($"DemiGCDsRemaining: {DemiGCDsRemaining}");
        ImGui.Separator();
        ImGui.Text($"InTitan: {InTitan}  InGaruda: {InGaruda}  InIfrit: {InIfrit}");
        ImGui.Text($"IsTitanReady: {IsTitanReady}  IsGarudaReady: {IsGarudaReady}  IsIfritReady: {IsIfritReady}");
        ImGui.Text($"AttunementCount: {AttunementCount}");
        ImGui.Text($"HasTitanFavor: {HasTitanFavor}  HasGarudaFavor: {HasGarudaFavor}  HasIfritFavor: {HasIfritFavor}");
        ImGui.Text($"HasCrimsonStrike: {HasCrimsonStrike}");
        ImGui.Separator();
        ImGui.Text($"HasAetherflowStacks: {HasAetherflowStacks}  Stacks: {SMNAetherflowStacks}");
        ImGui.Text($"HasFurtherRuin: {HasFurtherRuin}");
        ImGui.Text($"HasSearingLight: {HasSearingLight}");
        ImGui.Text($"SummonTime: {SummonTime:F1}  AttunmentTime: {AttunmentTime:F1}");
        ImGui.Text($"IsSolarBahamutReady: {IsSolarBahamutReady}");
        ImGui.Text($"IsBahamutReady: {IsBahamutReady}");
        ImGui.Text($"IsPhoenixReady: {IsPhoenixReady}");
        ImGui.Text("--- BMR Timeline ---");
        ImGui.Text($"Active: {BmrActive}{(BmrActive ? $" ({DataCenter.BmrActiveModuleName})" : "")}");
        if (BmrActive)
        {
            ImGui.Text($"Raidwide In: {(BmrRaidwideIn < 9999f ? $"{BmrRaidwideIn:F1}s" : "None")}");
            ImGui.Text($"Knockback In: {(BmrKnockbackIn < 9999f ? $"{BmrKnockbackIn:F1}s" : "None")}");
            ImGui.Text($"Downtime In: {(BmrDowntimeIn < 9999f ? $"{BmrDowntimeIn:F1}s" : "None")}");
        }
    }

    #endregion

    #region Countdown & Opener
    // === SMN OPENER (7.4 Balance) ===
    // Pre-pull: Summon Carbuncle(if needed) → Pot(-2s) → Ruin III precast(-1.5s)
    // GCD1: Ruin III (lands on pull) → Summon Solar Bahamut (weave)
    // → Searing Light (weave, party buff) → GCD2: Umbral Impulse → Searing Flash (weave)
    // GCD3: Umbral Impulse → Energy Drain (weave) → Enkindle Solar Bahamut (weave)
    // GCD4: Umbral Impulse → Necrotize (weave) → Necrotize (weave)
    // GCD5: Umbral Impulse → Sunflare (Astral Flow finisher)
    // → Summon primals: Titan first (instant Topaz GCDs for mobility) → Garuda → Ifrit
    //
    // === EVEN BURST (120s) ===
    // Searing Light (party buff) + Demi-summon phase (Bahamut/Phoenix/Solar Bahamut)
    // Enkindle + Energy Drain + Necrotize dump under raid buffs
    // Hold Necrotize charges for even windows when possible
    //
    // === ODD BURST (60s) ===
    // Energy Drain + Necrotize/Fester only — Searing Light is 120s
    // Demi-summon naturally aligns with even windows (60s per demi cycle)
    //
    // === FILLER / SUSTAIN ===
    // Primal order: Titan (instant GCDs, mobility) → Garuda (caster GCDs) → Ifrit (long casts)
    // Ruin III/IV: filler GCD between primal phases, always be casting
    // Energy Drain: use on CD, generates Aetherflow for Necrotize/Fester
    // Necrotize: hold 1 charge for even burst if close, spend to avoid overcap
    // Primal gems: use Topaz/Emerald/Ruby spells before summoning next primal
    // Radiant Aegis: free shield on 60s CD, use for survivability

    protected override IAction? CountDownAction(float remainTime)
    {
        // Summon Carbuncle if pet is missing
        if (SummonCarbunclePvE.CanUse(out var act))
            return act;

        // Medicine at ~2s prepull for opener pot
        if (BurstMed && remainTime <= 2f && remainTime > 1f && UseBurstMedicine(out act))
            return act;

        // Precast Ruin III at ~1.5s (hardcast before pull)
        if (HasSummon && remainTime <= RuinIiiPvE.Info.CastTime + 0.4f
            && remainTime > 0.4f && !InCombat)
        {
            if (RuinIiiPvE.CanUse(out act))
                return act;
        }

        return base.CountDownAction(remainTime);
    }

    #endregion

    #region Defense & Utility

    [RotationDesc(ActionID.AddlePvE, ActionID.RadiantAegisPvE)]
    protected sealed override bool DefenseAreaAbility(IAction nextGCD, out IAction? act)
    {
        // BMR-aware: Addle + Radiant Aegis proactively before raidwide
        bool rwSoon = BmrActive && BmrRaidwideIn is > 0 and <= 5f;

        if (rwSoon)
        {
            // Addle first for party benefit
            if (AddlePvE.CanUse(out act))
                return true;
            // Radiant Aegis: personal shield for survivability
            if (!HasRadiantAegisStatus && RadiantAegisPvE.CanUse(out act))
                return true;
            return base.DefenseAreaAbility(nextGCD, out act);
        }

        // Non-BMR: use when framework triggers defense
        if (!HasRadiantAegisStatus && RadiantAegisPvE.CanUse(out act))
            return true;
        if (AddlePvE.CanUse(out act))
            return true;

        return base.DefenseAreaAbility(nextGCD, out act);
    }

    [RotationDesc(ActionID.RadiantAegisPvE)]
    protected override bool DefenseSingleAbility(IAction nextGCD, out IAction? act)
    {
        if (!HasRadiantAegisStatus && RadiantAegisPvE.CanUse(out act))
            return true;

        return base.DefenseSingleAbility(nextGCD, out act);
    }

    [RotationDesc(ActionID.LuxSolarisPvE)]
    protected override bool HealAreaAbility(IAction nextGCD, out IAction? act)
    {
        // Lux Solaris: AoE heal from Refulgent Lux status (Solar Bahamut follow-up)
        if (LuxSolarisPvE.CanUse(out act))
            return true;

        return base.HealAreaAbility(nextGCD, out act);
    }

    [RotationDesc(ActionID.RekindlePvE)]
    protected override bool HealSingleAbility(IAction nextGCD, out IAction? act)
    {
        // Rekindle: strong single-target heal from Phoenix phase
        if (RekindlePvE.CanUse(out act))
            return true;

        // Radiant Aegis if no shield
        if (!HasRadiantAegisStatus && RadiantAegisPvE.CanUse(out act))
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

    #endregion

    #region General Ability (non-attack oGCDs)

    protected override bool GeneralAbility(IAction nextGCD, out IAction? act)
    {
        // Prevent Refulgent Lux (Lux Solaris proc) from expiring
        if (StatusHelper.PlayerWillStatusEndGCD(3, 0, true, StatusID.RefulgentLux))
        {
            if (LuxSolarisPvE.CanUse(out act))
                return true;
        }

        // Prevent Rekindle from expiring during Phoenix phase
        if (StatusHelper.PlayerWillStatusEndGCD(2, 0, true, StatusID.FirebirdTrance))
        {
            if (RekindlePvE.CanUse(out act))
                return true;
        }

        return base.GeneralAbility(nextGCD, out act);
    }

    #endregion

    #region Emergency Ability (highest priority oGCDs)

    protected override bool EmergencyAbility(IAction nextGCD, out IAction? act)
    {
        // === Medicine: use during Searing Light / demi-summon burst windows ===
        if (BurstMed && InCombat && HasSearingLight && UseBurstMedicine(out act))
            return true;

        // === Swiftcast management ===
        if (TrySwiftcast(nextGCD, out act))
            return true;

        return base.EmergencyAbility(nextGCD, out act);
    }

    #endregion

    #region Attack Ability (damage oGCDs)

    protected override bool AttackAbility(IAction nextGCD, out IAction? act)
    {
        // ======================================================================
        // SMN oGCD priority during 120s cycle:
        //
        // 1. Searing Light (120s raid buff) - fire during demi-summon phase
        // 2. Searing Flash (follow-up to Searing Light, requires Ruby's Glimmer)
        // 3. Energy Drain / Energy Siphon (refresh Aetherflow, grant FurtherRuin)
        // 4. Enkindle (Akh Morn / Revelation / Exodus) - demi-summon big hit
        // 5. Astral Flow (Deathflare / Rekindle / Sunflare) - demi-summon finisher
        // 6. Necrotize / Fester / Painflare (spend Aetherflow stacks)
        // 7. Mountain Buster (Titan Astral Flow, on each Topaz Rite)
        // ======================================================================

        // === 1. Searing Light: 120s raid buff ===
        // Balance guide: Use during demi-summon, align with party raid buffs.
        // Fire early in the demi phase for maximum coverage of burst window.
        // At level 100 we align with Solar Bahamut (every 120s).
        // Below level 100, align with Bahamut.
        if (CanBurst && InCombat)
        {
            bool shouldSearingLight = false;

            if (SummonSolarBahamutPvE.EnoughLevel)
            {
                // Level 100: Searing Light during Solar Bahamut (120s alignment)
                // Also allow during Bahamut/Phoenix if SL is available (drift recovery)
                shouldSearingLight = InSolarBahamut || (InDemiSummon && SearingLightPvE.Cooldown.HasOneCharge);
            }
            else if (SummonBahamutPvE.EnoughLevel)
            {
                // Below 100: Searing Light during Bahamut
                shouldSearingLight = InBahamut;
            }
            else
            {
                // Very low level: use on cooldown
                shouldSearingLight = true;
            }

            if (shouldSearingLight && SearingLightPvE.CanUse(out act))
                return true;
        }

        // === 2. Searing Flash: follow-up to Searing Light ===
        // Requires Ruby's Glimmer status (granted by Searing Light).
        // Use promptly - it is a free oGCD damage hit.
        if (SearingFlashPvE.CanUse(out act))
            return true;

        // === 3. Energy Drain / Energy Siphon: refresh Aetherflow ===
        // Balance guide: "generally weaved early in the opener to reduce risk of losing a use"
        // Use during demi-summon phase or when Searing Light is active.
        // At level 100: use during Solar Bahamut phase preferentially (even minute),
        // and during Bahamut/Phoenix phase (odd minute).
        if (!HasAetherflowStacks)
        {
            bool shouldDrain = false;

            // During demi-summon: always drain (weave with demi GCDs)
            if (InDemiSummon)
                shouldDrain = true;

            // During Searing Light window outside demi: also drain
            if (HasSearingLight && !InDemiSummon)
                shouldDrain = true;

            // Fallback: if SL not learned, drain on cooldown
            if (!SearingLightPvE.EnoughLevel)
                shouldDrain = true;

            if (shouldDrain)
            {
                if (EnergySiphonPvE.CanUse(out act))
                    return true;
                if (EnergyDrainPvE.CanUse(out act))
                    return true;
            }
        }

        // === 4. Enkindle: demi-summon big hit (Akh Morn / Revelation / Exodus) ===
        // Use once per demi phase. Weave it after the first couple GCDs.
        if (InDemiSummon)
        {
            if (EnkindleSolarBahamutPvE.CanUse(out act))
                return true;
            if (EnkindleBahamutPvE.CanUse(out act))
                return true;
            if (EnkindlePhoenixPvE.CanUse(out act))
                return true;
        }

        // === 5. Astral Flow: demi-summon finisher (Deathflare / Sunflare) ===
        // Rekindle (Phoenix Astral Flow) handled in HealSingleAbility / GeneralAbility.
        // Deathflare and Sunflare are damage oGCDs - use during demi phase.
        if (InDemiSummon)
        {
            if (SunflarePvE.CanUse(out act))
                return true;
            if (DeathflarePvE.CanUse(out act))
                return true;
        }

        // === 6. Necrotize / Fester / Painflare: spend Aetherflow stacks ===
        // Balance guide: "should always be used in raid buffs where possible"
        // Save for Searing Light windows when possible.
        // Odd-minute Necrotizes can be held for the next 2-min buff window.
        if (HasAetherflowStacks)
        {
            bool shouldSpend = false;

            // During Searing Light: always spend (maximize buff value)
            if (HasSearingLight)
                shouldSpend = true;

            // During demi-summon phase: spend (natural weave windows)
            if (InDemiSummon)
                shouldSpend = true;

            // If Energy Drain is coming off cooldown soon, spend to avoid overcap
            // Energy Drain CD is 60s; if within ~10s of next use, spend now
            if (EnergyDrainPvE.Cooldown.WillHaveOneCharge(10))
                shouldSpend = true;

            // If Searing Light is not learned, just spend on cooldown
            if (!SearingLightPvE.EnoughLevel)
                shouldSpend = true;

            if (shouldSpend)
            {
                // AoE: Painflare
                if (PainflarePvE.CanUse(out act))
                    return true;
                // ST: Necrotize (upgraded Fester)
                if (NecrotizePvE.CanUse(out act))
                    return true;
                if (FesterPvE.CanUse(out act))
                    return true;
            }
        }

        // === 7. Mountain Buster: Titan's Astral Flow ===
        // Triggered by Topaz Rite / Topaz Catastrophe (grants Titan's Favor).
        // Use immediately after each Titan GCD - it is a free oGCD.
        if (MountainBusterPvE.CanUse(out act))
            return true;

        return base.AttackAbility(nextGCD, out act);
    }

    #endregion

    #region GCD Logic

    [RotationDesc(ActionID.CrimsonCyclonePvE)]
    protected override bool MoveForwardGCD(out IAction? act)
    {
        // Crimson Cyclone is a gap-closer during Ifrit phase
        if (CrimsonCyclonePvE.CanUse(out act))
            return true;
        return base.MoveForwardGCD(out act);
    }

    protected override bool GeneralGCD(out IAction? act)
    {
        // ======================================================================
        // SMN GCD priority (120s cycle):
        //
        // Phase 1: Summon Carbuncle (if pet missing)
        // Phase 2: Demi-summon GCDs (highest priority - Astral Impulse etc.)
        // Phase 3: Summon Demi (Bahamut/Phoenix/Solar Bahamut)
        // Phase 4: Primal attunement GCDs (Gemshine / Precious Brilliance)
        // Phase 5: Primal favor abilities (Slipstream, Crimson Cyclone/Strike)
        // Phase 6: Summon primals
        // Phase 7: Filler (Ruin IV proc > Ruin III)
        // ======================================================================

        // === 0. Summon Carbuncle if pet is missing ===
        if (SummonCarbunclePvE.CanUse(out act))
            return true;

        // === 1. Demi-summon GCDs: press on cooldown during demi phase ===
        // These replace Ruin III while a demi is active.
        if (InDemiSummon)
        {
            if (TryDemiSummonGCD(out act))
                return true;
        }

        // === 2. Summon Demi: enter demi-summon phase ===
        // Priority: Solar Bahamut (100) > Phoenix (if phoenix ready) > Bahamut
        // Never delay demi-summon - losing a use is the biggest DPS loss.
        if (TrySummonDemi(out act))
            return true;

        // === 3. Primal Favor abilities (special GCDs from primal buffs) ===
        // These must be used before the favor status expires.

        // Crimson Strike: follow-up to Crimson Cyclone (must use immediately)
        if (HasCrimsonStrike && CrimsonStrikePvE.CanUse(out act))
            return true;

        // Slipstream: Garuda's Favor - 3s hardcast channeled GCD
        // Balance: cast during Garuda phase, creates a DoT ground effect
        if (HasGarudaFavor && TrySlipstream(out act))
            return true;

        // Crimson Cyclone: Ifrit's Favor - gap closer + melee combo starter
        // Balance: use after spending Ifrit attunement stacks, or during movement
        if (HasIfritFavor && TryCrimsonCyclone(out act))
            return true;

        // === 4. Primal attunement GCDs (Gemshine / Precious Brilliance) ===
        // Spend attunement stacks before they expire.
        if (AttunementCount > 0 && (InTitan || InGaruda || InIfrit))
        {
            if (TryPrimalAttunementGCD(out act))
                return true;
        }

        // === 5. Summon Primals ===
        // After demi resolves, cycle through primals in configured order.
        // Only summon when no demi is active and no attunement/favor remains.
        if (TrySummonPrimal(out act))
            return true;

        // === 6. Filler GCDs ===
        // Ruin IV (instant, from Further Ruin proc) > Ruin III (hardcast filler)
        // Balance: "aim to only cast one Ruin IV between each demi-primal.
        // Do not cast Ruin IV when a demi-primal is active."
        if (!InDemiSummon)
        {
            // Ruin IV: instant cast, higher potency, use procs between phases
            if (HasFurtherRuin && RuinIvPvE.CanUse(out act))
                return true;

            // Ruin III / Tri-disaster: standard filler
            if (TridisasterPvE.CanUse(out act))
                return true;
            if (RuinIiiPvE.CanUse(out act))
                return true;
            if (RuinIiPvE.CanUse(out act))
                return true;
            if (RuinPvE.CanUse(out act))
                return true;
        }

        return base.GeneralGCD(out act);
    }

    #endregion

    #region GCD Helper Methods

    /// <summary>
    /// Press the appropriate demi-summon GCD based on active demi phase.
    /// </summary>
    private bool TryDemiSummonGCD(out IAction? act)
    {
        act = null;

        // Solar Bahamut: Umbral Flare (AoE) > Umbral Impulse (ST)
        if (InSolarBahamut)
        {
            if (UmbralFlarePvE.CanUse(out act))
                return true;
            if (UmbralImpulsePvE.CanUse(out act))
                return true;
        }

        // Phoenix: Brand of Purgatory (AoE) > Fountain of Fire (ST)
        if (InPhoenix)
        {
            if (BrandOfPurgatoryPvE.CanUse(out act))
                return true;
            if (FountainOfFirePvE.CanUse(out act))
                return true;
        }

        // Bahamut: Astral Flare (AoE) > Astral Impulse (ST)
        if (InBahamut)
        {
            if (AstralFlarePvE.CanUse(out act))
                return true;
            if (AstralImpulsePvE.CanUse(out act))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Attempt to summon the appropriate demi (Solar Bahamut > Phoenix > Bahamut > Dreadwyrm > Aethercharge).
    /// </summary>
    private bool TrySummonDemi(out IAction? act)
    {
        act = null;

        // Don't summon demi if we're already in one or still have primal work to do
        if (InDemiSummon) return false;

        // Don't summon demi if we still have attunement stacks or primal favors active
        if (AttunementCount > 0 || HasIfritFavor || HasGarudaFavor || HasTitanFavor || HasCrimsonStrike)
            return false;

        // Solar Bahamut (level 100): highest priority demi
        if (SummonSolarBahamutPvE.EnoughLevel && IsSolarBahamutReady)
        {
            if (SummonSolarBahamutPvE.CanUse(out act))
                return true;
        }

        // Phoenix
        if (SummonPhoenixPvE.EnoughLevel && IsPhoenixReady)
        {
            if (SummonPhoenixPvE.CanUse(out act))
                return true;
        }

        // Bahamut
        if (SummonBahamutPvE.EnoughLevel && IsBahamutReady)
        {
            if (SummonBahamutPvE.CanUse(out act))
                return true;
        }

        // Pre-Bahamut: Dreadwyrm Trance
        if (!SummonBahamutPvE.EnoughLevel && DreadwyrmTrancePvE.EnoughLevel)
        {
            if (DreadwyrmTrancePvE.CanUse(out act))
                return true;
        }

        // Pre-Dreadwyrm: Aethercharge
        if (!DreadwyrmTrancePvE.EnoughLevel && AetherchargePvE.EnoughLevel)
        {
            if (AetherchargePvE.CanUse(out act))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Attempt to summon a primal in configured order.
    /// Only called when no demi is active and no attunement/favor remains.
    /// </summary>
    private bool TrySummonPrimal(out IAction? act)
    {
        act = null;

        // Guard: don't summon primal during demi phase or while attunement/favor is active
        if (InDemiSummon) return false;
        if (AttunementCount > 0) return false;
        if (HasIfritFavor || HasGarudaFavor || HasTitanFavor || HasCrimsonStrike) return false;

        // If no primals are ready, nothing to do (wait for demi cooldown)
        if (!AnyPrimalReady) return false;

        return PrimalOrder switch
        {
            PrimalOrderType.TitanGarudaIfrit =>
                TrySummonTitan(out act) || TrySummonGaruda(out act) || TrySummonIfrit(out act),
            PrimalOrderType.TitanIfritGaruda =>
                TrySummonTitan(out act) || TrySummonIfrit(out act) || TrySummonGaruda(out act),
            PrimalOrderType.GarudaTitanIfrit =>
                TrySummonGaruda(out act) || TrySummonTitan(out act) || TrySummonIfrit(out act),
            PrimalOrderType.IfritTitanGaruda =>
                TrySummonIfrit(out act) || TrySummonTitan(out act) || TrySummonGaruda(out act),
            _ =>
                TrySummonTitan(out act) || TrySummonGaruda(out act) || TrySummonIfrit(out act),
        };
    }

    private bool TrySummonTitan(out IAction? act)
    {
        act = null;
        if (!IsTitanReady) return false;
        // Try highest level version first
        return SummonTitanIiPvE.CanUse(out act)
            || SummonTitanPvE.CanUse(out act)
            || SummonTopazPvE.CanUse(out act);
    }

    private bool TrySummonGaruda(out IAction? act)
    {
        act = null;
        if (!IsGarudaReady) return false;
        return SummonGarudaIiPvE.CanUse(out act)
            || SummonGarudaPvE.CanUse(out act)
            || SummonEmeraldPvE.CanUse(out act);
    }

    private bool TrySummonIfrit(out IAction? act)
    {
        act = null;
        if (!IsIfritReady) return false;
        return SummonIfritIiPvE.CanUse(out act)
            || SummonIfritPvE.CanUse(out act)
            || SummonRubyPvE.CanUse(out act);
    }

    /// <summary>
    /// Use primal attunement GCDs (Gemshine / Precious Brilliance equivalents).
    /// These are the elemental variants of Gemshine (ST) and Precious Brilliance (AoE).
    /// </summary>
    private bool TryPrimalAttunementGCD(out IAction? act)
    {
        act = null;

        if (InTitan)
        {
            // Titan: all instant casts, ideal for burst/movement
            if (TopazCatastrophePvE.CanUse(out act)) return true;
            if (TopazRitePvE.CanUse(out act)) return true;
            if (TopazDisasterPvE.CanUse(out act)) return true;
            if (TopazRuinIiiPvE.CanUse(out act)) return true;
            if (TopazOutburstPvE.CanUse(out act)) return true;
            if (TopazRuinIiPvE.CanUse(out act)) return true;
            if (TopazRuinPvE.CanUse(out act)) return true;
        }

        if (InGaruda)
        {
            // Garuda: instant casts with 1.5s recast (fast but limited weave windows)
            if (EmeraldCatastrophePvE.CanUse(out act)) return true;
            if (EmeraldRitePvE.CanUse(out act)) return true;
            if (EmeraldDisasterPvE.CanUse(out act)) return true;
            if (EmeraldRuinIiiPvE.CanUse(out act)) return true;
            if (EmeraldOutburstPvE.CanUse(out act)) return true;
            if (EmeraldRuinIiPvE.CanUse(out act)) return true;
            if (EmeraldRuinPvE.CanUse(out act)) return true;
        }

        if (InIfrit)
        {
            // Ifrit: hardcast GCDs (Ruby Rite ~2.8s cast)
            if (RubyCatastrophePvE.CanUse(out act)) return true;
            if (RubyRitePvE.CanUse(out act)) return true;
            if (RubyDisasterPvE.CanUse(out act)) return true;
            if (RubyRuinIiiPvE.CanUse(out act)) return true;
            if (RubyOutburstPvE.CanUse(out act)) return true;
            if (RubyRuinIiPvE.CanUse(out act)) return true;
            if (RubyRuinPvE.CanUse(out act)) return true;
        }

        return false;
    }

    /// <summary>
    /// Handle Slipstream (Garuda's Favor GCD - 3s channel).
    /// </summary>
    private bool TrySlipstream(out IAction? act)
    {
        act = null;
        if (!HasGarudaFavor) return false;

        // If Swiftcast Slipstream is enabled and available, use with skipCastingCheck
        if (SwiftcastSlipstream)
        {
            return SlipstreamPvE.CanUse(out act, skipCastingCheck: true);
        }

        // Standard: hardcast Slipstream when not moving
        if (!IsMoving && SlipstreamPvE.CanUse(out act))
            return true;

        // If moving: try to use Ruin IV or attunement GCDs instead, Slipstream can wait
        // But if we have no attunement left, we just have to wait for a standstill
        return false;
    }

    /// <summary>
    /// Handle Crimson Cyclone (Ifrit's Favor gap-closer GCD).
    /// Balance: use after spending Ifrit attunement, or while moving.
    /// </summary>
    private bool TryCrimsonCyclone(out IAction? act)
    {
        act = null;
        if (!HasIfritFavor) return false;

        bool inRange = CrimsonCycloneAnywhere
            || CrimsonCyclonePvE.Target.Target.DistanceToPlayer() <= CrimsonCycloneDistance;

        // While moving: use as instant-cast movement GCD
        if (IsMoving && CrimsonCycloneMoving && inRange)
        {
            return CrimsonCyclonePvE.CanUse(out act);
        }

        // Standard: use after all attunement stacks are spent
        if (AttunementCount == 0 && inRange)
        {
            return CrimsonCyclonePvE.CanUse(out act);
        }

        return false;
    }

    #endregion

    #region Swiftcast Helper

    /// <summary>
    /// Manage Swiftcast usage for SMN.
    /// Priority: Resurrection > Slipstream (if configured) > Ruby hardcasts while moving.
    /// </summary>
    private bool TrySwiftcast(IAction nextGCD, out IAction? act)
    {
        act = null;
        if (!SwiftcastPvE.CanUse(out act))
            return false;

        // Swiftcast Resurrection
        if (SwiftcastRaise && nextGCD.IsTheSameTo(false, ResurrectionPvE))
            return true;

        // Swiftcast Slipstream (if configured)
        if (SwiftcastSlipstream && nextGCD.IsTheSameTo(false, SlipstreamPvE)
            && ElementalMasteryTrait.EnoughLevel && InGaruda)
            return true;

        // Swiftcast Ruby hardcasts while moving (mobility safety net)
        if (IsMoving && InIfrit && AttunementCount > 0
            && nextGCD.IsTheSameTo(false, RubyRitePvE, RubyCatastrophePvE,
                RubyRuinIiiPvE, RubyRuinIiPvE, RubyRuinPvE))
        {
            // Only if we don't have a Ruin IV proc to use instead
            if (!HasFurtherRuin)
                return true;
        }

        act = null;
        return false;
    }

    #endregion

    #region CanHealSingleSpell

    public override bool CanHealSingleSpell
    {
        get
        {
            // Only use healing GCDs if no healers are alive
            int aliveHealers = 0;
            var healers = PartyMembers.GetJobCategory(JobRole.Healer);
            foreach (var h in healers)
            {
                if (!h.IsDead) aliveHealers++;
            }
            return base.CanHealSingleSpell && aliveHealers == 0;
        }
    }

    #endregion
}
