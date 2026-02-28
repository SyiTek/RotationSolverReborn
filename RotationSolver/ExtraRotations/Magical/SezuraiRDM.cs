namespace RotationSolver.ExtraRotations.Magical;

/// <summary>
/// Balance-aligned Red Mage rotation for Dawntrail 7.4x.
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
/// </summary>
[Rotation("SezuraiRDM", CombatType.PvE, GameVersion = "7.41",
    Description = "Balance-aligned RDM with Embolden burst, mana balance optimization, double melee combo, and Dawntrail abilities.")]
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
        ImGui.Text($"EnoughManaForCombo: {HasEnoughManaForCombo}");
        ImGui.Text($"PoolMana: {PoolMana}  ManaNeededW: {ManaNeededWhite()}  ManaNeededB: {ManaNeededBlack()}");
    }

    #endregion

    #region Countdown & Opener
    // === RDM OPENER (7.4 Balance) ===
    // Pre-pull: Acceleration(-5s) → Pot(-2s) → Veraero III precast (hardcast w/ Accel for Grand Impact proc)
    // GCD1: Veraero III → GCD2: Grand Impact (instant, from Acceleration)
    // → Fleche (weave) → GCD3: Verthunder III/Veraero III (Dualcast)
    // → Embolden (weave) → Manafication (weave) → GCD4: Veraero III (Dualcast)
    // GCD5: Enchanted Riposte → Vice of Thorns (weave)
    // GCD6: Enchanted Zwerchhau → GCD7: Enchanted Redoublement
    // → Prefulgence (weave) → GCD8: Verholy/Verflare → GCD9: Scorch → GCD10: Resolution
    // → Second melee combo under remaining Embolden
    //
    // === EVEN BURST (120s) ===
    // Embolden (party buff) + Manafication (instant melee combo stacks)
    // Double melee combo: 2x Riposte→Zwerchhau→Redoublement→finisher chain
    // Prefulgence + Vice of Thorns + Grand Impact + Fleche + Contre Sixte under buffs
    //
    // === ODD BURST (60s) ===
    // Single melee combo only — Embolden + Manafication are 120s
    // Fleche + Contre Sixte on cooldown, pool mana for even window
    //
    // === FILLER / SUSTAIN ===
    // Dualcast loop: hardcast Verthunder/Veraero III → instant Dualcast proc → repeat
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
        if (AddlePvE.CanUse(out act))
            return true;
        if (MagickBarrierPvE.CanUse(out act))
            return true;
        return base.DefenseAreaAbility(nextGCD, out act);
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
        if (HasEmbolden
            || EmboldenPvE.Cooldown.HasOneCharge
            || (EmboldenPvE.Cooldown.WillHaveOneCharge(4f) && !IsInMeleeCombo))
        {
            if (InCombat && HasHostilesInMaxRange && ManaficationPvE.CanUse(out act))
                return true;
        }

        // === EMBOLDEN ===
        // 120s party buff: 5% party damage, 10% personal magic damage for 20s.
        // Use on cooldown, aligned with 2-min party buffs.
        // Grants Thorned Flourish -> Vice of Thorns.
        if (CanBurst && InCombat && HasHostilesInRange && EmboldenPvE.CanUse(out act))
            return true;

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

            if (!holdForBurst)
            {
                bool useUp = HasEmbolden || !EmboldenPvE.EnoughLevel
                    || AccelerationPvE.Cooldown.WillHaveXChargesGCD(2, 1);

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
        if (CanPrefulgence)
        {
            bool aboutToExpire = StatusHelper.PlayerWillStatusEndGCD(1, 0, true, StatusID.PrefulgenceReady);

            if (!DelayBurstOGCDs)
            {
                // No delay: use under Embolden or before expiry
                if ((HasEmbolden || aboutToExpire) && PrefulgencePvE.CanUse(out act))
                    return true;
            }
            else
            {
                // Delayed: prefer inside Embolden, fallback on expiry
                if ((HasEmbolden || aboutToExpire) && PrefulgencePvE.CanUse(out act))
                    return true;
            }
        }

        // === VICE OF THORNS (Embolden follow-up) ===
        // Available after using Embolden (Thorned Flourish buff).
        // Use inside Embolden window for buff value.
        if (HasThornedFlourish)
        {
            if (!DelayBurstOGCDs)
            {
                if (ViceOfThornsPvE.CanUse(out act))
                    return true;
            }
            else
            {
                // Delayed: only inside Embolden
                if (HasEmbolden && ViceOfThornsPvE.CanUse(out act))
                    return true;
            }
        }

        // === ENGAGEMENT (2 charges, 35s CD) ===
        // Spend freely during Embolden. Outside buffs: prevent overcap.
        // Preferred over Displacement for safety.
        {
            bool useUp = HasEmbolden || !EmboldenPvE.EnoughLevel
                || EngagementPvE.Cooldown.WillHaveXChargesGCD(2, 1);
            if (EngagementPvE.CanUse(out act, usedUp: useUp))
                return true;
        }

        // === CORPS-A-CORPS (2 charges, 35s CD) ===
        // Spend freely during Embolden. Outside buffs: prevent overcap.
        // Only use when not moving (prevents accidental gap close).
        {
            bool useUp = HasEmbolden || !EmboldenPvE.EnoughLevel
                || CorpsacorpsPvE.Cooldown.WillHaveXChargesGCD(2, 1);
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
        if (HasEnoughManaForCombo && !InFinisherChain)
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
                || (!PoolMana && EnoughManaComboNoPooling);

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
        // PRIORITY 5: ENCHANTED REPRISE (movement fallback)
        // =====================================================================
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
