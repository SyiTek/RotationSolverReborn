namespace RotationSolver.ExtraRotations.Melee;

[Rotation("SezuraiSAM", CombatType.PvE, GameVersion = "7.41",
    Description = "Balance-aligned SAM with Tendo burst, Higanbana management, Kenki optimization, and BMR timeline integration.")]
[SourceCode(Path = "main/ExtraRotations/Melee/SezuraiSAM.cs")]
[ExtraRotation]
public sealed class SezuraiSAM : SamuraiRotation
{
    #region Config Options

    [Range(0f, 0.25f, ConfigUnitType.Percent)]
    [RotationConfig(CombatType.PvE, Name = "Action Ahead Override (0 = use global setting)")]
    public float ActionAheadOverride { get; set; } = 0f;

    [RotationConfig(CombatType.PvE, Name = "Auto Pot Usage (Gemdraught during Ikishoten burst windows + pre-pull)")]
    public bool BurstMed { get; set; } = true;

    [RotationConfig(CombatType.PvE, Name = "Prevent Higanbana use if there is more than one target")]
    public bool HiganbanaTargets { get; set; } = true;

    [Range(10, 60, ConfigUnitType.Seconds, 1)]
    [RotationConfig(CombatType.PvE, Name = "Minimum time-to-kill for Higanbana application (seconds)")]
    public int HiganbanaMinTime { get; set; } = 48;

    [Range(25, 75, ConfigUnitType.None, 5)]
    [RotationConfig(CombatType.PvE, Name = "Kenki threshold for Shinten/Kyuten during filler (save for burst below this)")]
    public int KenkiSpendThreshold { get; set; } = 50;

    [RotationConfig(CombatType.PvE, Name = "Use BMR timeline for proactive mitigation, downtime planning, and burst hold")]
    public bool UseBmr { get; set; } = true;

    #endregion

    #region Burst State

    /// <summary>
    /// Whether the user has burst enabled in the framework.
    /// </summary>
    private bool CanBurst => MergedStatus.HasFlag(AutoStatus.Burst);

    /// <summary>
    /// True when Ikishoten has a charge ready OR was recently used (within 30s).
    /// This covers the broad burst-is-available/active window for the 120s cycle.
    /// </summary>
    private bool InBurstWindow => IkishotenPvE.EnoughLevel
        && (IkishotenPvE.Cooldown.HasOneCharge || IkishotenPvE.Cooldown.JustUsedAfter(30));

    /// <summary>
    /// True during the big 120s burst: Ikishoten was recently used, or we have
    /// Ogi Namikiri / Zanshin buffs active. This is the window for maximum damage.
    /// </summary>
    private bool IsBigBurst => IkishotenPvE.EnoughLevel
        && (IkishotenPvE.Cooldown.JustUsedAfter(30) || HasOgiNamikiri || HasZanshinReady);

    /// <summary>
    /// True during odd-minute mini-burst: Senei is available but Ikishoten is not.
    /// The 60s window uses Meikyo + Midare/Tendo but no Ogi Namikiri.
    /// </summary>
    private bool IsMiniBurst => HissatsuSeneiPvE.EnoughLevel
        && HissatsuSeneiPvE.Cooldown.HasOneCharge
        && !IsBigBurst;

    /// <summary>
    /// True when we are within 10s of Ikishoten coming off cooldown.
    /// Used to conserve Kenki for the upcoming burst.
    /// </summary>
    private bool IsPreBurst => IkishotenPvE.EnoughLevel
        && IkishotenPvE.Cooldown.IsCoolingDown
        && !IkishotenPvE.Cooldown.HasOneCharge
        && IkishotenPvE.Cooldown.RecastTimeRemain <= 10;

    #endregion

    #region BMR Helpers

    /// <summary>
    /// True when BMR is active AND the user has enabled our BMR config toggle.
    /// All BMR checks go through this so there's a single kill-switch.
    /// </summary>
    private bool BmrUsable => UseBmr && BMRActive;

    /// <summary>
    /// BMR: downtime is imminent and close enough to worry about (~20s).
    /// Used for resource dump decisions -- dump Kenki, fire Ikishoten early, etc.
    /// </summary>
    private bool BmrDowntimeSoon => BmrUsable && BMRDowntimeIn is > 0 and <= 20f;

    /// <summary>
    /// BMR: downtime is very close (~10s). Aggressive dump: spend all Kenki,
    /// fire any remaining Sen as Iaijutsu, and stop starting new combos.
    /// </summary>
    private bool BmrDowntimeImminent => BmrUsable && BMRDowntimeIn is > 0 and <= 10f;

    /// <summary>
    /// BMR: downtime too close for Midare/Tendo cast (~3s). These have a ~1.3s cast
    /// time and the follow-up Kaeshi takes another GCD. Don't start if we can't finish.
    /// </summary>
    private bool BmrBlockLongCast => BmrUsable && BMRDowntimeIn is > 0 and <= 3f;

    /// <summary>
    /// BMR: downtime too close to start a new 3-GCD combo (~5s).
    /// Prefer finishing current combo, dumping Sen, or using ranged fallback.
    /// </summary>
    private bool BmrBlockNewCombo => BmrUsable && BMRDowntimeIn is > 0 and <= 5f;

    /// <summary>
    /// BMR: vulnerability window coming within 30s -- hold Ikishoten for it.
    /// Per Balance intermediate: align burst with party buff / vuln windows.
    /// </summary>
    private bool BmrHoldIkiForVuln => BmrUsable
        && BMRVulnerableIn is > 0 and <= 30f
        && IkishotenPvE.Cooldown.HasOneCharge;

    /// <summary>
    /// BMR: Ikishoten won't be useful before downtime (too late to burst).
    /// Don't waste Iki if downtime < 15s and no burst is active.
    /// The full burst sequence (Ogi + Zanshin + Tendo + Midare) takes ~12-15s.
    /// </summary>
    private bool BmrBlockIkiBeforeDowntime => BmrUsable
        && BMRDowntimeIn is > 0 and <= 15f
        && !IsBigBurst;

    /// <summary>
    /// BMR: hold one Meikyo charge for post-downtime re-entry.
    /// After downtime, Meikyo lets us skip to finishers to re-establish buffs + Sen quickly.
    /// Only hold if downtime is within 15s and we have a charge to spare.
    /// </summary>
    private bool BmrHoldMeikyoForDowntime => BmrUsable
        && BMRDowntimeIn is > 0 and <= 15f
        && MeikyoShisuiPvE.Cooldown.CurrentCharges <= 1;

    /// <summary>
    /// BMR: raidwide damage incoming soon (~3s). Use Third Eye/Tengentsu proactively.
    /// Per Balance: "each time you successfully use Tengentsu you have effectively gained 100 potency."
    /// </summary>
    private bool BmrRaidwideSoon => BmrUsable && BMRRaidwideIn is > 0 and <= 3f;

    /// <summary>
    /// BMR: raidwide damage incoming within Feint's application window (~5s).
    /// Feint lasts 10s and reduces physical damage by 10% + magic by 5%.
    /// </summary>
    private bool BmrFeintWindow => BmrUsable && BMRRaidwideIn is > 0 and <= 5f;

    /// <summary>
    /// BMR: damage (raidwide or generic) incoming soon -- use self-heal proactively.
    /// Bloodbath + Second Wind before the hit lands to top off HP.
    /// </summary>
    private bool BmrDamageSoon => BmrUsable
        && (BMRRaidwideIn is > 0 and <= 4f || BMRDamageIn is > 0 and <= 4f);

    #endregion

    #region Weave Helpers

    /// <summary>
    /// Late-weave window: last ~45% of GCD where a single oGCD fits without clipping.
    /// </summary>
    private static float LateWeaveWindow => WeaponTotal * 0.45f;

    /// <summary>
    /// True when there's enough remaining GCD time to safely weave an oGCD.
    /// </summary>
    private static bool EnoughWeaveTime => WeaponRemain >= 0.6f;

    /// <summary>
    /// True when in the late-weave window and weaving is safe.
    /// </summary>
    private static bool CanLateWeave => WeaponRemain <= LateWeaveWindow && EnoughWeaveTime;

    #endregion

    #region Countdown & Opener
    // === SAM OPENER (7.4 Balance) ===
    // Pre-pull: Meikyo Shisui(-14s) -> True North(-5s) -> Pot(-2s)
    // GCD1: Gekko (rear, grants Getsu) -> GCD2: Kasha (flank, grants Ka)
    // -> Ikishoten (weave, grants Ogi Namikiri + 50 Kenki)
    // GCD3: Yukikaze (grants Setsu) -> Tendo Setsugekka (3 Sen, powered-up Iaijutsu)
    // -> Meikyo Shisui (weave) -> GCD4: Gekko -> GCD5: Kasha -> GCD6: Yukikaze
    // -> Midare Setsugekka -> Kaeshi Setsugekka (follow-up)
    // -> Ogi Namikiri -> Kaeshi Namikiri -> Shoha (weave, 3 Meditation stacks)
    //
    // === EVEN BURST (120s) ===
    // Ikishoten + double Meikyo -> Tendo Setsugekka + Midare + Ogi Namikiri
    // Dump all Kenki: Senei + Shinten spam under raid buffs
    // Pot before first Tendo Setsugekka for max snapshot
    //
    // === ODD BURST (60s) ===
    // Meikyo -> Midare Setsugekka + Senei
    // Save Ikishoten + second Meikyo for even windows
    // Still use Shinten to spend Kenki, but less aggressively
    //
    // === FILLER / SUSTAIN ===
    // 29-GCD loop at 2.08 GCD: Hakaze -> Jinpu -> Gekko -> Hakaze -> Shifu -> Kasha
    //   -> Hakaze -> Yukikaze -> Midare Setsugekka -> repeat
    // Higanbana: apply at start, reapply when >=48s remaining on fight
    // Never overcap Kenki -- Shinten at 50+ outside burst, pool to ~25 for burst
    // Use Meikyo to skip to Sen-granting finishers for alignment

    protected override IAction? CountDownAction(float remainTime)
    {
        // Meikyo Shisui at ~14s: grants 3 stacks to skip combo for opener Sen generation
        // Also grants Tendo buff for first Tendo Setsugekka
        if (remainTime <= 14 && MeikyoShisuiPvE.CanUse(out IAction? act))
            return act;

        // True North at ~5s: positional freedom for opener Gekko (rear)
        if (remainTime <= 5 && TrueNorthPvE.CanUse(out act))
            return act;

        // Pre-pull medicine at ~2s: pot animation lands before first GCD
        if (BurstMed && remainTime <= 2f && remainTime > 1f && UseBurstMedicine(out act))
            return act;

        return base.CountDownAction(remainTime);
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
        ImGui.Text("--- Burst State ---");
        ImGui.Text($"CanBurst: {CanBurst}");
        ImGui.Text($"InBurstWindow: {InBurstWindow}");
        ImGui.Text($"IsBigBurst: {IsBigBurst}");
        ImGui.Text($"IsMiniBurst: {IsMiniBurst}");
        ImGui.Text($"IsPreBurst: {IsPreBurst}");
        ImGui.Text("--- Sen ---");
        ImGui.Text($"SenCount: {SenCount} | Getsu: {HasGetsu} | Ka: {HasKa} | Setsu: {HasSetsu}");
        ImGui.Text("--- Gauge ---");
        ImGui.Text($"Kenki: {Kenki} | Meditation: {MeditationStacks}");
        ImGui.Text("--- Buffs ---");
        ImGui.Text($"Fugetsu: {(HasFugetsu ? $"{FugetsuTime:F1}s" : "None")} | Fuka: {(HasFuka ? $"{FukaTime:F1}s" : "None")}");
        ImGui.Text($"HasFugetsuAndFuka: {HasFugetsuAndFuka}");
        ImGui.Text($"WillFugetsuEnd: {WillFugetsuEnd} | WillFukaEnd: {WillFukaEnd}");
        ImGui.Text($"HasMeikyoShisui: {HasMeikyoShisui} | HasTendo: {HasTendo}");
        ImGui.Text($"HasOgiNamikiri: {HasOgiNamikiri} | HasZanshinReady: {HasZanshinReady}");
        ImGui.Text($"HasTsubamegaeshiReady: {HasTsubamegaeshiReady}");
        ImGui.Text($"Medicated: {StatusHelper.PlayerHasStatus(true, StatusID.Medicated)}");
        ImGui.Text("--- Iaijutsu State ---");
        ImGui.Text($"HiganbanaReady: {HiganbanaReady} | TenkaGokenReady: {TenkaGokenReady}");
        ImGui.Text($"MidareReady: {MidareSetsugekkaReady} | TendoSetsugekkaReady: {TendoSetsugekkaReady}");
        ImGui.Text($"TendoGokenReady: {TendoGokenReady}");
        ImGui.Text("--- Kaeshi State ---");
        ImGui.Text($"KaeshiSetsugekka: {KaeshiSetsugekkaReady} | KaeshiGoken: {KaeshiGokenReady}");
        ImGui.Text($"KaeshiNamikiri: {KaeshiNamikiriReady}");
        ImGui.Text($"TendoKaeshiSetsugekka: {TendoKaeshiSetsugekkaReady} | TendoKaeshiGoken: {TendoKaeshiGokenReady}");
        ImGui.Text("--- Weave ---");
        ImGui.Text($"WeaponRemain: {WeaponRemain:F2}s | WeaponTotal: {WeaponTotal:F2}s");
        ImGui.Text($"CanLateWeave: {CanLateWeave} | EnoughWeaveTime: {EnoughWeaveTime}");
        ImGui.Text($"IkishotenCD: {(IkishotenPvE.Cooldown.IsCoolingDown ? $"{IkishotenPvE.Cooldown.RecastTimeRemain:F1}s" : "Ready")}");
        ImGui.Text($"SeneiCD: {(HissatsuSeneiPvE.Cooldown.IsCoolingDown ? $"{HissatsuSeneiPvE.Cooldown.RecastTimeRemain:F1}s" : "Ready")}");
        ImGui.Text($"MeikyoCharges: {MeikyoShisuiPvE.Cooldown.CurrentCharges}");
        ImGui.Text("--- BMR Decisions ---");
        ImGui.Text($"BmrUsable: {BmrUsable} | UseBmr: {UseBmr}");
        ImGui.Text($"DowntimeSoon: {BmrDowntimeSoon} | DowntimeImminent: {BmrDowntimeImminent}");
        ImGui.Text($"BlockLongCast: {BmrBlockLongCast} | BlockNewCombo: {BmrBlockNewCombo}");
        ImGui.Text($"HoldIkiForVuln: {BmrHoldIkiForVuln} | BlockIkiBeforeDowntime: {BmrBlockIkiBeforeDowntime}");
        ImGui.Text($"HoldMeikyoForDowntime: {BmrHoldMeikyoForDowntime}");
        ImGui.Text($"FeintWindow: {BmrFeintWindow} | RaidwideSoon: {BmrRaidwideSoon}");
        ImGui.Text($"DamageSoon: {BmrDamageSoon}");
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

    #region Additional oGCD Logic

    [RotationDesc(ActionID.HissatsuGyotenPvE)]
    protected override bool MoveForwardAbility(IAction nextGCD, out IAction? act)
    {
        if (HissatsuGyotenPvE.CanUse(out act))
            return true;
        return base.MoveForwardAbility(nextGCD, out act);
    }

    [RotationDesc]
    protected override bool HealSingleAbility(IAction nextGCD, out IAction? act)
    {
        // BMR-proactive: pre-heal before raidwide/damage hits so we survive
        if (BmrDamageSoon && !HasZanshinReady)
        {
            if (BloodbathPvE.CanUse(out act))
                return true;
            if (SecondWindPvE.CanUse(out act))
                return true;
        }

        // Standard self-healing (framework-triggered or non-BMR)
        if (SecondWindPvE.CanUse(out act))
            return true;
        if (BloodbathPvE.CanUse(out act))
            return true;
        return base.HealSingleAbility(nextGCD, out act);
    }

    [RotationDesc(ActionID.FeintPvE, ActionID.TengentsuPvE, ActionID.ThirdEyePvE)]
    protected sealed override bool DefenseAreaAbility(IAction nextGCD, out IAction? act)
    {
        // Don't use defensive oGCDs when Zanshin is ready (it shares the oGCD slot)
        if (HasZanshinReady)
            return base.DefenseAreaAbility(nextGCD, out act);

        // BMR-aware: Feint proactively when raidwide imminent
        // Feint: 10% phys + 5% magic -- skip if purely magic damage
        if (BmrFeintWindow && !IsBigBurst
            && (IsPhysicalDamageIncoming || !IsMagicalDamageIncoming))
        {
            if (FeintPvE.CanUse(out act))
                return true;
        }

        // BMR-aware: Third Eye/Tengentsu proactively before raidwide damage
        // Per Balance: "each time you successfully use Tengentsu you have effectively gained 100 potency"
        // These are short-duration buffs (4s) so use them close to the hit
        if (BmrRaidwideSoon)
        {
            if (TengentsuPvE.CanUse(out act))
                return true;
            if (ThirdEyePvE.CanUse(out act))
                return true;
        }

        // Non-BMR fallback: Feint when framework triggers defense
        if (!BmrUsable && FeintPvE.CanUse(out act))
            return true;

        return base.DefenseAreaAbility(nextGCD, out act);
    }

    [RotationDesc(ActionID.TengentsuPvE, ActionID.ThirdEyePvE)]
    protected override bool DefenseSingleAbility(IAction nextGCD, out IAction? act)
    {
        if (HasZanshinReady)
            return base.DefenseSingleAbility(nextGCD, out act);

        // BMR-aware: use Third Eye/Tengentsu before raidwide or tankbuster damage
        // Raidwide hits everyone including melee -- this is free mitigation + Kenki
        if (BmrRaidwideSoon || (BmrUsable && BMRTankbusterIn is > 0 and <= 3f))
        {
            if (TengentsuPvE.CanUse(out act))
                return true;
            if (ThirdEyePvE.CanUse(out act))
                return true;
        }

        // Non-BMR fallback
        if (!BmrUsable)
        {
            if (TengentsuPvE.CanUse(out act))
                return true;
            if (ThirdEyePvE.CanUse(out act))
                return true;
        }

        return base.DefenseSingleAbility(nextGCD, out act);
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
        if (LegSweepPvE.CanUse(out act))
            return true;
        return base.InterruptAbility(nextGCD, out act);
    }

    #endregion

    #region Emergency Ability

    [RotationDesc]
    protected override bool EmergencyAbility(IAction nextGCD, out IAction? act)
    {
        // Medicine: use when Ikishoten burst is about to start (within 5s) or during Ogi window
        if (BurstMed && InCombat)
        {
            // Pre-burst: Ikishoten coming off CD soon
            if (IkishotenPvE.EnoughLevel
                && IkishotenPvE.Cooldown.IsCoolingDown
                && IkishotenPvE.Cooldown.RecastTimeRemain <= 5
                && UseBurstMedicine(out act))
                return true;

            // During burst: Ogi or Zanshin is active
            if ((HasOgiNamikiri || HasZanshinReady) && UseBurstMedicine(out act))
                return true;
        }

        return base.EmergencyAbility(nextGCD, out act);
    }

    #endregion

    #region oGCD Logic

    protected override bool AttackAbility(IAction nextGCD, out IAction? act)
    {
        bool isTargetBoss = CurrentTarget?.IsBossFromTTK() ?? false;
        bool isTargetDying = CurrentTarget?.IsDying() ?? false;

        // ============================================================
        // 0. BMR: DUMP BURST BEFORE DOWNTIME
        // If downtime is approaching and Ikishoten is available, fire it
        // now so we can spend Kenki + Ogi before the boss leaves.
        // Skip if vuln window is coming (save burst for damage amp).
        // ============================================================
        if (BmrDowntimeSoon && !BmrHoldIkiForVuln && !BmrBlockIkiBeforeDowntime
            && CanBurst && HasFugetsuAndFuka && !HasZanshinReady && !CombatElapsedLessGCD(2))
        {
            if (IkishotenPvE.CanUse(out act))
                return true;
        }

        // ============================================================
        // BMR: AGGRESSIVE KENKI DUMP BEFORE DOWNTIME
        // Kenki is wasted during downtime. Spend it all on Shinten/Kyuten.
        // Also fire Senei/Guren if available -- better to use than waste.
        // ============================================================
        if (BmrDowntimeImminent && !HasZanshinReady && Kenki >= 25)
        {
            // Fire Senei/Guren if available (big damage before boss leaves)
            if (HissatsuGurenPvE.CanUse(out act, skipAoeCheck: !HissatsuSeneiPvE.EnoughLevel))
                return true;
            if (HissatsuSeneiPvE.CanUse(out act))
                return true;

            // Dump remaining Kenki via Shinten/Kyuten
            if (HissatsuKyutenPvE.CanUse(out act))
                return true;
            if (HissatsuShintenPvE.CanUse(out act))
                return true;
        }

        // ============================================================
        // 1. MEIKYO SHISUI: grants 3 combo-skip stacks + Tendo buff
        // Use during burst, after Tsubamegaeshi follow-ups, or to maintain buffs.
        // Balance guide: one charge for burst Tendo, one for filler/realignment.
        // BMR: Hold one charge for post-downtime re-entry when downtime is close.
        // ============================================================
        if (CanBurst && HasHostilesInRange && HasFugetsuAndFuka)
        {
            // During burst: use after Kaeshi follow-ups to set up next Tendo Setsugekka
            if (TsubamegaeshiActionReady || IsLastAction(false, TendoKaeshiSetsugekkaPvE, KaeshiSetsugekkaPvE, KaeshiNamikiriPvE, TendoKaeshiGokenPvE, KaeshiGokenPvE))
            {
                // BMR: still allow burst Meikyo even near downtime -- we need it for Tendo
                if (MeikyoShisuiPvE.CanUse(out act, usedUp: true))
                    return true;
            }

            // At 0 Sen after burst finishes: use to quickly rebuild 3 Sen
            // BMR: hold if downtime coming and we only have 1 charge
            if (SenCount == 0 && !HasMeikyoShisui && !TsubamegaeshiActionReady && !BmrHoldMeikyoForDowntime)
            {
                if (MeikyoShisuiPvE.CanUse(out act, usedUp: EnhancedMeikyoShisuiTrait.EnoughLevel && MeikyoShisuiPvE.Cooldown.WillHaveXChargesGCD(2, 1)))
                    return true;
            }
        }

        // Meikyo outside burst: use when buffs need refresh, target dying, or during filler
        // Balance: Meikyo IS used during filler phases to fast-track Midare cycles
        // BMR: hold for post-downtime if downtime is close and only 1 charge
        if (!HasMeikyoShisui && !TsubamegaeshiActionReady && SenCount == 0 && !BmrHoldMeikyoForDowntime)
        {
            // Emergency: buffs about to fall off or target dying
            if (!HasFugetsuAndFuka || (isTargetBoss && isTargetDying))
            {
                if (MeikyoShisuiPvE.CanUse(out act))
                    return true;
            }

            // Filler Meikyo: use at 0 Sen with both buffs up to accelerate Midare cycles
            // Only when not approaching burst (Ikishoten > 30s away)
            if (HasFugetsuAndFuka && !IsBigBurst && !IsPreBurst
                && (!IkishotenPvE.EnoughLevel || IkishotenPvE.Cooldown.RecastTimeRemain > 30))
            {
                if (MeikyoShisuiPvE.CanUse(out act))
                    return true;
            }
        }

        // Overcap prevention: if at 2 charges and burst is not imminent
        if (EnhancedMeikyoShisuiTrait.EnoughLevel
            && MeikyoShisuiPvE.Cooldown.CurrentCharges >= 2
            && !HasMeikyoShisui && !TsubamegaeshiActionReady)
        {
            if (MeikyoShisuiPvE.CanUse(out act, usedUp: true))
                return true;
        }

        // ============================================================
        // 2. ZANSHIN: HIGHEST priority oGCD when ready (50 Kenki)
        // Massive damage, granted by Ikishoten. Use immediately.
        // ============================================================
        if (ZanshinPvE.CanUse(out act))
            return true;

        // ============================================================
        // 3. IKISHOTEN: grants Ogi Namikiri Ready + Zanshin Ready + 50 Kenki
        // 120s cooldown. Use in burst window after establishing buffs.
        // Note: base class ActionCheck requires Kenki >= 50 and InCombat.
        // BMR: hold for vuln windows, block if downtime is too close.
        // ============================================================
        {
            bool bmrBlockIki = BmrHoldIkiForVuln || BmrBlockIkiBeforeDowntime;

            if (!bmrBlockIki && CanBurst && !HasZanshinReady && HasFugetsuAndFuka && !CombatElapsedLessGCD(2))
            {
                if (IkishotenPvE.CanUse(out act))
                    return true;
            }
        }

        // ============================================================
        // 4. SHOHA: at 3 Meditation stacks (generated by Iaijutsu/Ogi)
        // Prefer during burst or before next Iaijutsu to avoid overcap.
        // ============================================================
        {
            bool nextGCDIsIaijutsu = nextGCD.IsTheSameTo(true,
                ActionID.OgiNamikiriPvE, ActionID.HiganbanaPvE,
                ActionID.TenkaGokenPvE, ActionID.MidareSetsugekkaPvE,
                ActionID.TendoGokenPvE, ActionID.TendoSetsugekkaPvE);

            // Use Shoha when Meditation is full and we won't waste the oGCD window on Zanshin
            if (!HasZanshinReady && (nextGCDIsIaijutsu || IkishotenPvE.Cooldown.RecastTimeElapsed < 30))
            {
                if (ShohaPvE.CanUse(out act))
                    return true;
            }
        }

        // ============================================================
        // 5. SENEI / GUREN: 25 Kenki nuke on 60s CD
        // Senei = single target, Guren = AoE (3+ targets).
        // Use when Ikishoten is on cooldown (don't use before first Ikishoten).
        // ============================================================
        if (!HasZanshinReady && !CombatElapsedLessGCD(2))
        {
            if (IkishotenPvE.Cooldown.IsCoolingDown || !IkishotenPvE.EnoughLevel)
            {
                if (HissatsuGurenPvE.CanUse(out act, skipAoeCheck: !HissatsuSeneiPvE.EnoughLevel))
                    return true;
                if (HissatsuSeneiPvE.CanUse(out act))
                    return true;
            }
        }

        // ============================================================
        // 6. SHOHA fallback: use at 3 stacks even outside burst to prevent waste
        // ============================================================
        if (!HasZanshinReady && ShohaPvE.CanUse(out act))
            return true;

        // ============================================================
        // 7. SHINTEN / KYUTEN: Kenki spender (25 each)
        // Spend when above threshold to prevent overcapping.
        // During burst, spend more aggressively. During filler, hold for burst.
        // Never spend if Zanshin is ready (it costs 50 Kenki).
        // BMR: dump all Kenki when downtime is approaching.
        // ============================================================
        if (!HasZanshinReady)
        {
            // Aggressive spend during burst, high Kenki, target dying, or BMR downtime
            bool shouldSpend = IsBigBurst
                || IsMiniBurst
                || Kenki >= KenkiSpendThreshold
                || (!IkishotenPvE.EnoughLevel && Kenki >= 25)
                || (isTargetBoss && isTargetDying && Kenki >= 25)
                || (BmrDowntimeSoon && Kenki >= 25);

            if (shouldSpend)
            {
                if (HissatsuKyutenPvE.CanUse(out act))
                    return true;
                if (HissatsuShintenPvE.CanUse(out act))
                    return true;
            }
        }

        // ============================================================
        // 8. HAGAKURE: convert Sen to Kenki for realignment
        // Use when we have Sen but need to realign (e.g., switching to AoE).
        // Also useful if stuck with wrong Sen count approaching burst.
        // ============================================================
        // Intentionally not used automatically -- Hagakure is a manual realignment
        // tool and automatic usage can desync the rotation. The base class ActionCheck
        // already handles Kenki overflow protection.

        return base.AttackAbility(nextGCD, out act);
    }

    #endregion

    #region GCD Logic

    protected override bool GeneralGCD(out IAction? act)
    {
        if (BMRPyreticActive) { act = null; return false; }
        bool isTargetBoss = CurrentTarget?.IsBossFromTTK() ?? false;
        bool isTargetDying = CurrentTarget?.IsDying() ?? false;

        // ================================================================
        // PRIORITY 1: ALWAYS FINISH KAESHI / TSUBAMEGAESHI FOLLOW-UPS
        // These are free follow-up GCDs that must be pressed immediately.
        // Even during downtime approach -- they're instant and high potency.
        // ================================================================

        // Kaeshi Namikiri (follow-up to Ogi Namikiri)
        if (KaeshiNamikiriPvE.CanUse(out act))
            return true;

        // Tendo Kaeshi Setsugekka (follow-up to Tendo Setsugekka)
        if (TendoKaeshiSetsugekkaPvE.CanUse(out act))
            return true;

        // Tendo Kaeshi Goken (follow-up to Tendo Goken)
        if (TendoKaeshiGokenPvE.CanUse(out act, skipAoeCheck: true))
            return true;

        // Kaeshi Setsugekka (follow-up to Midare Setsugekka)
        if (KaeshiSetsugekkaPvE.CanUse(out act))
            return true;

        // Kaeshi Goken (follow-up to Tenka Goken)
        if (KaeshiGokenPvE.CanUse(out act, skipComboCheck: true, skipAoeCheck: true))
            return true;

        // ================================================================
        // BMR: DUMP SEN AS IAIJUTSU BEFORE DOWNTIME
        // If downtime is imminent (<=10s), fire any available Iaijutsu to avoid
        // wasting Sen. Midare/Tendo at 3 Sen, Tenka/Tendo Goken at 2 Sen.
        // Even Higanbana at 1 Sen is better than losing the Sen entirely.
        // But don't start a cast if downtime < 3s (won't finish the cast + Kaeshi).
        // ================================================================
        if (BmrDowntimeImminent && !BmrBlockLongCast && HasFugetsuAndFuka)
        {
            // Ogi Namikiri: instant follow-up after cast, dump before downtime
            if (OgiNamikiriPvE.CanUse(out act) && OgiNamikiriPvE.Target.Target != null)
                return true;

            // Tendo Setsugekka / Goken (strongest, use first if available)
            if (TendoSetsugekkaPvE.CanUse(out act, skipComboCheck: true))
                return true;
            if (TendoGokenPvE.CanUse(out act, skipComboCheck: true))
                return true;

            // Standard Midare / Tenka Goken
            if (MidareSetsugekkaPvE.CanUse(out act, skipComboCheck: true))
                return true;
            if (TenkaGokenPvE.CanUse(out act, skipComboCheck: true))
                return true;

            // Higanbana: 1 Sen, better than losing it. Skip if it's already on target.
            if (SenCount == 1 && HiganbanaPvE.CanUse(out act, skipComboCheck: true, skipTTKCheck: true))
                return true;
        }

        // ================================================================
        // PRIORITY 2: OGI NAMIKIRI (burst-aligned, 120s)
        // Only use when Higanbana is already on the target (boss) and
        // both personal buffs (Fugetsu + Fuka) are active.
        // BMR: Don't start if downtime < 3s (cast time ~1.3s + Kaeshi follow-up).
        // ================================================================
        if (!BmrBlockLongCast && OgiNamikiriPvE.CanUse(out act) && OgiNamikiriPvE.Target.Target != null)
        {
            bool targetHasHiganbana = OgiNamikiriPvE.Target.Target?.HasStatus(true, StatusID.Higanbana) ?? false;
            bool isNonBoss = !isTargetBoss;

            // Use if: non-boss target, or boss has Higanbana, and buffs are up
            if ((isNonBoss || targetHasHiganbana) && HasFugetsuAndFuka)
                return true;

            // AoE scenario: 2+ targets, just use it
            if (NumberOfHostilesInRange >= 2)
                return true;
        }

        // ================================================================
        // PRIORITY 3: TENDO IAIJUTSU (Tendo buff + 3 Sen / 2 Sen)
        // Tendo versions are stronger. Use when Tendo buff is active.
        // BMR: Don't start if downtime < 3s (cast + Kaeshi won't complete).
        // ================================================================

        if (!BmrBlockLongCast)
        {
            // Tendo Setsugekka: 3 Sen + Tendo buff (ST)
            if (TendoSetsugekkaPvE.CanUse(out act, skipComboCheck: true))
                return true;

            // Tendo Goken: 2 Sen + Tendo buff (AoE)
            if (TendoGokenPvE.CanUse(out act, skipComboCheck: true))
                return true;
        }

        // ================================================================
        // PRIORITY 4: STANDARD IAIJUTSU (no Tendo buff)
        // BMR: Don't start if downtime < 3s.
        // ================================================================

        if (!BmrBlockLongCast)
        {
            // Midare Setsugekka: 3 Sen, no Tendo (ST)
            if (MidareSetsugekkaPvE.CanUse(out act, skipComboCheck: true))
                return true;

            // Tenka Goken: 2 Sen, no Tendo (AoE, 3+ targets)
            if (TenkaGokenPvE.CanUse(out act, skipComboCheck: true))
                return true;
        }

        // ================================================================
        // PRIORITY 5: HIGANBANA (1 Sen, DoT management)
        // Only on boss targets. Refresh when about to expire.
        // Skip during Meikyo (don't waste stacks on 1-Sen move).
        // Skip when we have 3 Sen or are in Tendo (should use Midare/Tendo instead).
        // BMR: Don't apply if downtime is imminent (DoT won't tick fully).
        // ================================================================
        if (!HasMeikyoShisui && !MidareSetsugekkaReady && !TendoSetsugekkaReady
            && HasFugetsuAndFuka && !WillFugetsuEnd && !WillFukaEnd
            && !BmrDowntimeImminent)
        {
            // Multi-target gate: don't use Higanbana if setting enabled and 2+ targets
            bool higanbanaAllowed = !HiganbanaTargets || NumberOfAllHostilesInRange < 2;

            if (higanbanaAllowed)
            {
                if (HiganbanaPvE.CanUse(out act, skipComboCheck: true,
                    skipTTKCheck: isTargetBoss || IsInHighEndDuty))
                    return true;
            }
        }

        // ================================================================
        // PRIORITY 6: AOE COMBO (3+ targets, Fuko/Fuga -> Mangetsu/Oka)
        // ================================================================

        // AoE finishers: Mangetsu (Getsu + Fugetsu) / Oka (Ka + Fuka)
        if (HasFugetsuAndFuka)
        {
            // Refresh whichever buff ends first
            switch (FugetsuOrFukaEndsFirst)
            {
                case "Fugetsu":
                    if (MangetsuPvE.CanUse(out act, skipStatusProvideCheck: true,
                        skipComboCheck: HasMeikyoShisui && !HasGetsu))
                        return true;
                    break;
                case "Fuka":
                    if (OkaPvE.CanUse(out act, skipStatusProvideCheck: true,
                        skipComboCheck: HasMeikyoShisui && !HasKa))
                        return true;
                    break;
                case "Equal":
                case null:
                    if (MangetsuPvE.CanUse(out act, skipStatusProvideCheck: true,
                        skipComboCheck: HasMeikyoShisui && !HasGetsu))
                        return true;
                    if (OkaPvE.CanUse(out act, skipStatusProvideCheck: true,
                        skipComboCheck: HasMeikyoShisui && !HasKa))
                        return true;
                    break;
            }
        }
        if (!HasFugetsuAndFuka)
        {
            // Establish missing buffs via AoE combo
            if (!OkaPvE.EnoughLevel && MangetsuPvE.CanUse(out act, skipStatusProvideCheck: true,
                skipComboCheck: HasMeikyoShisui && !HasGetsu))
                return true;

            if (!HasFugetsu && MangetsuPvE.CanUse(out act))
                return true;
            if (!HasFuka && OkaPvE.CanUse(out act))
                return true;
        }

        // AoE base combo (Fuko or Fuga)
        // BMR: Don't start new AoE combo if downtime < 5s
        if (!HasMeikyoShisui && !BmrBlockNewCombo)
        {
            if (FugaMasteryTrait.EnoughLevel)
            {
                if (FukoPvE.CanUse(out act))
                    return true;
            }
            else if (FugaPvE.CanUse(out act))
            {
                return true;
            }
        }

        // ================================================================
        // PRIORITY 7: SINGLE TARGET COMBO FINISHERS
        // Gekko (rear, Getsu + Fugetsu), Kasha (flank, Ka + Fuka),
        // Yukikaze (Setsu). Meikyo lets us skip to these directly.
        // Prefer positionals we can actually hit.
        // ================================================================

        // With Meikyo active: use finishers directly, skipping combo steps.
        // Prioritize filling missing Sen, then favor positional-correct hits.
        if (HasMeikyoShisui)
        {
            // Fill missing Sen in order: prioritize what we don't have
            if (!HasGetsu && GekkoPvE.CanUse(out act, skipComboCheck: true))
                return true;
            if (!HasKa && KashaPvE.CanUse(out act, skipComboCheck: true))
                return true;
            if (!HasSetsu && HasFugetsuAndFuka && YukikazePvE.CanUse(out act, skipComboCheck: true))
                return true;

            // All Sen present or duplicating: try positional-correct first
            if (GekkoPvE.CanUse(out act, skipComboCheck: true) && GekkoPvE.Target.Target != null
                && CanHitPositional(EnemyPositional.Rear, GekkoPvE.Target.Target))
                return true;
            if (KashaPvE.CanUse(out act, skipComboCheck: true) && KashaPvE.Target.Target != null
                && CanHitPositional(EnemyPositional.Flank, KashaPvE.Target.Target))
                return true;

            // Fallback: any finisher that works
            if (GekkoPvE.CanUse(out act, skipComboCheck: true))
                return true;
            if (KashaPvE.CanUse(out act, skipComboCheck: true))
                return true;
        }

        // Normal combo finishers (not in Meikyo, combo is active)
        // Try positional-correct first for extra damage
        if (GekkoPvE.CanUse(out act) && GekkoPvE.Target.Target != null
            && CanHitPositional(EnemyPositional.Rear, GekkoPvE.Target.Target))
            return true;
        if (KashaPvE.CanUse(out act) && KashaPvE.Target.Target != null
            && CanHitPositional(EnemyPositional.Flank, KashaPvE.Target.Target))
            return true;

        // Yukikaze: Setsu Sen, no positional. Use when we need Setsu and have both buffs.
        if (!HasSetsu && HasFugetsuAndFuka && YukikazePvE.CanUse(out act))
            return true;

        // Fallback: finishers without positional check
        if (GekkoPvE.CanUse(out act))
            return true;
        if (KashaPvE.CanUse(out act))
            return true;

        // ================================================================
        // PRIORITY 8: MID-COMBO STEPS (Jinpu / Shifu)
        // Jinpu -> Gekko path (Fugetsu buff + Getsu Sen)
        // Shifu -> Kasha path (Fuka buff + Ka Sen)
        // Refresh whichever buff ends first.
        // ================================================================
        if (HasFugetsuAndFuka)
        {
            switch (FugetsuOrFukaEndsFirst)
            {
                case "Fugetsu":
                    if (JinpuPvE.CanUse(out act, skipStatusProvideCheck: true))
                        return true;
                    break;
                case "Fuka":
                    if (ShifuPvE.CanUse(out act, skipStatusProvideCheck: true))
                        return true;
                    break;
                case "Equal":
                case null:
                    if (JinpuPvE.CanUse(out act, skipStatusProvideCheck: true))
                        return true;
                    if (ShifuPvE.CanUse(out act, skipStatusProvideCheck: true))
                        return true;
                    break;
            }
        }
        if (!HasFugetsuAndFuka)
        {
            // Establish missing buffs: Fugetsu (damage) is higher priority than Fuka (speed)
            if (!HasFugetsu && JinpuPvE.CanUse(out act))
                return true;
            if (!HasFuka && ShifuPvE.CanUse(out act))
                return true;

            // Fallback for early levels / fresh combat
            if (JinpuPvE.CanUse(out act))
                return true;
            if (ShifuPvE.CanUse(out act))
                return true;
        }

        // ================================================================
        // PRIORITY 9: BASE COMBO STARTER (Hakaze / Gyofu)
        // Don't use during Meikyo (waste of stacks) or when Tsubamegaeshi is ready.
        // BMR: Don't start a new combo if downtime < 5s (combo won't complete).
        // ================================================================
        if (!HasMeikyoShisui && !TsubamegaeshiActionReady && !BmrBlockNewCombo)
        {
            if (GyofuPvE.EnoughLevel)
            {
                if (GyofuPvE.CanUse(out act))
                    return true;
            }
            if (HakazePvE.CanUse(out act))
                return true;
        }

        // ================================================================
        // PRIORITY 10: RANGED FALLBACK (Enpi)
        // Only when not in melee range. Doesn't break combo.
        // ================================================================
        if (EnpiPvE.CanUse(out act))
            return true;

        return base.GeneralGCD(out act);
    }

    #endregion
}
