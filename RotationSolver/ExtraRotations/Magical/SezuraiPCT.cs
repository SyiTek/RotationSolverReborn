namespace RotationSolver.ExtraRotations.Magical;

[Rotation("SezuraiPCT", CombatType.PvE, GameVersion = "7.41",
    Description = "Balance-aligned PCT with Starry Muse 9-spell burst, " +
                  "motif management, canvas cycling, and paint optimization.")]
[SourceCode(Path = "main/ExtraRotations/Magical/SezuraiPCT.cs")]
[ExtraRotation]
public sealed class SezuraiPCT : PictomancerRotation
{
    #region Config Options

    [Range(0f, 0.25f, ConfigUnitType.Percent)]
    [RotationConfig(CombatType.PvE, Name = "Action Ahead Override (0 = use global setting)")]
    public float ActionAheadOverride { get; set; } = 0f;

    [RotationConfig(CombatType.PvE, Name = "Experimental Pot Usage (during Starry Muse burst windows)")]
    public bool BurstMed { get; set; } = true;

    [RotationConfig(CombatType.PvE, Name = "Use Holy in White / Comet in Black while moving")]
    public bool PaintWhileMoving { get; set; } = true;

    [Range(1, 5, ConfigUnitType.None, 1)]
    [RotationConfig(CombatType.PvE, Name = "Paint overcap threshold (spend paint at this count outside burst)")]
    public int PaintOvercapAt { get; set; } = 5;

    [RotationConfig(CombatType.PvE, Name = "Prefer Comet in Black over Holy in White for overcap spending")]
    public bool PreferCometOvercap { get; set; } = false;

    [RotationConfig(CombatType.PvE, Name = "Use Swiftcast on hardcast Rainbow Drip (highest priority)")]
    public bool SwiftRainbowDrip { get; set; } = true;

    [RotationConfig(CombatType.PvE, Name = "Use Swiftcast on Creature Motif during burst prep")]
    public bool SwiftCreatureMotif { get; set; } = true;

    [RotationConfig(CombatType.PvE, Name = "Block defensive abilities during Starry Muse burst")]
    public bool BlockDefenseDuringBurst { get; set; } = true;

    [Range(20, 60, ConfigUnitType.None, 5)]
    [RotationConfig(CombatType.PvE, Name = "Seconds before Starry Muse to start drawing motifs (20-60)")]
    public int MotifPrepWindow { get; set; } = 30;

    #endregion

    #region Burst State Tracking

    /// <summary>
    /// Whether the user has burst enabled in the framework.
    /// </summary>
    private bool CanBurst => MergedStatus.HasFlag(AutoStatus.Burst);

    /// <summary>
    /// True when the Starry Muse buff is active (our 120s burst window).
    /// </summary>
    private static bool InBurstWindow => HasStarryMuse;

    /// <summary>
    /// True when Starry Muse will be ready within the specified seconds.
    /// Used for burst preparation.
    /// </summary>
    private bool StarryReadyWithin(float seconds) =>
        StarryMusePvE.Cooldown.WillHaveOneCharge(seconds);

    /// <summary>
    /// True when Inspiration is active (Hyperphantasia buff that reduces cast time).
    /// </summary>
    private static bool HasInspiration =>
        StatusHelper.PlayerHasStatus(true, StatusID.Inspiration);

    /// <summary>
    /// Hyperphantasia stack count (counts down from 5).
    /// </summary>
    private static byte HyperphantasiaStacks
    {
        get
        {
            byte stacks = StatusHelper.PlayerStatusStack(true, StatusID.Hyperphantasia);
            return stacks == byte.MaxValue ? (byte)0 : stacks;
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
        ImGui.Text($"--- Sezurai PCT Debug ---");
        ImGui.Text($"CanBurst: {CanBurst}");
        ImGui.Text($"InBurstWindow: {InBurstWindow}");
        ImGui.Text($"HasStarryMuse: {HasStarryMuse}");
        ImGui.Text($"HasHyperphantasia: {HasHyperphantasia}");
        ImGui.Text($"HyperphantasiaStacks: {HyperphantasiaStacks}");
        ImGui.Text($"HasInspiration: {HasInspiration}");
        ImGui.Text($"HasRainbowBright: {HasRainbowBright}");
        ImGui.Text($"HasStarstruck: {HasStarstruck}");
        ImGui.Text($"HasMonochromeTones: {HasMonochromeTones}");
        ImGui.Text($"HasSubtractivePalette: {HasSubtractivePalette}");
        ImGui.Text($"HasSubtractiveSpectrum: {HasSubtractiveSpectrum}");
        ImGui.Text($"HasHammerTime: {HasHammerTime}");
        ImGui.Text($"HammerStacks: {HammerStacks}");
        ImGui.Text($"SubtractiveStacks: {SubtractiveStacks}");
        ImGui.Text($"Paint: {Paint}");
        ImGui.Text($"PaletteGauge: {PaletteGauge}");
        ImGui.Text($"CreatureMotifDrawn: {CreatureMotifDrawn}");
        ImGui.Text($"WeaponMotifDrawn: {WeaponMotifDrawn}");
        ImGui.Text($"LandscapeMotifDrawn: {LandscapeMotifDrawn}");
        ImGui.Text($"MooglePortraitReady: {MooglePortraitReady}");
        ImGui.Text($"MadeenPortraitReady: {MadeenPortraitReady}");
        ImGui.Text("--- BMR Timeline ---");
        ImGui.Text($"Active: {BmrActive}{(BmrActive ? $" ({DataCenter.BmrActiveModuleName})" : "")}");
        if (BmrActive)
        {
            ImGui.Text($"Raidwide In: {(BmrRaidwideIn < 9999f ? $"{BmrRaidwideIn:F1}s" : "None")}");
            ImGui.Text($"Knockback In: {(BmrKnockbackIn < 9999f ? $"{BmrKnockbackIn:F1}s" : "None")}");
            ImGui.Text($"Downtime In: {(BmrDowntimeIn < 9999f ? $"{BmrDowntimeIn:F1}s" : "None")}");
            ImGui.Text($"Vulnerable In: {(BmrVulnerableIn < 9999f ? $"{BmrVulnerableIn:F1}s" : "None")}");
        }
    }

    #endregion

    #region Countdown & Opener
    // === PCT OPENER (7.4 Balance — 2nd GCD Starry) ===
    // Pre-pull: Creature Motif(-15s) → Weapon Motif(-10s) → Landscape Motif(-5s)
    //         → Pot(-2s) → Rainbow Drip precast(-4s)
    // GCD1: Rainbow Drip (lands on pull) → Striking Muse (weave) → Starry Muse (weave)
    // GCD2: Holy in White → Star Prism (weave)
    // GCD3: Hammer Stamp → Living Muse (weave) → GCD4: Hammer Brush
    // GCD5: Polishing Hammer → GCD6: Comet in Black
    // → Subtractive Palette (weave) → GCD7: Stone in Yellow → GCD8: Thunder in Magenta
    // → GCD9: Blizzard in Cyan → 9-spell Hyperphantasia burst window
    //
    // === EVEN BURST (120s) ===
    // Starry Muse (party buff) + Star Prism + Hammer combo + Comet in Black
    // 9-spell Hyperphantasia burst under Starry Muse
    // Draw all 3 motifs beforehand — Creature + Weapon + Landscape must be ready
    //
    // === ODD BURST (60s) ===
    // No major burst CDs — Starry Muse is 120s
    // Hold Hammer combo for even windows when possible
    // Spend Living Muse on CD, draw motifs during filler
    //
    // === FILLER / SUSTAIN ===
    // Motif drawing: draw Creature/Weapon/Landscape motifs during filler (long cast times)
    // Prepare all 3 motifs before Starry Muse comes off CD
    // Subtractive Palette: use for Stone/Thunder/Blizzard in Yellow/Magenta/Cyan
    // Holy in White/Comet in Black: spend paint charges, don't overcap
    // Living Muse: summon on CD for portrait value
    // Fire in Red → Aero in Green → Water in Blue: basic filler GCDs when nothing else available

    protected override IAction? CountDownAction(float remainTime)
    {
        // Pre-pull pot at ~2s before pull
        if (BurstMed && remainTime <= 2f && remainTime > 1f && UseBurstMedicine(out var potAct))
            return potAct;

        // PCT opener: cast Rainbow Drip to land as pull hits.
        // Rainbow Drip has ~4s cast, schedule it to land at 0.
        if (remainTime < RainbowDripPvE.Info.CastTime + 0.4f + CountDownAhead)
        {
            if (RainbowDripPvE.CanUse(out var act))
                return act;
        }

        // Below level 92 (no Rainbow Drip), start with Fire in Red.
        if (remainTime < FireInRedPvE.Info.CastTime + CountDownAhead
            && DataCenter.PlayerSyncedLevel() < 92)
        {
            if (FireInRedPvE.CanUse(out var act))
                return act;
        }

        // Pre-pull motif drawing: ensure all three motifs are up before Rainbow Drip.
        // Creature Motif at ~15s (long cast time, draw first)
        if (remainTime <= 15f && remainTime > 10f && !CreatureMotifDrawn)
        {
            if (PomMotifPvE.CanUse(out var act)) return act;
            if (WingMotifPvE.CanUse(out var act2)) return act2;
            if (ClawMotifPvE.CanUse(out var act3)) return act3;
            if (MawMotifPvE.CanUse(out var act4)) return act4;
        }

        // Weapon Motif at ~10s
        if (remainTime <= 10f && remainTime > 5f && !WeaponMotifDrawn)
        {
            if (HammerMotifPvE.CanUse(out var act))
                return act;
        }

        // Landscape Motif at ~5s (needed for Starry Muse)
        if (remainTime <= 5f && remainTime > 2.5f && !LandscapeMotifDrawn)
        {
            if (StarrySkyMotifPvE.CanUse(out var act))
                return act;
        }

        return base.CountDownAction(remainTime);
    }

    #endregion

    #region Additional oGCD Logic

    protected override bool EmergencyAbility(IAction nextGCD, out IAction? act)
    {
        act = null;

        // Swiftcast on hardcast Rainbow Drip (highest priority).
        // Rainbow Bright makes it instant already, so only swift the hardcast version.
        if (SwiftRainbowDrip && !HasRainbowBright
            && nextGCD.IsTheSameTo(false, RainbowDripPvE)
            && SwiftcastPvE.CanUse(out act))
        {
            return true;
        }

        // Swiftcast on Creature Motif during burst prep:
        // Long cast time motifs are painful in combat. Swift the creature motif
        // to keep GCD uptime during motif preparation windows.
        if (SwiftCreatureMotif && InCombat)
        {
            bool isCreatureMotif =
                nextGCD.IsTheSameTo(false, PomMotifPvE) ||
                nextGCD.IsTheSameTo(false, WingMotifPvE) ||
                nextGCD.IsTheSameTo(false, ClawMotifPvE) ||
                nextGCD.IsTheSameTo(false, MawMotifPvE);

            if (isCreatureMotif && SwiftcastPvE.CanUse(out act))
                return true;
        }

        // Medicine: use during Starry Muse burst window or early opener.
        if (BurstMed && InCombat)
        {
            // Opener pot: use in first 10s when we have Hammer Time active (post-Striking Muse).
            if (CombatTime <= 10f && CombatTime >= 1f && HasHammerTime
                && UseBurstMedicine(out act))
                return true;

            // Subsequent bursts: use during Starry Muse.
            if (CombatTime > 10f && HasStarryMuse && UseBurstMedicine(out act))
                return true;
        }

        return base.EmergencyAbility(nextGCD, out act);
    }

    [RotationDesc(ActionID.SmudgePvE)]
    protected override bool MoveForwardAbility(IAction nextGCD, out IAction? act)
    {
        if (SmudgePvE.CanUse(out act))
            return true;
        return base.MoveForwardAbility(nextGCD, out act);
    }

    [RotationDesc(ActionID.TemperaCoatPvE, ActionID.TemperaGrassaPvE, ActionID.AddlePvE)]
    protected sealed override bool DefenseAreaAbility(IAction nextGCD, out IAction? act)
    {
        // BMR-aware: override burst-skip for genuine raidwides
        bool rwSoon = BmrActive && BmrRaidwideIn is > 0 and <= 5f;
        bool allowDefense = rwSoon || !BlockDefenseDuringBurst || !InBurstWindow;

        if (allowDefense)
        {
            // Spread mits across raidwides — use ONE per raidwide, not all at once
            // Priority: Tempera Coat/Grassa (party shield) first, then Addle on separate raidwide
            if (TemperaCoatPvE.CanUse(out act))
                return true;
            if (TemperaGrassaPvE.CanUse(out act))
                return true;
            // Only Addle if Tempera is on CD (spreading across different raidwides)
            if (!TemperaCoatPvE.Cooldown.IsCoolingDown || TemperaCoatPvE.Cooldown.RecastTimeRemain > 10f)
            {
                // Tempera is available or coming back soon — save Addle for next raidwide
            }
            else if (AddlePvE.CanUse(out act))
                return true;
        }

        return base.DefenseAreaAbility(nextGCD, out act);
    }

    [RotationDesc(ActionID.TemperaCoatPvE)]
    protected sealed override bool DefenseSingleAbility(IAction nextGCD, out IAction? act)
    {
        bool rwSoon = BmrActive && BmrRaidwideIn is > 0 and <= 5f;
        bool allowDefense = rwSoon || !BlockDefenseDuringBurst || !InBurstWindow;

        if (allowDefense && TemperaCoatPvE.CanUse(out act))
            return true;

        return base.DefenseSingleAbility(nextGCD, out act);
    }

    #endregion

    #region oGCD Logic (AttackAbility)

    protected override bool AttackAbility(IAction nextGCD, out IAction? act)
    {
        // === OPENER: Striking Muse early to get Hammer Time ===
        // In the first 5s of combat, fire Striking Muse immediately if canvas is ready.
        if (InCombat && CombatTime <= 5f
            && StrikingMusePvE.CanUse(out act, usedUp: true, skipCastingCheck: true))
        {
            return true;
        }

        // Timing thresholds for burst management.
        int openerDelay = DataCenter.PlayerSyncedLevel() < 92 ? 2 : 5;
        bool starryComingSoon5 = !HasStarryMuse && StarryReadyWithin(5f);
        bool starryComingSoon10 = !HasStarryMuse && StarryReadyWithin(10f);
        bool starryComingSoon40 = !HasStarryMuse && StarryReadyWithin(40f);
        bool starryComingSoon60 = !HasStarryMuse && StarryReadyWithin(60f);

        // Striking Muse charge preservation:
        // Hold the last Striking charge for burst if Starry is coming within 60s,
        // unless we are within 5s of Starry (then spend it as the prep charge).
        bool preserveStriking =
            starryComingSoon60 && !starryComingSoon5
            && StrikingMusePvE.Cooldown.CurrentCharges <= 1;

        // Living Muse charge preservation:
        // Keep at least 1 charge if burst is coming within 40s.
        bool preserveLiving =
            CombatTime > 5f && !HasStarryMuse
            && starryComingSoon40
            && LivingMusePvE.Cooldown.CurrentCharges <= 1;

        // Burst timing check for Striking Muse (outside opener).
        // Use Striking freely when Scenic Muse (Starry) is far from ready, or during burst,
        // or when Starry Muse is not yet unlocked.
        bool strikingTimingOk =
            !ScenicMusePvE.Cooldown.WillHaveOneCharge(60)
            || HasStarryMuse
            || !StarryMusePvE.EnoughLevel;

        // ============================================================
        // === 1. STARRY MUSE (120s burst buff) ===
        // ============================================================
        // Gate behind IsBurst (user burst toggle) and opener delay.
        if (IsBurst && CombatTime > openerDelay
            && StarryMusePvE.CanUse(out act, skipCastingCheck: true))
        {
            return true;
        }

        // ============================================================
        // === 2. SUBTRACTIVE PALETTE (oGCD) ===
        // ============================================================
        // Use Subtractive Palette when available and NOT about to enter Starry Muse.
        // Starry Muse grants a free Subtractive Spectrum, so don't waste gauge right before.
        // Also respect the action check: need 50 gauge or SubtractiveSpectrum, no MonochromeTones.
        if (!starryComingSoon10 && !HasSubtractivePalette
            && SubtractivePalettePvE.CanUse(out act))
        {
            return true;
        }

        // ============================================================
        // === 3. STRIKING MUSE (Weapon Canvas -> Hammer Time) ===
        // ============================================================

        // 3a. Prep charge: spend the last Striking charge ~5s before Starry to have
        //     Hammer Stamp ready for the burst window.
        if (starryComingSoon5 && StrikingMusePvE.Cooldown.CurrentCharges == 1
            && CombatTime > openerDelay
            && StrikingMusePvE.CanUse(out act, usedUp: true))
        {
            return true;
        }

        // 3b. Normal Striking Muse usage: spend charges when not preserving for burst.
        if (!preserveStriking && CombatTime > openerDelay
            && strikingTimingOk
            && StrikingMusePvE.CanUse(out act, usedUp: true))
        {
            return true;
        }

        // ============================================================
        // === 4. PORTRAIT ABILITIES (Mog of the Ages / Retribution of the Madeen) ===
        // ============================================================

        // Retribution of the Madeen: always prefer in burst, but use when available.
        // Higher potency and aligned with even-minute windows.
        if (HasStarryMuse && RetributionOfTheMadeenPvE.CanUse(out act))
            return true;

        // Mog of the Ages: use immediately if available. Ideally lines up with burst
        // but don't hold it excessively.
        if (RetributionOfTheMadeenPvE.CanUse(out act))
            return true;

        if (MogOfTheAgesPvE.CanUse(out act))
            return true;

        // ============================================================
        // === 5. LIVING MUSE (Creature Canvas -> Summon Creature) ===
        // ============================================================
        // Sequence: Pom -> Wing -> (Mog portrait ready) -> Claw -> Fang -> (Madeen portrait ready)
        // Use charges on cooldown, respecting burst preservation.
        if (!preserveLiving)
        {
            // Prioritize Fanged Muse (completes Madeen cycle).
            if (FangedMusePvE.CanUse(out act, usedUp: true))
                return true;

            // Clawed Muse (3rd in cycle). Don't use if Mog portrait would be overwritten.
            if (!MogOfTheAgesPvE.CanUse(out _) && ClawedMusePvE.CanUse(out act, usedUp: true))
                return true;

            // Winged Muse (2nd in cycle).
            if (WingedMusePvE.CanUse(out act, usedUp: true))
                return true;

            // Pom Muse (1st in cycle). Don't use if Madeen is ready (would waste portrait).
            if (!RetributionOfTheMadeenPvE.CanUse(out _)
                && PomMusePvE.CanUse(out act, usedUp: true))
                return true;
        }

        return base.AttackAbility(nextGCD, out act);
    }

    protected override bool GeneralAbility(IAction nextGCD, out IAction? act)
    {
        // Tempera Grassa: convert Tempera Coat shield into party shield when defense requested
        // or when Tempera Coat is about to expire unused.
        if ((MergedStatus.HasFlag(AutoStatus.DefenseArea)
             || StatusHelper.PlayerWillStatusEndGCD(2, 0, true, StatusID.TemperaCoat))
            && TemperaGrassaPvE.CanUse(out act))
        {
            return true;
        }

        return base.GeneralAbility(nextGCD, out act);
    }

    #endregion

    #region GCD Logic

    protected override bool GeneralGCD(out IAction? act)
    {
        // ============================================================
        // === OPENER PHASE (first 5 seconds) ===
        // ============================================================
        // After Rainbow Drip lands, the opener sequence is:
        //   Holy in White (instant, spends paint from pre-pull) ->
        //   Creature Motif (to prepare for Living Muse) ->
        //   Then normal burst flow begins.
        if (CombatTime < 5f && InCombat)
        {
            // Holy in White: instant GCD to weave Striking Muse + Living Muse behind.
            if (HolyInWhitePvE.CanUse(out act))
                return true;

            // Creature Motif: prepare canvas for Living Muse if not drawn.
            if (PomMotifPvE.CanUse(out act)) return true;
            if (WingMotifPvE.CanUse(out act)) return true;
            if (ClawMotifPvE.CanUse(out act)) return true;
            if (MawMotifPvE.CanUse(out act)) return true;
        }

        // ============================================================
        // === HIGHEST PRIORITY: Proc-based instant GCDs ===
        // ============================================================

        // Rainbow Drip (instant via Rainbow Bright proc from consuming all Hyperphantasia).
        // This is your biggest single hit after burst -- always use immediately.
        if (HasRainbowBright && RainbowDripPvE.CanUse(out act))
            return true;

        // Star Prism (instant, from Starstruck buff granted by Starry Muse).
        // High potency (1400), AoE. Use inside burst window.
        if (HasStarstruck && StarPrismPvE.CanUse(out act))
            return true;

        // ============================================================
        // === BURST WINDOW: Starry Muse active ===
        // ============================================================
        // During Starry Muse: fit maximum high-potency spells.
        // Hyperphantasia grants 5 stacks of reduced cast time. Inspiration makes
        // palette spells instant. Prioritize Comet in Black, paint spenders, and hammers.
        if (InBurstWindow)
        {
            // Comet in Black: 940 potency instant (Monochrome Tones buff from Subtractive).
            // Use before Holy in White since it requires the special buff.
            if (CometInBlackPvE.CanUse(out act, skipCastingCheck: true))
                return true;

            // Hammer combo: instant GCDs, guaranteed crit/dhit. Complete the chain.
            // Do NOT use hammers while Inspiration + SubtractivePalette are both active --
            // spend the Subtractive palette combo first (higher priority during Hyperphantasia).
            if (!(HasInspiration && HasSubtractivePalette))
            {
                if (PolishingHammerPvE.CanUse(out act, skipComboCheck: true)) return true;
                if (HammerBrushPvE.CanUse(out act, skipComboCheck: true)) return true;
                if (HammerStampPvE.CanUse(out act, skipComboCheck: true)) return true;
            }

            // During Hyperphantasia: palette spells are instant (Inspiration buff).
            // Burn through Subtractive combo (Blizzard/Stone/Thunder) for fast paint + damage.
            if (HasHyperphantasia || HasInspiration)
            {
                if (ThunderInMagentaPvE.CanUse(out act)) return true;
                if (StoneInYellowPvE.CanUse(out act)) return true;
                if (BlizzardInCyanPvE.CanUse(out act)) return true;

                if (WaterInBluePvE.CanUse(out act)) return true;
                if (AeroInGreenPvE.CanUse(out act)) return true;
                if (FireInRedPvE.CanUse(out act)) return true;
            }

            // Holy in White: instant, 480 potency. Spend extra paint in burst.
            if (HolyInWhitePvE.CanUse(out act))
                return true;
        }

        // ============================================================
        // === HAMMER COMBO (outside burst, when Hammer Time is active) ===
        // ============================================================
        // Hammer Stamp -> Hammer Brush -> Polishing Hammer (3 instant GCDs).
        // Great for movement. Don't delay excessively -- Hammer Time has a timer.
        // Block hammer chain if we just pre-loaded Striking Muse and Starry is imminent.
        bool blockPrepHammer = !HasStarryMuse && StarryReadyWithin(1f)
                               && !HasHyperphantasia;

        if (!blockPrepHammer && !(HasInspiration && HasSubtractivePalette))
        {
            if (PolishingHammerPvE.CanUse(out act, skipComboCheck: true)) return true;
            if (HammerBrushPvE.CanUse(out act, skipComboCheck: true)) return true;
            if (HammerStampPvE.CanUse(out act, skipComboCheck: true)) return true;
        }

        // ============================================================
        // === OUT OF COMBAT: Motif Preparation ===
        // ============================================================
        // Paint all motifs between pulls. Order: Creature -> Weapon -> Landscape.
        if (!InCombat)
        {
            if (PomMotifPvE.CanUse(out act)) return true;
            if (WingMotifPvE.CanUse(out act)) return true;
            if (ClawMotifPvE.CanUse(out act)) return true;
            if (MawMotifPvE.CanUse(out act)) return true;
            if (HammerMotifPvE.CanUse(out act)) return true;
            if (!HasHyperphantasia && StarrySkyMotifPvE.CanUse(out act)) return true;
            if (RainbowDripPvE.CanUse(out act)) return true;
        }

        // ============================================================
        // === BURST PREPARATION: Motif Drawing (30s before Starry) ===
        // ============================================================
        // Landscape Motif: must be drawn before Starry Muse can be activated.
        // Start drawing within the configured prep window before Starry is ready.
        if (ScenicMusePvE.Cooldown.RecastTimeRemainOneCharge <= MotifPrepWindow
            && !HasStarryMuse && !HasHyperphantasia)
        {
            if (StarrySkyMotifPvE.CanUse(out act))
                return true;

            // Also prep Weapon Motif in the same window if needed.
            if (HammerMotifPvE.CanUse(out act))
                return true;
        }

        // Creature Motif: draw when Living Muse has a charge or is about to come up.
        // This ensures the canvas is ready when the Muse charge refreshes.
        if ((LivingMusePvE.Cooldown.HasOneCharge
             || LivingMusePvE.Cooldown.RecastTimeRemainOneCharge <= CreatureMotifPvE.Info.CastTime * 1.7f)
            && !HasStarryMuse && !HasHyperphantasia)
        {
            if (PomMotifPvE.CanUse(out act)) return true;
            if (WingMotifPvE.CanUse(out act)) return true;
            if (ClawMotifPvE.CanUse(out act)) return true;
            if (MawMotifPvE.CanUse(out act)) return true;
        }

        // Weapon Motif: draw when Steel Muse has a charge or is about to come up.
        if ((SteelMusePvE.Cooldown.HasOneCharge
             || SteelMusePvE.Cooldown.RecastTimeRemainOneCharge <= WeaponMotifPvE.Info.CastTime)
            && !HasStarryMuse && !HasHyperphantasia)
        {
            if (HammerMotifPvE.CanUse(out act))
                return true;
        }

        // ============================================================
        // === MOVEMENT: Instant GCDs while moving ===
        // ============================================================
        if (IsMoving && !HasSwift)
        {
            // Finish hammer chain first (instant).
            if (!blockPrepHammer && !(HasInspiration && HasSubtractivePalette))
            {
                if (PolishingHammerPvE.CanUse(out act, skipComboCheck: true)) return true;
                if (HammerBrushPvE.CanUse(out act, skipComboCheck: true)) return true;
                if (HammerStampPvE.CanUse(out act, skipComboCheck: true)) return true;
            }

            if (PaintWhileMoving)
            {
                // Comet in Black if available and not saving for burst.
                bool starryComingSoon10 = !HasStarryMuse && StarryReadyWithin(12f);
                if (!starryComingSoon10 && CometInBlackPvE.CanUse(out act))
                    return true;

                if (HolyInWhitePvE.CanUse(out act))
                    return true;
            }
        }

        // ============================================================
        // === SWIFTCAST MOTIF USAGE ===
        // ============================================================
        // When Swiftcast is active and a motif needs drawing, use it now.
        if (HasSwift && (!LandscapeMotifDrawn || !CreatureMotifDrawn || !WeaponMotifDrawn))
        {
            if (PomMotifPvE.CanUse(out act, skipCastingCheck: true)) return true;
            if (WingMotifPvE.CanUse(out act, skipCastingCheck: true)) return true;
            if (ClawMotifPvE.CanUse(out act, skipCastingCheck: true)) return true;
            if (MawMotifPvE.CanUse(out act, skipCastingCheck: true)) return true;
            if (HammerMotifPvE.CanUse(out act, skipCastingCheck: true)) return true;
            if (!HasHyperphantasia && StarrySkyMotifPvE.CanUse(out act, skipCastingCheck: true))
                return true;
        }

        // ============================================================
        // === PAINT OVERCAP PROTECTION ===
        // ============================================================
        // Don't let paint reach 5 outside of burst. Spend it on Comet or Holy.
        if (Paint >= PaintOvercapAt && !HasStarryMuse)
        {
            if (CometInBlackPvE.CanUse(out act))
                return true;

            if (!PreferCometOvercap && HolyInWhitePvE.CanUse(out act))
                return true;
        }

        // ============================================================
        // === AOE COMBO (3+ targets, Subtractive) ===
        // ============================================================
        if (ThunderIiInMagentaPvE.CanUse(out act)) return true;
        if (StoneIiInYellowPvE.CanUse(out act)) return true;
        if (BlizzardIiInCyanPvE.CanUse(out act)) return true;

        // === AOE COMBO (3+ targets, Additive) ===
        if (WaterIiInBluePvE.CanUse(out act)) return true;
        if (AeroIiInGreenPvE.CanUse(out act)) return true;
        if (FireIiInRedPvE.CanUse(out act)) return true;

        // ============================================================
        // === ST COMBO (Subtractive: Blizzard -> Stone -> Thunder) ===
        // ============================================================
        if (ThunderInMagentaPvE.CanUse(out act)) return true;
        if (StoneInYellowPvE.CanUse(out act)) return true;
        if (BlizzardInCyanPvE.CanUse(out act)) return true;

        // ============================================================
        // === ST COMBO (Additive: Fire -> Aero -> Water) ===
        // ============================================================
        if (WaterInBluePvE.CanUse(out act)) return true;
        if (AeroInGreenPvE.CanUse(out act)) return true;
        if (FireInRedPvE.CanUse(out act)) return true;

        // ============================================================
        // === FALLBACK: Motif refresh (no target or nothing else to do) ===
        // ============================================================
        if (PomMotifPvE.CanUse(out act)) return true;
        if (WingMotifPvE.CanUse(out act)) return true;
        if (ClawMotifPvE.CanUse(out act)) return true;
        if (MawMotifPvE.CanUse(out act)) return true;
        if (HammerMotifPvE.CanUse(out act)) return true;
        if (StarrySkyMotifPvE.CanUse(out act)) return true;

        return base.GeneralGCD(out act);
    }

    #endregion
}
