namespace RotationSolver.ExtraRotations.Tank;

[Rotation("SezuraiPLD", CombatType.PvE, GameVersion = "7.41",
    Description = "Balance-aligned PLD with FoF burst, Confiteor combo, Atonement optimization, and proper defensive layering.")]
[SourceCode(Path = "main/ExtraRotations/Tank/SezuraiPLD.cs")]
[ExtraRotation]
public sealed class SezuraiPLD : PaladinRotation
{
    #region Config Options

    [Range(0f, 0.25f, ConfigUnitType.Percent)]
    [RotationConfig(CombatType.PvE, Name = "Action Ahead Override (0 = use global setting)")]
    public float ActionAheadOverride { get; set; } = 0f;

    [RotationConfig(CombatType.PvE, Name = "Use Gemdraught/Pot during Fight or Flight windows")]
    public bool BurstMed { get; set; } = true;

    [Range(50, 100, ConfigUnitType.Pixels)]
    [RotationConfig(CombatType.PvE, Name = "Oath gauge threshold for auto-Sheltron (to prevent overcap)")]
    public int SheltronThreshold { get; set; } = 100;

    [RotationConfig(CombatType.PvE, Name = "Auto-use defensive cooldowns when taking damage")]
    public bool AutoMitigation { get; set; } = true;

    [RotationConfig(CombatType.PvE, Name = "Use both Intervene charges during Fight or Flight")]
    public bool InterveneInBurst { get; set; } = true;

    [RotationConfig(CombatType.PvE, Name = "Use Holy Spirit when out of melee range")]
    public bool HolySpiritRanged { get; set; } = true;

    [Range(0.1f, 0.5f, ConfigUnitType.Percent)]
    [RotationConfig(CombatType.PvE, Name = "HP% to use Clemency in emergencies (0.1 = 10%)")]
    public float ClemencyThreshold { get; set; } = 0.2f;

    [RotationConfig(CombatType.PvE, Name = "Use Intervene as gap closer")]
    public bool UseGapCloser { get; set; } = true;

    #endregion

    #region Burst State

    /// <summary>
    /// Whether the user has burst enabled in the framework.
    /// </summary>
    private bool CanBurst => MergedStatus.HasFlag(AutoStatus.Burst);

    /// <summary>
    /// True when Fight or Flight buff is active or we just activated it.
    /// This defines the burst window where we dump our strongest GCDs and oGCDs.
    /// </summary>
    private bool InBurstWindow => HasFightOrFlight;

    /// <summary>
    /// True when we are mid-Confiteor combo and MUST finish it.
    /// Confiteor -> Blade of Faith -> Blade of Truth -> Blade of Valor.
    /// Dropping the combo is a major DPS loss.
    /// </summary>
    private bool InConfiteorCombo => BladeOfFaithReady || BladeOfTruthReady || BladeOfValorReady;

    /// <summary>
    /// True when we are mid-Atonement chain and should finish it.
    /// Atonement -> Supplication -> Sepulchre.
    /// </summary>
    private bool InAtonementChain => SupplicationReady || SepulchreReady;

    /// <summary>
    /// True when Blade of Honor (follow-up to Imperator) is ready.
    /// Must be used immediately after Imperator.
    /// </summary>
    private bool HasBladeOfHonor => BladeOfHonorReady;

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
        ImGui.Text($"--- Sezurai PLD ---");
        ImGui.Separator();
        ImGui.Text($"CanBurst: {CanBurst}");
        ImGui.Text($"InBurstWindow (FoF): {InBurstWindow}");
        ImGui.Text($"HasFightOrFlight: {HasFightOrFlight}");
        ImGui.Spacing();
        ImGui.Text($"OathGauge: {OathGauge}");
        ImGui.Text($"RequiescatStacks: {RequiescatStacks}");
        ImGui.Spacing();
        ImGui.Text($"HasConfiteorReady: {HasConfiteorReady}");
        ImGui.Text($"InConfiteorCombo: {InConfiteorCombo}");
        ImGui.Text($"HasBladeOfHonor: {HasBladeOfHonor}");
        ImGui.Spacing();
        ImGui.Text($"HasAtonementReady: {HasAtonementReady}");
        ImGui.Text($"SupplicationReady: {SupplicationReady}");
        ImGui.Text($"SepulchreReady: {SepulchreReady}");
        ImGui.Text($"InAtonementChain: {InAtonementChain}");
        ImGui.Spacing();
        ImGui.Text($"HasDivineMight: {HasDivineMight}");
        ImGui.Text($"HasGoringBlade: {StatusHelper.PlayerHasStatus(true, StatusID.GoringBladeReady)}");
        ImGui.Spacing();
        ImGui.Text($"FoF CD Remain: {FightOrFlightPvE.Cooldown.RecastTimeRemainOneCharge:F1}s");
        ImGui.Text($"--- BMR Timeline ---");
        ImGui.Text($"Active: {BmrActive}{(BmrActive ? $" ({DataCenter.BmrActiveModuleName})" : "")}");
        if (BmrActive)
        {
            ImGui.Text($"Raidwide In: {(BmrRaidwideIn < 9999f ? $"{BmrRaidwideIn:F1}s" : "None")}");
            ImGui.Text($"Tankbuster In: {(BmrTankbusterIn < 9999f ? $"{BmrTankbusterIn:F1}s" : "None")}");
            ImGui.Text($"Knockback In: {(BmrKnockbackIn < 9999f ? $"{BmrKnockbackIn:F1}s" : "None")}");
            ImGui.Text($"Downtime In: {(BmrDowntimeIn < 9999f ? $"{BmrDowntimeIn:F1}s" : "None")}");
            ImGui.Text($"Vulnerable In: {(BmrVulnerableIn < 9999f ? $"{BmrVulnerableIn:F1}s" : "None")}");
        }
    }

    #endregion

    #region Countdown & Opener
    // === PLD OPENER (7.4 Balance / Icy Veins) ===
    // Pre-pull: Holy Spirit precast(-1.75s)
    // GCD1: Fast Blade → Pot (weave)
    // GCD2: Riot Blade
    // GCD3: Royal Authority → FoF (weave) + Imperator (weave)
    // GCD4: Confiteor → Circle of Scorn (weave) + Expiacion (weave)
    // GCD5: Blade of Faith → Intervene (weave)
    // GCD6: Blade of Truth → Intervene (weave)
    // GCD7: Blade of Valor → Blade of Honor (weave)
    // GCD8: Goring Blade
    // GCD9: Atonement → GCD10: Supplication → GCD11: Sepulchre
    // GCD12: Holy Spirit (Divine Might)
    //
    // === BURST WINDOWS (60s cycle — every window is the same) ===
    // PLD has FoF on 60s CD with no 120s cooldowns, so every window is identical:
    //   FoF + Imperator → Confiteor combo (4 GCDs) → Goring Blade → Atonement chain
    //   Weave Circle of Scorn + Expiacion + Intervene x2 during FoF
    //
    // === FILLER (outside FoF) ===
    // 9-GCD loop: Royal Authority → Atonement → Fast Blade → Riot Blade → Supplication
    //   → Holy Spirit → Sepulchre → Fast Blade → Riot Blade → (repeat)
    // Use Circle of Scorn + Expiacion on CD between FoF windows
    // Never use Royal Authority before spending ALL existing procs

    protected override IAction? CountDownAction(float remainTime)
    {
        // Pre-pull pot at ~2s before pull if burst med is enabled
        if (BurstMed && remainTime <= 2f && remainTime > 1f && UseBurstMedicine(out var potAct))
            return potAct;

        // Shield Lob at ~0.7s for instant ranged pull (consistent with other tanks)
        if (remainTime <= 0.7f && ShieldLobPvE.CanUse(out var act))
            return act;

        return base.CountDownAction(remainTime);
    }

    #endregion

    #region Additional oGCD Logic

    [RotationDesc(ActionID.IntervenePvE)]
    protected override bool MoveForwardAbility(IAction nextGCD, out IAction? act)
    {
        if (UseGapCloser && IntervenePvE.CanUse(out act))
            return true;
        return base.MoveForwardAbility(nextGCD, out act);
    }

    [RotationDesc(ActionID.DivineVeilPvE, ActionID.PassageOfArmsPvE)]
    protected override bool DefenseAreaAbility(IAction nextGCD, out IAction? act)
    {
        // BMR-aware: time Divine Veil before raidwides (shield needs a heal to pop)
        // Balance: "Divine Veil creates a party barrier when you receive a heal"
        bool rwSoon = BmrActive && BmrRaidwideIn is > 0 and <= 5f;

        if (rwSoon && DivineVeilPvE.CanUse(out act))
            return true;

        // Without BMR: use whenever framework triggers
        if (!BmrActive && DivineVeilPvE.CanUse(out act))
            return true;

        // Passage of Arms: channel-based, only for specific mechanics
        // Don't use with BMR timing (locks you in place = DPS loss)
        if (!BmrActive && PassageOfArmsPvE.CanUse(out act))
            return true;

        return base.DefenseAreaAbility(nextGCD, out act);
    }

    [RotationDesc(ActionID.HolySheltronPvE, ActionID.SheltronPvE, ActionID.SentinelPvE, ActionID.GuardianPvE, ActionID.RampartPvE, ActionID.BulwarkPvE, ActionID.ReprisalPvE)]
    protected override bool DefenseSingleAbility(IAction nextGCD, out IAction? act)
    {
        if (!AutoMitigation)
            return base.DefenseSingleAbility(nextGCD, out act);

        // Don't use defensive CDs under Hallowed Ground
        if (StatusHelper.PlayerHasStatus(true, StatusID.HallowedGround))
            return base.DefenseSingleAbility(nextGCD, out act);

        // BMR-aware: when TB is imminent, Sheltron + ONE longer CD
        bool tbSoon = BmrActive && BmrTankbusterIn is > 0 and <= 6f;

        // 1. Holy Sheltron / Sheltron — always first (short CD, Oath spender)
        if (UseOath(out act))
            return true;

        if (tbSoon)
        {
            // Layer ONE longer CD for big TBs, then stop
            if (BulwarkPvE.CanUse(out act, skipAoeCheck: true))
                return true;
            return base.DefenseSingleAbility(nextGCD, out act);
        }

        // Non-BMR / reactive path: stagger long CDs
        // 2. Bulwark (block rate buff)
        if (BulwarkPvE.CanUse(out act, skipAoeCheck: true))
            return true;

        // 3. Sentinel/Guardian (30% mitigation, 120s CD)
        if ((!RampartPvE.Cooldown.IsCoolingDown || RampartPvE.Cooldown.ElapsedAfter(60))
            && GuardianPvE.CanUse(out act) && GuardianPvE.EnoughLevel)
            return true;

        if ((!RampartPvE.Cooldown.IsCoolingDown || RampartPvE.Cooldown.ElapsedAfter(60))
            && SentinelPvE.CanUse(out act) && !GuardianPvE.EnoughLevel)
            return true;

        // 4. Rampart (20% mitigation, 90s CD)
        if (((GuardianPvE.EnoughLevel && GuardianPvE.Cooldown.IsCoolingDown && GuardianPvE.Cooldown.ElapsedAfter(60))
            || (!GuardianPvE.EnoughLevel && SentinelPvE.EnoughLevel && SentinelPvE.Cooldown.IsCoolingDown && SentinelPvE.Cooldown.ElapsedAfter(60))
            || !SentinelPvE.EnoughLevel)
            && RampartPvE.CanUse(out act))
            return true;

        // 5. Reprisal (10% damage down on enemies)
        if (ReprisalPvE.CanUse(out act, skipAoeCheck: true))
            return true;

        return base.DefenseSingleAbility(nextGCD, out act);
    }

    [RotationDesc(ActionID.ClemencyPvE)]
    protected override bool HealSingleGCD(out IAction? act)
    {
        // Emergency Clemency: only when healers are dead or HP is critical
        if (ClemencyPvE.CanUse(out act) && ClemencyPvE.Target.Target?.GetHealthRatio() < ClemencyThreshold)
            return true;

        return base.HealSingleGCD(out act);
    }

    [RotationDesc]
    protected override bool AntiKnockbackAbility(IAction nextGCD, out IAction? act)
    {
        if (ArmsLengthPvE.CanUse(out act))
            return true;
        return base.AntiKnockbackAbility(nextGCD, out act);
    }

    [RotationDesc]
    protected sealed override bool InterruptAbility(IAction nextGCD, out IAction? act)
    {
        if (InterjectPvE.CanUse(out act))
            return true;
        if (LowBlowPvE.CanUse(out act))
            return true;
        return base.InterruptAbility(nextGCD, out act);
    }

    [RotationDesc(ActionID.ShieldBashPvE)]
    protected override bool MyInterruptGCD(out IAction? act)
    {
        // Shield Bash as backup stun if Low Blow is on CD
        if (LowBlowPvE.Cooldown.IsCoolingDown && ShieldBashPvE.CanUse(out act))
            return true;

        return base.MyInterruptGCD(out act);
    }

    #endregion

    #region oGCD Logic

    protected override bool EmergencyAbility(IAction nextGCD, out IAction? act)
    {
        // Hallowed Ground: emergency invuln at critical HP
        if (HallowedGroundPvE.CanUse(out act) && Player?.GetHealthRatio() <= HealthForDyingTanks)
            return true;

        // Medicine: use during Fight or Flight window
        if (BurstMed && HasFightOrFlight && InCombat && UseBurstMedicine(out act))
            return true;

        // Fight or Flight: 60s burst buff, use on cooldown when burst is enabled
        // Balance guide: "Fight or Flight should always be used on cooldown"
        // Opener timing: use after Royal Authority (when we have Atonement Ready + Divine Might)
        // During combat: use on CD, preferring when we have procs to spend in the window
        if (CanBurst && InCombat && HasHostilesInRange)
        {
            if (FightOrFlightPvE.CanUse(out act))
                return true;
        }

        // Imperator: weave immediately after Fight or Flight
        // Grants Confiteor Ready + Requiescat stacks + Blade of Honor follow-up
        if (RequiescatMasteryTrait.EnoughLevel)
        {
            if ((IsLastAbility(true, FightOrFlightPvE) || HasFightOrFlight)
                && ImperatorPvE.CanUse(out act, skipAoeCheck: true, usedUp: true, skipTTKCheck: true))
                return true;

            // Fallback: Requiescat if Imperator not available
            if ((IsLastAbility(true, FightOrFlightPvE) || HasFightOrFlight)
                && RequiescatPvE.CanUse(out act, skipAoeCheck: true, usedUp: true))
                return true;
        }

        // Pre-Imperator: Requiescat at lower levels
        if (!RequiescatMasteryTrait.EnoughLevel)
        {
            if ((IsLastAbility(true, FightOrFlightPvE) || HasFightOrFlight)
                && RequiescatPvE.CanUse(out act, skipAoeCheck: true, usedUp: true))
                return true;
        }

        return base.EmergencyAbility(nextGCD, out act);
    }

    [RotationDesc(ActionID.BladeOfHonorPvE, ActionID.CircleOfScornPvE, ActionID.ExpiacionPvE, ActionID.IntervenePvE)]
    protected override bool AttackAbility(IAction nextGCD, out IAction? act)
    {
        // 1. Blade of Honor: follow-up to Imperator, use immediately (cannot hold)
        if (BladeOfHonorPvE.CanUse(out act, skipAoeCheck: true))
            return true;

        // 2. Circle of Scorn (DoT + damage, 30s CD)
        // During FoF: weave after Confiteor. Outside FoF: use on CD, don't hold.
        // Guide: "Circle of Scorn and Expiacion should be used on cooldown after the opener"
        if (CircleOfScornPvE.CanUse(out act, skipAoeCheck: true, skipTTKCheck: true))
        {
            // Ensure FoF/Imperator have already been used this window before we fire oGCDs
            if (FightOrFlightPvE.Cooldown.IsCoolingDown
                && (ImperatorPvE.EnoughLevel && ImperatorPvE.Cooldown.IsCoolingDown || !ImperatorPvE.EnoughLevel))
                return true;

            // Outside burst: use on CD to prevent drift (30s CD, fits 1 free use between 60s FoF windows)
            if (!HasFightOrFlight && !FightOrFlightPvE.Cooldown.WillHaveOneCharge(5))
                return true;
        }

        // 3. Expiacion (damage, 30s CD) - same logic as Circle of Scorn
        if (ExpiacionPvE.EnoughLevel && ExpiacionPvE.CanUse(out act, skipAoeCheck: true))
        {
            if (FightOrFlightPvE.Cooldown.IsCoolingDown
                && (ImperatorPvE.EnoughLevel && ImperatorPvE.Cooldown.IsCoolingDown || !ImperatorPvE.EnoughLevel))
                return true;

            if (!HasFightOrFlight && !FightOrFlightPvE.Cooldown.WillHaveOneCharge(5))
                return true;
        }

        // Low level: Spirits Within replaces Expiacion
        if (!ExpiacionPvE.EnoughLevel && SpiritsWithinPvE.CanUse(out act, skipAoeCheck: true))
        {
            if (FightOrFlightPvE.Cooldown.IsCoolingDown)
                return true;
        }

        // 4. Intervene (gap closer, 2 charges, 30s recharge)
        // During FoF: spend both charges for damage. Outside: hold for FoF unless capping.
        // Guide: "Two charges; prioritize holding for FoF windows"
        if (!IsMoving && IntervenePvE.CanUse(out act, usedUp: InterveneInBurst && HasFightOrFlight))
            return true;

        return base.AttackAbility(nextGCD, out act);
    }

    [RotationDesc(ActionID.HolySheltronPvE, ActionID.SheltronPvE)]
    protected override bool GeneralAbility(IAction nextGCD, out IAction? act)
    {
        // Auto-Sheltron to prevent Oath gauge overcap
        if (InCombat && OathGauge >= SheltronThreshold && SheltronThreshold > 0 && UseOath(out act))
            return true;

        return base.GeneralAbility(nextGCD, out act);
    }

    #endregion

    #region GCD Logic

    protected override bool GeneralGCD(out IAction? act)
    {
        // =======================================================
        // PRIORITY 1: Confiteor combo (MUST finish once started)
        // Confiteor -> Blade of Faith -> Blade of Truth -> Blade of Valor
        // The base class ConfiteorPvE action handles the entire chain
        // via action replacement (Confiteor becomes BoF -> BoT -> BoV)
        // =======================================================
        if (ConfiteorPvE.CanUse(out act, usedUp: true, skipAoeCheck: true))
            return true;

        // =======================================================
        // PRIORITY 2: Goring Blade (high-potency weaponskill)
        // Granted by Fight or Flight, must be used during the buff window.
        // Guide: "use Goring Blade during FoF for DoT"
        // =======================================================
        if (GoringBladePvE.CanUse(out act))
            return true;

        // =======================================================
        // PRIORITY 3: Atonement chain (always finish if started)
        // Atonement -> Supplication -> Sepulchre
        // Each step replaces the previous on the action button.
        // Do NOT start Atonement if FoF is about to come up and we
        // would waste the proc outside the buff window.
        // =======================================================

        // Continue chain: Supplication (mid-chain, must finish)
        if (SupplicationReady && SupplicationPvE.CanUse(out act))
            return true;

        // Continue chain: Sepulchre (final hit, must finish)
        if (SepulchreReady && SepulchrePvE.CanUse(out act))
            return true;

        // Start chain: Atonement (only if we have the proc)
        // During FoF: always use. Outside FoF: use to avoid losing the proc,
        // but prefer combo if FoF is coming up very soon (within 1 GCD)
        if (HasAtonementReady)
        {
            // Use if: in FoF, or FoF is far away, or about to expire
            if (HasFightOrFlight
                || !FightOrFlightPvE.Cooldown.WillHaveOneCharge(1)
                || StatusHelper.PlayerWillStatusEndGCD(1, 0, true, StatusID.AtonementReady))
            {
                if (AtonementPvE.CanUse(out act))
                    return true;
            }
        }

        // =======================================================
        // PRIORITY 4: Holy Spirit with Divine Might / Requiescat
        // Divine Might: instant-cast Holy Spirit proc from Royal Authority / Prominence
        // Requiescat stacks: instant-cast from Imperator/Requiescat
        // Use in FoF window or when about to expire.
        // =======================================================

        // AoE: Holy Circle with Divine Might or Requiescat stacks (3+ targets)
        if ((HasDivineMight || RequiescatStacks > 0) && HolyCirclePvE.CanUse(out act, skipCastingCheck: true))
            return true;

        // Single target: Holy Spirit with Divine Might or Requiescat stacks
        if ((HasDivineMight || RequiescatStacks > 0) && HolySpiritPvE.CanUse(out act, skipCastingCheck: true))
            return true;

        // =======================================================
        // PRIORITY 5: AoE Combo (2+ targets)
        // Total Eclipse -> Prominence
        // Prominence grants Divine Might at high enough level.
        // =======================================================
        if (ProminencePvE.CanUse(out act, skipStatusProvideCheck: !EnhancedProminenceTrait.EnoughLevel))
            return true;

        if (TotalEclipsePvE.CanUse(out act))
            return true;

        // =======================================================
        // PRIORITY 6: Single Target Combo
        // Fast Blade -> Riot Blade -> Royal Authority
        // Royal Authority grants Atonement Ready + Divine Might.
        // During filler phase, rebuild procs for the next FoF window.
        // =======================================================

        // If we still have unspent procs and FoF is far away,
        // try to interleave combo to set up for next burst.
        // However, do NOT break an active combo.
        if (RoyalAuthorityPvE.CanUse(out act))
            return true;

        if (!RoyalAuthorityPvE.Info.EnoughLevelAndQuest() && RageOfHalonePvE.CanUse(out act))
            return true;

        if (RiotBladePvE.CanUse(out act))
            return true;

        if (FastBladePvE.CanUse(out act))
            return true;

        // =======================================================
        // PRIORITY 7: Ranged fallback
        // Holy Spirit (hardcast) when out of melee range
        // Shield Lob if Holy Spirit is not available or moving
        // =======================================================
        if (HolySpiritRanged && StopMovingTime > 1 && HolySpiritPvE.CanUse(out act))
            return true;

        if (ShieldLobPvE.CanUse(out act))
            return true;

        return base.GeneralGCD(out act);
    }

    #endregion

    #region Extra Methods

    /// <summary>
    /// Uses Holy Sheltron (or Sheltron at lower levels) to spend Oath gauge.
    /// Oath gauge is purely defensive; spending it prevents overcap.
    /// </summary>
    private bool UseOath(out IAction? act)
    {
        if (HolySheltronPvE.CanUse(out act) && HolySheltronPvE.EnoughLevel)
            return true;

        if (SheltronPvE.CanUse(out act) && !HolySheltronPvE.EnoughLevel)
            return true;

        return false;
    }

    /// <summary>
    /// Override CanHealSingleSpell to restrict Clemency usage.
    /// PLD should almost never cast Clemency in optimized play;
    /// only allow it when healers are dead.
    /// </summary>
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

            return base.CanHealSingleSpell && aliveHealerCount == 0;
        }
    }

    #endregion
}
