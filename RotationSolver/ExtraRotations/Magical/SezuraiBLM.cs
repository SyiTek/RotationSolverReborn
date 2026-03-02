namespace RotationSolver.ExtraRotations.Magical;

[Rotation("SezuraiBLM", CombatType.PvE, GameVersion = "7.41",
    Description = "Balance-aligned BLM with optimized Fire/Ice phases, Flare Star management, Manafont double AF, and movement tools.")]
[SourceCode(Path = "main/ExtraRotations/Magical/SezuraiBLM.cs")]
[ExtraRotation]
public sealed class SezuraiBLM : BlackMageRotation
{
    #region Config Options

    [Range(0f, 0.25f, ConfigUnitType.Percent)]
    [RotationConfig(CombatType.PvE, Name = "Action Ahead Override (0 = use global setting)")]
    public float ActionAheadOverride { get; set; } = 0f;

    [RotationConfig(CombatType.PvE, Name = "Auto Pot Usage (Gemdraught of Intelligence during Ley Lines)")]
    public bool BurstMed { get; set; } = true;

    [RotationConfig(CombatType.PvE, Name = "Use Ley Lines on cooldown (otherwise requires Burst enabled)")]
    public bool LeyLinesOnCooldown { get; set; } = true;

    [RotationConfig(CombatType.PvE, Name = "Use Retrace to return to Ley Lines when standing still")]
    public bool UseRetrace { get; set; } = true;

    [RotationConfig(CombatType.PvE, Name = "Use Transpose optimization (Firestarter proc transition)")]
    public bool UseTransposeOptimization { get; set; } = true;

    [RotationConfig(CombatType.PvE, Name = "Pool Xenoglossy for movement (keep 1 charge)")]
    public bool PoolXenoForMovement { get; set; } = true;

    #endregion

    #region Burst State

    /// <summary>
    /// Whether the user has burst enabled in the framework.
    /// </summary>
    private bool CanBurst => MergedStatus.HasFlag(AutoStatus.Burst);

    /// <summary>
    /// True when Ley Lines is active (our only personal buff window for burst alignment).
    /// </summary>
    private bool InBurstWindow => HasLeyLines;

    #endregion

    #region Phase Helpers

    /// <summary>
    /// Whether we have enough MP for at least one more Fire IV cast in AF.
    /// Fire IV costs 800 MP in AF3 with Umbral Hearts, but costs vary.
    /// We check against a safe threshold.
    /// </summary>
    private bool CanCastFireIV => InAstralFire && CurrentMp >= 1600;

    /// <summary>
    /// Whether we should finish AF with Despair because MP is low.
    /// Despair uses all remaining MP as an AF finisher.
    /// After Manafont we get a full AF extension, so check that path too.
    /// </summary>
    private bool ShouldDespair => InAstralFire
        && CurrentMp > 0
        && CurrentMp < 1600
        && DespairPvE.EnoughLevel;

    /// <summary>
    /// Whether MP is fully restored in Umbral Ice (9600+ for transition to fire).
    /// </summary>
    private bool MpFullForFire => CurrentMp >= 9600;

    /// <summary>
    /// Whether we are out of MP in AF and need to transition to UI or use Manafont.
    /// </summary>
    private bool NeedIceTransition => InAstralFire && CurrentMp == 0 && ManafontPvE.Cooldown.IsCoolingDown;

    /// <summary>
    /// Whether we should use Manafont (out of MP in AF and Manafont available).
    /// </summary>
    private bool ShouldManafont => InAstralFire && CurrentMp == 0 && !ManafontPvE.Cooldown.IsCoolingDown;

    /// <summary>
    /// Whether Thunderhead buff is about to expire (use thunder soon).
    /// Thunderhead lasts 30s, but we want to avoid overcapping.
    /// </summary>
    private bool ThunderheadExpiring => HasThunder && StatusHelper.PlayerWillStatusEnd(5f, true, StatusID.Thunderhead);

    /// <summary>
    /// Whether target's Thunder DoT needs refreshing (less than 3s remaining).
    /// Checks all Thunder status IDs across level sync.
    /// </summary>
    private bool TargetNeedsThunder
    {
        get
        {
            if (!HasThunder) return false;
            if (HostileTarget == null) return true;

            return HostileTarget.WillStatusEndGCD(1, 0, true,
                StatusID.Thunder, StatusID.ThunderIi, StatusID.ThunderIii,
                StatusID.ThunderIv, StatusID.HighThunder, StatusID.HighThunder_3872);
        }
    }

    #endregion

    #region UpdateInfo

    protected override void UpdateInfo()
    {
        DataCenter.RotationActionAheadOverride = ActionAheadOverride > 0f ? ActionAheadOverride : null;
        base.UpdateInfo();
    }

    #endregion

    #region Status Display

    public override void DisplayRotationStatus()
    {
        ImGui.TextColored(new System.Numerics.Vector4(1f, 0.5f, 0f, 1f), "=== SezuraiBLM Debug ===");

        ImGui.Text($"CanBurst: {CanBurst}");
        ImGui.Text($"InBurstWindow (LeyLines): {InBurstWindow}");

        ImGui.Separator();
        ImGui.TextColored(new System.Numerics.Vector4(1f, 0.3f, 0.3f, 1f), "-- Phase State --");
        ImGui.Text($"InAstralFire: {InAstralFire}  Stacks: {AstralFireStacks}");
        ImGui.Text($"InUmbralIce: {InUmbralIce}  Stacks: {UmbralIceStacks}");
        ImGui.Text($"AstralSoulStacks: {AstralSoulStacks}");
        ImGui.Text($"UmbralHearts: {UmbralHearts}");
        ImGui.Text($"IsParadoxActive: {IsParadoxActive}");
        ImGui.Text($"IsEnochianActive: {IsEnochianActive}");
        ImGui.Text($"CurrentMp: {CurrentMp}");

        ImGui.Separator();
        ImGui.TextColored(new System.Numerics.Vector4(0.3f, 0.7f, 1f, 1f), "-- Resources --");
        ImGui.Text($"PolyglotStacks: {PolyglotStacks}");
        ImGui.Text($"IsPolyglotStacksMaxed: {IsPolyglotStacksMaxed}");
        ImGui.Text($"HasFire (Firestarter): {HasFire}");
        ImGui.Text($"HasThunder (Thunderhead): {HasThunder}");
        ImGui.Text($"HasLeyLines: {HasLeyLines}");
        ImGui.Text($"NextGCDisInstant: {NextGCDisInstant}");
        ImGui.Text($"CanMakeInstant: {CanMakeInstant}");
        ImGui.Text($"ThisManyInstantCasts: {ThisManyInstantCasts}");

        ImGui.Separator();
        ImGui.TextColored(new System.Numerics.Vector4(0.5f, 1f, 0.5f, 1f), "-- Decision Helpers --");
        ImGui.Text($"CanCastFireIV: {CanCastFireIV}");
        ImGui.Text($"ShouldDespair: {ShouldDespair}");
        ImGui.Text($"ShouldManafont: {ShouldManafont}");
        ImGui.Text($"NeedIceTransition: {NeedIceTransition}");
        ImGui.Text($"MpFullForFire: {MpFullForFire}");
        ImGui.Text($"TargetNeedsThunder: {TargetNeedsThunder}");
        ImGui.Text($"--- BMR Timeline ---");
        ImGui.Text($"Active: {BmrActive}{(BmrActive ? $" ({DataCenter.BmrActiveModuleName})" : "")}");
        if (BmrActive)
        {
            ImGui.Text($"Raidwide In: {(BmrRaidwideIn < 9999f ? $"{BmrRaidwideIn:F1}s" : "None")}");
            ImGui.Text($"Knockback In: {(BmrKnockbackIn < 9999f ? $"{BmrKnockbackIn:F1}s" : "None")}");
            ImGui.Text($"Downtime In: {(BmrDowntimeIn < 9999f ? $"{BmrDowntimeIn:F1}s" : "None")}");
        }
    }

    #endregion

    #region Countdown & Opener
    // === BLM OPENER (7.4 Balance — "Standard" opener) ===
    // Pre-pull: Pot(-2s) → Fire III precast(-3.5s)
    // GCD1: Fire III (enters AF3) → Triplecast (weave)
    // GCD2-5: Fire IV x4 (instant via Triplecast + Swiftcast)
    // → Ley Lines (weave between F4s)
    // GCD6: Fire IV → GCD7: Despair (all remaining MP) → GCD8: Flare Star (6 Astral Soul)
    // → Manafont (weave) → Triplecast (weave)
    // GCD9-11: Fire IV x3 (instant) → GCD12: Despair → GCD13: Flare Star
    // → Transpose into UI → Umbral Soul → UI spells
    //
    // === BURST ALIGNMENT ===
    // BLM has no traditional even/odd — largely self-sufficient DPS
    // Ley Lines (120s): align with party buffs when possible for haste value
    // Triplecast (60s, 2 charges): use for instant Fire IVs + mobility
    // Maximize Fire IV count per AF phase, always end AF with Despair → Flare Star
    //
    // === FILLER / SUSTAIN ===
    // AF phase: Fire III → Fire IV spam → Despair → Flare Star → repeat
    // UI phase: Transpose/Blizzard III → Blizzard IV (restore Umbral Hearts) → Thunder → back to AF
    // Umbral Soul: free UI maintenance during downtime
    // Thunder III/IV: refresh during UI phase, don't clip in AF
    // Xenoglossy (Polyglot): instant GCD for movement or weaving, don't overcap at 2 stacks
    // Ley Lines: plant during AF phases for maximum Fire IV value, use Between the Lines to return

    protected override IAction? CountDownAction(float remainTime)
    {
        // Pre-pull pot at ~2s before pull
        if (BurstMed && remainTime <= 2f && remainTime > 1f && UseBurstMedicine(out var potAct))
            return potAct;

        // Pre-pull: Fire III at ~3.5s (cast time) to land as pull happens.
        // This enters Astral Fire immediately on pull.
        if (remainTime < FireIiiPvE.Info.CastTime + CountDownAhead)
        {
            if (FireIiiPvE.CanUse(out IAction act))
                return act;
        }

        return base.CountDownAction(remainTime);
    }

    #endregion

    #region Additional oGCD Logic

    [RotationDesc(ActionID.AetherialManipulationPvE)]
    protected override bool MoveForwardAbility(IAction nextGCD, out IAction? act)
    {
        if (AetherialManipulationPvE.CanUse(out act))
            return true;
        return base.MoveForwardAbility(nextGCD, out act);
    }

    [RotationDesc(ActionID.BetweenTheLinesPvE)]
    protected override bool MoveBackAbility(IAction nextGCD, out IAction? act)
    {
        if (BetweenTheLinesPvE.CanUse(out act))
            return true;
        return base.MoveBackAbility(nextGCD, out act);
    }

    [RotationDesc(ActionID.ManawardPvE)]
    protected override bool DefenseSingleAbility(IAction nextGCD, out IAction? act)
    {
        // BMR-aware: Manaward before raidwide for self-shield
        bool rwSoon = BmrActive && BmrRaidwideIn is > 0 and <= 5f;

        if ((rwSoon || !BmrActive) && ManawardPvE.CanUse(out act))
            return true;

        return base.DefenseSingleAbility(nextGCD, out act);
    }

    [RotationDesc(ActionID.AddlePvE)]
    protected sealed override bool DefenseAreaAbility(IAction nextGCD, out IAction? act)
    {
        // BMR-aware: Addle when raidwide imminent (magic damage reduction on boss)
        bool rwSoon = BmrActive && BmrRaidwideIn is > 0 and <= 5f;

        if ((rwSoon || !BmrActive) && AddlePvE.CanUse(out act))
            return true;

        return base.DefenseAreaAbility(nextGCD, out act);
    }

    [RotationDesc]
    protected sealed override bool AntiKnockbackAbility(IAction nextGCD, out IAction? act)
    {
        if (ArmsLengthPvE.CanUse(out act))
            return true;
        return base.AntiKnockbackAbility(nextGCD, out act);
    }

    #endregion

    #region oGCD Logic

    protected override bool EmergencyAbility(IAction nextGCD, out IAction? act)
    {
        // === MANAFONT: Double AF phase ===
        // When out of MP in Astral Fire and Manafont is available,
        // use it immediately to extend the AF phase with another full set of Fire IVs.
        // Manafont grants: full MP + 3 Umbral Hearts + Thunderhead + Paradox marker.
        if (ShouldManafont && ManafontPvE.CanUse(out act))
            return true;

        // === TRANSPOSE: Emergency ice transition ===
        // If we're out of MP in AF and Manafont is on CD, Transpose to UI.
        // This prevents the AF phase from being wasted with no casts.
        if (NeedIceTransition && TransposePvE.CanUse(out act))
            return true;

        // === TRANSPOSE OPTIMIZATION: Firestarter proc transition ===
        // In UI with Firestarter proc and full MP: Transpose to AF1 then use Fire III proc
        // for free instant Fire III -> AF3. This is a small optimization from The Balance.
        if (UseTransposeOptimization && InUmbralIce && MpFullForFire
            && UmbralIceStacks == MaxSoulCount && UmbralHearts >= 3
            && HasFire && TransposePvE.CanUse(out act))
            return true;

        // === MEDICINE: During Ley Lines burst ===
        if (BurstMed && InBurstWindow && InCombat && UseBurstMedicine(out act))
            return true;

        return base.EmergencyAbility(nextGCD, out act);
    }

    [RotationDesc(ActionID.LeyLinesPvE, ActionID.TriplecastPvE, ActionID.AmplifierPvE)]
    protected override bool AttackAbility(IAction nextGCD, out IAction? act)
    {
        // === LEY LINES: BLM's only personal buff (120s, 15% haste) ===
        // Never let this drift. Use on CD or when Burst is enabled.
        // Balance: "Ley Lines is a 120-second cooldown that should be used as close
        // to on cooldown as possible."
        if (InCombat && HasHostilesInRange)
        {
            bool useLeyLines = LeyLinesOnCooldown || CanBurst;
            if (useLeyLines && LeyLinesPvE.CanUse(out act))
                return true;
        }

        // === RETRACE: Return to Ley Lines ===
        // If we have Ley Lines active but moved away, Retrace teleports us back.
        if (UseRetrace && !IsLastAbility(ActionID.LeyLinesPvE) && RetracePvE.CanUse(out act))
            return true;

        // === AMPLIFIER: Free Polyglot charge ===
        // Use on cooldown as long as we won't overcap Polyglot stacks.
        // Balance: "Use Amplifier on cooldown to avoid losing Polyglot charges."
        if (AmplifierPvE.CanUse(out act))
            return true;

        // === TRIPLECAST: Movement tool + Fire IV spam ===
        // In AF: use Triplecast for instant Fire IVs (movement or just efficiency).
        // Save at least one charge for movement if possible.
        // In UI: use if Paradox is not available and we need an instant for transition.
        if (InAstralFire)
        {
            // Use Triplecast during AF for instant Fire IVs.
            // Prefer to use when we have multiple Fire IVs remaining.
            if (AstralSoulStacks <= 3 && TriplecastPvE.CanUse(out act, gcdCountForAbility: 5))
                return true;
        }

        // In UI without Paradox, use Swiftcast or Triplecast for instant transition.
        if (InUmbralIce && UmbralIceStacks == MaxSoulCount && !IsParadoxActive && !HasFire)
        {
            if (SwiftcastPvE.CanUse(out act))
                return true;
            if (TriplecastPvE.CanUse(out act, usedUp: true))
                return true;
        }

        // === SWIFTCAST: Movement or emergency instant ===
        // If moving and no other instant casts available, use Swiftcast.
        if (IsMoving && InCombat && HasHostilesInRange && !NextGCDisInstant
            && PolyglotStacks == 0 && SwiftcastPvE.CanUse(out act))
            return true;

        // Triplecast while moving as a fallback.
        if (IsMoving && InCombat && HasHostilesInRange && !NextGCDisInstant
            && TriplecastPvE.CanUse(out act, usedUp: true))
            return true;

        return base.AttackAbility(nextGCD, out act);
    }

    #endregion

    #region GCD Logic

    protected override bool GeneralGCD(out IAction? act)
    {
        // ============================================================
        // PRIORITY 0: FLARE STAR (6 Astral Soul stacks)
        // Flare Star is Dawntrail's new payoff spell. Must cast immediately
        // at 6 stacks before any phase transition can consume them.
        // ============================================================
        if (FlareStarPvE.CanUse(out act))
            return true;

        // ============================================================
        // AoE ROTATION (3+ targets)
        // Loop: UI (Freeze/HighBlizzard2 -> Thunder AoE) ->
        //   AF (HighFire2/Flare x2 -> Flare Star) -> repeat
        // ============================================================
        if (AoERotation(out act))
            return true;

        // ============================================================
        // SINGLE TARGET ROTATION
        // Standard loop: UI -> AF (Fire IV x6 -> Despair -> Flare Star)
        // ============================================================

        // --- ASTRAL FIRE PHASE ---
        if (InAstralFire)
        {
            if (AstralFirePhase(out act))
                return true;
        }

        // --- UMBRAL ICE PHASE ---
        if (InUmbralIce)
        {
            if (UmbralIcePhase(out act))
                return true;
        }

        // --- NEUTRAL STATE (no AF or UI active) ---
        // Enter the rotation from neutral: start with Fire III if MP is high,
        // Blizzard III if MP is low.
        if (!InAstralFire && !InUmbralIce)
        {
            if (NeutralStart(out act))
                return true;
        }

        // ============================================================
        // POLYGLOT OVERCAP PREVENTION (any phase)
        // If Polyglot is maxed and Amplifier or Enochian timer would waste a stack,
        // dump a charge immediately.
        // ============================================================
        if (PolyglotDump(out act))
            return true;

        // ============================================================
        // OUT-OF-COMBAT / DOWNTIME MAINTENANCE
        // ============================================================
        if (DowntimeMaintenance(out act))
            return true;

        // Last resort: Scathe for at least some damage while moving.
        if (ScathePvE.CanUse(out act))
            return true;

        return base.GeneralGCD(out act);
    }

    #endregion

    #region Single Target Phases

    /// <summary>
    /// Astral Fire single-target phase.
    /// Standard: Fire IV x3 -> Paradox -> Fire IV x3 -> Flare Star -> Despair
    /// Manafont: extends with Fire IV x3-4 -> Despair -> Flare Star
    /// </summary>
    private bool AstralFirePhase(out IAction? act)
    {
        act = null;

        // 1. PARADOX IN AF: Grants guaranteed Firestarter proc.
        // Use when we have the Paradox marker and enough MP to keep casting Fire IVs after.
        // Best used mid-AF (after 3 Fire IVs) as a weave/timer refresh point.
        // Paradox in AF is instant cast and grants Firestarter.
        if (IsParadoxActive && CurrentMp >= 1600 && AstralSoulStacks <= 3)
        {
            if (ParadoxPvE.CanUse(out act))
                return true;
        }

        // 2. FIRE IV: Main damage spell. Build Astral Soul stacks (1 per cast, need 6).
        // Cast as many as MP allows before finishing with Despair.
        if (CanCastFireIV)
        {
            if (FireIvPvE.CanUse(out act))
                return true;
        }

        // 3. THUNDER: Apply during AF only with Thunderhead proc and target needs refresh.
        // Low priority in AF - only if DoT is about to fall off and we have the proc.
        if (HasThunder && TargetNeedsThunder && CurrentMp >= 1600)
        {
            if (ApplyThunder(out act))
                return true;
        }

        // 4. DESPAIR: AF finisher, uses all remaining MP. Always cast before transitioning.
        // This should be used when MP is too low for Fire IV but still > 0.
        if (ShouldDespair)
        {
            if (DespairPvE.CanUse(out act))
                return true;
        }

        // 5. PARADOX IN AF (low MP): If we still have Paradox marker and can't Fire IV,
        // use it to extend AF and get the Firestarter proc before Despair.
        if (IsParadoxActive && CurrentMp > 0)
        {
            if (ParadoxPvE.CanUse(out act))
                return true;
        }

        // 6. XENOGLOSSY: Instant GCD, use for movement during AF or to dump before overcap.
        // In burst windows, weave Xeno for damage.
        if (IsMoving && !NextGCDisInstant && PolyglotStacks > 0)
        {
            if (XenoglossyPvE.CanUse(out act))
                return true;
        }

        // 7. FIRESTARTER PROC: Free instant Fire III.
        // In AF this just refreshes AF timer. Usually save for UI -> AF transition,
        // but use if moving and no other instants available.
        if (HasFire && IsMoving && !NextGCDisInstant)
        {
            if (FireIiiPvE.CanUse(out act))
                return true;
        }

        // 8. TRANSITION TO ICE: Out of MP and Manafont on CD.
        // Blizzard III is the standard transition spell.
        if (CurrentMp == 0)
        {
            // Flare Star check (already handled at top of GeneralGCD, but safety)
            if (FlareStarPvE.CanUse(out act))
                return true;

            if (BlizzardIiiPvE.CanUse(out act))
                return true;

            // Fallback: Transpose if somehow Blizzard III can't be used
            if (TransposePvE.CanUse(out act))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Umbral Ice single-target phase.
    /// Standard: Blizzard IV (Umbral Hearts) -> Paradox -> Thunder -> Fire III (transition)
    /// With Firestarter: Can Transpose -> Fire III proc for optimized transition.
    /// </summary>
    private bool UmbralIcePhase(out IAction? act)
    {
        act = null;

        // 1. GET TO UI3: If we entered via Transpose or have UI1/UI2, need to reach UI3.
        if (UmbralIceStacks < MaxSoulCount)
        {
            // Blizzard III to reach UI3 if we're at UI1/UI2
            if (BlizzardIiiPvE.CanUse(out act))
                return true;
            if (BlizzardPvE.CanUse(out act))
                return true;
        }

        // 2. BLIZZARD IV: Grants 3 Umbral Hearts (essential for next AF phase MP economy).
        // Must be in UI3. Only needed once per UI phase.
        if (UmbralIceStacks == MaxSoulCount && UmbralHearts < 3
            && !IsLastGCD(ActionID.BlizzardIvPvE, ActionID.FreezePvE))
        {
            if (BlizzardIvPvE.CanUse(out act))
                return true;
        }

        // 3. PARADOX IN UI: Instant cast in Umbral Ice. Use for free damage.
        // Also serves as a weave window for oGCDs.
        if (IsParadoxActive)
        {
            if (ParadoxPvE.CanUse(out act))
                return true;
        }

        // 4. THUNDER: Best applied in UI because it's a "free" GCD while waiting for MP.
        // Thunderhead proc is required to cast. Apply when DoT needs refreshing.
        if (HasThunder && TargetNeedsThunder)
        {
            if (ApplyThunder(out act))
                return true;
        }

        // 5. THUNDERHEAD ABOUT TO EXPIRE: Use it even if DoT has time left.
        if (ThunderheadExpiring)
        {
            if (ApplyThunder(out act))
                return true;
        }

        // 6. POLYGLOT DUMP: Use Xenoglossy in UI if stacks are maxed to prevent overcap.
        // Also good to use during buff windows.
        if (PolyglotDump(out act))
            return true;

        // Spend Xeno in burst windows during UI fill time.
        if (InBurstWindow && PolyglotStacks > 0 && !MpFullForFire)
        {
            if (XenoglossyPvE.CanUse(out act))
                return true;
        }

        // 7. TRANSITION TO FIRE: When MP is full and hearts are stocked.
        if (MpFullForFire && UmbralHearts >= 3)
        {
            // Firestarter proc: use Fire III instant for free AF3 entry.
            if (HasFire)
            {
                if (FireIiiPvE.CanUse(out act))
                    return true;
            }

            // Standard: cast Fire III to enter AF3.
            if (FireIiiPvE.CanUse(out act))
                return true;
        }

        // 8. MP NOT FULL YET: If somehow MP hasn't restored, use filler.
        // Paradox (already checked), Xeno, or just wait (Blizzard IV already done).
        if (!MpFullForFire && UmbralHearts >= 3)
        {
            // Use Xenoglossy as filler if available and not pooling for movement.
            if (PolyglotStacks > (PoolXenoForMovement ? 1 : 0))
            {
                if (XenoglossyPvE.CanUse(out act))
                    return true;
            }

            // Thunder as filler if proc available.
            if (HasThunder)
            {
                if (ApplyThunder(out act))
                    return true;
            }

            // Transpose to AF1 if we have Firestarter for instant Fire III -> AF3.
            // (Already handled in EmergencyAbility for Transpose optimization.)
        }

        // 9. SAFETY: Blizzard to refresh UI if nothing else works.
        if (UmbralHearts < 3)
        {
            if (BlizzardIvPvE.CanUse(out act))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Enter the rotation from neutral state (neither AF nor UI active).
    /// </summary>
    private bool NeutralStart(out IAction? act)
    {
        act = null;

        // High MP: enter AF with Fire III.
        if (CurrentMp >= 7200)
        {
            if (FireIiiPvE.CanUse(out act))
                return true;
            if (FirePvE.CanUse(out act))
                return true;
        }

        // Low MP: enter UI with Blizzard III.
        if (BlizzardIiiPvE.CanUse(out act))
            return true;
        if (BlizzardPvE.CanUse(out act))
            return true;

        return false;
    }

    #endregion

    #region AoE Rotation

    /// <summary>
    /// AoE rotation for 3+ targets.
    /// AF: High Fire II / Flare x2 -> Flare Star
    /// UI: High Blizzard II / Freeze -> Thunder AoE -> Transpose/Fire III
    /// Manafont: extra Flare x2 -> Flare Star
    /// </summary>
    private bool AoERotation(out IAction? act)
    {
        act = null;

        // --- AoE in Astral Fire ---
        if (InAstralFire)
        {
            // High Fire II (upgraded Flare AoE in Dawntrail, 3+ targets)
            if (HighFireIiPvE.CanUse(out act))
                return true;

            // Flare: AoE finisher in AF. Uses all MP. 2+ targets.
            if (FlarePvE.CanUse(out act))
                return true;

            // Fire II for lower levels
            if (FireIiPvE.CanUse(out act))
                return true;
        }

        // --- AoE in Umbral Ice ---
        if (InUmbralIce)
        {
            // High Blizzard II (upgraded Freeze AoE in Dawntrail, 3+ targets)
            if (HighBlizzardIiPvE.CanUse(out act))
                return true;

            // Freeze: AoE in UI, grants Umbral Hearts. 3+ targets.
            if (FreezePvE.CanUse(out act))
                return true;

            // Thunder AoE
            if (HasThunder)
            {
                if (HighThunderIiPvE.CanUse(out act))
                    return true;
                if (ThunderIvPvE.CanUse(out act))
                    return true;
                if (ThunderIiPvE.CanUse(out act))
                    return true;
            }

            // Foul: AoE Polyglot spender, 3+ targets.
            if (FoulPvE.CanUse(out act))
                return true;

            // Blizzard II for lower levels
            if (BlizzardIiPvE.CanUse(out act))
                return true;
        }

        return false;
    }

    #endregion

    #region Thunder Helper

    /// <summary>
    /// Apply the appropriate Thunder spell based on level.
    /// Requires Thunderhead proc (ActionCheck in base class handles this).
    /// Single target: High Thunder > Thunder III > Thunder
    /// </summary>
    private bool ApplyThunder(out IAction? act)
    {
        act = null;

        // Single target thunder (highest level first)
        if (HighThunderPvE.CanUse(out act))
            return true;
        if (ThunderIiiPvE.CanUse(out act))
            return true;
        if (!ThunderIiiPvE.Info.EnoughLevelAndQuest() && ThunderPvE.CanUse(out act))
            return true;

        return false;
    }

    #endregion

    #region Polyglot Management

    /// <summary>
    /// Dump Polyglot stacks when about to overcap.
    /// Overcap occurs when stacks are maxed and either:
    /// - Amplifier is about to come off CD
    /// - Enochian timer is about to grant another stack
    /// Also dump during burst windows for damage.
    /// </summary>
    private bool PolyglotDump(out IAction? act)
    {
        act = null;

        // Overcap prevention: maxed stacks and about to gain another
        if (IsPolyglotStacksMaxed
            && (EnochianEndAfterGCD(2) || AmplifierPvE.Cooldown.WillHaveOneChargeGCD(2)))
        {
            if (FoulPvE.CanUse(out act, skipAoeCheck: !XenoglossyPvE.EnoughLevel))
                return true;
            if (XenoglossyPvE.CanUse(out act))
                return true;
        }

        // Burst dump: use Xeno during Ley Lines for extra damage
        if (InBurstWindow && IsPolyglotStacksMaxed)
        {
            if (XenoglossyPvE.CanUse(out act))
                return true;
        }

        return false;
    }

    #endregion

    #region Downtime Maintenance

    /// <summary>
    /// Out-of-combat or downtime maintenance.
    /// Umbral Soul in UI to maintain Enochian and build hearts.
    /// Transpose from AF to UI if no targets available.
    /// </summary>
    private bool DowntimeMaintenance(out IAction? act)
    {
        act = null;

        if (CombatElapsedLess(6))
            return false;

        // Umbral Soul: free cast in UI, maintains Enochian and grants hearts.
        if (UmbralSoulPvE.CanUse(out act))
            return true;

        // Transpose from AF to UI if no enemies in range (downtime).
        if (InAstralFire && !HasHostilesInRange && TransposePvE.CanUse(out act))
            return true;

        return false;
    }

    #endregion
}
