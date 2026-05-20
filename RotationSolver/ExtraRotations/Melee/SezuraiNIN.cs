namespace RotationSolver.ExtraRotations.Melee;

[Rotation("SezuraiNIN", CombatType.PvE, GameVersion = "7.41",
    Description = "Balance-aligned NIN with 60s burst around Kunai's Bane, mudra optimization, Kazematoi management, and BMR timeline integration.")]
[SourceCode(Path = "main/ExtraRotations/Melee/SezuraiNIN.cs")]
[ExtraRotation]
public sealed class SezuraiNIN : NinjaRotation
{
    #region Config Options

    [Range(0f, 0.25f, ConfigUnitType.Percent)]
    [RotationConfig(CombatType.PvE, Name = "Action Ahead Override (0 = use global setting)")]
    public float ActionAheadOverride { get; set; } = 0f;

    [RotationConfig(CombatType.PvE, Name = "Auto Pot Usage (Gemdraught 5s before Kunai's Bane + pre-pull in countdown)")]
    public bool BurstMed { get; set; } = true;

    [RotationConfig(CombatType.PvE, Name = "Use Forked Raiju instead of Fleeting Raiju")]
    public bool UseForkedRaiju { get; set; } = false;

    [RotationConfig(CombatType.PvE, Name = "Use Hide to reset mudra charges out of combat")]
    public bool UseHide { get; set; } = true;

    [RotationConfig(CombatType.PvE, Name = "Auto remove Hidden status when combat starts")]
    public bool AutoUnhide { get; set; } = true;

    [RotationConfig(CombatType.PvE, Name = "BMR: Hold burst for vulnerability windows (within 30s)")]
    public bool BmrHoldBurstForVuln { get; set; } = true;

    [RotationConfig(CombatType.PvE, Name = "BMR: Dump resources before downtime")]
    public bool BmrDumpBeforeDowntime { get; set; } = true;

    #endregion

    #region Burst State

    /// <summary>
    /// Whether the user has burst enabled in the framework.
    /// </summary>
    private bool CanBurst => MergedStatus.HasFlag(AutoStatus.Burst);

    /// <summary>
    /// True when Kunai's Bane is off cooldown or was recently used (within 17s window).
    /// This represents the broad burst availability window.
    /// </summary>
    private bool InBurstWindow => KunaisBanePvE.EnoughLevel
        && (KunaisBanePvE.Cooldown.HasOneCharge || KunaisBanePvE.Cooldown.JustUsedAfter(17));

    /// <summary>
    /// True when Kunai's Bane was just used and we are within the damage buff window.
    /// The debuff lasts ~16.25s.
    /// </summary>
    private bool InActiveBurst => KunaisBanePvE.EnoughLevel
        && KunaisBanePvE.Cooldown.IsCoolingDown
        && !KunaisBanePvE.Cooldown.ElapsedAfter(17);

    /// <summary>
    /// True when we are within 20 seconds of Kunai's Bane coming off cooldown.
    /// Used to prepare Suiton for Shadow Walker.
    /// </summary>
    private bool IsPreBurst => KunaisBanePvE.EnoughLevel
        && KunaisBanePvE.Cooldown.IsCoolingDown
        && !KunaisBanePvE.Cooldown.HasOneCharge
        && KunaisBanePvE.Cooldown.RecastTimeRemain <= 20;

    /// <summary>
    /// True when we are close to burst and should save resources.
    /// Within 7s of Kunai's Bane becoming available.
    /// </summary>
    private bool IsBurstSoon => KunaisBanePvE.EnoughLevel
        && KunaisBanePvE.Cooldown.IsCoolingDown
        && !KunaisBanePvE.Cooldown.HasOneCharge
        && KunaisBanePvE.Cooldown.RecastTimeRemain <= 7;

    /// <summary>
    /// True when Dokumori window is active (120s CD, every other burst).
    /// </summary>
    private bool InDokumoriWindow => DokumoriPvE.EnoughLevel
        && DokumoriPvE.Cooldown.IsCoolingDown
        && !DokumoriPvE.Cooldown.ElapsedAfter(21);

    #endregion

    #region BMR Helpers

    /// <summary>
    /// BMR signals downtime within 20s — we should dump burst cooldowns now.
    /// Falls back to false when BMR is not active.
    /// </summary>
    private bool BmrDowntimeSoon => BmrDumpBeforeDowntime && BMRActive && BMRDowntimeIn is > 0 and <= 20f;

    /// <summary>
    /// BMR signals downtime within 10s — dump remaining resources immediately.
    /// </summary>
    private bool BmrDowntimeImminent => BmrDumpBeforeDowntime && BMRActive && BMRDowntimeIn is > 0 and <= 10f;

    /// <summary>
    /// BMR signals a vulnerability window within 30s — hold Kunai's Bane for it.
    /// Only applies if burst is not already active and the option is enabled.
    /// </summary>
    private bool BmrShouldHoldBurstForVuln => BmrHoldBurstForVuln && BMRActive
        && BMRVulnerableIn is > 0 and <= 30f
        && !InActiveBurst;

    /// <summary>
    /// BMR signals downtime within 15s and Kunai's Bane CD is 60s,
    /// so using it now would waste it (boss leaves before window ends
    /// and it won't be back for the return). Don't pop burst.
    /// </summary>
    private bool BmrShouldSkipBurst => BmrDumpBeforeDowntime && BMRActive
        && BMRDowntimeIn is > 0 and <= 15f
        && !InActiveBurst
        && KunaisBanePvE.Cooldown.HasOneCharge;

    /// <summary>
    /// True when BMR says downtime is within 5s — don't start TCJ (takes ~5s to execute).
    /// </summary>
    private bool BmrBlockTCJ => BMRActive && BMRDowntimeIn is > 0 and <= 5f;

    /// <summary>
    /// True when BMR says downtime is within 3s — don't start new ninjutsu (takes ~3s).
    /// </summary>
    private bool BmrBlockNinjutsu => BMRActive && BMRDowntimeIn is > 0 and <= 3f;

    /// <summary>
    /// Raidwide is coming within 5s — for defensive timing.
    /// </summary>
    private bool BmrRaidwideSoon => BMRActive && BMRRaidwideIn is > 0 and <= 5f;

    /// <summary>
    /// Raidwide is coming within 3s — tighter timing for Shade Shift.
    /// </summary>
    private bool BmrRaidwideImminent => BMRActive && BMRRaidwideIn is > 0 and <= 3f;

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

    #region Mudra State Machine

    /// <summary>
    /// Tracks the last ninjutsu aim that was cleared (for debug display).
    /// </summary>
    private IBaseAction? _lastNinActionAim;

    /// <summary>
    /// Holds the next ninjutsu action to perform.
    /// When set, the GCD logic will execute the mudra sequence to reach this ninjutsu.
    /// </summary>
    private IBaseAction? _ninActionAim;

    /// <summary>
    /// True when no ninjutsu is currently being prepared
    /// (the adjusted Ninjutsu action ID matches the base Ninjutsu ID).
    /// </summary>
    private static bool NoActiveNinjutsu => AdjustId(ActionID.NinjutsuPvE) == ActionID.NinjutsuPvE;

    /// <summary>
    /// True when the current adjusted ninjutsu result is Rabbit Medium (failed mudra).
    /// </summary>
    private static bool RabbitMediumCurrent => AdjustId(ActionID.NinjutsuPvE) == ActionID.RabbitMediumPvE;

    /// <summary>
    /// Current ninjutsu state checks.
    /// </summary>
    private static bool FumaShurikenCurrent => AdjustId(ActionID.NinjutsuPvE) == ActionID.FumaShurikenPvE;
    private static bool RaitonCurrent => AdjustId(ActionID.NinjutsuPvE) == ActionID.RaitonPvE;
    private static bool SuitonCurrent => AdjustId(ActionID.NinjutsuPvE) == ActionID.SuitonPvE;
    private static bool KatonCurrent => AdjustId(ActionID.NinjutsuPvE) == ActionID.KatonPvE;
    private static bool HyotonCurrent => AdjustId(ActionID.NinjutsuPvE) == ActionID.HyotonPvE;
    private static bool HutonCurrent => AdjustId(ActionID.NinjutsuPvE) == ActionID.HutonPvE;
    private static bool DotonCurrent => AdjustId(ActionID.NinjutsuPvE) == ActionID.DotonPvE;
    private static bool GokaMekkyakuCurrent => AdjustId(ActionID.NinjutsuPvE) == ActionID.GokaMekkyakuPvE;
    private static bool HyoshoRanryuCurrent => AdjustId(ActionID.NinjutsuPvE) == ActionID.HyoshoRanryuPvE;

    /// <summary>
    /// Sets the target ninjutsu to execute. Guards against setting during active
    /// mudra sequences or when Rabbit Medium is queued.
    /// </summary>
    private void SetNinjutsu(IBaseAction act)
    {
        if (AdjustId(ActionID.NinjutsuPvE) == ActionID.RabbitMediumPvE)
            return;

        if (_ninActionAim != null && IsLastAction(false, TenPvE, JinPvE, ChiPvE,
            FumaShurikenPvE_18873, FumaShurikenPvE_18874, FumaShurikenPvE_18875))
            return;

        if (_ninActionAim != act)
            _ninActionAim = act;
    }

    /// <summary>
    /// Clears the ninjutsu action aim, storing the last aim for debug display.
    /// </summary>
    private void ClearNinjutsu()
    {
        if (_ninActionAim != null)
        {
            _lastNinActionAim = _ninActionAim;
            _ninActionAim = null;
        }
    }

    /// <summary>
    /// Decides which ninjutsu to prepare based on game state.
    /// Called from EmergencyAbility each cycle to keep the aim updated.
    /// </summary>
    private void ChoiceNinjutsu()
    {
        // BMR: Don't start new ninjutsu if downtime is imminent (< 3s) — waste of mudra charge.
        // Exception: Kassatsu ninjutsu should still be used (it's free and high-damage).
        if (BmrBlockNinjutsu && !HasKassatsu)
            return;

        // If Kassatsu is active, prioritize empowered ninjutsu
        if (HasKassatsu)
        {
            // AoE: Goka Mekkyaku
            if ((DeathBlossomPvE.CanUse(out _) || HakkeMujinsatsuPvE.CanUse(out _))
                && GokaMekkyakuPvE.EnoughLevel && !IsLastAction(false, GokaMekkyakuPvE)
                && GokaMekkyakuPvE.IsEnabled && ChiPvE.Info.IsQuestUnlocked())
            {
                SetNinjutsu(GokaMekkyakuPvE);
                return;
            }

            // ST: Hyosho Ranryu
            if (!(DeathBlossomPvE.CanUse(out _) || HakkeMujinsatsuPvE.CanUse(out _))
                && HyoshoRanryuPvE.EnoughLevel && !IsLastAction(false, HyoshoRanryuPvE)
                && HyoshoRanryuPvE.IsEnabled && JinPvE.Info.IsQuestUnlocked())
            {
                SetNinjutsu(HyoshoRanryuPvE);
                return;
            }

            // If Kassatsu but no Hyosho, fall through to Raiton
            if (!(DeathBlossomPvE.CanUse(out _) || HakkeMujinsatsuPvE.CanUse(out _))
                && !HyoshoRanryuPvE.EnoughLevel && RaitonPvE.EnoughLevel
                && RaitonPvE.IsEnabled && ChiPvE.Info.IsQuestUnlocked())
            {
                SetNinjutsu(RaitonPvE);
                return;
            }

            return;
        }

        // Normal mudra usage (no Kassatsu)
        // BMR: If downtime is soon, use mudra charges aggressively to avoid overcapping during downtime.
        bool bmrDumpMudra = BmrDowntimeSoon && !BmrBlockNinjutsu;
        bool shouldUseMudra = TenPvE.CanUse(out _, usedUp: ShadowWalkerNeeded || InTrickAttack
            || bmrDumpMudra
            || TenPvE.Cooldown.WillHaveXChargesGCD(2, 2, 0));

        if (!shouldUseMudra || _ninActionAim != null)
            return;

        // Priority 1: Suiton for Shadow Walker prep
        if (ShadowWalkerNeeded && !IsShadowWalking && !HasTenChiJin
            && SuitonPvE.EnoughLevel && JinPvE.Info.IsQuestUnlocked())
        {
            // AoE: use Huton instead (grants Shadow Walker in AoE scenarios)
            if ((DeathBlossomPvE.CanUse(out _) || HakkeMujinsatsuPvE.CanUse(out _))
                && HutonPvE.EnoughLevel && HutonPvE.IsEnabled)
            {
                SetNinjutsu(HutonPvE);
                return;
            }

            if (SuitonPvE.IsEnabled
                && ((TrickAttackPvE.IsEnabled && !KunaisBanePvE.EnoughLevel)
                    || (KunaisBanePvE.IsEnabled && KunaisBanePvE.EnoughLevel)))
            {
                SetNinjutsu(SuitonPvE);
                return;
            }
        }

        // Priority 2: AoE ninjutsu
        if (DeathBlossomPvE.CanUse(out _) || HakkeMujinsatsuPvE.CanUse(out _))
        {
            // Doton if not already up and not about to use TCJ
            if (!HasDoton && !IsMoving && !IsLastGCD(true, DotonPvE)
                && DotonPvE.EnoughLevel && JinPvE.Info.IsQuestUnlocked()
                && DotonPvE.IsEnabled && !TenChiJinPvE.Cooldown.WillHaveOneCharge(6))
            {
                SetNinjutsu(DotonPvE);
                return;
            }

            // Katon
            if (KatonPvE.EnoughLevel && KatonPvE.IsEnabled && ChiPvE.Info.IsQuestUnlocked())
            {
                SetNinjutsu(KatonPvE);
                return;
            }
        }

        // Priority 3: ST Raiton (main filler ninjutsu)
        if (!(DeathBlossomPvE.CanUse(out _) || HakkeMujinsatsuPvE.CanUse(out _))
            && !ShadowWalkerNeeded)
        {
            if (RaitonPvE.EnoughLevel && RaitonPvE.IsEnabled && ChiPvE.Info.IsQuestUnlocked()
                && (!HasRaijuReady || RaijuStacks < 3))
            {
                SetNinjutsu(RaitonPvE);
                return;
            }

            // Fuma Shuriken fallback (pre-Raiton or Raiju stacks capped)
            if (FumaShurikenPvE.EnoughLevel && FumaShurikenPvE.IsEnabled
                && TenPvE.Info.IsQuestUnlocked()
                && (!RaitonPvE.EnoughLevel || (HasRaijuReady && RaijuStacks == 3)))
            {
                SetNinjutsu(FumaShurikenPvE);
            }
        }
    }

    #endregion

    #region Ninjutsu Execution

    private bool DoRabbitMedium(out IAction? act)
    {
        act = null;
        if (RabbitMediumCurrent)
        {
            if (RabbitMediumPvE.CanUse(out act))
                return true;
            ClearNinjutsu();
        }
        return false;
    }

    private bool DoTenChiJin(out IAction? act)
    {
        act = null;
        if (!HasTenChiJin) return false;

        uint tenId = AdjustId(TenPvE.ID);
        uint chiId = AdjustId(ChiPvE.ID);
        uint jinId = AdjustId(JinPvE.ID);

        // First mudra: Fuma Shuriken
        if (tenId == FumaShurikenPvE_18873.ID
            && !IsLastAction(false, FumaShurikenPvE_18875, FumaShurikenPvE_18873))
        {
            // AoE path
            if (DeathBlossomPvE.CanUse(out _))
            {
                if (FumaShurikenPvE_18875.CanUse(out act))
                    return true;
            }
            // ST path
            if (FumaShurikenPvE_18873.CanUse(out act))
                return true;
        }
        // Second: Katon (AoE) or Raiton (ST)
        else if (tenId == KatonPvE_18876.ID && !IsLastAction(false, KatonPvE_18876))
        {
            if (KatonPvE_18876.CanUse(out act, skipAoeCheck: true))
                return true;
        }
        else if (chiId == RaitonPvE_18877.ID && !IsLastAction(false, RaitonPvE_18877))
        {
            if (RaitonPvE_18877.CanUse(out act, skipAoeCheck: true))
                return true;
        }
        // Third: Suiton (ST) or Doton (AoE)
        else if (jinId == SuitonPvE_18881.ID && !IsLastAction(false, SuitonPvE_18881))
        {
            if (SuitonPvE_18881.CanUse(out act, skipAoeCheck: true, skipStatusProvideCheck: true))
                return true;
        }
        else if (chiId == DotonPvE_18880.ID && !IsLastAction(false, DotonPvE_18880) && !HasDoton)
        {
            if (DotonPvE_18880.CanUse(out act, skipAoeCheck: true, skipStatusProvideCheck: true))
                return true;
        }

        return false;
    }

    private bool DoHyoshoRanryu(out IAction? act)
    {
        act = null;
        if (_ninActionAim != HyoshoRanryuPvE) return false;

        if (RabbitMediumCurrent) { ClearNinjutsu(); return false; }
        if (HyoshoRanryuCurrent) return HyoshoRanryuPvE.CanUse(out act, skipAoeCheck: true);
        if (FumaShurikenCurrent) return JinPvE_18807.CanUse(out act, usedUp: true);
        if (NoActiveNinjutsu) return ChiPvE_18806.CanUse(out act, usedUp: true);

        return false;
    }

    private bool DoGokaMekkyaku(out IAction? act)
    {
        act = null;
        if (_ninActionAim != GokaMekkyakuPvE) return false;

        if (RabbitMediumCurrent) { ClearNinjutsu(); return false; }
        if (GokaMekkyakuCurrent) return GokaMekkyakuPvE.CanUse(out act, skipAoeCheck: true);
        if (FumaShurikenCurrent) return TenPvE_18805.CanUse(out act, usedUp: true);
        if (NoActiveNinjutsu) return ChiPvE_18806.CanUse(out act, usedUp: true);

        return false;
    }

    private bool DoRaiton(out IAction? act)
    {
        act = null;
        if (_ninActionAim != RaitonPvE) return false;

        if (RabbitMediumCurrent) { ClearNinjutsu(); return false; }
        if (RaitonCurrent) return RaitonPvE.CanUse(out act);
        if (FumaShurikenCurrent) return ChiPvE_18806.CanUse(out act, usedUp: true);
        if (NoActiveNinjutsu) return TenPvE.CanUse(out act, usedUp: true);

        return false;
    }

    private bool DoSuiton(out IAction? act)
    {
        act = null;
        if (_ninActionAim != SuitonPvE) return false;

        if (RabbitMediumCurrent) { ClearNinjutsu(); return false; }
        if (SuitonCurrent) return SuitonPvE.CanUse(out act);
        if (RaitonCurrent) return JinPvE_18807.CanUse(out act, usedUp: true);
        if (FumaShurikenCurrent) return ChiPvE_18806.CanUse(out act, usedUp: true);
        if (NoActiveNinjutsu) return TenPvE.CanUse(out act, usedUp: true);

        return false;
    }

    private bool DoKaton(out IAction? act)
    {
        act = null;
        if (_ninActionAim != KatonPvE) return false;

        if (RabbitMediumCurrent) { ClearNinjutsu(); return false; }
        if (KatonCurrent) return KatonPvE.CanUse(out act, skipAoeCheck: true);
        if (FumaShurikenCurrent) return TenPvE_18805.CanUse(out act, usedUp: true);
        if (NoActiveNinjutsu) return ChiPvE.CanUse(out act, usedUp: true);

        return false;
    }

    private bool DoDoton(out IAction? act)
    {
        act = null;
        if (_ninActionAim != DotonPvE) return false;

        if (RabbitMediumCurrent) { ClearNinjutsu(); return false; }
        if (DotonCurrent) return DotonPvE.CanUse(out act, skipAoeCheck: true);
        if (HyotonCurrent) return ChiPvE_18806.CanUse(out act, usedUp: true);
        if (FumaShurikenCurrent) return JinPvE_18807.CanUse(out act, usedUp: true);
        if (NoActiveNinjutsu) return TenPvE.CanUse(out act, usedUp: true);

        return false;
    }

    private bool DoHuton(out IAction? act)
    {
        act = null;
        if (_ninActionAim != HutonPvE) return false;

        if (RabbitMediumCurrent) { ClearNinjutsu(); return false; }
        if (HutonCurrent) return HutonPvE.CanUse(out act, skipAoeCheck: true);
        if (HyotonCurrent) return TenPvE_18805.CanUse(out act, usedUp: true);
        if (FumaShurikenCurrent) return JinPvE_18807.CanUse(out act, usedUp: true);
        if (NoActiveNinjutsu) return ChiPvE.CanUse(out act, usedUp: true);

        return false;
    }

    private bool DoFumaShuriken(out IAction? act)
    {
        act = null;
        if (_ninActionAim != FumaShurikenPvE) return false;

        if (RabbitMediumCurrent) { ClearNinjutsu(); return false; }
        if (FumaShurikenCurrent) return FumaShurikenPvE.CanUse(out act);
        if (NoActiveNinjutsu) return TenPvE.CanUse(out act, usedUp: true);

        return false;
    }

    #endregion

    #region Countdown & Opener
    // === NIN OPENER (7.4 Balance) ===
    // Pre-pull: Hide(-6s) → Suiton mudra(-5s) → Pot(-2s)
    // GCD1: Spinning Edge → Kassatsu (weave)
    // GCD2: Gust Slash → Dokumori (weave, 120s party vuln debuff)
    // GCD3: Aeolian Edge → Bunshin (weave) → Dream Within a Dream (weave)
    // GCD4: Spinning Edge → Kunai's Bane (weave, consumes Suiton)
    // GCD5: Phantom Kamaitachi → Ten Chi Jin (weave)
    // TCJ: Fuma Shuriken → Raiton → Suiton → Meisui (weave)
    // → Fleeting/Forked Raiju → Zesho Meppo (Ninki dump)
    //
    // === EVEN BURST (120s) ===
    // Dokumori (party vuln) + Kunai's Bane + Kassatsu + TCJ + Bunshin
    // Full Ninki dump: Zesho Meppo + Bhavacakra under raid buffs
    // Hyosho Ranryu (from Kassatsu) under burst window
    //
    // === ODD BURST (60s) ===
    // Kunai's Bane + Kassatsu → Hyosho Ranryu
    // Use Phantom Kamaitachi in odd windows (save PK for odd, not even)
    // Hold TCJ and Bunshin for even windows
    //
    // === FILLER / SUSTAIN ===
    // Combo: Spinning Edge → Gust Slash → Aeolian Edge (rear) or Armor Crush (flank)
    // Kazematoi: Aeolian Edge grants 2 stacks, spend for positional flexibility
    // Raiton as default ninjutsu between bursts (don't waste Suiton outside burst prep)
    // Spend Ninki on Bhavacakra at ~85+ to avoid overcap, pool for burst below that
    // Use Fleeting/Forked Raiju ASAP to avoid losing proc

    protected override IAction? CountDownAction(float remainTime)
    {
        // Clear ninjutsu if countdown is long
        if (remainTime > 6)
        {
            ClearNinjutsu();
        }

        // Pre-pull Suiton: start mudra at ~5s, cast finishes around pull
        if (DoSuiton(out IAction? act))
        {
            return act == SuitonPvE && remainTime > CountDownAhead ? null : act;
        }

        if (remainTime < 5)
        {
            SetNinjutsu(SuitonPvE);
        }
        else if (remainTime < 6)
        {
            // Use Hide to reset mudra charges if available
            if (_ninActionAim == null && TenPvE.Cooldown.IsCoolingDown && HidePvE.CanUse(out act))
                return act;
        }

        // Pre-pull medicine at ~2s
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
        ImGui.Text("--- Mudra State ---");
        ImGui.Text($"Ninjutsu Aim: {_ninActionAim?.Name ?? "None"}");
        ImGui.Text($"Last Cleared: {_lastNinActionAim?.Name ?? "None"}");
        ImGui.Text($"NoNinjutsu: {NoNinjutsu} | IsExecutingMudra: {IsExecutingMudra}");
        ImGui.Text($"NoActiveNinjutsu: {NoActiveNinjutsu}");
        ImGui.Text("--- Burst State ---");
        ImGui.Text($"InBurstWindow: {InBurstWindow}");
        ImGui.Text($"InActiveBurst: {InActiveBurst}");
        ImGui.Text($"IsPreBurst: {IsPreBurst}");
        ImGui.Text($"IsBurstSoon: {IsBurstSoon}");
        ImGui.Text($"InTrickAttack: {InTrickAttack}");
        ImGui.Text($"InDokumoriWindow: {InDokumoriWindow}");
        ImGui.Text($"CanBurst: {CanBurst}");
        ImGui.Text("--- Gauge ---");
        ImGui.Text($"Ninki: {Ninki}/100");
        ImGui.Text($"Kazematoi: {Kazematoi}/5");
        ImGui.Text($"RaijuStacks: {RaijuStacks}");
        ImGui.Text("--- Buffs ---");
        ImGui.Text($"IsShadowWalking: {IsShadowWalking}");
        ImGui.Text($"HasKassatsu: {HasKassatsu}");
        ImGui.Text($"HasTenChiJin: {HasTenChiJin}");
        ImGui.Text($"HasPhantomKamaitachi: {HasPhantomKamaitachi}");
        ImGui.Text($"HasRaijuReady: {HasRaijuReady}");
        ImGui.Text($"TenriJindoPvEReady: {TenriJindoPvEReady}");
        ImGui.Text($"ZeshoMeppoPvEReady: {ZeshoMeppoPvEReady}");
        ImGui.Text($"DeathfrogMediumPvEReady: {DeathfrogMediumPvEReady}");
        ImGui.Text($"ShadowWalkerNeeded: {ShadowWalkerNeeded}");
        ImGui.Text($"Medicated: {StatusHelper.PlayerHasStatus(true, StatusID.Medicated)}");
        ImGui.Text("--- Weave ---");
        ImGui.Text($"WeaponRemain: {WeaponRemain:F2}s | WeaponTotal: {WeaponTotal:F2}s");
        ImGui.Text($"CanLateWeave: {CanLateWeave} | EnoughWeaveTime: {EnoughWeaveTime}");
        ImGui.Text($"KunaiCD: {(KunaisBanePvE.Cooldown.IsCoolingDown ? $"{KunaisBanePvE.Cooldown.RecastTimeRemain:F1}s" : "Ready")}");
        ImGui.Text($"DokumoriCD: {(DokumoriPvE.Cooldown.IsCoolingDown ? $"{DokumoriPvE.Cooldown.RecastTimeRemain:F1}s" : "Ready")}");
        ImGui.Text("--- BMR Decisions ---");
        ImGui.Text($"BmrDowntimeSoon: {BmrDowntimeSoon} | BmrDowntimeImminent: {BmrDowntimeImminent}");
        ImGui.Text($"BmrHoldBurstForVuln: {BmrShouldHoldBurstForVuln} | BmrSkipBurst: {BmrShouldSkipBurst}");
        ImGui.Text($"BmrBlockTCJ: {BmrBlockTCJ} | BmrBlockNinjutsu: {BmrBlockNinjutsu}");
        ImGui.Text($"BmrRaidwideSoon: {BmrRaidwideSoon} | BmrRaidwideImminent: {BmrRaidwideImminent}");
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

    #region Emergency oGCD Logic

    [RotationDesc]
    protected override bool EmergencyAbility(IAction nextGCD, out IAction? act)
    {
        act = null;

        // 1. Ninjutsu cleanup: clear aim after ninjutsu finishes or on bad state
        if (IsLastAction(false, FumaShurikenPvE, KatonPvE, RaitonPvE, HyotonPvE, DotonPvE, SuitonPvE)
            || (IsShadowWalking && (_ninActionAim == SuitonPvE || _ninActionAim == HutonPvE))
            || (_ninActionAim == GokaMekkyakuPvE && IsLastGCD(false, GokaMekkyakuPvE))
            || (_ninActionAim == HyoshoRanryuPvE && IsLastGCD(false, HyoshoRanryuPvE))
            || (_ninActionAim == GokaMekkyakuPvE && !HasKassatsu)
            || (_ninActionAim == HyoshoRanryuPvE && !HasKassatsu))
        {
            ClearNinjutsu();
        }

        // 2. Refresh ninjutsu decision (side-effect only, does not consume oGCD slot)
        if (InCombat && HasHostilesInMaxRange)
        {
            ChoiceNinjutsu();
        }

        if (!InCombat)
        {
            ClearNinjutsu();
        }

        // 3. Rabbit Medium recovery
        if (RabbitMediumPvE.CanUse(out act))
            return true;

        // If currently executing mudra or not in combat, defer
        if (!NoNinjutsu || !InCombat)
            return base.EmergencyAbility(nextGCD, out act);

        // 4. Tenri Jindo (from TCJ, high priority oGCD)
        if (TenriJindoPvE.CanUse(out act))
            return true;

        // 5. Kassatsu: use when Shadow Walker is active (about to burst) or during burst
        if (NoNinjutsu && !nextGCD.IsTheSameTo(false, ActionID.TenPvE, ActionID.ChiPvE, ActionID.JinPvE))
        {
            if (IsShadowWalking && KassatsuPvE.CanUse(out act))
                return true;

            // Also use during burst if not already Shadow Walking
            if (InActiveBurst && KassatsuPvE.CanUse(out act))
                return true;
        }

        // 6. Meisui: convert Shadow Walker to Ninki
        // Use after TCJ is on cooldown or when Shadow Walker is about to expire
        if (IsShadowWalking
            && (!TenChiJinPvE.Cooldown.IsCoolingDown
                || StatusHelper.PlayerWillStatusEndGCD(2, 0, true, StatusID.ShadowWalker))
            && MeisuiPvE.CanUse(out act))
        {
            return true;
        }

        // 7. Dokumori (120s CD, every other burst, apply debuff + Higi)
        // BMR: Skip Dokumori if we're holding burst for vuln, or if downtime will waste the window.
        // BMR: Force Dokumori if downtime is soon and we should dump (window will still get some value).
        {
            bool bmrBlockDokumori = BmrShouldHoldBurstForVuln || BmrShouldSkipBurst;
            bool bmrForceDokumori = BmrDowntimeSoon && !BmrShouldSkipBurst;

            if (!CombatElapsedLess(5) && (CanBurst || bmrForceDokumori) && !bmrBlockDokumori)
            {
                if (!DokumoriPvE.EnoughLevel)
                {
                    if (MugPvE.CanUse(out act))
                        return true;
                }
                else
                {
                    if (DokumoriPvE.CanUse(out act))
                        return true;
                }
            }
        }

        // 8. Kunai's Bane / Trick Attack (main burst window opener)
        // BMR: Hold if vuln window is coming soon (use burst there for more value).
        // BMR: Skip if downtime will cut the burst window short and it won't be worth it.
        // BMR: Force if downtime is imminent and we should dump before boss leaves.
        {
            bool bmrBlockBurst = BmrShouldHoldBurstForVuln || BmrShouldSkipBurst;
            bool bmrForceBurst = BmrDowntimeSoon && !BmrShouldSkipBurst;

            if (!CombatElapsedLess(6) && (CanBurst || bmrForceBurst) && !bmrBlockBurst)
            {
                if (!KunaisBanePvE.EnoughLevel)
                {
                    if (TrickAttackPvE.CanUse(out act, skipStatusProvideCheck: IsShadowWalking))
                        return true;
                }
                else
                {
                    if (KunaisBanePvE.CanUse(out act, skipAoeCheck: true, skipStatusProvideCheck: IsShadowWalking))
                        return true;
                }
            }
        }

        // 9. Meisui fallback: if Trick/Kunai is on CD and TCJ is on CD
        if (!CombatElapsedLess(6) && IsShadowWalking
            && TenChiJinPvE.Cooldown.IsCoolingDown
            && (KunaisBanePvE.Cooldown.IsCoolingDown || TrickAttackPvE.Cooldown.IsCoolingDown)
            && MeisuiPvE.CanUse(out act))
        {
            return true;
        }

        // 10. Burst medicine: use ~5s before Kunai's Bane becomes available
        if (BurstMed && KunaisBanePvE.EnoughLevel
            && KunaisBanePvE.Cooldown.IsCoolingDown
            && KunaisBanePvE.Cooldown.RecastTimeRemain <= 5
            && UseBurstMedicine(out act))
        {
            return true;
        }

        // Also use medicine during burst window if not yet medicated
        if (BurstMed && InActiveBurst && InCombat && UseBurstMedicine(out act))
            return true;

        return base.EmergencyAbility(nextGCD, out act);
    }

    #endregion

    #region Attack oGCD Logic

    protected override bool AttackAbility(IAction nextGCD, out IAction? act)
    {
        act = null;

        // No oGCDs during mudra execution
        if (!NoNinjutsu || !InCombat)
            return base.AttackAbility(nextGCD, out act);

        // 1. Ten Chi Jin (use in burst, when not Shadow Walking to avoid consuming it)
        // BMR: Don't start TCJ if downtime < 5s (TCJ takes ~5s to execute and you can't move during it).
        {
            if (!BmrBlockTCJ && InTrickAttack && !IsShadowWalking
                && !TenPvE.Cooldown.ElapsedAfter(30)
                && TenChiJinPvE.CanUse(out act))
            {
                return true;
            }
        }

        // 2. Bunshin (spend 50 Ninki, grants Phantom Kamaitachi)
        // BMR: Force Bunshin before downtime to avoid wasting Ninki and ensure PK is ready post-downtime.
        {
            bool bmrForceBunshin = BmrDowntimeSoon && Ninki >= 50;
            if ((!CombatElapsedLess(5) || bmrForceBunshin) && BunshinPvE.CanUse(out act))
                return true;
        }

        // 3. Dream Within A Dream (use during burst)
        // BMR: Also force DWAD before downtime to avoid wasting the charge.
        {
            bool bmrForceDWAD = BmrDowntimeImminent;
            if (InTrickAttack || bmrForceDWAD)
            {
                if (DreamWithinADreamPvE.CanUse(out act))
                    return true;

                if (!DreamWithinADreamPvE.Info.EnoughLevelAndQuest() && AssassinatePvE.CanUse(out act))
                    return true;
            }
        }

        // 4. Ninki spenders
        // Priority: Zesho Meppo (Higi-enhanced) > Deathfrog Medium (AoE enhanced) > Bhavacakra > Hellfrog Medium
        // Spend during burst or when about to overcap, but save 50 for Bunshin if needed
        // BMR: Dump all Ninki before downtime regardless of burst state.
        bool bmrDumpNinki = BmrDowntimeSoon && Ninki >= 50;
        bool shouldSpendNinki = bmrDumpNinki
            || ((!InMug || InTrickAttack)
                && (!BunshinPvE.Cooldown.WillHaveOneCharge(10) || HasPhantomKamaitachi || Ninki >= 85));

        if (shouldSpendNinki || Ninki >= 85)
        {
            // Zesho Meppo (enhanced Bhavacakra during Higi)
            if (ZeshoMeppoPvE.CanUse(out act))
                return true;

            // Deathfrog Medium (enhanced Hellfrog during Higi, AoE)
            if (DeathfrogMediumPvE.CanUse(out act, skipAoeCheck: !BhavacakraPvE.EnoughLevel))
                return true;

            // Hellfrog Medium (AoE Ninki spender)
            if (HellfrogMediumPvE.CanUse(out act, skipAoeCheck: !BhavacakraPvE.EnoughLevel))
                return true;

            // Bhavacakra (ST Ninki spender)
            if (BhavacakraPvE.CanUse(out act))
                return true;
        }

        // 5. Hard overcap protection at 100 Ninki
        if (Ninki == 100)
        {
            if (HellfrogMediumPvE.CanUse(out act, skipAoeCheck: !BhavacakraPvE.EnoughLevel))
                return true;
            if (BhavacakraPvE.CanUse(out act))
                return true;
        }

        // 5b. Kassatsu dump before downtime: if downtime imminent and Kassatsu available, pop it
        // so the next ninjutsu will be empowered (Hyosho/Goka) before boss leaves.
        if (BmrDowntimeImminent && NoNinjutsu && KassatsuPvE.CanUse(out act))
            return true;

        // 6. Movement
        if (MergedStatus.HasFlag(AutoStatus.MoveForward) && MoveForwardAbility(nextGCD, out act))
            return true;

        return base.AttackAbility(nextGCD, out act);
    }

    #endregion

    #region Defense / Utility

    // Note: DefenseAreaAbility (Feint) and MoveForwardAbility (Shukuchi) are sealed in the
    // base NinjaRotation class and cannot be overridden here. They are already handled.

    [RotationDesc(ActionID.ShadeShiftPvE)]
    protected override bool DefenseSingleAbility(IAction nextGCD, out IAction? act)
    {
        // BMR-aware: Shade Shift as self-shield before raidwide.
        // Tight timing (3s) — Shade Shift is instant and the shield lasts 20s,
        // but we want to avoid wasting it too early when BMR data is available.
        if (BmrRaidwideImminent && ShadeShiftPvE.CanUse(out act))
            return true;

        // BMR-aware: Bloodbath before incoming damage for self-sustain.
        // Broader window (5s) since Bloodbath heals over time as we hit.
        if (BmrRaidwideSoon && !IsExecutingMudra && BloodbathPvE.CanUse(out act))
            return true;

        // Fallback when BMR is not active — use Shade Shift whenever framework requests defense.
        if (!BMRActive && ShadeShiftPvE.CanUse(out act))
            return true;

        return base.DefenseSingleAbility(nextGCD, out act);
    }

    [RotationDesc]
    protected override bool HealSingleAbility(IAction nextGCD, out IAction? act)
    {
        if (!IsExecutingMudra && SecondWindPvE.CanUse(out act))
            return true;
        if (!IsExecutingMudra && BloodbathPvE.CanUse(out act))
            return true;
        return base.HealSingleAbility(nextGCD, out act);
    }

    [RotationDesc(ActionID.ForkedRaijuPvE)]
    protected override bool MoveForwardGCD(out IAction? act)
    {
        if (ForkedRaijuPvE.CanUse(out act))
            return true;
        return base.MoveForwardGCD(out act);
    }

    [RotationDesc(ActionID.ArmsLengthPvE)]
    protected override bool AntiKnockbackAbility(IAction nextGCD, out IAction? act)
    {
        if (ArmsLengthPvE.CanUse(out act) && !IsExecutingMudra)
            return true;
        return base.AntiKnockbackAbility(nextGCD, out act);
    }

    [RotationDesc(ActionID.LegSweepPvE)]
    protected override bool InterruptAbility(IAction nextGCD, out IAction? act)
    {
        if (LegSweepPvE.CanUse(out act) && !IsExecutingMudra)
            return true;
        return base.InterruptAbility(nextGCD, out act);
    }

    #endregion

    #region GCD Logic

    protected override bool GeneralGCD(out IAction? act)
    {
        if (BMRPyreticActive) { act = null; return false; }
        // 1. Phantom Kamaitachi: use during burst for alignment, or if buff is about to expire.
        // BMR: Also use before downtime so the proc is not wasted.
        if (!IsExecutingMudra && NoNinjutsu && !HasRaijuReady
            && !HasTenChiJin
            && PhantomKamaitachiPvE.CanUse(out act))
        {
            return true;
        }

        // 2. Raiju: spend stacks (does not break mudra, but skip during mudra execution)
        // BMR: Dump Raiju stacks before downtime to avoid losing the proc.
        if (!IsExecutingMudra)
        {
            if (!UseForkedRaiju && FleetingRaijuPvE.CanUse(out act))
                return true;
            if (UseForkedRaiju && ForkedRaijuPvE.CanUse(out act))
                return true;
            // Fallback: use whichever is available
            if (FleetingRaijuPvE.CanUse(out act))
                return true;
        }

        // 3. Ten Chi Jin execution (must complete the sequence — never interrupt mid-TCJ)
        if (DoTenChiJin(out act))
            return true;

        // 4. Rabbit Medium recovery
        if (DoRabbitMedium(out act))
            return true;

        // 5. Ninjutsu execution (mudra state machine)
        // BMR: If we already started a mudra sequence, always finish it.
        // BMR: If downtime is within 3s and we haven't started, ChoiceNinjutsu already blocked it.
        if (_ninActionAim != null)
        {
            if (DoGokaMekkyaku(out act)) return true;
            if (DoHyoshoRanryu(out act)) return true;
            if (DoHuton(out act)) return true;
            if (DoDoton(out act)) return true;
            if (DoKaton(out act)) return true;
            if (DoSuiton(out act)) return true;
            if (DoRaiton(out act)) return true;
            if (DoFumaShuriken(out act)) return true;
        }

        // If currently executing mudra, do not proceed to weaponskills
        if (IsExecutingMudra)
            return base.GeneralGCD(out act);

        // 6. AoE combo
        if (HakkeMujinsatsuPvE.CanUse(out act))
            return true;
        if (DeathBlossomPvE.CanUse(out act))
            return true;

        // 7. Single Target combo with Kazematoi management
        if (AeolianEdgePvE.EnoughLevel)
        {
            if (!ArmorCrushPvE.EnoughLevel)
            {
                // Pre-Armor Crush: just use Aeolian Edge
                if (AeolianEdgePvE.CanUse(out act))
                    return true;
            }
            else
            {
                // Kazematoi 0: must use Armor Crush to refill
                if (Kazematoi == 0 && ArmorCrushPvE.CanUse(out act))
                    return true;

                // Kazematoi > 0: prefer Aeolian Edge if we can hit rear positional
                if (Kazematoi > 0 && AeolianEdgePvE.CanUse(out act)
                    && AeolianEdgePvE.Target.Target != null
                    && CanHitPositional(EnemyPositional.Rear, AeolianEdgePvE.Target.Target))
                    return true;

                // Kazematoi < 4: prefer Armor Crush if we can hit flank positional
                if (Kazematoi < 4 && ArmorCrushPvE.CanUse(out act)
                    && ArmorCrushPvE.Target.Target != null
                    && CanHitPositional(EnemyPositional.Flank, ArmorCrushPvE.Target.Target))
                    return true;

                // Fallback: Aeolian Edge if gauge > 0
                if (Kazematoi > 0 && AeolianEdgePvE.CanUse(out act))
                    return true;

                // Fallback: Armor Crush if gauge < 4
                if (Kazematoi < 4 && ArmorCrushPvE.CanUse(out act))
                    return true;
            }
        }

        if (GustSlashPvE.CanUse(out act))
            return true;

        if (SpinningEdgePvE.CanUse(out act))
            return true;

        // 8. Ranged fallback
        if (!IsExecutingMudra && ThrowingDaggerPvE.CanUse(out act))
            return true;

        // 9. Auto-unhide when in combat
        if (StateEnabled && AutoUnhide && IsHidden)
        {
            StatusHelper.StatusOff(StatusID.Hidden);
        }

        // 10. Hide out of combat to reset mudra charges
        if (!InCombat && _ninActionAim == null && UseHide
            && TenPvE.Cooldown.IsCoolingDown && HidePvE.CanUse(out act))
        {
            return true;
        }

        return base.GeneralGCD(out act);
    }

    #endregion

    #region Burst Detection

    /// <inheritdoc/>
    public override bool IsBursting()
    {
        return InTrickAttack;
    }

    #endregion
}
