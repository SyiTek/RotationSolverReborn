namespace RotationSolver.ExtraRotations.Tank;

[Rotation("SezuraiPLD", CombatType.PvE, GameVersion = "7.41",
    Description = "BMR-smart Balance-aligned PLD with timeline-aware mitigation, downtime-aware burst, FoF/Confiteor optimization, and proper defensive layering.")]
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

    /// <summary>
    /// True when we are medicated (potion active).
    /// </summary>
    private static bool IsMedicated => StatusHelper.PlayerHasStatus(true, StatusID.Medicated);

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
        ImGui.Text($"--- Burst ---");
        ImGui.Text($"CanBurst: {CanBurst}");
        ImGui.Text($"InBurstWindow (FoF): {InBurstWindow}");
        ImGui.Text($"HasFightOrFlight: {HasFightOrFlight}");
        ImGui.Text($"FoF CD: {(FightOrFlightPvE.Cooldown.IsCoolingDown ? $"{FightOrFlightPvE.Cooldown.RecastTimeRemainOneCharge:F1}s" : "Ready")}");
        ImGui.Spacing();
        ImGui.Text($"--- Gauge & Procs ---");
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
        ImGui.Text($"--- Status ---");
        ImGui.Text($"IsMedicated: {IsMedicated}");
        ImGui.Text($"InCombat: {InCombat}");
        ImGui.Text($"HP: {Player?.GetHealthRatio():P0}");
        ImGui.Text($"--- Defensive CDs ---");
        ImGui.Text($"HolySheltron: {(HolySheltronPvE.Cooldown.IsCoolingDown ? $"{HolySheltronPvE.Cooldown.RecastTimeRemain:F1}s" : "Ready")} (Oath: {OathGauge})");
        ImGui.Text($"Guardian: {(GuardianPvE.Cooldown.IsCoolingDown ? $"{GuardianPvE.Cooldown.RecastTimeRemain:F1}s" : "Ready")}");
        ImGui.Text($"Rampart: {(RampartPvE.Cooldown.IsCoolingDown ? $"{RampartPvE.Cooldown.RecastTimeRemain:F1}s" : "Ready")}");
        ImGui.Text($"Bulwark: {(BulwarkPvE.Cooldown.IsCoolingDown ? $"{BulwarkPvE.Cooldown.RecastTimeRemain:F1}s" : "Ready")}");
        ImGui.Text($"DivineVeil: {(DivineVeilPvE.Cooldown.IsCoolingDown ? $"{DivineVeilPvE.Cooldown.RecastTimeRemain:F1}s" : "Ready")}");
        ImGui.Text($"Reprisal: {(ReprisalPvE.Cooldown.IsCoolingDown ? $"{ReprisalPvE.Cooldown.RecastTimeRemain:F1}s" : "Ready")}");
        ImGui.Text($"HallowedGround: {(HallowedGroundPvE.Cooldown.IsCoolingDown ? $"{HallowedGroundPvE.Cooldown.RecastTimeRemain:F1}s" : "Ready")}");
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

    #region Countdown & Opener
    // === PLD OPENER (7.4 Balance / Icy Veins) ===
    // Pre-pull: Holy Spirit precast(-1.75s)
    // GCD1: Fast Blade -> Pot (weave)
    // GCD2: Riot Blade
    // GCD3: Royal Authority -> FoF (weave) + Imperator (weave)
    // GCD4: Confiteor -> Circle of Scorn (weave) + Expiacion (weave)
    // GCD5: Blade of Faith -> Intervene (weave)
    // GCD6: Blade of Truth -> Intervene (weave)
    // GCD7: Blade of Valor -> Blade of Honor (weave)
    // GCD8: Goring Blade
    // GCD9: Atonement -> GCD10: Supplication -> GCD11: Sepulchre
    // GCD12: Holy Spirit (Divine Might)
    //
    // === BURST WINDOWS (60s cycle -- every window is the same) ===
    // PLD has FoF on 60s CD with no 120s cooldowns, so every window is identical:
    //   FoF + Imperator -> Confiteor combo (4 GCDs) -> Goring Blade -> Atonement chain
    //   Weave Circle of Scorn + Expiacion + Intervene x2 during FoF
    //
    // === FILLER (outside FoF) ===
    // 9-GCD loop: Royal Authority -> Atonement -> Fast Blade -> Riot Blade -> Supplication
    //   -> Holy Spirit -> Sepulchre -> Fast Blade -> Riot Blade -> (repeat)
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

    [RotationDesc(ActionID.DivineVeilPvE, ActionID.PassageOfArmsPvE, ActionID.ReprisalPvE)]
    protected override bool DefenseAreaAbility(IAction nextGCD, out IAction? act)
    {
        if (!AutoMitigation)
            return base.DefenseAreaAbility(nextGCD, out act);

        // === BMR-AWARE RAIDWIDE MITIGATION ===
        // Balance: spread mits across raidwides, don't dump everything on one hit.
        // Mitigation is multiplicative (two 10% = 19%, not 20%), so spreading is more efficient.
        // Use 1-2 mits per raidwide max. Don't fire if raidwide is >8s away.
        //
        // Divine Veil: party barrier that activates when PLD receives a heal.
        // Must be used 3-5s before the raidwide so healers have time to trigger it.
        // Icy Veins: "Divine Veil...shine on raidwide damage. Use liberally."
        //
        // Passage of Arms: 15% reduction while channeling, persists 5s after flash.
        // Icy Veins: "flashing it briefly on the party is sufficient."
        // DPS loss from channel, so only use when we know a big raidwide is coming.
        //
        // Reprisal: 10% enemy damage reduction — use on SEPARATE raidwides from Veil.
        bool rwSoon = BMRActive && BMRRaidwideIn is > 0 and <= 5f;
        bool rwMedium = BMRActive && BMRRaidwideIn is > 5f and <= 8f;

        // With BMR active and no raidwide coming soon, don't waste party mits
        if (BMRActive && !rwSoon && !rwMedium)
            return base.DefenseAreaAbility(nextGCD, out act);

        // BMR: Divine Veil 3-8s before raidwide (shield needs a heal to pop)
        // Use at medium range so healers have time to trigger it before damage snapshot
        if ((rwSoon || rwMedium) && DivineVeilPvE.CanUse(out act))
            return true;

        // BMR: Reprisal when raidwide is imminent (1-5s) — don't stack with Veil on same RW
        // Only fire if Divine Veil is already up (covering this RW) or on cooldown
        if (rwSoon && ReprisalPvE.CanUse(out act, skipAoeCheck: true))
        {
            // Stack Reprisal if Veil is on CD (different RW coverage), or if Veil already activated
            if (DivineVeilPvE.Cooldown.IsCoolingDown
                || StatusHelper.PlayerHasStatus(true, StatusID.DivineVeil_1362))
                return true;
        }

        // BMR: Passage of Arms flash for very heavy raidwides when other tools are on CD
        // Only use when both Veil and Reprisal are on CD and a raidwide is imminent
        if (rwSoon && DivineVeilPvE.Cooldown.IsCoolingDown
            && ReprisalPvE.Cooldown.IsCoolingDown
            && PassageOfArmsPvE.CanUse(out act))
            return true;

        // === NON-BMR FALLBACK ===
        // Without BMR: use whenever the framework says DefenseArea is needed
        if (!BMRActive && DivineVeilPvE.CanUse(out act))
            return true;

        if (!BMRActive && ReprisalPvE.CanUse(out act, skipAoeCheck: true))
            return true;

        // Passage of Arms: channel-based, only for specific mechanics
        // Don't use with BMR timing by default (locks you in place = DPS loss)
        if (!BMRActive && PassageOfArmsPvE.CanUse(out act))
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

        // Skip stacking more mitigation if party already has 30%+ covered
        if (GetCurrentMitigationPercent() > 0.30f)
            return base.DefenseSingleAbility(nextGCD, out act);

        // === BMR-AWARE TANKBUSTER MITIGATION ===
        // Balance: Holy Sheltron on every TB + liberally on autos.
        // Layer ONE of Rampart/Guardian/Bulwark for heavy TBs, don't stack all.
        // Mitigation is multiplicative -- spreading across TBs is more efficient.
        // Icy Veins: "Use one of Rampart, Guardian, or Bulwark on every tankbuster."
        bool tbSoon = BMRActive && BMRTankbusterIn is > 0 and <= 6f;
        bool tbImminent = BMRActive && BMRTankbusterIn is > 0 and <= 3f;

        // With BMR active and no TB coming soon, don't waste single-target mits
        // Still allow Sheltron for overcap prevention (handled by GeneralAbility)
        if (BMRActive && !tbSoon)
            return base.DefenseSingleAbility(nextGCD, out act);

        // --- TB is imminent (BMR path) ---

        // 1. Holy Sheltron / Sheltron -- always first (short CD, Oath spender)
        // Balance: "Use Holy Sheltron on every tankbuster and liberally on auto-attacks"
        // Knight's Resolve (15% DR) + Knight's Benediction (HoT) make this very efficient
        if (UseOath(out act))
            return true;

        // 2. Layer ONE longer CD for the TB, then stop -- save others for next TB
        // Priority: Bulwark (shorter CD, block rate) > Guardian/Sentinel (30% DR) > Rampart (20% DR)
        // Don't stack -- one heavier mit per TB is the Balance-recommended approach

        // Bulwark: block rate buff, 90s CD -- good for auto-attack heavy phases too
        if (BulwarkPvE.CanUse(out act, skipAoeCheck: true))
            return true;

        // Guardian/Sentinel: 30% mitigation, 120s CD -- use for heavier TBs
        if (GuardianPvE.EnoughLevel && GuardianPvE.CanUse(out act))
            return true;
        if (!GuardianPvE.EnoughLevel && SentinelPvE.CanUse(out act))
            return true;

        // Rampart: 20% mitigation, 90s CD -- fallback when heavier CDs are down
        if (RampartPvE.CanUse(out act))
            return true;

        // Reprisal: 10% enemy damage reduction -- use if all personal CDs are on CD
        // Also helps co-tank if TB is shared
        if (tbImminent && ReprisalPvE.CanUse(out act, skipAoeCheck: true))
            return true;

        // BMR path: only one long CD per TB -- don't dump everything
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
        // === HALLOWED GROUND ===
        // Balance: "Hallowed Ground is a powerful tool and should be used proactively
        // on high-damage sequences like multi-hit tankbusters."
        // BMR-aware: only Hallowed if TB is actually imminent AND HP is critical.
        // Don't panic-Hallowed from random damage -- save for planned invulns.
        // Without BMR: use the framework's health threshold as before.
        bool tbImminent = BMRActive && BMRTankbusterIn is > 0 and <= 3f;
        bool hpCritical = Player?.GetHealthRatio() <= HealthForDyingTanks;

        if (HallowedGroundPvE.CanUse(out act))
        {
            // BMR path: only invuln if TB is about to hit and we're low
            if (BMRActive && tbImminent && hpCritical)
                return true;

            // Non-BMR path: use framework HP threshold
            if (!BMRActive && hpCritical)
                return true;
        }

        // === MEDICINE ===
        // Balance: pot should cover the full FoF window including Confiteor chain.
        // BMR-aware: don't pot if downtime is imminent (waste of pot duration).
        if (BurstMed && HasFightOrFlight && InCombat)
        {
            bool downtimeWastesPot = BMRActive && BMRDowntimeIn is > 0 and <= 10f;
            if (!downtimeWastesPot && UseBurstMedicine(out act))
                return true;
        }

        // === FIGHT OR FLIGHT ===
        // 60s burst buff, use on cooldown when burst is enabled.
        // Balance: "Fight or Flight should always be used on cooldown"
        // Opener timing: use after Royal Authority (when we have Atonement Ready + Divine Might)
        // During combat: use on CD, preferring when we have procs to spend in the window
        //
        // BMR: Don't start FoF if downtime is <12s (needs ~11 GCDs to get full value).
        // BMR: Hold briefly for vulnerability windows if very close.
        if (CanBurst && InCombat && HasHostilesInRange)
        {
            bool downtimeTooClose = BMRActive && BMRDowntimeIn is > 0 and < 12f;
            bool holdForVuln = BMRActive && BMRVulnerableIn is > 0 and <= 5f
                && !FightOrFlightPvE.Cooldown.WillHaveOneChargeGCD(2);

            if (!downtimeTooClose && !holdForVuln)
            {
                if (FightOrFlightPvE.CanUse(out act))
                    return true;
            }
        }

        // === BMR: DUMP FoF BEFORE DOWNTIME ===
        // If downtime is close (8-12s) and FoF is available, pop it NOW to get partial value
        // rather than losing it entirely during the untargetable phase.
        // Only if we have at least some procs/resources to spend.
        bool downtimeVerySoon = BMRActive && BMRDowntimeIn is > 0 and <= 12f;
        if (downtimeVerySoon && InCombat && HasHostilesInRange
            && (HasConfiteorReady || RequiescatStacks > 0 || HasAtonementReady || HasDivineMight))
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

        // === BMR: DUMP IMPERATOR/REQUIESCAT BEFORE DOWNTIME ===
        // If downtime is imminent and Imperator/Requiescat is available, fire it now
        // so we can dump Confiteor chain during remaining uptime.
        if (downtimeVerySoon && InCombat && HasHostilesInRange)
        {
            if (RequiescatMasteryTrait.EnoughLevel
                && ImperatorPvE.CanUse(out act, skipAoeCheck: true, usedUp: true, skipTTKCheck: true))
                return true;
            if (RequiescatPvE.CanUse(out act, skipAoeCheck: true, usedUp: true))
                return true;
        }

        return base.EmergencyAbility(nextGCD, out act);
    }

    [RotationDesc(ActionID.BladeOfHonorPvE, ActionID.CircleOfScornPvE, ActionID.ExpiacionPvE, ActionID.IntervenePvE)]
    protected override bool AttackAbility(IAction nextGCD, out IAction? act)
    {
        // === BMR DOWNTIME/VULNERABILITY AWARENESS ===
        bool downtimeSoon = BMRActive && BMRDowntimeIn is > 0 and <= 15f;
        bool downtimeVeryClose = BMRActive && BMRDowntimeIn is > 0 and <= 8f;

        // 1. Blade of Honor: follow-up to Imperator, use immediately (cannot hold)
        if (BladeOfHonorPvE.CanUse(out act, skipAoeCheck: true))
            return true;

        // === BMR: DUMP oGCDs BEFORE DOWNTIME ===
        // If downtime is approaching, fire Circle of Scorn + Expiacion now
        // rather than losing them during the untargetable phase.
        if (downtimeSoon)
        {
            if (CircleOfScornPvE.CanUse(out act, skipAoeCheck: true, skipTTKCheck: true))
                return true;
            if (ExpiacionPvE.EnoughLevel && ExpiacionPvE.CanUse(out act, skipAoeCheck: true))
                return true;
            if (!ExpiacionPvE.EnoughLevel && SpiritsWithinPvE.CanUse(out act, skipAoeCheck: true))
                return true;
            // Dump Intervene charges before downtime
            if (!IsMoving && IntervenePvE.CanUse(out act, usedUp: true))
                return true;
        }

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
        if (BMRPyreticActive) { act = null; return false; }
        // === BMR DOWNTIME AWARENESS ===
        bool downtimeVeryClose = BMRActive && BMRDowntimeIn is > 0 and <= 3f;
        bool downtimeSoon = BMRActive && BMRDowntimeIn is > 0 and <= 10f;

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
        // BMR: DUMP PROCS BEFORE DOWNTIME
        // If downtime is approaching (<10s), dump all remaining procs
        // (Atonement chain, Divine Might, Requiescat stacks) NOW
        // rather than losing them during the untargetable phase.
        // =======================================================
        if (downtimeSoon)
        {
            // Dump Atonement chain regardless of FoF timing
            if (HasAtonementReady && AtonementPvE.CanUse(out act))
                return true;

            // Dump Holy Spirit/Circle with Divine Might or Requiescat stacks
            if ((HasDivineMight || RequiescatStacks > 0) && HolyCirclePvE.CanUse(out act, skipCastingCheck: true))
                return true;
            if ((HasDivineMight || RequiescatStacks > 0) && HolySpiritPvE.CanUse(out act, skipCastingCheck: true))
                return true;
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
        // BMR: DON'T START NEW COMBOS BEFORE DOWNTIME
        // If downtime is <=3s away, don't start a new combo -- it'll
        // drop during the untargetable phase and waste combo progress.
        // Still allow finishing an in-progress combo.
        // =======================================================
        if (downtimeVeryClose)
        {
            // Still allow finishing an in-progress combo (CanUse checks combo state)
            if (RoyalAuthorityPvE.CanUse(out act))
                return true;
            if (!RoyalAuthorityPvE.Info.EnoughLevelAndQuest() && RageOfHalonePvE.CanUse(out act))
                return true;
            if (RiotBladePvE.CanUse(out act))
                return true;
            // Don't start Fast Blade -- use Shield Lob/Holy Spirit for a clean hit instead
            if (HolySpiritRanged && StopMovingTime > 1 && HolySpiritPvE.CanUse(out act))
                return true;
            if (ShieldLobPvE.CanUse(out act))
                return true;
            return base.GeneralGCD(out act);
        }

        // =======================================================
        // BMR: DON'T START BLADE COMBO BEFORE DOWNTIME
        // If downtime is <10s away, don't start Royal Authority combo
        // (takes ~3 GCDs = ~7.5s to complete). Spend existing procs instead.
        // =======================================================
        // (Already handled above: downtimeSoon dumps procs, and the combo
        //  section below runs normally when downtime is >10s or non-BMR)

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
