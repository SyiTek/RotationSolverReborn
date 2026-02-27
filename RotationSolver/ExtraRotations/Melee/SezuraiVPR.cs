namespace RotationSolver.ExtraRotations.Melee;

[Rotation("SezuraiVPR", CombatType.PvE, GameVersion = "7.41", Description = "Balance-aligned VPR with burst timing, 10-second rule, and opener. Start at REAR for opener.")]
[SourceCode(Path = "main/ExtraRotations/Melee/SezuraiVPR.cs")]
[ExtraRotation]
public sealed class SezuraiVPR : ViperRotation
{
    #region Config Options

    [RotationConfig(CombatType.PvE, Name = "Hold one charge of Uncoiled Fury after burst for movement")]
    public bool BurstUncoiledFuryHold { get; set; } = true;

    [RotationConfig(CombatType.PvE, Name = "Use up all charges of Uncoiled Fury if you have used Tincture/Gemdraught (Overrides next option)")]
    public bool MedicineUncoiledFury { get; set; } = true;

    [RotationConfig(CombatType.PvE, Name = "Allow Uncoiled Fury and Writhing Snap to overwrite oGCDs when at range")]
    public bool UFGhosting { get; set; } = true;

    [Range(1, 3, ConfigUnitType.None, 1)]
    [RotationConfig(CombatType.PvE, Name = "How many charges of Uncoiled Fury before using outside burst (3 = hold for burst/movement only)")]
    public int MaxUncoiledStacksUser { get; set; } = 3;

    [Range(1, 30, ConfigUnitType.None, 1)]
    [RotationConfig(CombatType.PvE, Name = "How long on the status time for Swift needs to be to allow reawaken use (setting this too low can lead to dropping buff)")]
    public int SwiftTimer { get; set; } = 10;

    [Range(1, 30, ConfigUnitType.None, 1)]
    [RotationConfig(CombatType.PvE, Name = "How long on the status time for Hunt needs to be to allow reawaken use (setting this too low can lead to dropping buff)")]
    public int HuntersTimer { get; set; } = 10;

    [Range(5, 15, ConfigUnitType.None, 1)]
    [RotationConfig(CombatType.PvE, Name = "Seconds before Serpent's Ire to stop using Vicewinder/Vicepit (10-second rule)")]
    public int PreBurstWindow { get; set; } = 10;

    [RotationConfig(CombatType.PvE, Name = "Enable Balance opener (Swiftscaled first, delay Vicewinder)")]
    public bool UseOpener { get; set; } = true;

    [RotationConfig(CombatType.PvE, Name = "Experimental Pot Usage (used up to 5 seconds before Serpent's Ire comes off cooldown)")]
    public bool BurstMed { get; set; } = false;

    [RotationConfig(CombatType.PvE, Name = "Restrict GCD use if Serpent's Tail, Twinblood, or Twinfang oGCDs can be used")]
    public bool AbilityPrio2 { get; set; } = true;

    #endregion

    #region Burst State

    /// <summary>
    /// Whether the user has burst enabled in the framework.
    /// </summary>
    private bool CanBurst => MergedStatus.HasFlag(AutoStatus.Burst);

    /// <summary>
    /// True when Serpent's Ire has a charge ready OR was just used (within 30s).
    /// This is the broad "burst is available/active" window.
    /// </summary>
    private bool InBurstWindow => SerpentsIrePvE.EnoughLevel
        && (SerpentsIrePvE.Cooldown.HasOneCharge || SerpentsIrePvE.Cooldown.JustUsedAfter(30));

    /// <summary>
    /// True when we are within PreBurstWindow seconds of Serpent's Ire coming off cooldown.
    /// Used for the 10-second rule: stop using Vicewinder/Vicepit to avoid having
    /// an active Dread combo when burst starts.
    /// </summary>
    private bool IsPreBurst => SerpentsIrePvE.EnoughLevel
        && SerpentsIrePvE.Cooldown.IsCoolingDown
        && !SerpentsIrePvE.Cooldown.HasOneCharge
        && SerpentsIrePvE.Cooldown.RecastTimeRemain <= PreBurstWindow;

    /// <summary>
    /// True during the active burst sequence: Ire was just pressed, or we have
    /// ReadyToReawaken / are mid-Reawaken. This is the window where we spend gauge.
    /// </summary>
    private bool InActiveBurst => SerpentsIrePvE.EnoughLevel
        && (SerpentsIrePvE.Cooldown.JustUsedAfter(30) || HasReadyToReawaken || HasReawakenedActive);

    /// <summary>
    /// 10-second rule: block Vicewinder/Vicepit when we're approaching burst
    /// and not already mid-combo.
    /// </summary>
    private bool ShouldBlockVicewinder => SerpentsIrePvE.EnoughLevel
        && IsPreBurst && !InActiveBurst && !DreadActive && !PitActive;

    /// <summary>
    /// True when Ire was used very recently (within ~3s / 1 GCD).
    /// Used to insert one filler GCD between Ire and the first Reawaken
    /// per Balance intermediate guide: "execute one dual wield combo GCD,
    /// then immediately chain two full Reawakens."
    /// </summary>
    private bool IreJustFired => SerpentsIrePvE.EnoughLevel
        && SerpentsIrePvE.Cooldown.IsCoolingDown
        && SerpentsIrePvE.Cooldown.JustUsedAfter(3);

    /// <summary>
    /// Pre-burst coil dump: spend Rattling Coils before Ire fires to avoid
    /// overcapping when Ire grants a new coil. Keep 1 for movement safety.
    /// Balance basic: "spend them before using Serpent's Ire as it will grant another."
    /// </summary>
    private bool ShouldDumpCoilsPreBurst => SerpentsIrePvE.EnoughLevel
        && IsPreBurst && !InActiveBurst
        && RattlingCoilStacks > 1;

    #endregion

    #region Opener State

    private bool OpenerCompleted { get; set; }

    /// <summary>
    /// True during the opener sequence: enabled, not yet completed, in combat, within first 25s.
    /// </summary>
    private bool InOpener => UseOpener && !OpenerCompleted && InCombat && CombatElapsedLess(25);

    /// <summary>
    /// Block Vicewinder during the first 4 GCDs of the opener to get dual wield
    /// combo buffs established before spending charges.
    /// </summary>
    private bool OpenerBlockVicewinder => InOpener && CombatElapsedLessGCD(4);

    /// <summary>
    /// Updates opener completion state. Called each GCD cycle.
    /// Opener is complete once Serpent's Ire goes on cooldown for the first time.
    /// </summary>
    private void UpdateOpenerState()
    {
        if (!InCombat)
        {
            OpenerCompleted = false;
            return;
        }

        if (!OpenerCompleted && SerpentsIrePvE.EnoughLevel
            && SerpentsIrePvE.Cooldown.IsCoolingDown
            && !SerpentsIrePvE.Cooldown.HasOneCharge)
        {
            OpenerCompleted = true;
        }
    }

    #endregion

    #region Status Display

    public override void DisplayRotationStatus()
    {
        ImGui.Text($"No Last Combo Action: {IsNoActionCombo()}");
        ImGui.Text($"InBurstWindow: {InBurstWindow}");
        ImGui.Text($"InActiveBurst: {InActiveBurst}");
        ImGui.Text($"IsPreBurst: {IsPreBurst}");
        ImGui.Text($"ShouldBlockVicewinder: {ShouldBlockVicewinder}");
        ImGui.Text($"InOpener: {InOpener}");
        ImGui.Text($"OpenerCompleted: {OpenerCompleted}");
        ImGui.Text($"SerpentOffering: {SerpentOffering}");
        ImGui.Text($"RattlingCoilStacks: {RattlingCoilStacks}");
    }

    #endregion

    #region Additional oGCD Logic

    [RotationDesc]
    protected override bool EmergencyAbility(IAction nextGCD, out IAction? act)
    {
        // Reawaken Combo oGCDs (Legacies)
        if (HasReawakenedActive)
        {
            if (FourthLegacyPvE.CanUse(out act))
                return true;
            if (ThirdLegacyPvE.CanUse(out act))
                return true;
            if (SecondLegacyPvE.CanUse(out act))
                return true;
            if (FirstLegacyPvE.CanUse(out act))
                return true;
        }

        // Uncoiled Fury Combo oGCDs
        switch ((HasPoisedFang, HasPoisedBlood))
        {
            case (true, _):
                if (UncoiledTwinfangPvE.CanUse(out act))
                    return true;
                break;
            case (_, true):
                if (UncoiledTwinbloodPvE.CanUse(out act))
                    return true;
                break;
            case (false, false):
                if (TimeSinceLastAction.TotalSeconds < 2)
                    break;
                if (UncoiledTwinfangPvE.CanUse(out act))
                    return true;
                if (UncoiledTwinbloodPvE.CanUse(out act))
                    return true;
                break;
        }

        // AOE Dread Combo oGCDs
        switch ((HasFellHuntersVenom, HasFellSkinsVenom))
        {
            case (true, _):
                if (TwinfangThreshPvE.CanUse(out act))
                    return true;
                break;
            case (_, true):
                if (TwinbloodThreshPvE.CanUse(out act))
                    return true;
                break;
            case (false, false):
                if (TimeSinceLastAction.TotalSeconds < 2)
                    break;
                if (TwinfangThreshPvE.CanUse(out act))
                    return true;
                if (TwinbloodThreshPvE.CanUse(out act))
                    return true;
                break;
        }

        // Single Target Dread Combo oGCDs
        switch ((HasHunterVenom, HasSwiftVenom))
        {
            case (true, _):
                if (TwinfangBitePvE.CanUse(out act))
                    return true;
                break;
            case (_, true):
                if (TwinbloodBitePvE.CanUse(out act))
                    return true;
                break;
            case (false, false):
                if (TimeSinceLastAction.TotalSeconds < 2)
                    break;
                if (TwinfangBitePvE.CanUse(out act))
                    return true;
                if (TwinbloodBitePvE.CanUse(out act))
                    return true;
                break;
        }

        // Serpent Combo oGCDs
        if (LastLashPvE.CanUse(out act))
            return true;
        if (DeathRattlePvE.CanUse(out act))
            return true;

        // Burst medicine: use when Serpent's Ire is about to come off cooldown
        if (NoAbilityReady && BurstMed && SerpentsIrePvE.EnoughLevel
            && SerpentsIrePvE.Cooldown.IsCoolingDown
            && SerpentsIrePvE.Cooldown.RecastTimeRemain <= 5
            && UseBurstMedicine(out act))
        {
            return true;
        }

        return base.EmergencyAbility(nextGCD, out act);
    }

    [RotationDesc]
    protected override bool MoveForwardAbility(IAction nextGCD, out IAction? act)
    {
        if (SlitherPvE.CanUse(out act))
            return true;
        return base.MoveForwardAbility(nextGCD, out act);
    }

    [RotationDesc]
    protected override bool HealSingleAbility(IAction nextGCD, out IAction? act)
    {
        if (NoAbilityReady && SecondWindPvE.CanUse(out act))
            return true;
        if (NoAbilityReady && BloodbathPvE.CanUse(out act))
            return true;
        return base.HealSingleAbility(nextGCD, out act);
    }

    [RotationDesc]
    protected sealed override bool DefenseAreaAbility(IAction nextGCD, out IAction? act)
    {
        if (NoAbilityReady && FeintPvE.CanUse(out act))
            return true;
        return base.DefenseAreaAbility(nextGCD, out act);
    }

    [RotationDesc]
    protected sealed override bool AntiKnockbackAbility(IAction nextGCD, out IAction? act)
    {
        if (NoAbilityReady && ArmsLengthPvE.CanUse(out act))
            return true;
        return base.AntiKnockbackAbility(nextGCD, out act);
    }

    [RotationDesc]
    protected sealed override bool InterruptAbility(IAction nextGCD, out IAction? act)
    {
        if (NoAbilityReady && LegSweepPvE.CanUse(out act))
            return true;
        return base.InterruptAbility(nextGCD, out act);
    }

    #endregion

    #region oGCD Logic

    protected override bool AttackAbility(IAction nextGCD, out IAction? act)
    {
        // Serpent's Ire: only use when burst is enabled and both buffs are active
        if (CanBurst && HasHunterAndSwift)
        {
            if (!SerpentsLineageTrait.EnoughLevel)
            {
                if (SerpentsIrePvE.CanUse(out act))
                    return true;
            }

            if (SerpentsLineageTrait.EnoughLevel && ReawakenPvE.IsEnabled)
            {
                if (SerpentsIrePvE.CanUse(out act))
                    return true;
            }
        }

        return base.AttackAbility(nextGCD, out act);
    }

    #endregion

    #region GCD Logic

    protected override bool GeneralGCD(out IAction? act)
    {
        // 1. AbilityPrio2: restrict GCDs if oGCDs are pending
        if (AbilityPrio2 && !NoAbilityReady)
        {
            if ((UFGhosting || NoAbilityReady) && !HasHostilesInRange && UncoiledFuryPvE.CanUse(out act, usedUp: true))
                return true;
            if ((UFGhosting || NoAbilityReady) && !HasHostilesInRange && WrithingSnapPvE.CanUse(out act))
                return true;
            return base.GeneralGCD(out act);
        }

        // 2. Reawaken Combo (always finish if active)
        if (OuroborosPvE.CanUse(out act))
            return true;
        if (FourthGenerationPvE.CanUse(out act))
            return true;
        if (ThirdGenerationPvE.CanUse(out act))
            return true;
        if (SecondGenerationPvE.CanUse(out act))
            return true;
        if (FirstGenerationPvE.CanUse(out act))
            return true;

        // 3. Update opener state
        UpdateOpenerState();

        // 4. Reawaken Entry (burst-aligned)
        // Balance intermediate: "Press Ire, execute one dual wield combo GCD, then chain two Reawakens."
        // We delay Reawaken by one GCD after Ire fires for raid buff alignment (~6.5s application).
        if (LiveComboTime > GCDTime(6) && SwiftTime > SwiftTimer && HuntersTime > HuntersTimer)
        {
            // Overcap protection at 100 gauge -> always use regardless of burst state
            if (SerpentOffering == 100 && ReawakenPvE.CanUse(out act, skipComboCheck: true))
                return true;

            // ReadyToReawaken from Ire: delay one GCD for buff alignment, then use
            if (HasReadyToReawaken && !IreJustFired && ReawakenPvE.CanUse(out act, skipComboCheck: true))
                return true;

            // Double Reawaken: chain second Reawaken immediately after first completes
            if (SerpentOffering >= 50 && InActiveBurst && CanBurst
                && !HasReadyToReawaken && ReawakenPvE.CanUse(out act, skipComboCheck: true))
                return true;

            // Below level for Ire: use Reawaken freely when gauge is sufficient
            if (!SerpentsIrePvE.EnoughLevel && ReawakenPvE.CanUse(out act, skipComboCheck: true))
                return true;
        }

        // 5. Uncoiled Fury (burst-aligned)
        if (LiveComboTime > GCDTime(1) && !WillSwiftEnd && !WillHunterEnd)
        {
            bool isTargetBoss = CurrentTarget?.IsBossFromTTK() ?? false;
            bool isTargetDying = CurrentTarget?.IsDying() ?? false;

            // Overcap protection: at max stacks or target dying
            if ((MaxRattling == RattlingCoilStacks || (isTargetBoss && isTargetDying && RattlingCoilStacks > 0))
                && !HasReadyToReawaken && NoAbilityReady)
            {
                if (UncoiledFuryPvE.CanUse(out act, usedUp: true))
                    return true;
            }

            // Under Medicated: dump all charges
            if (MedicineUncoiledFury && StatusHelper.PlayerHasStatus(true, StatusID.Medicated)
                && !HasReadyToReawaken && NoAbilityReady)
            {
                if (UncoiledFuryPvE.CanUse(out act, usedUp: true))
                    return true;
            }

            // After Reawaken in burst: use UF for max damage in buffs
            if (InActiveBurst && !HasReawakenedActive && !HasReadyToReawaken
                && (RattlingCoilStacks > 1 || !BurstUncoiledFuryHold)
                && NoAbilityReady)
            {
                if (UncoiledFuryPvE.CanUse(out act, usedUp: true))
                    return true;
            }

            // Pre-burst coil dump: spend excess coils before Ire grants another
            // Balance basic: "spend them before using Serpent's Ire as it will grant another"
            if (ShouldDumpCoilsPreBurst && !HasReadyToReawaken && NoAbilityReady)
            {
                if (UncoiledFuryPvE.CanUse(out act, usedUp: true))
                    return true;
            }

            // Outside burst: use at user-configured threshold
            if (!InActiveBurst && RattlingCoilStacks >= MaxUncoiledStacksUser
                && !HasReadyToReawaken && NoAbilityReady)
            {
                if (UncoiledFuryPvE.CanUse(out act, usedUp: true))
                    return true;
            }
        }

        // 6. AOE Dread Combo (Pit active)
        if (PitActive)
        {
            if (HasHunterAndSwift)
            {
                if (WillSwiftEnd)
                {
                    if (SwiftskinsDenPvE.CanUse(out act, skipStatusProvideCheck: true, skipComboCheck: true, skipAoeCheck: true))
                        return true;
                }
                if (WillHunterEnd)
                {
                    if (HuntersDenPvE.CanUse(out act, skipStatusProvideCheck: true, skipComboCheck: true, skipAoeCheck: true))
                        return true;
                }
                switch (HunterOrSwiftEndsFirst)
                {
                    case "Hunter":
                        if (HuntersDenPvE.CanUse(out act, skipStatusProvideCheck: true, skipComboCheck: true, skipAoeCheck: true))
                            return true;
                        break;
                    case "Swift":
                        if (SwiftskinsDenPvE.CanUse(out act, skipStatusProvideCheck: true, skipComboCheck: true, skipAoeCheck: true))
                            return true;
                        break;
                    case "Equal":
                    case null:
                    default:
                        if (HuntersDenPvE.CanUse(out act, skipStatusProvideCheck: true, skipComboCheck: true, skipAoeCheck: true))
                            return true;
                        if (SwiftskinsDenPvE.CanUse(out act, skipStatusProvideCheck: true, skipComboCheck: true, skipAoeCheck: true))
                            return true;
                        break;
                }
            }
            if (!HasHunterAndSwift)
            {
                if (!IsSwift)
                {
                    if (SwiftskinsDenPvE.CanUse(out act, skipStatusProvideCheck: true, skipComboCheck: true, skipAoeCheck: true))
                        return true;
                }
                if (!IsHunter)
                {
                    if (HuntersDenPvE.CanUse(out act, skipStatusProvideCheck: true, skipComboCheck: true, skipAoeCheck: true))
                        return true;
                }
            }
        }

        if (!PitActive)
        {
            if (SwiftskinsDenPvE.CanUse(out act, skipStatusProvideCheck: true, skipComboCheck: true, skipAoeCheck: true))
                return true;
            if (HuntersDenPvE.CanUse(out act, skipStatusProvideCheck: true, skipComboCheck: true, skipAoeCheck: true))
                return true;
        }

        // 7. AOE Vicepit (with 10-second rule block)
        if (!ShouldBlockVicewinder && LiveComboTime > GCDTime(3) && IsSwift && VicepitPvE.Cooldown.CurrentCharges > 0)
        {
            if (VicepitPvE.Cooldown.CurrentCharges == 1 && VicepitPvE.Cooldown.RecastTimeRemainOneCharge < 10)
            {
                if (VicepitPvE.CanUse(out act, usedUp: true))
                    return true;
            }
            if (VicepitPvE.CanUse(out act, usedUp: true))
                return true;
        }

        // 8. AOE Serpent Combo
        // aoe 3
        switch ((HasGrimHunter, HasGrimSkin))
        {
            case (true, _):
                if (JaggedMawPvE.CanUse(out act, skipAoeCheck: true, skipStatusProvideCheck: true, skipComboCheck: true))
                    return true;
                break;
            case (_, true):
                if (BloodiedMawPvE.CanUse(out act, skipAoeCheck: true, skipStatusProvideCheck: true, skipComboCheck: true))
                    return true;
                break;
            case (false, false):
                if (JaggedMawPvE.CanUse(out act, skipAoeCheck: true, skipStatusProvideCheck: true, skipComboCheck: true))
                    return true;
                if (BloodiedMawPvE.CanUse(out act, skipAoeCheck: true, skipStatusProvideCheck: true, skipComboCheck: true))
                    return true;
                break;
        }

        // aoe 2
        if (SwiftskinsBitePvE.EnoughLevel)
        {
            if (HasHunterAndSwift)
            {
                switch (HunterOrSwiftEndsFirst)
                {
                    case "Hunter":
                        if (HuntersBitePvE.CanUse(out act, skipAoeCheck: true, skipStatusProvideCheck: true, skipComboCheck: true))
                            return true;
                        break;
                    case "Swift":
                        if (SwiftskinsBitePvE.CanUse(out act, skipAoeCheck: true, skipStatusProvideCheck: true, skipComboCheck: true))
                            return true;
                        break;
                    case "Equal":
                    case null:
                    default:
                        if (SwiftskinsBitePvE.CanUse(out act, skipAoeCheck: true, skipStatusProvideCheck: true, skipComboCheck: true))
                            return true;
                        break;
                }
            }
            if (!HasHunterAndSwift)
            {
                if (!IsHunter && !IsSwift)
                {
                    if (SwiftskinsBitePvE.CanUse(out act, skipAoeCheck: true, skipStatusProvideCheck: true, skipComboCheck: true))
                        return true;
                }
                if (!IsSwift)
                {
                    if (SwiftskinsBitePvE.CanUse(out act, skipAoeCheck: true, skipStatusProvideCheck: true, skipComboCheck: true))
                        return true;
                }
                if (!IsHunter)
                {
                    if (HuntersBitePvE.CanUse(out act, skipAoeCheck: true, skipStatusProvideCheck: true, skipComboCheck: true))
                        return true;
                }
            }
        }
        if (!SwiftskinsBitePvE.EnoughLevel)
        {
            if (HuntersBitePvE.CanUse(out act, skipAoeCheck: true, skipStatusProvideCheck: true, skipComboCheck: true))
                return true;
        }

        // aoe 1
        if (!FlankstingStrikePvE.EnoughLevel || IsNoActionCombo() || IsLastComboAction(ActionID.FlankstingStrikePvE, ActionID.FlanksbaneFangPvE, ActionID.HindstingStrikePvE, ActionID.HindsbaneFangPvE, ActionID.JaggedMawPvE, ActionID.BloodiedMawPvE))
        {
            switch ((HasSteel, HasReavers))
            {
                case (true, _):
                    if (SteelMawPvE.CanUse(out act))
                        return true;
                    break;
                case (_, true):
                    if (ReavingMawPvE.CanUse(out act))
                        return true;
                    break;
                case (false, false):
                    if (ReavingMawPvE.CanUse(out act))
                        return true;
                    if (SteelMawPvE.CanUse(out act))
                        return true;
                    break;
            }
        }

        // 9. Single Target Dread Combo
        if (DreadActive)
        {
            if (HasHunterAndSwift)
            {
                if (WillSwiftEnd)
                {
                    if (SwiftskinsCoilPvE.CanUse(out act, skipStatusProvideCheck: true, skipComboCheck: true))
                        return true;
                }
                if (WillHunterEnd)
                {
                    if (HuntersCoilPvE.CanUse(out act, skipStatusProvideCheck: true, skipComboCheck: true))
                        return true;
                }
                if (HuntersCoilPvE.CanUse(out act, skipStatusProvideCheck: true, skipComboCheck: true) && HuntersCoilPvE.Target.Target != null && CanHitPositional(EnemyPositional.Flank, HuntersCoilPvE.Target.Target))
                    return true;
                if (SwiftskinsCoilPvE.CanUse(out act, skipStatusProvideCheck: true, skipComboCheck: true) && SwiftskinsCoilPvE.Target.Target != null && CanHitPositional(EnemyPositional.Rear, SwiftskinsCoilPvE.Target.Target))
                    return true;
                switch (HunterOrSwiftEndsFirst)
                {
                    case "Hunter":
                        if (HuntersCoilPvE.CanUse(out act, skipStatusProvideCheck: true, skipComboCheck: true))
                            return true;
                        break;
                    case "Swift":
                        if (SwiftskinsCoilPvE.CanUse(out act, skipStatusProvideCheck: true, skipComboCheck: true))
                            return true;
                        break;
                    case "Equal":
                    case null:
                    default:
                        if (HuntersCoilPvE.CanUse(out act, skipStatusProvideCheck: true, skipComboCheck: true))
                            return true;
                        if (SwiftskinsCoilPvE.CanUse(out act, skipStatusProvideCheck: true, skipComboCheck: true))
                            return true;
                        break;
                }
            }
            if (!HasHunterAndSwift)
            {
                if (!IsHunter && !IsSwift)
                {
                    if (HuntersCoilPvE.CanUse(out act, skipStatusProvideCheck: true, skipComboCheck: true) && HuntersCoilPvE.Target.Target != null && CanHitPositional(EnemyPositional.Flank, HuntersCoilPvE.Target.Target))
                        return true;
                    if (SwiftskinsCoilPvE.CanUse(out act, skipStatusProvideCheck: true, skipComboCheck: true) && SwiftskinsCoilPvE.Target.Target != null && CanHitPositional(EnemyPositional.Rear, SwiftskinsCoilPvE.Target.Target))
                        return true;
                    if (HuntersCoilPvE.CanUse(out act, skipStatusProvideCheck: true, skipComboCheck: true))
                        return true;
                    if (SwiftskinsCoilPvE.CanUse(out act, skipStatusProvideCheck: true, skipComboCheck: true))
                        return true;
                }
                if (!IsSwift)
                {
                    if (SwiftskinsCoilPvE.CanUse(out act, skipStatusProvideCheck: true, skipComboCheck: true))
                        return true;
                }
                if (!IsHunter)
                {
                    if (HuntersCoilPvE.CanUse(out act, skipStatusProvideCheck: true, skipComboCheck: true))
                        return true;
                }
            }
        }

        if (!DreadActive)
        {
            if (HuntersCoilPvE.CanUse(out act, skipStatusProvideCheck: true, skipComboCheck: true))
                return true;
            if (SwiftskinsCoilPvE.CanUse(out act, skipStatusProvideCheck: true, skipComboCheck: true))
                return true;
        }

        // 10. ST Vicewinder (with 10-second rule + opener block)
        if (!ShouldBlockVicewinder && !OpenerBlockVicewinder && LiveComboTime > GCDTime(3) && IsSwift)
        {
            if (VicewinderPvE.Cooldown.CurrentCharges == 1 && VicewinderPvE.Cooldown.RecastTimeRemainOneCharge < 10)
            {
                if (VicewinderPvE.CanUse(out act, usedUp: true))
                    return true;
            }
            if (VicewinderPvE.CanUse(out act, usedUp: true))
                return true;
        }

        // 11. Single Target Serpent Combo (opener: prioritize Swiftskins path first for Swiftscaled ASAP)
        // st 3
        switch ((HasHindstung, HasHindsbane, HasFlankstung, HasFlanksbane))
        {
            case (true, _, _, _):
                if (HindstingStrikePvE.CanUse(out act, skipStatusProvideCheck: true))
                    return true;
                break;
            case (_, true, _, _):
                if (HindsbaneFangPvE.CanUse(out act, skipStatusProvideCheck: true))
                    return true;
                break;
            case (_, _, true, _):
                if (FlankstingStrikePvE.CanUse(out act, skipStatusProvideCheck: true))
                    return true;
                break;
            case (_, _, _, true):
                if (FlanksbaneFangPvE.CanUse(out act, skipStatusProvideCheck: true))
                    return true;
                break;
            case (false, false, false, false):
                if (HindstingStrikePvE.CanUse(out act, skipStatusProvideCheck: true))
                    return true;
                if (HindsbaneFangPvE.CanUse(out act, skipStatusProvideCheck: true))
                    return true;
                if (FlankstingStrikePvE.CanUse(out act, skipStatusProvideCheck: true))
                    return true;
                if (FlanksbaneFangPvE.CanUse(out act, skipStatusProvideCheck: true))
                    return true;
                break;
        }

        // st 2
        if (SwiftskinsStingPvE.EnoughLevel)
        {
            // Opener: prioritize Swiftskins path to get Swiftscaled ASAP
            if (InOpener && CombatElapsedLessGCD(2) && !IsSwift)
            {
                if (SwiftskinsStingPvE.CanUse(out act, skipStatusProvideCheck: true, skipComboCheck: true))
                    return true;
            }

            // Emergency buff refresh: if a buff is about to drop, prioritize that path
            if (WillHunterEnd && !WillSwiftEnd)
            {
                if (HuntersStingPvE.CanUse(out act, skipStatusProvideCheck: true, skipComboCheck: true))
                    return true;
            }
            if (WillSwiftEnd && !WillHunterEnd)
            {
                if (SwiftskinsStingPvE.CanUse(out act, skipStatusProvideCheck: true, skipComboCheck: true))
                    return true;
            }

            if (HasHunterAndSwift)
            {
                if (HasHind || HasFlank)
                {
                    if (HasHind)
                    {
                        if (SwiftskinsStingPvE.CanUse(out act, skipStatusProvideCheck: true, skipComboCheck: true))
                            return true;
                    }
                    if (HasFlank)
                    {
                        if (HuntersStingPvE.CanUse(out act, skipStatusProvideCheck: true, skipComboCheck: true))
                            return true;
                    }
                }
                if (!HasHind && !HasFlank)
                {
                    switch (HunterOrSwiftEndsFirst)
                    {
                        case "Hunter":
                            if (HuntersStingPvE.CanUse(out act, skipStatusProvideCheck: true, skipComboCheck: true))
                                return true;
                            break;
                        case "Swift":
                            if (SwiftskinsStingPvE.CanUse(out act, skipStatusProvideCheck: true, skipComboCheck: true))
                                return true;
                            break;
                        case "Equal":
                        case null:
                        default:
                            if (SwiftskinsStingPvE.CanUse(out act, skipStatusProvideCheck: true, skipComboCheck: true))
                                return true;
                            break;
                    }
                }
            }
            if (!HasHunterAndSwift)
            {
                if (HasHind || HasFlank)
                {
                    if (HasHind)
                    {
                        if (SwiftskinsStingPvE.CanUse(out act, skipStatusProvideCheck: true, skipComboCheck: true))
                            return true;
                    }
                    if (HasFlank)
                    {
                        if (HuntersStingPvE.CanUse(out act, skipStatusProvideCheck: true, skipComboCheck: true))
                            return true;
                    }
                }
                if (!HasHind && !HasFlank)
                {
                    if (!IsHunter && !IsSwift)
                    {
                        // Opener preference: Swiftskins first
                        if (InOpener && !IsSwift)
                        {
                            if (SwiftskinsStingPvE.CanUse(out act, skipStatusProvideCheck: true, skipComboCheck: true))
                                return true;
                        }
                        if (SwiftskinsStingPvE.CanUse(out act, skipStatusProvideCheck: true, skipComboCheck: true))
                            return true;
                    }
                    if (!IsSwift)
                    {
                        if (SwiftskinsStingPvE.CanUse(out act, skipStatusProvideCheck: true, skipComboCheck: true))
                            return true;
                    }
                    if (!IsHunter)
                    {
                        if (HuntersStingPvE.CanUse(out act, skipStatusProvideCheck: true, skipComboCheck: true))
                            return true;
                    }
                }
            }
        }
        if (!SwiftskinsStingPvE.EnoughLevel)
        {
            if (HuntersStingPvE.CanUse(out act, skipStatusProvideCheck: true, skipComboCheck: true))
                return true;
        }

        // 12. ST Base Combo
        if (!JaggedMawPvE.EnoughLevel || IsNoActionCombo() || IsLastComboAction(ActionID.FlankstingStrikePvE, ActionID.FlanksbaneFangPvE, ActionID.HindstingStrikePvE, ActionID.HindsbaneFangPvE, ActionID.JaggedMawPvE, ActionID.BloodiedMawPvE))
        {
            // Opener: start with Swiftskins path (ReavingFangs -> SwiftskinsSting)
            if (InOpener && CombatElapsedLessGCD(1) && !IsSwift)
            {
                switch ((HasSteel, HasReavers))
                {
                    case (_, true):
                        if (ReavingFangsPvE.CanUse(out act))
                            return true;
                        break;
                    default:
                        if (ReavingFangsPvE.CanUse(out act))
                            return true;
                        if (SteelFangsPvE.CanUse(out act))
                            return true;
                        break;
                }
            }
            else
            {
                switch ((HasSteel, HasReavers))
                {
                    case (true, _):
                        if (SteelFangsPvE.CanUse(out act))
                            return true;
                        break;
                    case (_, true):
                        if (ReavingFangsPvE.CanUse(out act))
                            return true;
                        break;
                    case (false, false):
                        if (ReavingFangsPvE.CanUse(out act))
                            return true;
                        if (SteelFangsPvE.CanUse(out act))
                            return true;
                        break;
                }
            }
        }

        // 13. Ranged fallback
        if ((UFGhosting || NoAbilityReady) && UncoiledFuryPvE.CanUse(out act, usedUp: true))
            return true;
        if ((UFGhosting || NoAbilityReady) && WrithingSnapPvE.CanUse(out act))
            return true;

        return base.GeneralGCD(out act);
    }

    #endregion
}
