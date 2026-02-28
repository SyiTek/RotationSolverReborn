using Dalamud.Game.ClientState.JobGauge.Types;
using ECommons.DalamudServices;

namespace RotationSolver.ExtraRotations.Ranged;

[Rotation("SezuraiBRD", CombatType.PvE, GameVersion = "7.41",
    Description = "Balance-aligned BRD with song cycle management, Radiant Finale burst windows, Pitch Perfect optimization, and Empyreal Arrow drift prevention.")]
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
    // Pre-pull: Pot(-2s) → Wanderer's Minuet (late weave, starts song cycle)
    // GCD1: Stormbite (DoT) → GCD2: Caustic Bite (DoT)
    // → Raging Strikes (weave) → Empyreal Arrow (weave)
    // GCD3: Burst Shot → Battle Voice (weave) → Radiant Finale (weave)
    // GCD4: Burst Shot → Barrage (weave) → GCD5: Refulgent Arrow (forced proc)
    // → Sidewinder (weave) → Heartbreak Shot dump
    // → Continue Burst Shot / Refulgent Arrow under buffs
    //
    // === BURST WINDOWS (120s cycle — no distinct even/odd) ===
    // Raging Strikes + Battle Voice + Radiant Finale (triple stack, all 120s)
    // Barrage + Sidewinder + Empyreal Arrow + Heartbreak Shot dump
    // Apex Arrow at 80+ Soul Voice gauge under full buffs
    // Blast Arrow follow-up under remaining buffs
    //
    // === FILLER / SUSTAIN ===
    // Song cycle: Wanderer's Minuet (43s) → Mage's Ballad (43s) → Army's Paeon (34s)
    // DoTs: refresh Stormbite + Caustic Bite at ≤3s remaining (snapshot under buffs when possible)
    // Empyreal Arrow: strictly on cooldown, never hold
    // Pitch Perfect: spend at 3 stacks in WM, or 2+ if WM is about to end
    // Heartbreak Shot: use charges to avoid overcap (3 max), pool for burst
    // Apex Arrow: fire at 80+ Soul Voice, or 100 for Blast Arrow follow-up

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
        // Don't mit during burst
        if (InFullBurst)
            return base.DefenseAreaAbility(nextGCD, out act);

        if (TroubadourPvE.CanUse(out act))
            return true;

        return base.DefenseAreaAbility(nextGCD, out act);
    }

    [RotationDesc(ActionID.NaturesMinnePvE)]
    protected override bool HealSingleAbility(IAction nextGCD, out IAction? act)
    {
        if (NaturesMinnePvE.CanUse(out act))
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

        // Priority 1: Start first song (Wanderer's Minuet opens the fight)
        if (NoSong)
        {
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

            if (MagesBalladPvE.CanUse(out act))
                return true;
        }

        // MB -> AP at 3s remaining
        if (InMages && SongEndAfter(MB_EXIT_TIME))
        {
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

        // === 2-MINUTE BURST BUFFS (Radiant Finale -> Battle Voice -> Raging Strikes) ===
        // Apply in this order so Radiant Finale snapshots all 3 Coda, then BV + RS stack on top.
        // All three should be used during Wanderer's Minuet for Pitch Perfect value.
        if (CanBurst && InCombat && HasHostilesInRange)
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

        // === APEX ARROW: Soul Voice gauge spender ===
        // Fire at threshold (default 80+, ideally 100 during burst).
        // During burst: use at 80+ to fit under buffs.
        // Outside burst: use at 100 to prevent overcap, or 80+ during Mage's Ballad.
        if (TryUseApexArrow(out act))
            return true;

        // === IRON JAWS: DoT refresh ===
        // Refreshes both Caustic Bite and Stormbite. Requires both to be active.
        // Priority: during burst (snapshot raid buffs), or when DoTs are about to expire.
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
        if (!IronJawsPvE.EnoughLevel || !TargetHasDoTs)
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

        return false;
    }

    /// <summary>
    /// Barrage usage: guarantees a Refulgent Arrow and grants Resonant Arrow Ready.
    /// - Use during Raging Strikes for burst value
    /// - Don't use when Hawk's Eye is active (wastes the guaranteed proc, it would be consumed first)
    /// - Don't use at 3 Repertoire (EA proc might overflow)
    /// </summary>
    private bool TryUseBarrage(out IAction? act)
    {
        act = null;

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
    /// </summary>
    private bool TryUseSidewinder(out IAction? act)
    {
        act = null;

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
    /// </summary>
    private bool TryUseHeartbreakShot(out IAction? act)
    {
        act = null;

        bool atMaxCharges = BloodletterPvE.Cooldown.CurrentCharges >= BloodletterMax;
        bool willOvercap = BloodletterPvE.Cooldown.CurrentCharges >= BloodletterMax - 1
            && InMages; // MB resets give extra charges

        bool shouldSpend = InFullBurst || HasRagingStrikes || atMaxCharges || willOvercap;

        if (!shouldSpend)
            return false;

        // AoE: Rain of Death
        if (RainOfDeathPvE.CanUse(out act, usedUp: InFullBurst || atMaxCharges))
            return true;

        // ST: Heartbreak Shot (upgraded Bloodletter)
        if (HeartbreakShotPvE.CanUse(out act, usedUp: InFullBurst || atMaxCharges))
            return true;

        // Low level fallback
        if (BloodletterPvE.CanUse(out act, usedUp: InFullBurst || atMaxCharges))
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
    /// </summary>
    private bool TryUseApexArrow(out IAction? act)
    {
        act = null;

        if (SoulVoice < ApexArrowThreshold)
            return false;

        // Don't Apex if Barrage is active (use Refulgent first)
        if (HasBarrage)
            return false;

        // Don't use if DoTs are about to fall off (Iron Jaws is more urgent)
        if (DoTsEnding)
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

        // Hold for upcoming burst if close (within 25s of BV)
        if (BattleVoicePvE.EnoughLevel && BattleVoicePvE.Cooldown.WillHaveOneCharge(25))
            return false;

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
    /// </summary>
    private bool TryUseIronJaws(out IAction? act)
    {
        act = null;

        if (!IronJawsPvE.EnoughLevel)
            return false;

        // Must have both DoTs active on target to refresh
        if (!TargetHasDoTs)
            return false;

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

        ImGui.Text("--- Weave ---");
        ImGui.Text($"WeaponRemain: {WeaponRemain:F2}s | WeaponTotal: {WeaponTotal:F2}s");
        ImGui.Text($"EnoughWeaveTime: {EnoughWeaveTime}");
        ImGui.Text($"CanLateWeave: {CanLateWeave}");
    }

    #endregion
}
