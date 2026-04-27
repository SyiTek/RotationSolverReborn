using Dalamud.Game.ClientState.JobGauge.Types;
using ECommons.DalamudServices;

namespace RotationSolver.ExtraRotations.Ranged;

[Rotation("SezuraiBRD", CombatType.PvE, GameVersion = "7.41",
    Description = "Balance-aligned BRD with song cycle management, Radiant Finale burst windows, Pitch Perfect optimization, Empyreal Arrow drift prevention, and BMR timeline integration for proactive mitigation and downtime optimization.")]
[SourceCode(Path = "main/ExtraRotations/Ranged/SezuraiBRD.cs")]
[ExtraRotation]
public sealed class SezuraiBRD : BardRotation
{
    #region Config Options

    [Range(0f, 0.25f, ConfigUnitType.Percent)]
    [RotationConfig(CombatType.PvE, Name = "Action Ahead Override (0 = use global setting)")]
    public float ActionAheadOverride { get; set; } = 0f;

    [RotationConfig(CombatType.PvE, Name = "Auto Pot Usage (Gemdraught during 2-min burst)")]
    public bool BurstMed { get; set; } = true;

    [Range(80, 100, ConfigUnitType.None, 5)]
    [RotationConfig(CombatType.PvE, Name = "Apex Arrow minimum Soul Voice threshold")]
    public int ApexArrowThreshold { get; set; } = 80;

    [RotationConfig(CombatType.PvE, Name = "Hold Barrage for burst window (Raging Strikes)")]
    public bool HoldBarrageForBurst { get; set; } = true;

    [Range(2, 3, ConfigUnitType.None, 1)]
    [RotationConfig(CombatType.PvE, Name = "Pitch Perfect stacks before spending (2 or 3)")]
    public int PitchPerfectThreshold { get; set; } = 3;

    [RotationConfig(CombatType.PvE, Name = "BMR: Hold burst CDs for vulnerability window (within 30s)")]
    public bool BmrHoldBurstForVuln { get; set; } = true;

    [RotationConfig(CombatType.PvE, Name = "BMR: Dump oGCDs/gauge before downtime")]
    public bool BmrDumpBeforeDowntime { get; set; } = true;

    [RotationConfig(CombatType.PvE, Name = "BMR: Smart DoT management around downtime")]
    public bool BmrSmartDoTs { get; set; } = true;

    #endregion

    #region Burst State

    /// <summary>
    /// Whether the framework has burst mode enabled.
    /// </summary>
    private bool CanBurst => MergedStatus.HasFlag(AutoStatus.Burst);

    /// <summary>
    /// True when all three 2-minute buffs are active (Raging Strikes + Battle Voice + Radiant Finale).
    /// Falls back to whatever subset is available at current level.
    /// </summary>
    private bool InFullBurst =>
        (!BattleVoicePvE.EnoughLevel && !RadiantFinalePvE.EnoughLevel && HasRagingStrikes)
        || (!RadiantFinalePvE.EnoughLevel && HasRagingStrikes && HasBattleVoice)
        || (HasRagingStrikes && HasBattleVoice && HasRadiantFinale);

    /// <summary>
    /// True when any personal/party damage buff is active.
    /// </summary>
    private bool HasAnyBuff => HasRagingStrikes || HasBattleVoice || HasRadiantFinale;

    /// <summary>
    /// True when 2-minute CDs are approaching readiness (within 15s of Raging Strikes).
    /// Used to determine whether to hold resources for burst.
    /// </summary>
    private bool IsPreBurst => RagingStrikesPvE.EnoughLevel
        && RagingStrikesPvE.Cooldown.IsCoolingDown
        && !RagingStrikesPvE.Cooldown.HasOneCharge
        && RagingStrikesPvE.Cooldown.RecastTimeRemain <= 15;

    #endregion

    #region Song Helpers

    private static bool InWanderers => Song == Song.Wanderer;
    private static bool InMages => Song == Song.Mage;
    private static bool InArmys => Song == Song.Army;
    private static bool NoSong => Song == Song.None;

    /// <summary>
    /// Standard song cycle timing: swap out of each song at these remaining times.
    /// WM: leave at 3s remaining (42s uptime, 14 ticks possible)
    /// MB: leave at 3s remaining (42s uptime, 14 ticks possible)
    /// AP: leave at 12s remaining (33s uptime, transition song)
    /// </summary>
    private const float WM_EXIT_TIME = 3f;
    private const float MB_EXIT_TIME = 3f;
    private const float AP_EXIT_TIME = 12f;

    /// <summary>
    /// Whether the target has both DoTs applied (any version).
    /// </summary>
    private static bool TargetHasDoTs =>
        CurrentTarget?.HasStatus(true, StatusID.Windbite, StatusID.Stormbite) == true
        && CurrentTarget.HasStatus(true, StatusID.VenomousBite, StatusID.CausticBite);

    /// <summary>
    /// Whether DoTs are about to fall off the target.
    /// </summary>
    private static bool DoTsEnding =>
        CurrentTarget?.WillStatusEndGCD(1, 0.5f, true,
            StatusID.Windbite, StatusID.Stormbite,
            StatusID.VenomousBite, StatusID.CausticBite) ?? false;

    /// <summary>
    /// How many unique Coda (song types) we have stored for Radiant Finale.
    /// More Coda = stronger buff (1 = 2%, 2 = 4%, 3 = 6%).
    /// </summary>
    private static int CodaCount => Svc.Gauges.Get<BRDGauge>().Coda.Count(s => s != Song.None);

    /// <summary>
    /// Whether Resonant Arrow proc is available (granted by Barrage).
    /// </summary>
    private static bool HasResonantArrow =>
        StatusHelper.PlayerHasStatus(true, StatusID.ResonantArrowReady);

    /// <summary>
    /// Whether Radiant Encore proc is available (granted by Radiant Finale).
    /// </summary>
    private static bool HasRadiantEncore =>
        StatusHelper.PlayerHasStatus(true, StatusID.RadiantEncoreReady);

    #endregion

    #region BMR Helpers

    /// <summary>
    /// True when BMR reports downtime within the specified seconds.
    /// Always false when BMR is inactive (safe fallback).
    /// </summary>
    private bool BmrDowntimeWithin(float seconds)
        => BMRActive && BMRDowntimeIn is > 0 and < float.MaxValue && BMRDowntimeIn <= seconds;

    /// <summary>
    /// True when BMR reports a vulnerability window within the specified seconds.
    /// Always false when BMR is inactive (safe fallback).
    /// </summary>
    private bool BmrVulnWithin(float seconds)
        => BMRActive && BMRVulnerableIn is > 0 and < float.MaxValue && BMRVulnerableIn <= seconds;

    /// <summary>
    /// True when BMR reports a raidwide within the specified seconds.
    /// </summary>
    private bool BmrRaidwideWithin(float seconds)
        => BMRActive && BMRRaidwideIn is > 0 and < float.MaxValue && BMRRaidwideIn <= seconds;

    /// <summary>
    /// True when BMR reports a tankbuster within the specified seconds.
    /// </summary>
    private bool BmrTankbusterWithin(float seconds)
        => BMRActive && BMRTankbusterIn is > 0 and < float.MaxValue && BMRTankbusterIn <= seconds;

    /// <summary>
    /// True when BMR reports incoming damage (any type) within the specified seconds.
    /// </summary>
    private bool BmrDamageWithin(float seconds)
        => BMRActive && BMRDamageIn is > 0 and < float.MaxValue && BMRDamageIn <= seconds;

    #endregion

    #region Weave Helpers

    private static float LateWeaveWindow => WeaponTotal * 0.45f;
    private static bool EnoughWeaveTime => WeaponRemain > DataCenter.CalculatedActionAhead && WeaponRemain < WeaponTotal;
    private static bool CanLateWeave => WeaponRemain <= LateWeaveWindow && EnoughWeaveTime;

    #endregion

    #region UpdateInfo

    protected override void UpdateInfo()
    {
        DataCenter.RotationActionAheadOverride = ActionAheadOverride > 0f ? ActionAheadOverride : null;
    }

    #endregion

    #region Countdown & Opener
    // === BRD OPENER (7.4 Balance) ===
    // Pre-pull: Pot(-2s) -> Wanderer's Minuet (late weave, starts song cycle)
    // GCD1: Stormbite (DoT) -> GCD2: Caustic Bite (DoT)
    // -> Raging Strikes (weave) -> Empyreal Arrow (weave)
    // GCD3: Burst Shot -> Battle Voice (weave) -> Radiant Finale (weave)
    // GCD4: Burst Shot -> Barrage (weave) -> GCD5: Refulgent Arrow (forced proc)
    // -> Sidewinder (weave) -> Heartbreak Shot dump
    // -> Continue Burst Shot / Refulgent Arrow under buffs
    //
    // === BURST WINDOWS (120s cycle -- no distinct even/odd) ===
    // Raging Strikes + Battle Voice + Radiant Finale (triple stack, all 120s)
    // Barrage + Sidewinder + Empyreal Arrow + Heartbreak Shot dump
    // Apex Arrow at 80+ Soul Voice gauge under full buffs
    // Blast Arrow follow-up under remaining buffs
    //
    // === FILLER / SUSTAIN ===
    // Song cycle: Wanderer's Minuet (43s) -> Mage's Ballad (43s) -> Army's Paeon (34s)
    // DoTs: refresh Stormbite + Caustic Bite at <=3s remaining (snapshot under buffs when possible)
    // Empyreal Arrow: strictly on cooldown, never hold
    // Pitch Perfect: spend at 3 stacks in WM, or 2+ if WM is about to end
    // Heartbreak Shot: use charges to avoid overcap (3 max), pool for burst
    // Apex Arrow: fire at 80+ Soul Voice, or 100 for Blast Arrow follow-up
    //
    // === BMR INTEGRATION ===
    // Troubadour: party 10% mit, proactive use 5s before raidwide
    // Nature's Minne: 20% heal potency buff, proactive on tank 5s before tankbuster
    // DoT management: don't refresh if downtime < 5s, force-refresh if 5-15s away and DoTs would fall off
    // Burst dump: spend Apex Arrow, Heartbreak charges, procs before downtime
    // Burst hold: delay Raging/BV/RF if vulnerability window within 30s
    // Song management: don't start new song if downtime < 5s

    protected override IAction? CountDownAction(float remainTime)
    {
        // Medicine at ~2s prepull
        if (BurstMed && remainTime <= 2f && remainTime > 1f && UseBurstMedicine(out var act))
            return act;

        return base.CountDownAction(remainTime);
    }

    #endregion

    #region Defense & Utility

    [RotationDesc(ActionID.TroubadourPvE)]
    protected override bool DefenseAreaAbility(IAction nextGCD, out IAction? act)
    {
        // === BMR-aware: Troubadour when raidwide imminent ===
        // Troubadour: 15s party 10% damage reduction, 90s CD.
        // Proactive use 5s before raidwide is optimal (covers the hit + some aftermath).
        // Override burst-skip for genuine raidwides -- 10% party mit > marginal personal DPS.
        bool rwSoon = BmrRaidwideWithin(5f);

        if (rwSoon)
        {
            if (TroubadourPvE.CanUse(out act))
                return true;
            return base.DefenseAreaAbility(nextGCD, out act);
        }

        // Non-BMR: skip during burst to avoid clipping weave-heavy windows
        if (InFullBurst)
            return base.DefenseAreaAbility(nextGCD, out act);

        if (TroubadourPvE.CanUse(out act))
            return true;

        return base.DefenseAreaAbility(nextGCD, out act);
    }

    [RotationDesc(ActionID.NaturesMinnePvE)]
    protected override bool DefenseSingleAbility(IAction nextGCD, out IAction? act)
    {
        // === BMR-aware: Nature's Minne for tankbusters ===
        // Nature's Minne: 15s, 20% healing received buff on target.
        // For TBs: apply to tank 5s before hit so healers/tank self-heals get the bonus.
        // For RWs: apply to self/party to boost healer AoE heals post-hit.
        bool tbSoon = BmrTankbusterWithin(5f);

        if (tbSoon)
        {
            if (NaturesMinnePvE.CanUse(out act))
                return true;
        }

        // Also use for raidwides if Troubadour is on CD (stacks with Troub for extra survivability)
        bool rwSoon = BmrRaidwideWithin(5f);
        if (rwSoon && TroubadourPvE.Cooldown.IsCoolingDown)
        {
            if (NaturesMinnePvE.CanUse(out act))
                return true;
        }

        // Non-BMR fallback: let framework handle
        if (!BMRActive)
        {
            if (NaturesMinnePvE.CanUse(out act))
                return true;
        }

        return base.DefenseSingleAbility(nextGCD, out act);
    }

    [RotationDesc(ActionID.NaturesMinnePvE)]
    protected override bool HealSingleAbility(IAction nextGCD, out IAction? act)
    {
        // BMR-aware: Nature's Minne before raidwide to boost healer heals
        // 15s duration, 20% heal potency buff on target -- time it so healers benefit
        bool rwSoon = BmrRaidwideWithin(8f);

        if (rwSoon && NaturesMinnePvE.CanUse(out act))
            return true;

        // Non-BMR: use when framework triggers heal
        if (!BMRActive && NaturesMinnePvE.CanUse(out act))
            return true;

        // Self-healing: Second Wind when low HP, especially before incoming damage
        bool damageSoon = BmrDamageWithin(5f) || BmrRaidwideWithin(5f);
        if (damageSoon && Player?.GetHealthRatio() < 0.6f && SecondWindPvE.CanUse(out act))
            return true;

        if (SecondWindPvE.CanUse(out act))
            return true;

        return base.HealSingleAbility(nextGCD, out act);
    }

    [RotationDesc(ActionID.TheWardensPaeanPvE)]
    protected override bool DispelAbility(IAction nextGCD, out IAction? act)
    {
        if (TheWardensPaeanPvE.CanUse(out act))
            return true;

        return base.DispelAbility(nextGCD, out act);
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
        if (HeadGrazePvE.CanUse(out act))
            return true;
        return base.InterruptAbility(nextGCD, out act);
    }

    #endregion

    #region Emergency Ability

    protected override bool EmergencyAbility(IAction nextGCD, out IAction? act)
    {
        act = null;

        if (!InCombat)
            return base.EmergencyAbility(nextGCD, out act);

        // === MEDICINE ===
        // Use during full 2-minute burst window
        if (BurstMed && InFullBurst
            && !StatusHelper.PlayerHasStatus(true, StatusID.Medicated)
            && UseBurstMedicine(out act))
        {
            return true;
        }

        // === EMPYREAL ARROW: highest priority oGCD - NEVER let it drift ===
        // 15s cooldown, generates Repertoire + Soul Voice.
        // Must be used on CD regardless of song or burst state.
        if (EmpyrealArrowPvE.CanUse(out act))
            return true;

        // === PITCH PERFECT: use at 3 stacks, or any stacks before WM ends ===
        if (TryUsePitchPerfect(out act))
            return true;

        return base.EmergencyAbility(nextGCD, out act);
    }

    #endregion

    #region Song Cycle (GeneralAbility)

    protected override bool GeneralAbility(IAction nextGCD, out IAction? act)
    {
        act = null;

        if (!InCombat || !EnoughWeaveTime)
            return base.GeneralAbility(nextGCD, out act);

        // === SONG CYCLE: WM -> MB -> AP ===
        // The song cycle is BRD's most important mechanic. Maintain 100% song uptime.
        // Standard cycle: WM 42s -> MB 42s -> AP 33s = ~117s per cycle (close to 120s burst).
        //
        // BMR: Don't start a new song if downtime is very imminent (<5s).
        // The song's value comes from procs over time -- starting one just to lose it
        // during downtime wastes the CD and misaligns the song cycle post-downtime.
        // Exception: always start WM (burst song) even near downtime for Pitch Perfect value.

        // Priority 1: Start first song (Wanderer's Minuet opens the fight)
        if (NoSong)
        {
            // BMR: Don't start non-WM songs if downtime is very imminent
            // WM is always worth starting (Pitch Perfect has immediate value)
            if (BmrDumpBeforeDowntime && BmrDowntimeWithin(5f))
            {
                // Still start WM for PP value
                if (TheWanderersMinuetPvE.CanUse(out act))
                    return true;
                // Skip MB/AP if downtime imminent -- they need time to generate value
                return base.GeneralAbility(nextGCD, out act);
            }

            if (TryStartSong(out act))
                return true;
        }

        // Priority 2: Transition songs at appropriate remaining times
        // WM -> MB at 3s remaining (can't proc in last 3s)
        if (InWanderers && SongEndAfter(WM_EXIT_TIME))
        {
            // Spend remaining Pitch Perfect stacks before leaving WM
            if (Repertoire > 0 && PitchPerfectPvE.CanUse(out act))
                return true;

            // BMR: If downtime is imminent, don't transition to MB -- let WM expire
            // and restart song cycle post-downtime with fresh alignment
            if (BmrDumpBeforeDowntime && BmrDowntimeWithin(5f))
                return base.GeneralAbility(nextGCD, out act);

            if (MagesBalladPvE.CanUse(out act))
                return true;
        }

        // MB -> AP at 3s remaining
        if (InMages && SongEndAfter(MB_EXIT_TIME))
        {
            // BMR: Skip transition if downtime is imminent
            if (BmrDumpBeforeDowntime && BmrDowntimeWithin(5f))
                return base.GeneralAbility(nextGCD, out act);

            if (ArmysPaeonPvE.CanUse(out act))
                return true;
        }

        // AP -> WM at 12s remaining (transition song, minimize time here)
        if (InArmys && SongEndAfter(AP_EXIT_TIME))
        {
            if (TheWanderersMinuetPvE.CanUse(out act))
                return true;
        }

        // Fallback: if current song expired and no transition happened, start best available
        if (NoSong)
        {
            if (TryStartSong(out act))
                return true;
        }

        return base.GeneralAbility(nextGCD, out act);
    }

    #endregion

    #region Attack Ability (oGCDs)

    protected override bool AttackAbility(IAction nextGCD, out IAction? act)
    {
        act = null;

        if (!EnoughWeaveTime)
            return base.AttackAbility(nextGCD, out act);

        // === BMR: DUMP oGCDs BEFORE DOWNTIME ===
        // When downtime is imminent (<=10s), aggressively spend all oGCD charges
        // and resources that would be wasted during the untargetable phase.
        // Heartbreak Shot charges, Sidewinder, and Barrage should all go out.
        if (BmrDumpBeforeDowntime && BmrDowntimeWithin(10f))
        {
            // Sidewinder: high potency single oGCD, don't let it sit during downtime
            if (SidewinderPvE.CanUse(out act))
                return true;

            // Barrage: fire it even without RS if it would be wasted
            // (gives Resonant Arrow proc to use immediately)
            if (BarragePvE.CanUse(out act))
                return true;

            // Heartbreak Shot dump: spend all charges aggressively
            if (RainOfDeathPvE.CanUse(out act, usedUp: true))
                return true;
            if (HeartbreakShotPvE.CanUse(out act, usedUp: true))
                return true;
            if (BloodletterPvE.CanUse(out act, usedUp: true))
                return true;
        }

        // === 2-MINUTE BURST BUFFS (Radiant Finale -> Battle Voice -> Raging Strikes) ===
        // Apply in this order so Radiant Finale snapshots all 3 Coda, then BV + RS stack on top.
        // All three should be used during Wanderer's Minuet for Pitch Perfect value.
        //
        // BMR: Don't start burst if downtime is too close (< 20s).
        // Full burst window needs ~20s to get all GCDs under buffs.
        // Also hold burst for upcoming vulnerability window if configured.
        if (CanBurst && InCombat && HasHostilesInRange)
        {
            // BMR: Block burst activation if downtime is imminent
            // The 20s burst window would be truncated, wasting the 120s CDs.
            // Exception: if buffs are already rolling, keep spending (don't waste active buffs).
            bool bmrBlockBurst = BmrDumpBeforeDowntime && BmrDowntimeWithin(20f) && !HasAnyBuff;

            // BMR: Hold burst for upcoming vulnerability window
            // If boss becomes vulnerable within 30s, delay burst for the damage bonus.
            // Don't hold if vuln is already happening (< 3s) -- that means it's active now.
            bool bmrHoldForVuln = BmrHoldBurstForVuln
                && BmrVulnWithin(30f)
                && BMRVulnerableIn > 3f
                && RagingStrikesPvE.Cooldown.HasOneCharge;

            if (!bmrBlockBurst && !bmrHoldForVuln)
            {
                // Radiant Finale: use when we have Coda stored (ideally 3 for 6% buff)
                // Gate behind WM being active for proper song cycle alignment.
                if (RadiantFinalePvE.EnoughLevel && InWanderers)
                {
                    // First burst: use when BV is up (opener sequence)
                    // Subsequent bursts: use when BV is about to come off CD
                    if ((HasBattleVoice || BattleVoicePvE.Cooldown.WillHaveOneCharge(WeaponTotal))
                        && RadiantFinalePvE.CanUse(out act))
                        return true;
                }

                // Battle Voice: 20s party buff, use after/with Radiant Finale
                if (BattleVoicePvE.EnoughLevel && InWanderers)
                {
                    bool rfReady = !RadiantFinalePvE.EnoughLevel
                        || HasRadiantFinale
                        || IsLastAbility(ActionID.RadiantFinalePvE);

                    if (rfReady && BattleVoicePvE.CanUse(out act))
                        return true;
                }

                // Raging Strikes: personal 15% damage buff, use after party buffs are up
                if (RagingStrikesPvE.EnoughLevel)
                {
                    bool partyBuffsUp =
                        (!BattleVoicePvE.EnoughLevel && !RadiantFinalePvE.EnoughLevel)
                        || (!RadiantFinalePvE.EnoughLevel && HasBattleVoice)
                        || (HasBattleVoice && HasRadiantFinale);

                    if (partyBuffsUp && RagingStrikesPvE.CanUse(out act))
                        return true;
                }
            }
        }

        // === BARRAGE: guaranteed Refulgent Arrow proc ===
        // Use during Raging Strikes for maximum value.
        // Grants Resonant Arrow Ready as follow-up.
        // Don't use if Hawk's Eye is already active (would waste the forced proc).
        // Don't use at 3 Repertoire stacks (EA might overflow).
        if (TryUseBarrage(out act))
            return true;

        // === SIDEWINDER: 60s oGCD, use on CD ===
        // Ideally lands during Raging Strikes. Don't hold excessively.
        if (TryUseSidewinder(out act))
            return true;

        // === HEARTBREAK SHOT / BLOODLETTER / RAIN OF DEATH: charge-based oGCDs ===
        // Spend charges freely to prevent overcap. Dump during burst.
        // Rain of Death for AoE (3+ targets), Heartbreak Shot for ST.
        if (TryUseHeartbreakShot(out act))
            return true;

        return base.AttackAbility(nextGCD, out act);
    }

    #endregion

    #region GCD Logic

    protected override bool GeneralGCD(out IAction? act)
    {
        // === SPECIAL PROC GCDs (use before they expire) ===

        // Blast Arrow: follow-up to Apex Arrow, high potency. Use immediately.
        if (BlastArrowPvEReady && BlastArrowPvE.CanUse(out act))
            return true;

        // Resonant Arrow: proc from Barrage, use during burst.
        if (HasResonantArrow && ResonantArrowPvE.CanUse(out act))
            return true;

        // Radiant Encore: proc from Radiant Finale, use during burst.
        // Potency scales with Coda count at time of Finale cast.
        if (HasRadiantEncore && RadiantEncorePvE.CanUse(out act))
            return true;

        // === BMR: PRE-DOWNTIME GCD OPTIMIZATION ===
        // When downtime is imminent, prioritize high-potency GCDs over DoT refresh.
        // Apex Arrow (600 potency at 100 gauge) + Blast Arrow > Iron Jaws > filler.
        // Fire Apex even at lower thresholds if it would be lost to downtime.
        if (BmrDumpBeforeDowntime && BmrDowntimeWithin(8f))
        {
            // Apex Arrow: dump Soul Voice before downtime (gauge resets are bad)
            // Lower threshold for pre-downtime dump: 20+ gauge (minimum for Apex to fire)
            if (SoulVoice >= 20 && !HasBarrage && ApexArrowPvE.CanUse(out act))
                return true;
        }

        // === APEX ARROW: Soul Voice gauge spender ===
        // Fire at threshold (default 80+, ideally 100 during burst).
        // During burst: use at 80+ to fit under buffs.
        // Outside burst: use at 100 to prevent overcap, or 80+ during Mage's Ballad.
        if (TryUseApexArrow(out act))
            return true;

        // === IRON JAWS: DoT refresh ===
        // Refreshes both Caustic Bite and Stormbite. Requires both to be active.
        // Priority: during burst (snapshot raid buffs), or when DoTs are about to expire.
        // BMR: Smart DoT management around downtime (see TryUseIronJaws).
        if (TryUseIronJaws(out act))
            return true;

        // === AOE GCDs (3+ targets) ===
        // Shadowbite (proc) > Ladonsbite (filler)
        if (ShadowbitePvE.CanUse(out act))
            return true;
        if (LadonsbitePvE.CanUse(out act))
            return true;

        // === SINGLE TARGET GCDs ===

        // Refulgent Arrow: proc-based (Hawk's Eye or Barrage), higher potency than Burst Shot
        if (RefulgentArrowPvE.CanUse(out act))
            return true;

        // Wide Volley: AoE proc consumer (lower level version of Shadowbite)
        if (WideVolleyPvE.CanUse(out act))
            return true;

        // Burst Shot: standard filler GCD, has 35% chance to grant Hawk's Eye
        if (BurstShotPvE.CanUse(out act))
            return true;

        // === DOT APPLICATION (initial application, before Iron Jaws is available) ===
        // Stormbite first (higher initial potency), then Caustic Bite
        // BMR: Skip initial DoT application if downtime < 5s (DoTs wouldn't tick enough)
        if (!IronJawsPvE.EnoughLevel || !TargetHasDoTs)
        {
            // BMR: Don't apply fresh DoTs if boss goes untargetable very soon
            if (BmrSmartDoTs && BmrDowntimeWithin(5f))
            {
                // Skip DoT application, fall through to fillers
            }
            else
            {
                if (StormbitePvE.CanUse(out act))
                    return true;
                if (CausticBitePvE.CanUse(out act))
                    return true;

                // Low level: Windbite and Venomous Bite
                if (WindbitePvE.CanUse(out act))
                    return true;
                if (VenomousBitePvE.CanUse(out act))
                    return true;
            }
        }

        // Low level fillers
        if (StraightShotPvE.CanUse(out act))
            return true;
        if (HeavyShotPvE.CanUse(out act))
            return true;

        // AoE low level
        if (QuickNockPvE.CanUse(out act))
            return true;

        return base.GeneralGCD(out act);
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Start the best available song. Priority: WM > MB > AP.
    /// WM is preferred because it enables Pitch Perfect and aligns with burst.
    /// </summary>
    private bool TryStartSong(out IAction? act)
    {
        // Prefer WM (burst song)
        if (TheWanderersMinuetPvE.CanUse(out act))
            return true;
        // MB as second choice (Bloodletter resets)
        if (MagesBalladPvE.CanUse(out act))
            return true;
        // AP as last resort (speed buff, transition song)
        if (ArmysPaeonPvE.CanUse(out act))
            return true;

        act = null;
        return false;
    }

    /// <summary>
    /// Pitch Perfect management during Wanderer's Minuet.
    /// - Always use at 3 stacks (max potency)
    /// - Use at 2 stacks if Empyreal Arrow is about to come off CD (would overflow to 4)
    /// - Use any remaining stacks before WM ends (can't use PP outside WM)
    /// - BMR: Dump any PP stacks before downtime (lose them if WM expires during transition)
    /// </summary>
    private bool TryUsePitchPerfect(out IAction? act)
    {
        act = null;

        if (!InWanderers || Repertoire == 0)
            return false;

        if (!PitchPerfectPvE.CanUse(out act))
            return false;

        // At or above threshold: fire (default 3 = max potency)
        if (Repertoire >= PitchPerfectThreshold)
            return true;

        // Below threshold but EA coming soon: fire to prevent overflow
        // EA grants a Repertoire proc, so we need to make room
        if (Repertoire >= PitchPerfectThreshold - 1 && EmpyrealArrowPvE.Cooldown.WillHaveOneChargeGCD(1))
            return true;

        // Any stacks when WM is about to end: spend before song swap
        if (Repertoire > 0 && SongEndAfter(WM_EXIT_TIME + WeaponTotal))
            return true;

        // BMR: Dump any PP stacks before downtime
        // During downtime WM timer pauses but we can't target -- stacks are effectively wasted.
        // Spend at any count if downtime is imminent.
        if (BmrDumpBeforeDowntime && BmrDowntimeWithin(5f) && Repertoire > 0)
            return true;

        return false;
    }

    /// <summary>
    /// Barrage usage: guarantees a Refulgent Arrow and grants Resonant Arrow Ready.
    /// - Use during Raging Strikes for burst value
    /// - Don't use when Hawk's Eye is active (wastes the guaranteed proc, it would be consumed first)
    /// - Don't use at 3 Repertoire (EA proc might overflow)
    /// - BMR: Don't waste Barrage if downtime is imminent and we can't get value
    /// </summary>
    private bool TryUseBarrage(out IAction? act)
    {
        act = null;

        // BMR: Don't Barrage if downtime is < 3s (can't even use the Refulgent + Resonant)
        // The pre-downtime dump path in AttackAbility handles the aggressive dump case.
        if (BmrDumpBeforeDowntime && BmrDowntimeWithin(3f))
            return false;

        // Hold for Raging Strikes if configured and RS is available soon
        if (HoldBarrageForBurst && RagingStrikesPvE.EnoughLevel)
        {
            if (!HasRagingStrikes && RagingStrikesPvE.Cooldown.WillHaveOneCharge(15))
                return false;
            if (!HasRagingStrikes)
                return false;
        }

        // Don't Barrage if Hawk's Eye proc is active (would waste guaranteed Refulgent)
        // The Barrage buff itself also satisfies the Refulgent requirement, but
        // if we already have Hawk's Eye, the next GCD is already Refulgent -
        // we want Barrage on a Burst Shot that WOULD have been non-proc.
        // Exception: late in Raging Strikes, just use it.
        if (HasHawksEye && HasRagingStrikes
            && !StatusHelper.PlayerWillStatusEnd(5f, true, StatusID.RagingStrikes))
            return false;

        // Don't use at 3 Repertoire stacks (EA overflow risk)
        if (Repertoire >= 3 && InWanderers)
            return false;

        return BarragePvE.CanUse(out act);
    }

    /// <summary>
    /// Sidewinder: 60s cooldown, high potency oGCD.
    /// Use on cooldown. Ideally during Raging Strikes but don't hold excessively.
    /// BMR: Don't hold for burst if downtime would eat the CD.
    /// </summary>
    private bool TryUseSidewinder(out IAction? act)
    {
        act = null;

        // BMR: If downtime is within 10s, don't hold Sidewinder for burst --
        // it's better to use it now than lose it during the untargetable phase.
        if (BmrDumpBeforeDowntime && BmrDowntimeWithin(10f))
            return SidewinderPvE.CanUse(out act);

        // Hold briefly for burst if RS is coming soon
        if (RagingStrikesPvE.EnoughLevel
            && !HasRagingStrikes
            && RagingStrikesPvE.Cooldown.WillHaveOneCharge(5))
            return false;

        return SidewinderPvE.CanUse(out act);
    }

    /// <summary>
    /// Heartbreak Shot / Rain of Death charge management.
    /// 3 max charges (enhanced trait). Shares recast with Rain of Death.
    /// - Spend during burst (dump all charges under buffs)
    /// - Prevent overcap (use when at max charges)
    /// - Rain of Death for 3+ AoE targets
    /// - BMR: Aggressively dump before downtime
    /// </summary>
    private bool TryUseHeartbreakShot(out IAction? act)
    {
        act = null;

        bool atMaxCharges = BloodletterPvE.Cooldown.CurrentCharges >= BloodletterMax;
        bool willOvercap = BloodletterPvE.Cooldown.CurrentCharges >= BloodletterMax - 1
            && InMages; // MB resets give extra charges

        // BMR: Dump charges before downtime (they'd recharge during downtime anyway)
        bool bmrDump = BmrDumpBeforeDowntime && BmrDowntimeWithin(10f)
            && BloodletterPvE.Cooldown.CurrentCharges > 0;

        bool shouldSpend = InFullBurst || HasRagingStrikes || atMaxCharges || willOvercap || bmrDump;

        if (!shouldSpend)
            return false;

        bool usedUp = InFullBurst || atMaxCharges || bmrDump;

        // AoE: Rain of Death
        if (RainOfDeathPvE.CanUse(out act, usedUp: usedUp))
            return true;

        // ST: Heartbreak Shot (upgraded Bloodletter)
        if (HeartbreakShotPvE.CanUse(out act, usedUp: usedUp))
            return true;

        // Low level fallback
        if (BloodletterPvE.CanUse(out act, usedUp: usedUp))
            return true;

        return false;
    }

    /// <summary>
    /// Apex Arrow: fires the Soul Voice gauge.
    /// - 80+ gauge minimum for decent potency (scales 100-600 from 20-100 gauge)
    /// - 100 gauge for maximum potency
    /// - During burst: fire at 80+ to fit under buffs
    /// - Outside burst: fire at 100 to prevent waste, or 80+ during Mage's Ballad
    /// - Grants Blast Arrow Ready as follow-up
    /// - BMR: Fire at lower threshold before downtime (gauge lost during untargetable)
    /// </summary>
    private bool TryUseApexArrow(out IAction? act)
    {
        act = null;

        // BMR pre-downtime dump is handled in GeneralGCD (fires at 20+ gauge)
        // This method handles normal Apex Arrow logic.
        if (SoulVoice < ApexArrowThreshold)
            return false;

        // Don't Apex if Barrage is active (use Refulgent first)
        if (HasBarrage)
            return false;

        // Don't use if DoTs are about to fall off (Iron Jaws is more urgent)
        // BMR exception: if downtime is imminent, DoTs don't matter
        if (DoTsEnding && !BmrDowntimeWithin(8f))
            return false;

        if (!ApexArrowPvE.CanUse(out act))
            return false;

        // During burst: fire at threshold to get it under buffs
        if (HasRagingStrikes || InFullBurst)
            return true;

        // At 100 gauge: always fire to prevent waste (gauge can't exceed 100)
        if (SoulVoice >= 100)
            return true;

        // During Mage's Ballad: fire at threshold (good time, between bursts)
        // Aim for around halfway through MB (21-19s on timer)
        if (InMages && SongTime <= 22f && SoulVoice >= ApexArrowThreshold)
            return true;

        // BMR: Fire at threshold if downtime is approaching (15s)
        // Better to get Apex + Blast Arrow value than hold and lose gauge
        if (BmrDumpBeforeDowntime && BmrDowntimeWithin(15f) && SoulVoice >= ApexArrowThreshold)
            return true;

        // Hold for upcoming burst if close (within 25s of BV)
        // BMR override: don't hold if downtime would eat the burst anyway
        if (BattleVoicePvE.EnoughLevel && BattleVoicePvE.Cooldown.WillHaveOneCharge(25))
        {
            if (!BmrDowntimeWithin(25f))
                return false;
        }

        // Otherwise fire at threshold to prevent overcap during next WM
        if (SoulVoice >= ApexArrowThreshold)
            return true;

        return false;
    }

    /// <summary>
    /// Iron Jaws: refreshes both DoTs (Caustic Bite + Stormbite) in a single GCD.
    /// - Refresh when DoTs have less than ~3s remaining
    /// - Snapshot during burst (refresh early under raid buffs for stronger DoT ticks)
    /// - Never let DoTs fall off
    /// - BMR-aware DoT management:
    ///   - Don't refresh if downtime < 5s (DoTs wasted on untargetable boss)
    ///   - Force refresh if downtime is 15-30s away and DoTs would fall off during downtime
    ///     (want strong DoTs ticking when boss returns, if DoTs persist through transition)
    ///   - Skip refresh entirely if downtime < 5s (boss gone, DoTs meaningless)
    /// </summary>
    private bool TryUseIronJaws(out IAction? act)
    {
        act = null;

        if (!IronJawsPvE.EnoughLevel)
            return false;

        // Must have both DoTs active on target to refresh
        if (!TargetHasDoTs)
            return false;

        // === BMR SMART DOT MANAGEMENT ===
        if (BmrSmartDoTs && BMRActive && BMRDowntimeIn is > 0 and < float.MaxValue)
        {
            float dtIn = BMRDowntimeIn;

            // Don't refresh if downtime < 5s -- DoTs would tick on an untargetable boss.
            // Use remaining GCDs on Burst Shot/Refulgent (direct damage) instead.
            if (dtIn <= 5f)
                return false;

            // Downtime 5-15s away: refresh DoTs IF they would fall off before boss returns.
            // This ensures DoTs are ticking when boss comes back.
            // Check if DoTs will expire within the downtime window.
            if (dtIn is > 5f and <= 15f && DoTsEnding)
            {
                if (IronJawsPvE.CanUse(out act))
                    return true;
            }
        }
        else if (BmrSmartDoTs && !BMRActive)
        {
            // Non-BMR: original logic (no downtime awareness)
        }

        // Snapshot during burst: refresh with ~4-7s remaining while buffed
        // This gives us stronger DoT ticks for the full 45s duration
        if (InFullBurst && !BlastArrowPvEReady)
        {
            // Only snapshot if buffs are about to end (last GCD window of burst)
            if (StatusHelper.PlayerWillStatusEndGCD(1, 1, true,
                StatusID.BattleVoice, StatusID.RadiantFinale, StatusID.RagingStrikes))
            {
                if (IronJawsPvE.CanUse(out act))
                    return true;
            }
        }

        // Normal refresh: when DoTs are about to expire
        if (IronJawsPvE.CanUse(out act))
            return true;

        return false;
    }

    #endregion

    #region Debug Display

    public override void DisplayRotationStatus()
    {
        ImGui.Text("--- Song State ---");
        ImGui.Text($"Song: {Song} | SongTime: {SongTime:F1}s");
        ImGui.Text($"Repertoire: {Repertoire} | SoulVoice: {SoulVoice}");
        ImGui.Text($"LastSong: {LastSong} | CodaCount: {CodaCount}");
        ImGui.Text($"NoSong: {NoSong}");

        ImGui.Text("--- Burst ---");
        ImGui.Text($"CanBurst: {CanBurst}");
        ImGui.Text($"InFullBurst: {InFullBurst}");
        ImGui.Text($"IsPreBurst: {IsPreBurst}");
        ImGui.Text($"HasAnyBuff: {HasAnyBuff}");
        ImGui.Text($"HasRagingStrikes: {HasRagingStrikes}");
        ImGui.Text($"HasBattleVoice: {HasBattleVoice}");
        ImGui.Text($"HasRadiantFinale: {HasRadiantFinale}");
        ImGui.Text($"Medicated: {StatusHelper.PlayerHasStatus(true, StatusID.Medicated)}");

        ImGui.Text("--- Procs ---");
        ImGui.Text($"HasHawksEye: {HasHawksEye}");
        ImGui.Text($"HasBarrage: {HasBarrage}");
        ImGui.Text($"HasResonantArrow: {HasResonantArrow}");
        ImGui.Text($"HasRadiantEncore: {HasRadiantEncore}");
        ImGui.Text($"BlastArrowReady: {BlastArrowPvEReady}");

        ImGui.Text("--- Cooldowns ---");
        ImGui.Text($"RS: {(RagingStrikesPvE.Cooldown.IsCoolingDown ? $"{RagingStrikesPvE.Cooldown.RecastTimeRemain:F1}s" : "Ready")}");
        ImGui.Text($"BV: {(BattleVoicePvE.Cooldown.IsCoolingDown ? $"{BattleVoicePvE.Cooldown.RecastTimeRemain:F1}s" : "Ready")}");
        ImGui.Text($"RF: {(RadiantFinalePvE.Cooldown.IsCoolingDown ? $"{RadiantFinalePvE.Cooldown.RecastTimeRemain:F1}s" : "Ready")}");
        ImGui.Text($"EA: {(EmpyrealArrowPvE.Cooldown.IsCoolingDown ? $"{EmpyrealArrowPvE.Cooldown.RecastTimeRemain:F1}s" : "Ready")}");
        ImGui.Text($"SW: {(SidewinderPvE.Cooldown.IsCoolingDown ? $"{SidewinderPvE.Cooldown.RecastTimeRemain:F1}s" : "Ready")}");
        ImGui.Text($"Barrage: {(BarragePvE.Cooldown.IsCoolingDown ? $"{BarragePvE.Cooldown.RecastTimeRemain:F1}s" : "Ready")}");
        ImGui.Text($"BL Charges: {BloodletterPvE.Cooldown.CurrentCharges}/{BloodletterMax}");
        ImGui.Text($"Troubadour: {(TroubadourPvE.Cooldown.IsCoolingDown ? $"{TroubadourPvE.Cooldown.RecastTimeRemain:F1}s" : "Ready")}");
        ImGui.Text($"Minne: {(NaturesMinnePvE.Cooldown.IsCoolingDown ? $"{NaturesMinnePvE.Cooldown.RecastTimeRemain:F1}s" : "Ready")}");

        ImGui.Text("--- Weave ---");
        ImGui.Text($"WeaponRemain: {WeaponRemain:F2}s | WeaponTotal: {WeaponTotal:F2}s");
        ImGui.Text($"EnoughWeaveTime: {EnoughWeaveTime}");
        ImGui.Text($"CanLateWeave: {CanLateWeave}");

        ImGui.Text("--- BMR Timeline ---");
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
            ImGui.Text($"-- BMR Decision State --");
            ImGui.Text($"DowntimeWithin5: {BmrDowntimeWithin(5f)} | DT10: {BmrDowntimeWithin(10f)} | DT20: {BmrDowntimeWithin(20f)}");
            ImGui.Text($"RaidwideWithin5: {BmrRaidwideWithin(5f)} | TBWithin5: {BmrTankbusterWithin(5f)}");
            ImGui.Text($"VulnWithin30: {BmrVulnWithin(30f)}");
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
