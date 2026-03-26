namespace RotationSolver.ExtraRotations.Magical;

/// <summary>
/// Balance-aligned Red Mage rotation for Dawntrail 7.4x with BossModReborn timeline integration.
///
/// Key design principles (The Balance / Icy Veins 7.4):
///   - Dualcast loop: hardcast a short spell -> instant long spell, never waste Dualcast on a short cast.
///   - Mana balance: keep Black/White within 30 of each other; pick the lower-mana spell side when casting.
///   - Melee combo at 50/50+: Enchanted Riposte -> Zwerchhau -> Redoublement -> Verholy/Verflare -> Scorch -> Resolution.
///   - Burst window (120s): Embolden -> Manafication -> fit double melee combo under buffs.
///   - Manafication grants Magicked Swordplay (3 stacks, free melee) + Prefulgence Ready.
///   - Embolden grants Thorned Flourish -> Vice of Thorns.
///   - Grand Impact: separate instant from Acceleration, use when ready (never expires under melee/finisher).
///   - Fleche (25s) and Contre Sixte (35s): strict on-cooldown, not buff-dependent.
///   - Corps-a-corps / Engagement: 2 charges each, spend in buffs, prevent overcap.
///   - Acceleration: use on cooldown for Grand Impact generation, hold 1 charge if Embolden imminent.
///   - Swiftcast: alignment tool for double-instant, used after long-cast when no procs available.
///   - AoE: Verthunder II / Veraero II -> Impact (3+ targets), Enchanted Moulinet x3 (50/50).
///
/// BMR integration (BossModReborn timeline):
///   - Spread Addle and Magick Barrier across separate raidwides (never stack on same RW).
///   - Proactive Vercure self-heal before incoming raidwides when HP is low.
///   - Block melee combo entry when downtime < 8s (combo takes 6-7 GCDs to complete).
///   - Dump Embolden/Manafication before downtime for gauge value.
///   - Hold Embolden for vulnerability windows (within 30s).
///   - Avoid starting hardcasts when downtime < 3s.
///   - Force melee combo / mana dump before downtime to preserve gauge value.
///   - Full debug panel with BMR timeline breakdown.
/// </summary>
[Rotation("SezuraiRDM", CombatType.PvE, GameVersion = "7.41",
    Description = "Balance-aligned RDM with Embolden burst, mana balance optimization, double melee combo, Dawntrail abilities, and BMR timeline integration.")]
[SourceCode(Path = "main/ExtraRotations/Magical/SezuraiRDM.cs")]
[ExtraRotation]
public sealed class SezuraiRDM : RedMageRotation
{
    #region Config Options

    [Range(0f, 0.25f, ConfigUnitType.Percent)]
    [RotationConfig(CombatType.PvE, Name = "Action Ahead Override (0 = use global setting)")]
    public float ActionAheadOverride { get; set; } = 0f;

    [RotationConfig(CombatType.PvE, Name = "Auto Pot Usage (Intelligence Gemdraught during Embolden)")]
    public bool BurstMed { get; set; } = true;

    [RotationConfig(CombatType.PvE, Name = "Pool mana for double melee combo under Embolden")]
    public bool PoolMana { get; set; } = true;

    [RotationConfig(CombatType.PvE, Name = "Use Vercure for Dualcast when out of combat")]
    public bool UseVercure { get; set; } = false;

    [RotationConfig(CombatType.PvE, Name = "Use GCDs to heal (Ignored if healers are alive in party)")]
    public bool GCDHeal { get; set; } = false;

    [RotationConfig(CombatType.PvE, Name = "Prevent healing/raising during burst combos")]
    public bool PreventBurstHeal { get; set; } = true;

    [RotationConfig(CombatType.PvE, Name = "Cast Enchanted Reprise when moving with no instant-cast available")]
    public bool UseReprise { get; set; } = false;

    [RotationConfig(CombatType.PvE, Name = "Delay Prefulgence/Vice of Thorns until Embolden window")]
    public bool DelayBurstOGCDs { get; set; } = true;

    [Range(40, 100, ConfigUnitType.None, 5)]
    [RotationConfig(CombatType.PvE, Name = "Mana threshold for melee combo entry (40-100, step 5)")]
    public int ManaPoolTarget { get; set; } = 50;

    [RotationConfig(CombatType.PvE, Name = "BMR: Hold Embolden for vulnerability window (within 30s)")]
    public bool BmrHoldEmboldenForVuln { get; set; } = true;

    [RotationConfig(CombatType.PvE, Name = "BMR: Dump mana/burst before downtime")]
    public bool BmrDumpBeforeDowntime { get; set; } = true;

    [RotationConfig(CombatType.PvE, Name = "BMR: Spread Addle/Magick Barrier across raidwides")]
    public bool BmrSpreadMitigation { get; set; } = true;

    [RotationConfig(CombatType.PvE, Name = "BMR: Proactive Vercure self-heal before raidwide")]
    public bool BmrProactiveVercure { get; set; } = true;

    #endregion

    #region Burst State

    /// <summary>
    /// Whether the user has burst enabled in the framework.
    /// </summary>
    private bool CanBurst => MergedStatus.HasFlag(AutoStatus.Burst);

    /// <summary>
    /// True when Embolden is currently active on the player.
    /// </summary>
    private bool InEmboldenWindow => HasEmbolden;

    /// <summary>
    /// True when we are mid-melee combo or finisher chain and should not interrupt.
    /// </summary>
    private bool InBurstSequence =>
        IsInMeleeCombo || InFinisherChain || CanMagickedSwordplay || ManaStacks == 3;

    /// <summary>
    /// True when we are in the finisher chain (Verholy/Verflare -> Scorch -> Resolution).
    /// </summary>
    private bool InFinisherChain =>
        IsLastGCD(ActionID.VerholyPvE, ActionID.VerflarePvE, ActionID.ScorchPvE)
        || ScorchPvE.CanUse(out _)
        || ResolutionPvE.CanUse(out _);

    /// <summary>
    /// True if the next GCD will be instant (Dualcast/Swift/Accel/Grand Impact ready).
    /// </summary>
    private bool NextGCDIsInstant =>
        HasDualcast || HasSwift || HasAccelerate || CanGrandImpact;

    #endregion

    #region BMR Helpers

    /// <summary>
    /// True when BMR reports downtime within the specified seconds.
    /// Always false when BMR is inactive (safe fallback).
    /// </summary>
    private bool BmrDowntimeWithin(float seconds)
        => BmrActive && BmrDowntimeIn is > 0 and < float.MaxValue && BmrDowntimeIn <= seconds;

    /// <summary>
    /// True when BMR reports a vulnerability window within the specified seconds.
    /// Always false when BMR is inactive (safe fallback).
    /// </summary>
    private bool BmrVulnWithin(float seconds)
        => BmrActive && BmrVulnerableIn is > 0 and < float.MaxValue && BmrVulnerableIn <= seconds;

    /// <summary>
    /// True when BMR reports a raidwide within the specified seconds.
    /// </summary>
    private bool BmrRaidwideWithin(float seconds)
        => BmrActive && BmrRaidwideIn is > 0 and < float.MaxValue && BmrRaidwideIn <= seconds;

    /// <summary>
    /// True when BMR reports a tankbuster within the specified seconds.
    /// </summary>
    private bool BmrTankbusterWithin(float seconds)
        => BmrActive && BmrTankbusterIn is > 0 and < float.MaxValue && BmrTankbusterIn <= seconds;

    /// <summary>
    /// Tracks whether we used Addle on the most recent raidwide to spread mitigation.
    /// Reset when raidwide timer goes far enough (> 20s means we are past the previous RW).
    /// </summary>
    private bool _lastRwUsedAddle;

    /// <summary>
    /// The BmrRaidwideIn value when we last used a mitigation, used to detect new raidwides.
    /// </summary>
    private float _lastRwMitTime;

    #endregion

    #region Private Helpers

    /// <summary>
    /// For pre-pull countdown: create a non-targeting Veraero action for the long pre-cast.
    /// </summary>
    private static BaseAction VeraeroPvEStartUp { get; } = new BaseAction(ActionID.VeraeroPvE, false);

    /// <summary>
    /// Whether we have enough mana to start melee, respecting pooling settings.
    /// When pooling is enabled, we hold to higher thresholds near Embolden windows
    /// to enable double combos.
    /// </summary>
    private bool HasEnoughManaForCombo
    {
        get
        {
            // If Magicked Swordplay is active, always allow (free combo)
            if (CanMagickedSwordplay) return true;

            // Apply the configurable mana threshold (default 50 = standard)
            bool meetsThreshold = BlackMana >= ManaPoolTarget && WhiteMana >= ManaPoolTarget;

            if (PoolMana)
            {
                // Pooling cap safety: if either color is very high, start combo to prevent overcap
                bool poolCapReached =
                    (BlackMana >= 92 && WhiteMana >= 81) ||
                    (WhiteMana >= 92 && BlackMana >= 81);
                return poolCapReached || (meetsThreshold && EnoughManaComboPooling);
            }

            return meetsThreshold && EnoughManaComboNoPooling;
        }
    }

    /// <summary>
    /// Try Riposte starter - prefer the extended-range variant under Manafication.
    /// </summary>
    private bool TryRiposteStarter(out IAction? act)
    {
        if (HasManafication && EnchantedRipostePvE_45960.CanUse(out act))
            return true;
        if (EnchantedRipostePvE.CanUse(out act))
            return true;
        act = null;
        return false;
    }

    /// <summary>
    /// Check if the last GCD was any Riposte variant (prevent double-start).
    /// </summary>
    private bool IsLastRiposteStarter() =>
        IsLastGCD(true, EnchantedRipostePvE_45960) || IsLastGCD(true, EnchantedRipostePvE);

    #endregion

    #region UpdateInfo

    protected override void UpdateInfo()
    {
        DataCenter.RotationActionAheadOverride = ActionAheadOverride > 0f ? ActionAheadOverride : null;

        // BMR: Reset mitigation spread tracking when raidwide timer jumps
        // (indicates we've moved past the previous raidwide to a new one)
        if (BmrActive && BmrRaidwideIn < float.MaxValue)
        {
            // If the raidwide timer increased significantly, a new RW event appeared
            if (BmrRaidwideIn > _lastRwMitTime + 10f)
            {
                // New raidwide detected - reset alternation
            }
        }
        else
        {
            // No raidwide in sight, reset for next encounter
            _lastRwMitTime = 0f;
        }
    }

    #endregion

    #region Status Display

    public override void DisplayRotationStatus()
    {
        ImGui.Text($"--- Sezurai RDM Status ---");
        ImGui.Text($"WhiteMana: {WhiteMana}  BlackMana: {BlackMana}  ManaStacks: {ManaStacks}");
        ImGui.Text($"CanBurst: {CanBurst}  InEmbolden: {InEmboldenWindow}");
        ImGui.Text($"InMeleeCombo: {IsInMeleeCombo}  InFinisher: {InFinisherChain}");
        ImGui.Text($"Swordplay: {CanMagickedSwordplay}  Manafic: {HasManafication}");
        ImGui.Text($"Dualcast: {HasDualcast}  Swift: {HasSwift}  Accel: {HasAccelerate}");
        ImGui.Text($"GrandImpact: {CanGrandImpact}  Prefulgence: {CanPrefulgence}  Thorns: {HasThornedFlourish}");
        ImGui.Text($"VerFire: {CanVerFire}  VerStone: {CanVerStone}");
        ImGui.Spacing();
        ImGui.Text($"Embolden CD: {EmboldenPvE.Cooldown.RecastTimeRemain:F1}s");
        ImGui.Text($"Manafication CD: {ManaficationPvE.Cooldown.RecastTimeRemain:F1}s");
        ImGui.Text($"Fleche CD: {FlechePvE.Cooldown.RecastTimeRemain:F1}s");
        ImGui.Text($"C6 CD: {ContreSixtePvE.Cooldown.RecastTimeRemain:F1}s");
        ImGui.Text($"Addle CD: {(AddlePvE.Cooldown.IsCoolingDown ? $"{AddlePvE.Cooldown.RecastTimeRemain:F1}s" : "Ready")}");
        ImGui.Text($"MagickBarrier CD: {(MagickBarrierPvE.Cooldown.IsCoolingDown ? $"{MagickBarrierPvE.Cooldown.RecastTimeRemain:F1}s" : "Ready")}");
        ImGui.Text($"EnoughManaForCombo: {HasEnoughManaForCombo}");
        ImGui.Text($"PoolMana: {PoolMana}  ManaNeededW: {ManaNeededWhite()}  ManaNeededB: {ManaNeededBlack()}");
        ImGui.Spacing();
        ImGui.Text("--- BMR Timeline ---");
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
            ImGui.Text($"Damage In: {(BmrDamageIn < 9999f ? $"{BmrDamageIn:F1}s type={BmrDamageType}" : "None")}");
            ImGui.Spacing();
            ImGui.Text($"-- BMR Decision State --");
            ImGui.Text($"RW within 5s: {BmrRaidwideWithin(5f)}");
            ImGui.Text($"TB within 5s: {BmrTankbusterWithin(5f)}");
            ImGui.Text($"Downtime <8s: {BmrDowntimeWithin(8f)}");
            ImGui.Text($"Downtime <15s: {BmrDowntimeWithin(15f)}");
            ImGui.Text($"Vuln <30s: {BmrVulnWithin(30f)}");
            ImGui.Text($"LastRwUsedAddle: {_lastRwUsedAddle}");
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

    #region Countdown & Opener
    // === RDM OPENER (7.4 Balance) ===
    // Pre-pull: Acceleration(-5s) -> Pot(-2s) -> Veraero III precast (hardcast w/ Accel for Grand Impact proc)
    // GCD1: Veraero III -> GCD2: Grand Impact (instant, from Acceleration)
    // -> Fleche (weave) -> GCD3: Verthunder III/Veraero III (Dualcast)
    // -> Embolden (weave) -> Manafication (weave) -> GCD4: Veraero III (Dualcast)
    // GCD5: Enchanted Riposte -> Vice of Thorns (weave)
    // GCD6: Enchanted Zwerchhau -> GCD7: Enchanted Redoublement
    // -> Prefulgence (weave) -> GCD8: Verholy/Verflare -> GCD9: Scorch -> GCD10: Resolution
    // -> Second melee combo under remaining Embolden
    //
    // === EVEN BURST (120s) ===
    // Embolden (party buff) + Manafication (instant melee combo stacks)
    // Double melee combo: 2x Riposte->Zwerchhau->Redoublement->finisher chain
    // Prefulgence + Vice of Thorns + Grand Impact + Fleche + Contre Sixte under buffs
    //
    // === ODD BURST (60s) ===
    // Single melee combo only -- Embolden + Manafication are 120s
    // Fleche + Contre Sixte on cooldown, pool mana for even window
    //
    // === FILLER / SUSTAIN ===
    // Dualcast loop: hardcast Verthunder/Veraero III -> instant Dualcast proc -> repeat
    // Mana balance: keep White and Black mana roughly equal (within 30)
    // Melee entry at 50/50 mana minimum (Enchanted combo costs 50 of each)
    // Acceleration: use on CD for Grand Impact proc + instant cast, save 1 for movement
    // Fleche + Contre Sixte: strictly on CD, never hold outside burst
    // Jolt III: use when both Verfire/Verstone procs are down (lowest priority hardcast)
    // Corps-a-corps + Engagement: use charges to avoid overcap, weave freely

    protected override IAction? CountDownAction(float remainTime)
    {
        // Pre-pull pot at ~2s before pull
        if (BurstMed && remainTime <= 2f && remainTime > 1f && UseBurstMedicine(out var potAct))
            return potAct;

        // Pre-pull: hardcast Veraero III to enter combat with Dualcast ready
        // Cast at: remainTime < castTime + CountDownAhead
        if (remainTime < VeraeroPvEStartUp.Info.CastTime + CountDownAhead)
        {
            if (VeraeroPvEStartUp.CanUse(out IAction? act))
                return act;
        }

        // Clean up stale buffs if countdown overruns
        if (HasAccelerate && remainTime < 0f)
            StatusHelper.StatusOff(StatusID.Acceleration);
        if (HasSwift && remainTime < 0f)
            StatusHelper.StatusOff(StatusID.Swiftcast);

        return base.CountDownAction(remainTime);
    }

    #endregion

    #region Raise / Heal GCD

    [RotationDesc(ActionID.VercurePvE)]
    protected override bool HealSingleGCD(out IAction? act)
    {
        // Block healing during burst sequences to prevent DPS loss
        if (PreventBurstHeal && InBurstSequence)
            return base.HealSingleGCD(out act);

        // BMR: Proactive Vercure self-heal before raidwide damage.
        // If a raidwide is coming within 5s and player HP is below 70%, cast Vercure
        // to top up before the hit. This also procs Dualcast for an instant GCD after.
        // Only when not in burst sequence (already blocked above).
        if (BmrProactiveVercure && BmrRaidwideWithin(5f)
            && Player?.GetHealthRatio() < 0.70f
            && VercurePvE.CanUse(out act, skipStatusProvideCheck: true))
            return true;

        if (VercurePvE.CanUse(out act, skipStatusProvideCheck: true))
            return true;

        return base.HealSingleGCD(out act);
    }

    [RotationDesc(ActionID.VerraisePvE)]
    protected override bool RaiseGCD(out IAction? act)
    {
        // Block raising during burst sequences
        if (PreventBurstHeal && InBurstSequence)
            return base.RaiseGCD(out act);

        if (VerraisePvE.CanUse(out act))
            return true;

        return base.RaiseGCD(out act);
    }

    public override bool CanHealSingleSpell
    {
        get
        {
            int aliveHealerCount = 0;
            IEnumerable<IBattleChara> healers = PartyMembers.GetJobCategory(JobRole.Healer);
            foreach (IBattleChara h in healers)
            {
                if (!h.IsDead)
                    aliveHealerCount++;
            }
            return base.CanHealSingleSpell && (GCDHeal || aliveHealerCount == 0);
        }
    }

    #endregion

    #region Movement / Defense Abilities

    [RotationDesc(ActionID.CorpsacorpsPvE)]
    protected override bool MoveForwardAbility(IAction nextGCD, out IAction? act)
    {
        if (CorpsacorpsPvE.CanUse(out act, usedUp: true))
            return true;
        return base.MoveForwardAbility(nextGCD, out act);
    }

    [RotationDesc(ActionID.AddlePvE, ActionID.MagickBarrierPvE)]
    protected sealed override bool DefenseAreaAbility(IAction nextGCD, out IAction? act)
    {
        bool rwSoon = BmrRaidwideWithin(5f);

        if (rwSoon && BmrSpreadMitigation)
        {
            // BMR-aware: spread Addle and Magick Barrier across separate raidwides.
            // Addle (90s CD, 10% magic damage reduction on boss, 15s duration)
            // Magick Barrier (120s CD, 10% party magic mit + 5% heal boost, 10s duration)
            //
            // Strategy: alternate which one we use per raidwide.
            // Track via _lastRwUsedAddle: if we used Addle last time, prefer Barrier this time.
            // If the preferred one is on CD, fall through to the other.
            // Only use ONE per raidwide to maximize coverage across the fight.

            bool preferAddle = !_lastRwUsedAddle;

            // Addle: 10% magic + 5% phys -- prefer for magic damage
            bool addleEffective = IsMagicalDamageIncoming || !IsPhysicalDamageIncoming;

            if (preferAddle && addleEffective)
            {
                if (AddlePvE.CanUse(out act))
                {
                    _lastRwUsedAddle = true;
                    _lastRwMitTime = BmrRaidwideIn;
                    return true;
                }
            }

            // Magick Barrier: works for all damage types
            if (MagickBarrierPvE.CanUse(out act))
            {
                _lastRwUsedAddle = false;
                _lastRwMitTime = BmrRaidwideIn;
                return true;
            }

            // Fallback: Addle if Barrier is on CD and Addle would be effective
            if (!preferAddle && addleEffective && AddlePvE.CanUse(out act))
            {
                _lastRwUsedAddle = true;
                _lastRwMitTime = BmrRaidwideIn;
                return true;
            }

            return base.DefenseAreaAbility(nextGCD, out act);
        }

        if (rwSoon)
        {
            // BMR active but spread disabled: use whichever is available first, 1 per raidwide
            if (AddlePvE.CanUse(out act))
                return true;
            if (MagickBarrierPvE.CanUse(out act))
                return true;
            return base.DefenseAreaAbility(nextGCD, out act);
        }

        // Non-BMR: use both when framework triggers
        if (AddlePvE.CanUse(out act))
            return true;
        if (MagickBarrierPvE.CanUse(out act))
            return true;

        return base.DefenseAreaAbility(nextGCD, out act);
    }

    protected sealed override bool DefenseSingleAbility(IAction nextGCD, out IAction? act)
    {
        // RDM has no oGCD self-defense abilities.
        // Vercure is a GCD handled in HealSingleGCD.
        // Addle/Magick Barrier are area mitigation handled in DefenseAreaAbility.
        return base.DefenseSingleAbility(nextGCD, out act);
    }

    #endregion

    #region Emergency Ability (Embolden + Manafication)

    protected override bool EmergencyAbility(IAction nextGCD, out IAction? act)
    {
        // === MANAFICATION ===
        // Use when Embolden is active, just became available, or about to come off CD.
        // Manafication grants Magicked Swordplay (3 free melee stacks) + Prefulgence Ready.
        // In 7.4: Manafication no longer doubles mana, it purely grants free combo stacks.
        // Optimal: use shortly after Embolden for double combo.
        // BMR: Also use before downtime if Embolden is active (get value from Swordplay stacks before boss leaves)
        if (HasEmbolden
            || EmboldenPvE.Cooldown.HasOneCharge
            || (EmboldenPvE.Cooldown.WillHaveOneCharge(4f) && !IsInMeleeCombo))
        {
            if (InCombat && HasHostilesInMaxRange && ManaficationPvE.CanUse(out act))
                return true;
        }

        // BMR: Force Manafication before downtime for gauge value
        // If downtime is within 15s and we have Embolden or it won't come back before downtime,
        // use Manafication now to get free combo stacks we can spend before boss is untargetable.
        if (BmrDumpBeforeDowntime && BmrDowntimeWithin(15f)
            && InCombat && HasHostilesInMaxRange
            && !CanMagickedSwordplay && !HasManafication
            && ManaficationPvE.CanUse(out act))
        {
            return true;
        }

        // === EMBOLDEN ===
        // 120s party buff: 5% party damage, 10% personal magic damage for 20s.
        // Use on cooldown, aligned with 2-min party buffs.
        // Grants Thorned Flourish -> Vice of Thorns.
        {
            // BMR: Don't use Embolden if downtime is very soon (< 5s) -- buff would be wasted
            bool bmrBlockEmbolden = BmrDowntimeWithin(5f);

            // BMR: Hold Embolden for upcoming vulnerability window -- but only if within 30s
            // and not already happening (> 3s away). Don't hold forever.
            bool bmrHoldForVuln = BmrHoldEmboldenForVuln
                && BmrVulnWithin(30f)
                && BmrVulnerableIn > 3f
                && EmboldenPvE.Cooldown.HasOneCharge;

            // BMR: Force Embolden before downtime if it won't come back before boss returns
            // Use within 20s of downtime so the 20s buff duration gets some value
            bool bmrForceBeforeDowntime = BmrDumpBeforeDowntime
                && BmrDowntimeWithin(20f)
                && !BmrDowntimeWithin(5f)
                && EmboldenPvE.Cooldown.HasOneCharge;

            if (CanBurst && InCombat && HasHostilesInRange && !bmrBlockEmbolden && !bmrHoldForVuln)
            {
                if (EmboldenPvE.CanUse(out act))
                    return true;
            }

            // BMR: Force dump Embolden before downtime even without CanBurst
            if (bmrForceBeforeDowntime && InCombat && HasHostilesInRange && !bmrHoldForVuln)
            {
                if (EmboldenPvE.CanUse(out act))
                    return true;
            }
        }

        // === MEDICINE ===
        // Use during Embolden window for maximum burst value.
        if (BurstMed && InEmboldenWindow && InCombat && UseBurstMedicine(out act))
            return true;

        return base.EmergencyAbility(nextGCD, out act);
    }

    #endregion

    #region oGCD Attack Logic

    protected override bool AttackAbility(IAction nextGCD, out IAction? act)
    {
        act = null;

        // Check if next GCD is a melee weaponskill (don't weave things that conflict)
        bool nextIsMelee = nextGCD.IsTheSameTo(true,
            ActionID.RipostePvE, ActionID.ZwerchhauPvE, ActionID.RedoublementPvE,
            ActionID.MoulinetPvE, ActionID.ReprisePvE);

        bool blockSwift = IsInMeleeCombo || InFinisherChain;

        // === MOVEMENT RESCUE: Acceleration / Swiftcast ===
        // If moving and no instant available, use Acceleration or Swiftcast to prevent dropped casts.
        if (InCombat && HasHostilesInMaxRange && IsMoving && !NextGCDIsInstant
            && !nextIsMelee && !IsInMeleeCombo && ManaStacks != 3)
        {
            // Acceleration: don't use if Grand Impact already ready (would waste it)
            if (AccelerationPvE.EnoughLevel && !CanGrandImpact && !HasSwift
                && AccelerationPvE.CanUse(out act, usedUp: true, skipCastingCheck: true))
                return true;

            if (!blockSwift && SwiftcastPvE.CanUse(out act, usedUp: true, skipCastingCheck: true))
                return true;
        }

        // === ACCELERATION (proactive usage) ===
        // Use on cooldown for Grand Impact generation and guaranteed procs.
        // Hold a charge when Embolden is approaching and we have 50/50+ (about to burst).
        // In Embolden: spend freely (usedUp). Outside: prevent overcap (use at 2+ charges).
        if (AccelerationPvE.EnoughLevel && !nextIsMelee && !CanGrandImpact
            && !CanMagickedSwordplay && !HasManafication
            && InCombat && HasHostilesInMaxRange)
        {
            // Block Accel when Embolden is imminent and we're pooled for burst
            bool emboldenSoon = EmboldenPvE.EnoughLevel && !HasEmbolden
                && EmboldenPvE.Cooldown.WillHaveOneCharge(10f);
            bool holdForBurst = emboldenSoon && BlackMana >= 50 && WhiteMana >= 50 && !IsInMeleeCombo;

            // BMR: Don't hold Acceleration if downtime is imminent -- spend it for value
            if (BmrDumpBeforeDowntime && BmrDowntimeWithin(10f))
                holdForBurst = false;

            if (!holdForBurst)
            {
                bool useUp = HasEmbolden || !EmboldenPvE.EnoughLevel
                    || AccelerationPvE.Cooldown.WillHaveXChargesGCD(2, 1);

                // BMR: Spend Acceleration more aggressively before downtime
                if (BmrDumpBeforeDowntime && BmrDowntimeWithin(15f))
                    useUp = true;

                if (EnhancedAccelerationIiTrait.EnoughLevel)
                {
                    // 3 charges at max level
                    if (AccelerationPvE.CanUse(out act, usedUp: useUp))
                        return true;
                }
                else if (EnhancedAccelerationTrait.EnoughLevel)
                {
                    // 2 charges
                    if (AccelerationPvE.CanUse(out act, usedUp: useUp))
                        return true;
                }
                else
                {
                    // 1 charge: only during Embolden
                    if ((HasEmbolden || !EmboldenPvE.EnoughLevel)
                        && AccelerationPvE.CanUse(out act))
                        return true;
                }
            }
        }

        // === SWIFTCAST (alignment tool) ===
        // Use to create double-instant windows for oGCD alignment.
        // Only after a long cast when no procs are available (prevents waste on short casts).
        // Hold near Embolden (within 30s).
        if (InCombat && (HasHostilesInRange || HasHostilesInMaxRange)
            && ManaStacks != 3 && !blockSwift && !HasSwift
            && !HasAccelerate && !HasDualcast && !nextIsMelee)
        {
            bool holdForEmbolden = EmboldenPvE.EnoughLevel && !HasEmbolden
                && EmboldenPvE.Cooldown.WillHaveOneCharge(30);

            // BMR: Don't hold Swiftcast if downtime is imminent
            if (BmrDumpBeforeDowntime && BmrDowntimeWithin(10f))
                holdForEmbolden = false;

            if (!holdForEmbolden || !EmboldenPvE.EnoughLevel)
            {
                // Use after Verthunder/Veraero when no proc generated
                if (!CanVerFire && !CanVerStone
                    && IsLastGCD(false, VerthunderPvE, VerthunderIiiPvE, VeraeroPvE, VeraeroIiiPvE))
                {
                    if (SwiftcastPvE.CanUse(out act))
                        return true;
                }

                // Use when next GCD would be a long cast without a proc
                if (!CanVerStone && nextGCD.IsTheSameTo(false, VeraeroPvE, VeraeroIiiPvE))
                {
                    if (SwiftcastPvE.CanUse(out act))
                        return true;
                }

                if (!CanVerFire && nextGCD.IsTheSameTo(false, VerthunderPvE, VerthunderIiiPvE))
                {
                    if (SwiftcastPvE.CanUse(out act))
                        return true;
                }
            }
        }

        // === FLECHE (25s CD) ===
        // Strictly on cooldown. Flat damage, not affected by buffs in any meaningful way.
        if (FlechePvE.CanUse(out act))
            return true;

        // === CONTRE SIXTE (35s CD) ===
        // Strictly on cooldown. AoE damage.
        if (ContreSixtePvE.CanUse(out act))
            return true;

        // === PREFULGENCE (Manafication follow-up) ===
        // 1200 potency oGCD, available after Manafication.
        // Lasts 30s, so we can hold it for Embolden if desired.
        // Safety: if about to expire, use immediately.
        // BMR: Use immediately before downtime rather than hold for Embolden that won't happen.
        if (CanPrefulgence)
        {
            bool aboutToExpire = StatusHelper.PlayerWillStatusEndGCD(1, 0, true, StatusID.PrefulgenceReady);

            // BMR: Force use before downtime (don't lose 1200 potency to boss going away)
            bool bmrForceUse = BmrDumpBeforeDowntime && BmrDowntimeWithin(5f);

            if (!DelayBurstOGCDs)
            {
                // No delay: use under Embolden or before expiry or before downtime
                if ((HasEmbolden || aboutToExpire || bmrForceUse) && PrefulgencePvE.CanUse(out act))
                    return true;
            }
            else
            {
                // Delayed: prefer inside Embolden, fallback on expiry or downtime dump
                if ((HasEmbolden || aboutToExpire || bmrForceUse) && PrefulgencePvE.CanUse(out act))
                    return true;
            }
        }

        // === VICE OF THORNS (Embolden follow-up) ===
        // Available after using Embolden (Thorned Flourish buff).
        // Use inside Embolden window for buff value.
        // BMR: Use before downtime rather than lose it.
        if (HasThornedFlourish)
        {
            bool bmrForceUse = BmrDumpBeforeDowntime && BmrDowntimeWithin(5f);

            if (!DelayBurstOGCDs)
            {
                if (ViceOfThornsPvE.CanUse(out act))
                    return true;
            }
            else
            {
                // Delayed: only inside Embolden, or forced by BMR downtime dump
                if ((HasEmbolden || bmrForceUse) && ViceOfThornsPvE.CanUse(out act))
                    return true;
            }
        }

        // === ENGAGEMENT (2 charges, 35s CD) ===
        // Spend freely during Embolden. Outside buffs: prevent overcap.
        // Preferred over Displacement for safety.
        // BMR: Spend charges before downtime.
        {
            bool useUp = HasEmbolden || !EmboldenPvE.EnoughLevel
                || EngagementPvE.Cooldown.WillHaveXChargesGCD(2, 1);

            // BMR: Dump charges before downtime
            if (BmrDumpBeforeDowntime && BmrDowntimeWithin(10f))
                useUp = true;

            if (EngagementPvE.CanUse(out act, usedUp: useUp))
                return true;
        }

        // === CORPS-A-CORPS (2 charges, 35s CD) ===
        // Spend freely during Embolden. Outside buffs: prevent overcap.
        // Only use when not moving (prevents accidental gap close).
        // BMR: Spend charges before downtime.
        {
            bool useUp = HasEmbolden || !EmboldenPvE.EnoughLevel
                || CorpsacorpsPvE.Cooldown.WillHaveXChargesGCD(2, 1);

            // BMR: Dump charges before downtime
            if (BmrDumpBeforeDowntime && BmrDowntimeWithin(10f))
                useUp = true;

            if (!IsMoving && CorpsacorpsPvE.CanUse(out act, usedUp: useUp))
                return true;
        }

        return base.AttackAbility(nextGCD, out act);
    }

    #endregion

    #region General Ability (non-attack oGCDs)

    protected override bool GeneralAbility(IAction nextGCD, out IAction? act)
    {
        // Medicine fallback: if Embolden is up and EmergencyAbility didn't catch it
        if (BurstMed && HasEmbolden && InCombat && UseBurstMedicine(out act))
            return true;

        return base.GeneralAbility(nextGCD, out act);
    }

    #endregion

    #region GCD Logic

    protected override bool GeneralGCD(out IAction? act)
    {
        // Track whether we hold an instant-cast buff that should not be spent on a short spell
        bool hasInstantBuff = HasDualcast || HasSwift;

        // BMR: Precompute downtime state for use throughout GCD logic
        bool bmrDowntimeSoon = BmrDowntimeWithin(8f);
        bool bmrDowntimeImminent = BmrDowntimeWithin(3f);

        // =====================================================================
        // PRIORITY 1: FINISHER CHAIN (always complete, never interrupt)
        // Verholy/Verflare -> Scorch -> Resolution
        // =====================================================================

        // Scorch -> Resolution
        if (IsLastGCD(ActionID.ScorchPvE))
        {
            if (ResolutionPvE.CanUse(out act, skipStatusProvideCheck: true))
                return true;
        }

        // Verholy/Verflare -> Scorch
        if (IsLastGCD(ActionID.VerholyPvE, ActionID.VerflarePvE))
        {
            if (ScorchPvE.CanUse(out act, skipStatusProvideCheck: true))
                return true;
        }

        // ManaStacks == 3: choose Verholy or Verflare to balance mana
        if (ManaStacks == 3)
        {
            int diff = BlackMana - WhiteMana;
            int gap = Math.Abs(diff);

            // Force balance when gap is large or during Embolden (to guarantee proc)
            bool forceBalance = HasEmbolden || gap >= 19;

            if (forceBalance)
            {
                // Black leads -> Verholy (adds White, generates Verstone proc)
                if (diff > 0 && VerholyPvE.CanUse(out act)) return true;
                // White leads -> Verflare (adds Black, generates Verfire proc)
                if (diff < 0 && VerflarePvE.CanUse(out act)) return true;
            }
            else
            {
                // Small imbalance: be proc-aware to avoid overwriting existing procs
                if (CanVerFire && VerholyPvE.CanUse(out act)) return true;
                if (CanVerStone && VerflarePvE.CanUse(out act)) return true;
            }

            // Fallbacks: balance first, then proc-aware, then any
            if (diff > 0 && VerholyPvE.CanUse(out act)) return true;
            if (diff < 0 && VerflarePvE.CanUse(out act)) return true;
            if (CanVerFire && !CanVerStone && VerholyPvE.CanUse(out act)) return true;
            if (CanVerStone && !CanVerFire && VerflarePvE.CanUse(out act)) return true;
            if (VerholyPvE.CanUse(out act)) return true;
            if (VerflarePvE.CanUse(out act)) return true;
        }

        // =====================================================================
        // PRIORITY 2: MELEE COMBO CONTINUATION (never drop mid-combo)
        // =====================================================================

        // AoE combo continuation
        if (IsLastGCD(false, EnchantedMoulinetDeuxPvE) && EnchantedMoulinetTroisPvE.CanUse(out act))
            return true;
        if (IsLastGCD(false, EnchantedMoulinetPvE) && EnchantedMoulinetDeuxPvE.CanUse(out act))
            return true;

        // ST combo continuation: Redoublement (prefer extended range under Manafication)
        if (EnchantedRedoublementPvE_45962.CanUse(out act))
            return true;
        if (EnchantedRedoublementPvE.CanUse(out act))
            return true;

        // ST combo continuation: Zwerchhau (prefer extended range under Manafication)
        if (EnchantedZwerchhauPvE_45961.CanUse(out act))
            return true;
        if (EnchantedZwerchhauPvE.CanUse(out act))
            return true;

        // =====================================================================
        // PRIORITY 3: START MELEE COMBO (50/50+ mana, not in finisher chain)
        // =====================================================================
        // BMR-aware: Don't start melee combo if downtime < 8s (combo takes ~6-7 GCDs ~17s to complete)
        // Exception: Force melee dump if we have high mana before downtime (within 15s) to avoid
        // losing gauge value. The combo may get interrupted but partial value > no value.
        bool bmrBlockMelee = bmrDowntimeSoon && !InBurstSequence;

        // BMR: Force melee start if downtime within 15s and mana is very high
        // (better to start and get partial combo than lose all mana to downtime)
        bool bmrForceMeleeDump = BmrDumpBeforeDowntime
            && BmrDowntimeWithin(15f) && !bmrDowntimeSoon
            && BlackMana >= 50 && WhiteMana >= 50
            && !IsInMeleeCombo && !InFinisherChain;

        if (HasEnoughManaForCombo && !InFinisherChain && (!bmrBlockMelee || bmrForceMeleeDump))
        {
            // Burst start: when Manafication is active or swordplay stacks are running
            bool burstStartOK =
                CanMagickedSwordplay
                || HasManafication
                || StatusHelper.PlayerWillStatusEndGCD(4, 0, true, StatusID.MagickedSwordplay)
                || (HasEmbolden && CanMagickedSwordplay);

            // Pooling cap: if mana is very high, start regardless of burst conditions
            bool poolCapReached =
                (BlackMana >= 92 && WhiteMana >= 81) ||
                (WhiteMana >= 92 && BlackMana >= 81);

            bool canStart = burstStartOK || poolCapReached
                || (!PoolMana && EnoughManaComboNoPooling)
                || bmrForceMeleeDump;

            if (canStart)
            {
                // AoE melee at 3+ targets
                if (NumberOfHostilesInRangeOf(5) >= 3)
                {
                    if (!IsLastGCD(false, EnchantedMoulinetPvE) && EnchantedMoulinetPvE.CanUse(out act))
                        return true;
                }

                // ST melee: Enchanted Riposte starter
                if (!IsLastRiposteStarter() && TryRiposteStarter(out act))
                    return true;
            }
        }

        // =====================================================================
        // PRIORITY 4: GRAND IMPACT (Dawntrail proc, instant, does not consume Dualcast)
        // Use when available, not mid-melee combo.
        // =====================================================================
        if (CanGrandImpact
            && GrandImpactPvE.CanUse(out act, skipStatusProvideCheck: true, skipCastingCheck: true))
            return true;

        // Safety: if ManaStacks == 3 but finisher wasn't used above, bail
        if (ManaStacks == 3)
            return base.GeneralGCD(out act);

        // =====================================================================
        // PRIORITY 5: ENCHANTED REPRISE (movement fallback + BMR downtime mana dump)
        // =====================================================================
        // BMR: Use Enchanted Reprise to dump mana before downtime when we can't start a full combo
        // (downtime < 8s, so melee combo is blocked, but we still want to spend mana)
        if (BmrDumpBeforeDowntime && bmrDowntimeSoon && !bmrDowntimeImminent
            && BlackMana >= 5 && WhiteMana >= 5
            && ManaStacks == 0 && !IsInMeleeCombo && !InFinisherChain
            && EnchantedReprisePvE.CanUse(out act))
            return true;

        if (IsMoving && UseReprise
            && ManaStacks == 0 && (BlackMana < 50 || WhiteMana < 50)
            && !HasDualcast && !HasSwift && !HasAccelerate && !CanGrandImpact
            && EnchantedReprisePvE.CanUse(out act))
            return true;

        // If moving with no instant tools, wait for oGCD rescue (don't attempt hardcasts)
        if (IsMoving && InCombat && HasHostilesInMaxRange && ManaStacks != 3
            && !HasDualcast && !HasSwift && !HasAccelerate && !CanGrandImpact)
        {
            act = null;
            return false;
        }

        // =====================================================================
        // BMR: Don't start hardcasts when downtime is imminent (< 3s)
        // The cast won't complete before boss goes away. Prefer instants or do nothing.
        // =====================================================================
        if (bmrDowntimeImminent && !hasInstantBuff && !HasAccelerate && !CanGrandImpact)
        {
            // Only allow instant-cast GCDs through; block hardcasts near downtime.
            // If we have an instant buff, those will be handled below.
            // Fall through to Vercure/base if nothing instant is available.
            if (UseVercure && VercurePvE.CanUse(out act))
                return true;
            return base.GeneralGCD(out act);
        }

        // =====================================================================
        // PRIORITY 6: DUALCAST / INSTANT BUFF -> LONG CAST (the "2" spell)
        // Never spend Dualcast/Swiftcast on a short spell (proc or Jolt).
        // =====================================================================
        if (!IsInMeleeCombo && ManaStacks != 3 && InCombat
            && (HasHostilesInRange || HasHostilesInMaxRange)
            && hasInstantBuff)
        {
            // AoE at 3+ targets: Impact
            if (NumberOfHostilesInRangeOf(5) >= 3 && ImpactPvE.CanUse(out act))
                return true;

            // ST: pick the spell that balances mana
            if (BlackMana > WhiteMana)
            {
                // Black leads -> cast Veraero III (adds White)
                if (VeraeroIiiPvE.CanUse(out act, skipStatusProvideCheck: true)) return true;
                if (VeraeroPvE.CanUse(out act, skipStatusProvideCheck: true)) return true;
                // Fallback
                if (VerthunderIiiPvE.CanUse(out act, skipStatusProvideCheck: true)) return true;
                if (VerthunderPvE.CanUse(out act, skipStatusProvideCheck: true)) return true;
            }
            else
            {
                // White leads or equal -> cast Verthunder III (adds Black)
                if (VerthunderIiiPvE.CanUse(out act, skipStatusProvideCheck: true)) return true;
                if (VerthunderPvE.CanUse(out act, skipStatusProvideCheck: true)) return true;
                // Fallback
                if (VeraeroIiiPvE.CanUse(out act, skipStatusProvideCheck: true)) return true;
                if (VeraeroPvE.CanUse(out act, skipStatusProvideCheck: true)) return true;
            }
        }

        // Acceleration instant -> also treat as "2" spell when no procs available
        if (!IsInMeleeCombo && ManaStacks != 3 && HasAccelerate && !HasSwift && !HasDualcast
            && InCombat && HasHostilesInMaxRange)
        {
            // AoE
            if (NumberOfHostilesInRangeOf(5) >= 2 && ImpactPvE.CanUse(out act))
                return true;

            // ST: balance mana
            int diff = BlackMana - WhiteMana;
            if (diff > 0)
            {
                if (VeraeroIiiPvE.CanUse(out act)) return true;
                if (VeraeroPvE.CanUse(out act)) return true;
            }
            else
            {
                if (VerthunderIiiPvE.CanUse(out act)) return true;
                if (VerthunderPvE.CanUse(out act)) return true;
            }
        }

        // =====================================================================
        // PRIORITY 7: HARDCAST SHORT SPELLS (the "1" spell to generate Dualcast)
        // Proc spells first (Verfire/Verstone), then Jolt III as filler.
        // =====================================================================

        // When both procs are available, use the one expiring soonest to prevent drops
        if (VerstonePvE.EnoughLevel && !hasInstantBuff)
        {
            if (CanVerBoth)
            {
                switch (VerEndsFirst)
                {
                    case "VerFire":
                        if (VerfirePvE.CanUse(out act)) return true;
                        if (VerstonePvE.CanUse(out act)) return true;
                        break;
                    case "VerStone":
                        if (VerstonePvE.CanUse(out act)) return true;
                        if (VerfirePvE.CanUse(out act)) return true;
                        break;
                    case "Equal":
                    default:
                        // Equal duration: pick the one that helps balance mana
                        if (WhiteMana < BlackMana)
                        {
                            if (VerstonePvE.CanUse(out act)) return true;
                            if (VerfirePvE.CanUse(out act)) return true;
                        }
                        else
                        {
                            if (VerfirePvE.CanUse(out act)) return true;
                            if (VerstonePvE.CanUse(out act)) return true;
                        }
                        break;
                }
            }
            else
            {
                // Only one or no proc: use whichever is available
                if (VerfirePvE.CanUse(out act)) return true;
                if (VerstonePvE.CanUse(out act)) return true;
            }
        }

        // Pre-Verstone level: just use Verfire
        if (!VerstonePvE.EnoughLevel && !hasInstantBuff && VerfirePvE.CanUse(out act))
            return true;

        // No procs and no instant: hardcast filler
        if (!CanInstantCast && !CanVerEither)
        {
            // AoE filler at 3+ targets: Verthunder II / Veraero II (mana balance)
            if (NumberOfHostilesInRangeOf(5) >= 3)
            {
                if (WhiteMana < BlackMana)
                {
                    if (VeraeroIiPvE.CanUse(out act)) return true;
                    if (VerthunderIiPvE.CanUse(out act)) return true;
                }
                else
                {
                    if (VerthunderIiPvE.CanUse(out act)) return true;
                    if (VeraeroIiPvE.CanUse(out act)) return true;
                }
            }

            // ST filler: Jolt III / Jolt II / Jolt
            if (JoltPvE.CanUse(out act))
                return true;
        }

        // =====================================================================
        // OUT OF COMBAT: Vercure for Dualcast
        // =====================================================================
        if (UseVercure && !InCombat && VercurePvE.CanUse(out act))
            return true;

        return base.GeneralGCD(out act);
    }

    #endregion
}
