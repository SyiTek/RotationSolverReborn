namespace RotationSolver.ExtraRotations.Tank;

[Rotation("SezuraiDRK", CombatType.PvE, GameVersion = "7.41",
    Description = "Balance-aligned DRK with 5/2 Edge plan, Delirium burst, MP optimization, and Living Shadow alignment.")]
[SourceCode(Path = "main/ExtraRotations/Tank/SezuraiDRK.cs")]
[ExtraRotation]
public sealed class SezuraiDRK : DarkKnightRotation
{
    #region Config Options

    [Range(0f, 0.25f, ConfigUnitType.Percent)]
    [RotationConfig(CombatType.PvE, Name = "Action Ahead Override (0 = use global setting)")]
    public float ActionAheadOverride { get; set; } = 0f;

    [RotationConfig(CombatType.PvE, Name = "Auto Pot Usage (Gemdraught during even-minute burst)")]
    public bool BurstMed { get; set; } = true;

    [RotationConfig(CombatType.PvE, Name = "Auto TBN for Dark Arts generation before burst windows")]
    public bool AutoTBN { get; set; } = true;

    [RotationConfig(CombatType.PvE, Name = "Auto Mitigation (defensive CDs in tank-relevant content)")]
    public bool AutoMitigation { get; set; } = true;

    [RotationConfig(CombatType.PvE, Name = "Use Shadowstride as gap closer")]
    public bool UseShadowstride { get; set; } = true;

    [Range(0.3f, 0.8f, ConfigUnitType.Percent)]
    [RotationConfig(CombatType.PvE, Name = "HP% to auto-TBN during combat (not just pre-burst)")]
    public float TBNThreshold { get; set; } = 0.7f;

    #endregion

    #region Burst State

    /// <summary>
    /// Whether the user has burst enabled in the framework.
    /// </summary>
    private bool CanBurst => MergedStatus.HasFlag(AutoStatus.Burst);

    /// <summary>
    /// True when Living Shadow was recently summoned (within 24s).
    /// Living Shadow attacks for ~24 seconds after summoning.
    /// Even-minute windows have Living Shadow + Delirium together.
    /// </summary>
    private bool InEvenBurst => LivingShadowPvE.EnoughLevel
        && ShadowTime > 0;

    /// <summary>
    /// True when Delirium/Blood Weapon is on cooldown (recently used)
    /// and has been used within the last 15 seconds.
    /// </summary>
    private bool InDeliriumWindow => DeliriumPvE.EnoughLevel
        && DeliriumPvE.Cooldown.IsCoolingDown
        && !DeliriumPvE.Cooldown.ElapsedAfter(15);

    /// <summary>
    /// True during the combined burst window: either Living Shadow is active
    /// (even burst) or Delirium was recently used (any burst). This tells us
    /// we should be spending resources aggressively.
    /// </summary>
    private bool InBurstWindow => InEvenBurst || InDeliriumWindow || HasBuffs;

    /// <summary>
    /// True when both Blood Weapon and Delirium are on cooldown together,
    /// indicating the 2-minute mega-burst window.
    /// </summary>
    private bool InTwoMinBurst => DeliriumPvE.Cooldown.IsCoolingDown
        && ((LivingShadowPvE.Cooldown.IsCoolingDown && !LivingShadowPvE.Cooldown.ElapsedAfter(15))
            || !LivingShadowPvE.EnoughLevel);

    /// <summary>
    /// Odd-minute window: Delirium is on CD but Living Shadow is not available
    /// (it was used last even window and is still cooling down, but ShadowTime == 0).
    /// </summary>
    private bool InOddWindow => DeliriumPvE.EnoughLevel
        && DeliriumPvE.Cooldown.IsCoolingDown
        && !DeliriumPvE.Cooldown.ElapsedAfter(15)
        && !InEvenBurst;

    /// <summary>
    /// Whether the Scorn buff (Living Shadow follow-up for Disesteem) is active.
    /// </summary>
    private static bool HasDisesteem => StatusHelper.PlayerHasStatus(true, StatusID.Scorn);

    /// <summary>
    /// Not in the middle of a basic combo (Hard Slash / Syphon Strike).
    /// Safe to use Bloodspiller / Delirium GCDs without breaking combo.
    /// </summary>
    private static bool NoCombo => !IsLastGCD(ActionID.HardSlashPvE, ActionID.SyphonStrikePvE);

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
        ImGui.Text($"--- Sezurai DRK Status ---");
        ImGui.Separator();

        // Resources
        ImGui.Text($"MP: {CurrentMp} / 10000");
        ImGui.Text($"Blood: {Blood}");
        ImGui.Text($"HasDarkArts: {HasDarkArts}");
        ImGui.Text($"DarkSideTime: {DarkSideTime:F1}s");

        ImGui.Separator();

        // Burst State
        ImGui.Text($"CanBurst: {CanBurst}");
        ImGui.Text($"InEvenBurst: {InEvenBurst}");
        ImGui.Text($"InOddWindow: {InOddWindow}");
        ImGui.Text($"InBurstWindow: {InBurstWindow}");
        ImGui.Text($"InTwoMinBurst: {InTwoMinBurst}");
        ImGui.Text($"HasBuffs: {HasBuffs}");
        ImGui.Text($"PartyBuffDuration: {PartyBuffDuration:F1}s");

        ImGui.Separator();

        // Delirium
        ImGui.Text($"HasDelirium: {HasDelirium}");
        ImGui.Text($"DeliriumStacks: {DeliriumStacks}");
        ImGui.Text($"BloodWeaponStacks: {BloodWeaponStacks}");
        ImGui.Text($"ScarletDeliriumReady: {ScarletDeliriumReady}");
        ImGui.Text($"ComeuppanceReady: {ComeuppanceReady}");
        ImGui.Text($"TorcleaverReady: {TorcleaverReady}");

        ImGui.Separator();

        // Living Shadow
        ImGui.Text($"ShadowTime: {ShadowTime:F1}s");
        ImGui.Text($"HasDisesteem: {HasDisesteem}");

        ImGui.Separator();

        // Cooldown Tracking
        if (LivingShadowPvE.EnoughLevel)
            ImGui.Text($"Living Shadow CD: {LivingShadowPvE.Cooldown.RecastTimeRemainOneCharge:F1}s");
        if (DeliriumPvE.EnoughLevel)
            ImGui.Text($"Delirium CD: {DeliriumPvE.Cooldown.RecastTimeRemainOneCharge:F1}s");
        if (ShadowbringerPvE.EnoughLevel)
            ImGui.Text($"Shadowbringer Charges: {ShadowbringerPvE.Cooldown.CurrentCharges}");
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
    // === DRK OPENER (7.4 Balance / Icy Veins) ===
    // Pre-pull: TBN(-3s, on pulling tank for Dark Arts proc) → Pot(-2s) → Unmend(-1s)
    // GCD1: Hard Slash → Edge of Shadow (Dark Arts) + Living Shadow (weave)
    // GCD2: Syphon Strike
    // GCD3: Souleater → Delirium (weave) + Shadowbringer (weave)
    // GCD4: Disesteem → Salted Earth (weave) + Edge of Shadow (weave)
    // GCD5: Scarlet Delirium → Shadowbringer (weave) + Edge of Shadow (weave)
    // GCD6: Comeuppance → Carve and Spit (weave) + Edge of Shadow (weave)
    // GCD7: Torcleaver → Edge of Shadow (weave) + Salt and Darkness (weave)
    // GCD8: Bloodspiller
    //
    // === EVEN BURST (120s) — "5 Edge" window ===
    // Full resource dump: Living Shadow + Disesteem + Scarlet Delirium combo
    //   + 5x Edge of Shadow + 2x Shadowbringer + Carve and Spit
    // Align with raid buffs; enter with ~9000+ MP + Dark Arts proc banked
    //
    // === ODD BURST (60s) — "2 Edge" window ===
    // Delirium → 3x Bloodspiller (Scarlet combo is 120s) + 2-3x Edge of Shadow
    // Save MP, Shadowbringer charges, and Living Shadow for even windows
    //
    // === FILLER (5/2 MP Plan) ===
    // Even window: spend 5 Edge of Shadow under raid buffs
    // Odd window: spend 2 Edge of Shadow + 1 TBN (Dark Arts banked for next even)
    // Darkside maintenance: each Edge adds 30s (cap 60s), never let it drop
    // Blood: enter Delirium at ≤70 Blood; Bloodspiller to prevent overcap

    protected override IAction? CountDownAction(float remainTime)
    {
        // -3s: TBN on the pulling tank for Dark Arts proc going into the fight
        if (AutoTBN && remainTime <= 3f && TheBlackestNightPvE.CanUse(out IAction? act))
            return act;

        // -2s: Gemdraught of Strength for the opener
        if (BurstMed && remainTime <= 2f && remainTime > 1f && UseBurstMedicine(out act))
            return act;

        // Pull: Provoke if we have Grit, or Unmend for ranged pull
        if (remainTime <= CountDownAhead)
        {
            if (HasTankStance && ProvokePvE.CanUse(out act))
                return act;
        }

        // Unmend to initiate combat from range
        if (remainTime <= 0.7f && UnmendPvE.CanUse(out act))
            return act;

        return base.CountDownAction(remainTime);
    }

    #endregion

    #region oGCD Logic

    protected override bool EmergencyAbility(IAction nextGCD, out IAction? act)
    {
        // Medicine: use during even-minute burst (Living Shadow active or party buffs)
        if (BurstMed && InCombat && InBurstWindow && InEvenBurst && UseBurstMedicine(out act))
            return true;

        // TBN for Dark Arts generation:
        // - Use before burst windows to bank a free Edge of Shadow
        // - The shield must break to grant Dark Arts, so target self or tank being hit
        // Balance: "TBN is either damage neutral, or a gain, allowing you to move extra damage into buffs"
        if (AutoTBN && InCombat && !HasDarkArts && CurrentMp >= 3000)
        {
            // Pre-burst TBN: bank Dark Arts for the upcoming burst window
            // Use when Delirium is coming up soon and we don't have Dark Arts yet
            bool deliriumSoon = DeliriumPvE.EnoughLevel
                && DeliriumPvE.Cooldown.WillHaveOneCharge(8)
                && !DeliriumPvE.Cooldown.WillHaveOneCharge(2);

            if (deliriumSoon && TheBlackestNightPvE.CanUse(out act, targetOverride: TargetType.Self))
                return true;
        }

        return base.EmergencyAbility(nextGCD, out act);
    }

    [RotationDesc(ActionID.ShadowstridePvE)]
    protected override bool MoveForwardAbility(IAction nextGCD, out IAction? act)
    {
        if (UseShadowstride && ShadowstridePvE.CanUse(out act))
            return true;
        return base.MoveForwardAbility(nextGCD, out act);
    }

    protected override bool AttackAbility(IAction nextGCD, out IAction? act)
    {
        // === DARKSIDE MAINTENANCE ===
        // Edge/Flood of Darkness to maintain Darkside if it is about to fall off.
        // This takes absolute priority to avoid losing the 10% damage buff.
        if (DarkSideEndAfterGCD(3) && CurrentMp >= 3000)
        {
            if (FloodOfDarknessPvE.CanUse(out act))
                return true;
            if (EdgeOfDarknessPvE.CanUse(out act))
                return true;
        }

        // === DARK ARTS SPENDING (free Edge/Flood) ===
        // Dark Arts from TBN is free - spend it ASAP to avoid waste, especially in buffs
        if (HasDarkArts)
        {
            if (FloodOfShadowPvE.CanUse(out act))
                return true;
            if (EdgeOfShadowPvE.CanUse(out act))
                return true;
        }

        // === BURST COOLDOWNS ===
        if (CanBurst && InCombat && HasHostilesInRange)
        {
            // 1. Living Shadow: 120s CD, deploy during even-minute windows
            // Balance: "Living Shadow out early enough that its attacks fully fit into buffs"
            // Must have Darkside active for Shadowbringer (and Living Shadow needs Blood)
            if (!CombatElapsedLessGCD(1) && DarkSideTime > 0 && LivingShadowPvE.CanUse(out act, skipAoeCheck: true))
                return true;

            // 2. Delirium: 60s CD, grants 3 Delirium stacks + Blood Weapon stacks
            // Balance: "Use Delirium in your opener, and then on cooldown every 60 seconds"
            // Enter with <= 70 Blood to avoid overcapping from Blood Weapon
            if (!CombatElapsedLessGCD(1) && DeliriumPvE.CanUse(out act))
                return true;

            // Pre-Delirium Blood Weapon (if Delirium not high enough level)
            if (!DeliriumPvE.EnoughLevel && BloodWeaponPvE.CanUse(out act))
                return true;
        }

        // Don't dump oGCDs in first 3 seconds (let opener sequence properly)
        if (CombatElapsedLess(3))
        {
            act = null;
            return false;
        }

        // === MP SPENDING: EDGE OF SHADOW / FLOOD OF SHADOW ===
        // The "5/2 plan": 5 Edge of Shadow during even burst, 2 during odd burst
        // Even burst (120s): spend aggressively at 3000+ MP
        // Odd burst (60s): spend at 6000+ MP (save for upcoming even window)
        // Filler: only spend at 8500+ to avoid overcapping from Syphon Strike / Blood Weapon
        if (ShouldSpendMp())
        {
            if (FloodOfShadowPvE.CanUse(out act))
                return true;
            if (EdgeOfShadowPvE.CanUse(out act))
                return true;
        }

        // === SHADOWBRINGER (2 charges, 60s each) ===
        // Balance: "Hold both charges of Shadowbringer for 2-minute buffs"
        // During even burst: spend both charges
        // During odd burst or if about to overcap: spend one
        if (ShadowbringerPvE.EnoughLevel && DarkSideTime > 0)
        {
            // Even burst: use both charges aggressively
            if (InEvenBurst || InTwoMinBurst || HasBuffs)
            {
                if (ShadowbringerPvE.CanUse(out act, usedUp: true, skipAoeCheck: true))
                    return true;
            }

            // Overcap prevention: if at 2 charges, spend one
            if (ShadowbringerPvE.Cooldown.CurrentCharges >= 2)
            {
                if (ShadowbringerPvE.CanUse(out act, skipAoeCheck: true))
                    return true;
            }
        }

        // === CARVE AND SPIT / ABYSSAL DRAIN (60s CD) ===
        // Use on cooldown; Abyssal Drain for AoE, Carve and Spit for single target
        // Generates 600 MP (Carve and Spit)
        if (AbyssalDrainPvE.CanUse(out act))
            return true;
        if (CarveAndSpitPvE.CanUse(out act))
            return true;

        // === SALTED EARTH (90s ground AoE) ===
        // Place during burst windows, avoid when moving in non-high-end content
        if (!IsMoving || IsInHighEndDuty)
        {
            if (SaltedEarthPvE.CanUse(out act, skipAoeCheck: true))
                return true;
        }

        // === SALT AND DARKNESS (follow-up to Salted Earth) ===
        if (SaltAndDarknessPvE.CanUse(out act))
            return true;

        return base.AttackAbility(nextGCD, out act);
    }

    #endregion

    #region GCD Logic

    protected override bool GeneralGCD(out IAction? act)
    {
        // === DISESTEEM (Dawntrail addition) ===
        // Use immediately when Scorn buff is active. This is a high-potency
        // follow-up to Living Shadow and should be used in the burst window.
        // Guide: fire this inside raid buffs during even-minute windows.
        if (DisesteemPvE.CanUse(out act, skipComboCheck: true, skipAoeCheck: true))
            return true;

        // === SCARLET DELIRIUM COMBO (must finish once started) ===
        // Torcleaver -> Comeuppance -> Scarlet Delirium (check in reverse for combo chain)
        // These replace Bloodspiller during Delirium at level 96+.
        // The combo is: Scarlet Delirium -> Comeuppance -> Torcleaver
        // Each step checks its Ready flag via GetAdjustedActionId.
        if (TorcleaverPvE.CanUse(out act, skipComboCheck: true))
            return true;
        if (ComeuppancePvE.CanUse(out act, skipComboCheck: true))
            return true;
        if (ScarletDeliriumPvE.CanUse(out act, skipComboCheck: true))
            return true;

        // === AOE DELIRIUM: IMPALEMENT ===
        // AoE version of the Delirium spender at 96+
        if (ImpalementPvE.CanUse(out act))
            return true;

        // === BLOODSPILLER / QUIETUS (Blood Gauge Spenders) ===
        // Spend Blood: during Delirium (free), when overcapping (90+),
        // during burst windows, or when party buffs are active.
        if (ShouldSpendBlood())
        {
            if (QuietusPvE.CanUse(out act))
                return true;
            if (BloodspillerPvE.CanUse(out act, skipComboCheck: true))
                return true;
        }

        // === AOE COMBO: Unleash -> Stalwart Soul ===
        if (StalwartSoulPvE.CanUse(out act))
            return true;
        if (UnleashPvE.CanUse(out act))
            return true;

        // === SINGLE TARGET COMBO: Hard Slash -> Syphon Strike -> Souleater ===
        // Don't use basic combo GCDs if we have Delirium stacks to spend
        // (except at low level where Delirium doesn't replace combo)
        if (!HasDelirium || !ScarletDeliriumPvE.EnoughLevel)
        {
            if (SouleaterPvE.CanUse(out act))
                return true;
            if (SyphonStrikePvE.CanUse(out act))
                return true;
            if (HardSlashPvE.CanUse(out act))
                return true;
        }

        // === RANGED FALLBACK ===
        if (UnmendPvE.CanUse(out act))
            return true;

        return base.GeneralGCD(out act);
    }

    #endregion

    #region Defense Abilities

    [RotationDesc(ActionID.TheBlackestNightPvE, ActionID.OblationPvE, ActionID.DarkMindPvE,
        ActionID.ShadowedVigilPvE, ActionID.ShadowWallPvE, ActionID.RampartPvE)]
    protected override bool DefenseSingleAbility(IAction nextGCD, out IAction? act)
    {
        if (!AutoMitigation)
            return base.DefenseSingleAbility(nextGCD, out act);

        // BMR-aware: when TB is imminent, TBN is THE tool (25% HP shield, Dark Arts if broken)
        // Balance: "TBN is either damage neutral or a gain — always use it for tankbusters"
        bool tbSoon = BmrActive && BmrTankbusterIn is > 0 and <= 6f;

        if (tbSoon)
        {
            // TBN first: shield + Dark Arts generation
            if (CurrentMp >= 3000 && TheBlackestNightPvE.CanUse(out act, targetOverride: TargetType.Self))
                return true;
            // Oblation: 10% mit, use one charge
            if (OblationPvE.CanUse(out act, skipStatusProvideCheck: false, targetOverride: TargetType.Self))
                return true;
            // Don't dump all long CDs — 2 mits per TB is enough
            return base.DefenseSingleAbility(nextGCD, out act);
        }

        // Non-BMR / reactive path
        // Oblation: 10% mitigation, 2 charges, short CD - use first
        if (OblationPvE.CanUse(out act, usedUp: true, skipStatusProvideCheck: false, targetOverride: TargetType.Self))
            return true;

        // TBN: only use defensively if MP is comfortable and HP is low
        if (CurrentMp >= 6000
            && Player?.GetHealthRatio() < TBNThreshold
            && TheBlackestNightPvE.CanUse(out act, targetOverride: TargetType.Self))
            return true;

        // Dark Mind: 20% magic mitigation
        if (DarkMindPvE.CanUse(out act))
            return true;

        // Shadow Wall / Shadowed Vigil: 30% mitigation (alternate with Rampart)
        if ((!RampartPvE.Cooldown.IsCoolingDown || RampartPvE.Cooldown.ElapsedAfter(60))
            && ShadowedVigilPvE.CanUse(out act))
            return true;
        if ((!RampartPvE.Cooldown.IsCoolingDown || RampartPvE.Cooldown.ElapsedAfter(60))
            && ShadowWallPvE.CanUse(out act))
            return true;

        // Rampart: 20% mitigation
        if ((ShadowedVigilPvE.Cooldown.IsCoolingDown && ShadowedVigilPvE.Cooldown.ElapsedAfter(60)
             || ShadowWallPvE.Cooldown.IsCoolingDown && ShadowWallPvE.Cooldown.ElapsedAfter(60))
            && RampartPvE.CanUse(out act))
            return true;

        // Reprisal as a fallback
        if (ReprisalPvE.CanUse(out act))
            return true;

        return base.DefenseSingleAbility(nextGCD, out act);
    }

    [RotationDesc(ActionID.DarkMissionaryPvE, ActionID.ReprisalPvE)]
    protected override bool DefenseAreaAbility(IAction nextGCD, out IAction? act)
    {
        if (!AutoMitigation)
            return base.DefenseAreaAbility(nextGCD, out act);

        // BMR-aware: override burst-skip when raidwide is truly imminent
        // Balance: "Dark Missionary is 10% magic mitigation for the party"
        bool rwSoon = BmrActive && BmrRaidwideIn is > 0 and <= 5f;

        if (rwSoon)
        {
            if (DarkMissionaryPvE.CanUse(out act))
                return true;
            // Don't stack Reprisal on same raidwide — save for next one
            return base.DefenseAreaAbility(nextGCD, out act);
        }

        // Without BMR: skip during burst (oGCD slots needed for damage)
        if (!BmrActive && InBurstWindow)
            return base.DefenseAreaAbility(nextGCD, out act);

        // Dark Missionary: 10% magic mitigation for party
        if (DarkMissionaryPvE.CanUse(out act))
            return true;

        // Reprisal: 10% damage reduction on enemies
        if (ReprisalPvE.CanUse(out act, skipAoeCheck: true))
            return true;

        return base.DefenseAreaAbility(nextGCD, out act);
    }

    [RotationDesc(ActionID.ArmsLengthPvE)]
    protected sealed override bool AntiKnockbackAbility(IAction nextGCD, out IAction? act)
    {
        if (ArmsLengthPvE.CanUse(out act))
            return true;
        return base.AntiKnockbackAbility(nextGCD, out act);
    }

    [RotationDesc(ActionID.LowBlowPvE, ActionID.InterjectPvE)]
    protected sealed override bool InterruptAbility(IAction nextGCD, out IAction? act)
    {
        if (InterjectPvE.CanUse(out act))
            return true;
        if (LowBlowPvE.CanUse(out act))
            return true;
        return base.InterruptAbility(nextGCD, out act);
    }

    #endregion

    #region Extra Methods

    /// <summary>
    /// Prevents heal-single from firing during damage windows.
    /// </summary>
    public override bool CanHealSingleAbility => false;

    /// <summary>
    /// The "5/2 plan" MP spending logic.
    /// Balance: "Five Edge of Shadow in each party buff window" for even-minute,
    /// "two between burst windows" for the odd-minute filler.
    ///
    /// Even burst (120s): spend at 3000+ MP (aggressive, dump everything)
    /// Odd burst (60s): spend at 6000+ MP (moderate, save for next even window)
    /// Filler: spend at 8500+ MP only (overcap prevention)
    /// Darkside maintenance: always spend if Darkside is about to fall off
    /// </summary>
    private bool ShouldSpendMp()
    {
        // Not enough level for Edge/Flood of Shadow
        if (!FloodOfShadowPvE.EnoughLevel && !EdgeOfShadowPvE.EnoughLevel)
        {
            // Use Flood/Edge of Darkness for Darkside maintenance
            if (!FloodOfDarknessPvE.EnoughLevel && !EdgeOfDarknessPvE.EnoughLevel)
                return false;
            return CurrentMp >= 3000;
        }

        // Darkside about to fall off: always spend
        if (DarkSideEndAfterGCD(3))
            return CurrentMp >= 3000;

        // Dark Arts is free - already handled in AttackAbility above
        // (this method handles MP-costing Edge/Flood only)

        // During even-minute burst: spend aggressively (target 5 Edges)
        if (InEvenBurst || InTwoMinBurst)
            return CurrentMp >= 3000;

        // During party buffs: spend aggressively
        if (HasBuffs && PartyBuffDuration > WeaponTotal)
            return CurrentMp >= 3000;

        // During odd-minute burst: spend moderately (target 2 Edges)
        if (InOddWindow)
            return CurrentMp >= 6000;

        // Filler: only spend to prevent overcap
        // Syphon Strike restores 600 MP, Blood Weapon gives 600 per stack (3 stacks)
        // Max MP is 10000, so spend at 8500+ to prevent overcap from next combo
        return CurrentMp >= 8500;
    }

    /// <summary>
    /// Blood gauge spending logic.
    /// Balance: "Entering Delirium with 70 or fewer Blood Gauge points prevents overcapping"
    ///
    /// Spend during:
    /// - Delirium window (free Bloodspillers from stacks, handled by combo above)
    /// - Burst windows when at 50+ Blood
    /// - Overcap prevention at 90+ Blood
    /// - When party buffs are active
    ///
    /// Conserve when:
    /// - Living Shadow coming up soon (costs 50 Blood)
    /// - Delirium coming up soon (Blood Weapon will generate Blood)
    /// </summary>
    private bool ShouldSpendBlood()
    {
        // Must have enough Blood
        if (Blood < 50 && !HasDelirium)
            return false;

        // Delirium stacks: always spend (they are free and time-limited)
        if (HasDelirium)
            return true;

        // Save Blood for Living Shadow if it is coming up very soon
        if (LivingShadowPvE.EnoughLevel
            && LivingShadowPvE.Cooldown.WillHaveOneCharge(5)
            && Blood < 100)
            return false;

        // Overcap prevention: spend at 90+ Blood since combo/Blood Weapon will push to 100
        if (Blood >= 90)
            return true;

        // Don't break combo to spend Blood
        if (!NoCombo)
            return false;

        // During burst: spend Blood under party buffs for maximum value
        if (InBurstWindow && Blood >= 50)
            return true;

        // Party buffs active: spend Blood
        if (HasBuffs && PartyBuffDuration > WeaponTotal && Blood >= 50)
            return true;

        // Delirium coming soon: pre-spend Blood to enter at <= 70
        if (DeliriumPvE.EnoughLevel
            && DeliriumPvE.Cooldown.WillHaveOneCharge(8)
            && Blood > 70)
            return true;

        // Outside burst: spend at 70+ to prevent overcap from upcoming combo hits
        if (Blood >= 70 && !InBurstWindow)
            return true;

        return false;
    }

    #endregion
}
