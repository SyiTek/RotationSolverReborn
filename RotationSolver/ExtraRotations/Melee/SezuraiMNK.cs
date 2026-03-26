using System.ComponentModel;
using RotationSolver.Updaters;

namespace RotationSolver.ExtraRotations.Melee;

[Rotation("SezuraiMNK", CombatType.PvE, GameVersion = "7.4", Description = "The one who holds all weight.")]
[SourceCode(Path = "main/ExtraRotations/Melee/SezuraiMNK.cs")]
public sealed class SezuraiMNK : MonkRotation
{
    #region Properties

    #region Enums
    private enum OpenerType : byte
    {
        [Description("Double Lunar (Recommended)")] DoubleLunar,
        [Description("Solar Lunar (Safe)")] SolarLunar,
        [Description("Triple Lunar (Advanced)")] TripleLunar
    }

    private enum OpenerVariation : byte
    {
        [Description("Dragon Kick - 5s Buffs (Recommended)")] DragonKick5,
        [Description("Dragon Kick - 7s Buffs")] DragonKick7,
        [Description("Demolish - 7s Buffs")] Demolish7
    }

    private enum Nadi : byte
{
    [Description("None")] None,
    [Description("Lunar")] Lunar,
    [Description("Solar")] Solar,
}

    private enum Blitz : byte
    {
        [Description("None")] None,
        [Description("Elixir Burst")] ElixirBurst,         // Grants Lunar
        [Description("Rising Phoenix")] RisingPhoenix,     // Grants Solar
        [Description("Phantom Rush")] PhantomRush,         // Consumes Both
    }

    #endregion

    #region States

    private Nadi NextNadiGoal { get; set; } = Nadi.Lunar;
    private Blitz NextBlitz { get; set; }
    private bool PhantomRushed { get; set; }
    private bool LunarOddWindow => NextNadiGoal == Nadi.Lunar
                                   && BrotherhoodPvE.Cooldown.RecastTimeElapsedRaw is >30f and < 90f;
    private bool SolarOddWindow => NextNadiGoal == Nadi.Solar
                                   && BrotherhoodPvE.Cooldown.RecastTimeElapsedRaw is >30f and < 90f;
    private static bool HasBlitzReady => !BeastChakrasContains(BeastChakra.None);
    private static bool IsReadySoon(IBaseAction action, int maxGCD)
    {
        var gcdTotal = WeaponTotal;
        const float Buffer = 0.6f;

        for (var i = 0; i <= maxGCD; i++)
        {
            var deadLine = gcdTotal * i + (gcdTotal - Math.Abs(WeaponRemain - Buffer));
            if (action.Cooldown.WillHaveOneCharge(deadLine)) return true;
        }
        return false;
    }
    private static int IsReadyIndex(IBaseAction action, int maxGCDs)
    {
        var gcdTotal = WeaponTotal;
        const float Buffer = 0.6f;

        for (var i = 0; i <= maxGCDs; i++)
        {
            var deadline = gcdTotal * i + (gcdTotal - Buffer + WeaponRemain);
            if (action.Cooldown.RecastTimeRemain <= deadline) return i;
        }
        return -1;
    }
    private bool IsCooldownAligned(int range)
    {
        var bhIndex = IsReadyIndex(BrotherhoodPvE, range);
        var rofIndex = IsReadyIndex(RiddleOfFirePvE, range);

        if (bhIndex == -1 || rofIndex == -1) return false;

        var difference = Math.Abs(rofIndex - bhIndex);
        return difference <= 0.6f;
    }
    private bool CanBurst => MergedStatus.HasFlag(AutoStatus.Burst) && BrotherhoodPvE.IsEnabled;
    private static bool InBurst => HasBrotherhood && HasRiddleOfFire;
    private bool MustUseOpo => HasFormlessFist || IsLastGCD(true, FiresReplyPvE, MasterfulBlitzPvE, ElixirBurstPvE,
        RisingPhoenixPvE, PhantomRushPvE);
    private static bool IsNextGCDOpo => ActionUpdater.NextGCDAction != null &&
                                        ActionUpdater.NextGCDAction.IsTheSameTo(true, ActionID.DragonKickPvE,
                                            ActionID.LeapingOpoPvE, ActionID.BootshinePvE,
                                            ActionID.ShadowOfTheDestroyerPvE, ActionID.ArmOfTheDestroyerPvE);
    private bool IsLastGCDMasterfulBlitz => IsLastGCD(true, ElixirBurstPvE, PhantomRushPvE, RisingPhoenixPvE);
    private bool IsLastGCDOpo => IsLastGCD( true, DragonKickPvE, LeapingOpoPvE, BootshinePvE, ShadowOfTheDestroyerPvE, ArmOfTheDestroyerPvE);
    private static bool PerfectBalanceStacks(int stacks) => StatusHelper.PlayerStatusStack(true, StatusID.PerfectBalance) == stacks;
    private static bool CanLateWeave => WeaponRemain <= LateWeaveWindow;
    private static bool CanEarlyWeave => WeaponRemain >= LateWeaveWindow;
    private const float LateWeaveWindow = 1.15f;
    private static bool EnoughWeaveTime => WeaponRemain > 0.75f;
    private static bool IsOpenerStart => InCombat && CombatTime < 5.0f;
    private static bool HasBothNadi => HasLunar && HasSolar;
    private static bool HasNoNadi => !HasLunar && !HasSolar;
    private int BlitzCount { get; set; }
    private bool _canIncrement;
    private const float BossHealthThreshold = 0.1f;

    #endregion

    #region Updaters

    private void RotationUpdater()
    {
        if (!InCombat && HasNoNadi && OpoOpoFury == 0 && RaptorFury == 0 && CoeurlFury == 0)
        {
            ResetState();
            return;
        }

        if (IsLastGCD(true, PhantomRushPvE))
        {
            BlitzCount = 0; // Reset loop
            PhantomRushed = true;
        }

        var incrementTrigger = IsLastGCD(true, ElixirBurstPvE, RisingPhoenixPvE);

        if (!_canIncrement && incrementTrigger)
        {
                        BlitzCount++;
        }

        _canIncrement = incrementTrigger;


        // Determine the next Blitz based on the Opener or Standard Rotation
        NextBlitz = GetNextBlitz();

        // Determine the Nadi Goal based on the Blitz we want to execute
        NextNadiGoal = NextBlitz switch
        {
            Blitz.ElixirBurst => Nadi.Lunar,
            Blitz.RisingPhoenix => Nadi.Solar,
            Blitz.PhantomRush => Nadi.Lunar,
            _ => Nadi.None
        };
    }
    private Blitz GetNextBlitz()
    {
    // 1. Handle Openers (Fixed Sequences)
        if (!PhantomRushed)
        {
            return (ChosenOpener, BlitzCount) switch
            {
                // Solar Lunar: Solar -> Lunar -> Phantom Rush
                (OpenerType.SolarLunar, 0) => Blitz.RisingPhoenix,
                (OpenerType.SolarLunar, 1) => Blitz.ElixirBurst,
                (OpenerType.SolarLunar, 2) => Blitz.PhantomRush,

                // Double Lunar: Lunar -> Lunar -> Solar -> Phantom Rush
                (OpenerType.DoubleLunar, 0) => Blitz.ElixirBurst,
                (OpenerType.DoubleLunar, 1) => Blitz.ElixirBurst,
                (OpenerType.DoubleLunar, 2) => Blitz.RisingPhoenix,
                (OpenerType.DoubleLunar, 3) => Blitz.PhantomRush,

                // Triple Lunar (Theoretical/Niche): Lunar x3 -> Solar -> PR
                (OpenerType.TripleLunar, 0) => Blitz.ElixirBurst,
                (OpenerType.TripleLunar, 1) => Blitz.ElixirBurst,
                (OpenerType.TripleLunar, 2) => Blitz.ElixirBurst,
                (OpenerType.TripleLunar, 3) => Blitz.RisingPhoenix,
                (OpenerType.TripleLunar, 4) => Blitz.PhantomRush,

                // Default fallback if counts go out of bounds before PR
                _ => Blitz.None
        };
    }

        // 2. Handle Standard Loop (Post-Opener)
        // Logic: Fill missing Nadi -> Phantom Rush -> Repeat
        return (HasLunar, HasSolar) switch
        {
            (true, true) => Blitz.PhantomRush,      // Have both? Rush.
            (false, false) => Blitz.ElixirBurst,    // Have neither? Default to Lunar (Standard Loop start).
            (true, false) => Blitz.RisingPhoenix,   // Have Lunar? Get Solar.
            (false, true) => Blitz.ElixirBurst,     // Have Solar? Get Lunar.
        };
    }
    private void ResetState()
{
    BlitzCount = 0;
    PhantomRushed = false;
    _canIncrement = false;

    // Pre-combat prep: Set initial Blitz based on opener preference
    NextBlitz = ChosenOpener switch
    {
        OpenerType.SolarLunar => Blitz.RisingPhoenix,
        _ => Blitz.ElixirBurst // Double and Triple Lunar start with Elixir Burst
    };

    NextNadiGoal = NextBlitz == Blitz.RisingPhoenix ? Nadi.Solar : Nadi.Lunar;
}
    private static string CheckFuryGaugeState()
    {

        if (OpoOpoFury > 0)
        {
            return "Coeurl, Raptor, Opo";
        }

        if (RaptorFury > 0 || (OpoOpoFury == 0 && CoeurlFury == 0 && RaptorFury == 0))
        {
            return "Opo, Coeurl, Raptor";
        }

        return CoeurlFury > 0 ? "Opo, Raptor, Coeurl" : "Coeurl, Raptor, Opo";
    }
    #endregion

    #endregion

    #region Tracking Properties
    /// <summary>
    /// Displays the current rotation status in the UI.
    /// </summary>
    // csharp
    public override void DisplayRotationStatus()
    {
        // Colors
        var green = new Vector4(0.22f, 0.85f, 0.32f, 1f);
        var red = new Vector4(0.95f, 0.28f, 0.28f, 1f);
        var yellow = new Vector4(0.98f, 0.78f, 0.18f, 1f);
        var blue = new Vector4(0.33f, 0.66f, 0.95f, 1f);
        var gray = new Vector4(0.75f, 0.75f, 0.75f, 1f);

        if (!ImGui.BeginTable("Rotation Status", 2, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg))
        {
            ImGui.EndTable();
            return;
        }

        ImGui.TableSetupColumn("Property");
        ImGui.TableSetupColumn("Value");
        ImGui.TableHeadersRow();

        // Header / meta
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextUnformatted("Rotation Snapshot");
        ImGui.TableSetColumnIndex(1);
        ImGui.TextDisabled($"Updated: {DateTime.Now:T}");

        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextUnformatted("— Performance");
        ImGui.TableSetColumnIndex(1);
        ImGui.TextUnformatted(string.Empty);

        AddTableRowColored("Weapon Total", $"{WeaponTotal:F2}s", blue);
        AddTableRowColored("Animation Lock", $"{AnimationLock:F2}s", gray);
        AddTableRowColored("Late Weave Window", $"{LateWeaveWindow:F2}s", gray);

        // Weave hints
        AddTableRowColored("Can Early Weave", CanEarlyWeave ? "Yes" : "No", CanEarlyWeave ? green : red);
        AddTableRowColored("Enough Weave Time", EnoughWeaveTime ? "Yes" : "No", EnoughWeaveTime ? green : red);

        // Nadi / Blitz
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextUnformatted("— Nadi & Blitz");
        ImGui.TableSetColumnIndex(1);
        ImGui.TextUnformatted(string.Empty);

        var oddWindow = LunarOddWindow ? "Lunar Odd Window" : SolarOddWindow ? "Solar Odd Window" : "Normal Window";
        var oddWindowColor = LunarOddWindow ? blue : SolarOddWindow ? yellow : red;
        AddTableRowColored("Odd Window", oddWindow, oddWindowColor);

        var nadiLabel = HasNoNadi ? "No Nadi" : HasBothNadi ? "Both" : HasLunar ? "Lunar" : "Solar";
        var nadiColor = HasNoNadi ? gray : HasBothNadi ? yellow : HasLunar ? blue : new Vector4(1f, 0.55f, 0.12f, 1f);
        AddTableRowColored("Nadi State", nadiLabel, nadiColor);

        var blitzReadyLabel = ElixirBurstPvEReady ? "Elixir Burst" : RisingPhoenixPvEReady ? "Rising Phoenix" : PhantomRushPvEReady ? "Phantom Rush" : "None";
        var blitzReadyColor = ElixirBurstPvEReady || RisingPhoenixPvEReady || PhantomRushPvEReady ? green : gray;
        AddTableRowColored("Next Blitz", $"{NextBlitz} (Count: {BlitzCount})", blitzReadyColor);
        AddTableRowColored("Blitz Ready", blitzReadyLabel, blitzReadyColor);

        AddTableRowColored("Next Nadi Goal", $"{NextNadiGoal}", nadiColor);
        AddTableRowColored("Has Used Phantom Rush?", PhantomRushed ? "Yes ✓" : "No ✗", PhantomRushed ? gray : green);

        // Forms & Fury
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextUnformatted("— Forms & Fury");
        ImGui.TableSetColumnIndex(1);
        ImGui.TextUnformatted(string.Empty);

        var formLabel = InOpoopoForm ? "Opo-Opo" : InRaptorForm ? "Raptor" : InCoeurlForm ? "Coeurl" : "None";
        AddTableRowColored("Current Form", formLabel, InOpoopoForm || InRaptorForm || InCoeurlForm ? yellow : gray);

        AddTableRowColored("Beast Chakras", $"{BeastChakras[0]}, {BeastChakras[1]}, {BeastChakras[2]}", gray);
        AddTableRowColored("Fury Gauge State", CheckFuryGaugeState(), gray);

        // GCD / Opo checks
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextUnformatted("— GCD / Opener Hints");
        ImGui.TableSetColumnIndex(1);
        ImGui.TextUnformatted(string.Empty);

        AddTableRowColored("Is Last GCD Opo?", IsLastGCDOpo ? "Yes ✓" : "No ✗", IsLastGCDOpo ? yellow : gray);
        AddTableRowColored("Is Next GCD Opo", IsNextGCDOpo ? "Yes ✓" : "No ✗", IsNextGCDOpo ? yellow : gray);

        // Small helper badge row for important cooldowns
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextUnformatted("Key Cooldowns");
        ImGui.TableSetColumnIndex(1);
        ImGui.BeginGroup();
        AddBadge("Brotherhood", BrotherhoodPvE.IsEnabled && BrotherhoodPvE.Cooldown.HasOneCharge, green, gray);
        ImGui.SameLine();
        AddBadge("Riddle of Fire", RiddleOfFirePvE.IsEnabled && RiddleOfFirePvE.Cooldown.HasOneCharge, green, gray);
        ImGui.SameLine();
        // Ensure we pass both active and inactive colors (fourth param) to match AddBadge signature
        AddBadge("Perfect Balance", PerfectBalancePvE.IsEnabled && PerfectBalancePvE.Cooldown.HasOneCharge, BrotherhoodPvE.IsEnabled ? yellow : gray, gray);
        ImGui.EndGroup();

        ImGui.EndTable();

        // BMR Timeline section (outside table for simplicity)
        ImGui.Text($"--- BMR Timeline ---");
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

    // Helper: colored table row
    private static void AddTableRowColored(string label, string value, Vector4 color)
    {
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextUnformatted(label);
        ImGui.TableSetColumnIndex(1);
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.TextUnformatted(value);
        ImGui.PopStyleColor();
    }

    // Helper: small badge (simple rectangular label)
    private static void AddBadge(string text, bool active, Vector4 activeColor, Vector4 inactiveColor)
    {
        var color = active ? activeColor : inactiveColor;
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.TextUnformatted(active ? $"[{text} ✓]" : $"[{text} ✗]");
        ImGui.PopStyleColor();
    }

    #endregion

    #region Config Options

    [RotationConfig(CombatType.PvE, Name = "Opener Nadi Strategy",
        Tooltip = "Double Lunar (Recommended): Overcaps Lunar Nadi in opener to align Phantom Rush with 2-minute party buff windows (Brotherhood + raid buffs). Best for most savage encounters with known kill times.\n\n" +
                  "Solar Lunar (Safe): Earns Phantom Rush immediately in opener. Maximizes total Phantom Rush uses across any kill time. Best for progression or unknown fight lengths where you might lose a Phantom Rush with Double Lunar.\n\n" +
                  "Triple Lunar (Advanced): Extends Lunar overcapping into odd-minute windows too, putting maximum Opo-opo GCDs under Riddle of Fire. Highest risk of losing a Phantom Rush if kill time doesn't align. Only for optimized kill times.")]
    private OpenerType ChosenOpener { get; set; } = OpenerType.DoubleLunar;

    [RotationConfig(CombatType.PvE, Name = "Opener Buff Timing",
        Tooltip = "Controls when Brotherhood goes out after the pull. This does NOT affect Monk's personal DPS — it only changes when your party receives the Brotherhood buff.\n\n" +
                  "5s Buffs (Recommended): Brotherhood at ~5s into pull (around GCD3). Most party compositions prefer this timing as it aligns with the majority of jobs' burst windows.\n\n" +
                  "7s Buffs (DK): Brotherhood at ~7s into pull (around GCD4). Use when your party's other jobs prefer later buff timing.\n\n" +
                  "7s Buffs (Demolish): Same 7s timing but starts with Demolish instead of Dragon Kick. Slightly stronger opener and 1-minute burst than DK-7s. Preferred 7s option unless specific Fury stack alignment is needed.")]
    private OpenerVariation ChosenVariation { get; set; } = OpenerVariation.DragonKick5;

    [RotationConfig(CombatType.PvE, Name = "Auto Pot Usage (Gemdraught of Strength during Brotherhood)")]
    public bool BurstMed { get; set; } = true;

    [Range(0f, 0.25f, ConfigUnitType.Percent)]
    [RotationConfig(CombatType.PvE, Name = "Action Ahead Override (0 = use global setting)")]
    public float ActionAheadOverride { get; set; } = 0f;

    #endregion

    #region UpdateInfo

    protected override void UpdateInfo()
    {
        DataCenter.RotationActionAheadOverride = ActionAheadOverride > 0f ? ActionAheadOverride : null;
    }

    #endregion

    #region Countdown & Opener
    // === MNK OPENER (7.4 Balance — see config for variant selection) ===
    //
    // --- Double Lunar (Default, recommended for savage) ---
    // Pre-pull: FormShift(-15s) → Meditation(chakra gen) → True North(-2s) → Pot(-2s) → Thunderclap(-0.8s)
    // GCD1: Dragon Kick (Opo) → GCD2: Twin Snakes (Raptor) → GCD3: Demolish (Coeurl)
    // → Riddle of Fire (weave) → Brotherhood (weave at 5s or 7s per config) → Perfect Balance (weave)
    // GCD4-6: Opo x3 (Bootshine/DK — Opo-maxxing for Lunar Nadi) → Elixir Burst
    // → Riddle of Wind (weave) → Perfect Balance (weave)
    // GCD7-9: Opo x3 → second Elixir Burst (overcaps Lunar intentionally)
    // Result: LL pattern → first Phantom Rush aligns with 2-min raid buffs
    //
    // --- Solar Lunar (Safe for progression / unknown kill times) ---
    // Same pre-pull and GCD1-6 as Double Lunar
    // GCD7-9: one of each form (Opo+Raptor+Coeurl) → Rising Phoenix (grants Solar Nadi)
    // → Phantom Rush available immediately (both Nadi filled)
    // Result: SL pattern → max Phantom Rush uses but PR lands in odd windows (outside party buffs)
    //
    // --- Triple Lunar (Advanced optimization) ---
    // Same opener as Double Lunar (LL)
    // Odd windows: Lunar sequence instead of Solar → delays Phantom Rush further
    // Result: max Lunar (Opo-opo) GCDs under RoF, highest risk of lost PR if kill time is wrong
    //
    // --- Variation: 5s vs 7s / DK vs Demo ---
    // 5s: Brotherhood at ~GCD3 (~5s into pull) — most party comps prefer this
    // 7s: Brotherhood at ~GCD4 (~7s into pull) — for parties wanting later buffs
    // Demo-7s: starts Demolish instead of DK, slightly stronger opener/1-min than DK-7s
    //
    // === EVEN BURST (120s) ===
    // Riddle of Fire + Brotherhood + 2x Perfect Balance → 2 Blitz finishers
    // Dump all Forbidden Chakra (5 stacks) under raid buffs
    // Use PB after an Opo GCD for optimal blitz alignment
    // Double/Triple Lunar: Phantom Rush lands HERE (the payoff)
    //
    // === ODD BURST (60s) ===
    // Riddle of Fire only — Brotherhood is 120s
    // 1x Perfect Balance → 1 Blitz, save second PB charge for even window
    // Solar Lunar: Phantom Rush lands here instead (trade-off for more total PR uses)
    //
    // === FILLER / SUSTAIN ===
    // Opo-maxxing: always use Dragon Kick + Bootshine/Leaping Opo for Opo GCDs (highest potency)
    // Form rotation: Opo (DK/Boot) → Raptor (Twin Snakes) → Coeurl (Demolish)
    // Forbidden Chakra: spend at 5 stacks, don't overcap between bursts
    // RoF is 60s (every window), Brotherhood is 120s (even only)
    // Use PB after an Opo GCD for optimal blitz alignment

    protected override IAction? CountDownAction(float remainTime)
    {
        // Pre-pull pot at ~2s before pull
        if (BurstMed && remainTime <= 2f && remainTime > 1f && UseBurstMedicine(out var potAct))
            return potAct;

        if  (remainTime <= 0.8f && ThunderclapPvE.CanUse(out var act)
            || remainTime <= 2 && TrueNorthPvE.CanUse(out act)
            || remainTime <= 5 && Chakra < 5 && TryUseMeditations(out act))
        {
            return act;
        }

        return remainTime < 15 && FormShiftPvE.CanUse(out act) ? act : base.CountDownAction(remainTime);
    }
    #endregion

    #region oGCD Logic
    protected override bool EmergencyAbility(IAction nextGCD, out IAction? act)
    {
        // BMR-aware: If downtime is imminent (< 20s), dump burst CDs NOW so they're on cooldown
        // and come back faster for post-downtime. Brotherhood + RoF dumped before downtime
        // means they'll be closer to ready when the boss returns.
        if (BmrActive && BmrDowntimeIn is > 0 and <= 20f && EnoughWeaveTime)
        {
            // Only dump Brotherhood if it WON'T be back for post-downtime anyway
            // Brotherhood is 120s CD — if downtime is short, it's better to dump it
            if (BmrDowntimeIn is > 5f and <= 20f && BrotherhoodPvE.Cooldown.HasOneCharge
                && CanBurst && BrotherhoodPvE.CanUse(out act))
                return true;

            // Dump RoF before downtime so it starts ticking down during downtime
            if (BmrDowntimeIn is > 5f and <= 15f && RiddleOfFirePvE.Cooldown.HasOneCharge
                && RiddleOfFirePvE.CanUse(out act))
                return true;
        }

        return TryUseBuffs(out act)
               || TryUsePerfectBalance(out act)
               || base.EmergencyAbility(nextGCD, out act);
    }

    [RotationDesc(ActionID.ThunderclapPvE)]
    protected override bool MoveForwardAbility(IAction nextGCD, out IAction? act)
    {
        return ThunderclapPvE.CanUse(out act) || base.MoveForwardAbility(nextGCD, out act);
    }

    [RotationDesc(ActionID.FeintPvE)]
    protected override bool DefenseAreaAbility(IAction nextGCD, out IAction? act)
    {
        act = null;
        if (!EnoughWeaveTime) return false;

        // BMR-aware: Feint proactively when raidwide imminent (10% phys/5% magic mit on boss)
        // Per Balance: spread mits across raidwides, don't dump everything on one hit
        // Feint goes out 1-5s before raidwide so the debuff covers the damage snapshot
        bool rwSoon = BmrActive && BmrRaidwideIn is > 0 and <= 5f;

        // Feint: 10% phys + 5% magic -- skip if purely magic damage
        if ((rwSoon || !BmrActive)
            && (IsPhysicalDamageIncoming || !IsMagicalDamageIncoming)
            && FeintPvE.CanUse(out act))
            return true;

        return base.DefenseAreaAbility(nextGCD, out act);
    }

    [RotationDesc(ActionID.MantraPvE)]
    protected override bool HealAreaAbility(IAction nextGCD, out IAction? act)
    {
        act = null;
        if (!EnoughWeaveTime) return false;

        // Earth's Reply (follow-up to Riddle of Earth — 500 potency AoE heal)
        // Per Balance: use Earth's Reply after taking raidwide damage for party healing
        if (EarthsReplyPvE.CanUse(out act))
            return true;

        // BMR-aware: Mantra proactively before raidwide (10% heal potency buff for healers)
        // Use at ~8s before raidwide so healers benefit from the buff for post-hit healing
        // Don't stack with Feint on same raidwide — Mantra covers a DIFFERENT raidwide if possible
        // When Feint just went out (< 10s ago), prefer holding Mantra for the next raidwide
        bool rwSoon = BmrActive && BmrRaidwideIn is > 0 and <= 8f;
        bool feintJustUsed = BmrActive && FeintPvE.Cooldown.IsCoolingDown
                             && FeintPvE.Cooldown.RecastTimeElapsedRaw < 10f;

        // With BMR: use Mantra when raidwide is coming but Feint wasn't just used on this same raidwide
        // Without BMR: use whenever framework says to heal
        if (BmrActive && rwSoon && !feintJustUsed && MantraPvE.CanUse(out act))
            return true;
        if (BmrActive && rwSoon && feintJustUsed && BmrRaidwideIn <= 3f && MantraPvE.CanUse(out act))
            return true; // If raidwide is very close and we already feinted, Mantra is still better than nothing

        if (!BmrActive && MantraPvE.CanUse(out act))
            return true;

        return base.HealAreaAbility(nextGCD, out act);
    }

    [RotationDesc(ActionID.RiddleOfEarthPvE)]
    protected override bool DefenseSingleAbility(IAction nextGCD, out IAction? act)
    {
        act = null;
        if (!EnoughWeaveTime) return false;

        // BMR-aware: Riddle of Earth before raidwide or tankbuster for self 20% mit + Earth's Rumination
        // Per Balance: RoE grants 20% mit for 15s and enables Earth's Reply (500 potency AoE cure)
        // Use 1-3s before damage to cover the snapshot window
        bool rwSoon = BmrActive && BmrRaidwideIn is > 0 and <= 3f;
        bool tbSoon = BmrActive && BmrTankbusterIn is > 0 and <= 3f;
        bool dmgSoon = BmrActive && BmrDamageIn is > 0 and <= 3f;

        if ((rwSoon || tbSoon || dmgSoon || !BmrActive) && RiddleOfEarthPvE.CanUse(out act, usedUp: true))
            return true;

        return base.DefenseSingleAbility(nextGCD, out act);
    }

    [RotationDesc(ActionID.SecondWindPvE, ActionID.BloodbathPvE)]
    protected override bool HealSingleAbility(IAction nextGCD, out IAction? act)
    {
        act = null;
        if (!EnoughWeaveTime) return false;

        // BMR-aware: Second Wind / Bloodbath for self-sustain before incoming damage
        // Bloodbath before raidwide: upcoming GCD hits will heal us through the damage
        // Second Wind: flat 500 potency self-heal, use when HP is low or damage imminent
        bool rwSoon = BmrActive && BmrRaidwideIn is > 0 and <= 3f;
        bool dmgSoon = BmrActive && BmrDamageIn is > 0 and <= 3f;

        // Bloodbath first (GCD-based sustain is better when we're hitting things)
        if ((rwSoon || dmgSoon) && BloodbathPvE.CanUse(out act))
            return true;

        if (SecondWindPvE.CanUse(out act))
            return true;
        if (BloodbathPvE.CanUse(out act))
            return true;

        return base.HealSingleAbility(nextGCD, out act);
    }

    protected override bool AttackAbility(IAction nextGCD, out IAction? act)
    {
        // BMR-aware: dump Forbidden Chakra before downtime (don't waste 5 stacks going into untargetable)
        if (BmrActive && BmrDowntimeIn is > 0 and <= 5f && Chakra >= 5 && EnoughWeaveTime)
        {
            if (EnlightenmentPvE.CanUse(out act)) return true;
            if (TheForbiddenChakraPvE.CanUse(out act)) return true;
        }

        return TryUseRiddleOfWind(out act)
               || TryUseForbiddenChakra(out act)
               || base.AttackAbility(nextGCD, out act);
    }
    #endregion

    #region GCD Logic
    protected override bool GeneralGCD(out IAction? act)
    {
        RotationUpdater();

        // BMR-aware: If downtime is imminent, prioritize finishing blitz and spending resources
        // Per Balance advanced guide: "Downtime Blitz" — complete PB sequence, hold blitz for post-downtime
        if (BmrActive && BmrDowntimeIn is > 0 and <= 3f)
        {
            // If we have a blitz ready, fire it NOW before boss goes away
            if (HasBlitzReady && TryUseMasterfulBlitz(out act)) return true;

            // Use Fire's/Wind's Reply as instant GCDs rather than starting new combos
            if (TryUseFiresReply(out act)) return true;
            if (TryUseWindsReply(out act)) return true;

            // Six-Sided Star is a 5s GCD — perfect for covering short downtime gaps
            if (SixsidedStarPvE.CanUse(out act)) return true;
        }

        // After blitz/Fire's Reply, prefer Opo (gets Formless Fist) but DON'T hard-gate
        // If TryUseOpoOpo fails for any reason, fall through to other options to avoid stalling
        if (MustUseOpo)
        {
            if (CombatElapsedLessGCD(1) && TryUseOpenerVariation(out act)) return true;
            if (TryUseOpoOpo(out act)) return true;
            // Fall through instead of returning false - prevents deadlock
        }

        if (TryUseWindsReply(out act)) return true;
        if (TryUseFiresReply(out act)) return true;

        return  TryGenerateNadi(out act)
            || TryUseMasterfulBlitz(out act)
            || TryUseFiller(out act)
            || TryUseMeditations(out act)
            || base.GeneralGCD(out act);
    }

    #endregion

    #region  Extra Methods

    #region Form Execution
    private bool TryUseMeditations(out IAction? act)
    {
        act = null;
        if (InCombat && HasHostilesInRange) return false;

        if ((!HasHostilesInRange || !InCombat) && Chakra < 5)
        {
            return EnlightenedMeditationPvE.CanUse(out act)
                   || ForbiddenMeditationPvE.CanUse(out act)
                   || InspiritedMeditationPvE.CanUse(out act)
                   || !ForbiddenMeditationPvE.Info.EnoughLevelAndQuest() && SteeledMeditationPvE.CanUse(out act);
        }

        return false;
    }
    private bool TryUseOpoOpo(out IAction? act)
    {
        act = null;
        if (InOpoopoForm || HasFormlessFist || HasPerfectBalance)
        {
            if (ArmOfTheDestroyerPvE.CanUse(out act, skipComboCheck: true))
            {
                return true;
            }

            // Try preferred action first, then fallback to alternatives to prevent stalling
            switch (OpoOpoFury)
            {
                case > 0 when LeapingOpoPvE.EnoughLevel:
                    if (LeapingOpoPvE.CanUse(out act, skipComboCheck: true)) return true;
                    break;
                case > 0:
                    if (BootshinePvE.CanUse(out act, skipComboCheck: true)) return true;
                    break;
                case 0:
                    if (DragonKickPvE.CanUse(out act, skipComboCheck: true)) return true;
                    break;
            }

            // Fallback: try any Opo GCD if preferred one failed
            if (LeapingOpoPvE.CanUse(out act, skipComboCheck: true)) return true;
            if (DragonKickPvE.CanUse(out act, skipComboCheck: true)) return true;
            if (BootshinePvE.CanUse(out act, skipComboCheck: true)) return true;
        }
        return false;
    }
    private bool TryUseRaptor(out IAction? act)
    {
        act = null;
        if (InRaptorForm || HasFormlessFist || HasPerfectBalance)
        {
            if (FourpointFuryPvE.CanUse(out act, skipComboCheck: true))
            {
                return true;
            }

            switch (RaptorFury)
            {
                case > 0 when RisingRaptorPvE.EnoughLevel:
                    if (RisingRaptorPvE.CanUse(out act, skipComboCheck: true)) return true;
                    break;
                case > 0:
                    if (TrueStrikePvE.CanUse(out act, skipComboCheck: true)) return true;
                    break;
                case 0:
                    if (TwinSnakesPvE.CanUse(out act, skipComboCheck: true)) return true;
                    break;
            }

            // Fallback: try any Raptor GCD
            if (RisingRaptorPvE.CanUse(out act, skipComboCheck: true)) return true;
            if (TwinSnakesPvE.CanUse(out act, skipComboCheck: true)) return true;
            if (TrueStrikePvE.CanUse(out act, skipComboCheck: true)) return true;
        }
        return false;
    }
    private bool TryUseCoeurl(out IAction? act)
    {
        act = null;
        if (InCoeurlForm || HasFormlessFist || HasPerfectBalance)
        {
            if (RockbreakerPvE.CanUse(out act, skipComboCheck: true))
            {
                return true;
            }

            switch (CoeurlFury)
            {
                case > 0 when PouncingCoeurlPvE.EnoughLevel:
                    if (PouncingCoeurlPvE.CanUse(out act, skipComboCheck: true)) return true;
                    break;
                case > 0:
                    if (SnapPunchPvE.CanUse(out act, skipComboCheck: true)) return true;
                    break;
                case 0:
                    if (DemolishPvE.CanUse(out act, skipComboCheck: true)) return true;
                    break;
            }

            // Fallback: try any Coeurl GCD
            if (PouncingCoeurlPvE.CanUse(out act, skipComboCheck: true)) return true;
            if (DemolishPvE.CanUse(out act, skipComboCheck: true)) return true;
            if (SnapPunchPvE.CanUse(out act, skipComboCheck: true)) return true;
        }

        return false;
    }
    private bool TryUseFiller(out IAction? act)
    {
        // Follow current form naturally instead of always starting with Opo
        if (InRaptorForm)
            return TryUseRaptor(out act) || TryUseCoeurl(out act) || TryUseOpoOpo(out act);
        if (InCoeurlForm)
            return TryUseCoeurl(out act) || TryUseOpoOpo(out act) || TryUseRaptor(out act);

        // Opo form, formless, or no form -> start with Opo
        return TryUseOpoOpo(out act)
               || TryUseRaptor(out act)
               || TryUseCoeurl(out act)
               || TryUseFormShift(out act);
    }
    private bool TryUseOpenerVariation(out IAction? act)
    {
        act = null;
        if (!CombatElapsedLessGCD(1)) return false;

        return ChosenVariation switch
        {
            OpenerVariation.Demolish7 => TryUseCoeurl(out act),
            OpenerVariation.DragonKick5 => TryUseOpoOpo(out act),
            OpenerVariation.DragonKick7 => TryUseOpoOpo(out act),
            _ => false
        };
    }

    #endregion

    #region Perfect Balance / Nadi Generation

    private bool TryGenerateNadi(out IAction? act)
    {
        act = null;
                if (!HasPerfectBalance || !BeastChakrasContains(BeastChakra.None)) return false;

        return NextNadiGoal switch
        {
            Nadi.None => false,
            Nadi.Lunar when BeastChakrasContains(BeastChakra.None) => TryGenerateLunarNadi(out act),
            Nadi.Solar when BeastChakrasContains(BeastChakra.None) => TryGenerateSolarNadi(out act),
            _ => false
        };
    }

    private bool TryGenerateLunarNadi(out IAction? act)
    {
        act = null;
        if (!BeastChakrasContains(BeastChakra.None) || NextNadiGoal != Nadi.Lunar) return false;

        return TryUseOpoOpo(out act);
    }

    private bool TryGenerateSolarNadi(out IAction? act)
    {
        act = null;
        if (!BeastChakrasContains(BeastChakra.None) || NextNadiGoal != Nadi.Solar) return false;

        var furyState = CheckFuryGaugeState();

        return furyState switch
        {
            "Coeurl, Raptor, Opo" => !BeastChakrasContains(BeastChakra.Coeurl) && TryUseCoeurl(out act)
                                || !BeastChakrasContains(BeastChakra.Raptor) && TryUseRaptor(out act)
                                || !BeastChakrasContains(BeastChakra.OpoOpo) && TryUseOpoOpo(out act),

            "Opo, Coeurl, Raptor" => !BeastChakrasContains(BeastChakra.OpoOpo) && TryUseOpoOpo(out act)
                                || !BeastChakrasContains(BeastChakra.Coeurl) && TryUseCoeurl(out act)
                                || !BeastChakrasContains(BeastChakra.Raptor) && TryUseRaptor(out act),

            "Opo, Raptor, Coeurl" => !BeastChakrasContains(BeastChakra.OpoOpo) && TryUseOpoOpo(out act)
                                || !BeastChakrasContains(BeastChakra.Raptor) && TryUseRaptor(out act)
                                || !BeastChakrasContains(BeastChakra.Coeurl) && TryUseCoeurl(out act),

            _ => false,
        };
    }

    #endregion

    #region Masterful Blitz Execution

    private bool TryUseMasterfulBlitz(out IAction? act)
    {
        act = null;
        if (BeastChakrasContains(BeastChakra.None)) return false;

        if (HasBothNadi && PhantomRushPvEReady)
        {
            return PhantomRushPvE.CanUse(out act);
        }

        if (BeastChakrasAllSame() && ElixirBurstPvEReady)
        {
            return ElixirBurstPvE.CanUse(out act);
        }

        if (BeastChakrasAllDifferent() && RisingPhoenixPvEReady)
        {
            return RisingPhoenixPvE.CanUse(out act);
        }

        // Fallback: if beast chakras are full but no specific condition matched
        // (e.g. mixed 2+1 pattern = Celestial Revolution, or any Ready check mismatch)
        // Use the generic MasterfulBlitz which resolves to whatever the game says
        return MasterfulBlitzPvE.CanUse(out act);
    }

    #endregion

    #region  Other GCDs

    private bool TryUseFiresReply(out IAction? act)
    {
        act = null;
        if (!HasFiresRumination || HasPerfectBalance) return false;

        // Emergency: use Fire's Reply before buff expires (< 3s remaining)
        if (StatusHelper.PlayerStatusTime(true, StatusID.FiresRumination) < 3f
            && !HasBlitzReady && IsLastGCDOpo)
        {
            return FiresReplyPvE.CanUse(out act);
        }

        if (IsBurst && !HasBlitzReady)
        {
            if (FiresReplyPvE.CanUse(out act))
            {
                if (IsLastGCD(true, WindsReplyPvE))
                {
                    return true;
                }

                if (IsLastGCDOpo)
                {
                    return true;
                }
            }
        }

        if (!IsBurst && HasRiddleOfFire && !HasBlitzReady && IsLastGCDOpo)
        {
            // During RoF without Brotherhood, use after any Opo GCD
            return FiresReplyPvE.CanUse(out act);
        }

        if (!IsBurst && !HasRiddleOfFire && !HasBlitzReady)
        {
            if (FiresReplyPvE.CanUse(out act))
            {
                // Odd window usage - relaxed from strict BlitzCount gating
                if ((LunarOddWindow || SolarOddWindow) && IsLastGCDOpo)
                {
                    return true;
                }
            }
        }

        return false;
    }
    private bool TryUseWindsReply(out IAction? act)
    {
        act = null;
        if (!HasWindsRumination || HasPerfectBalance) return false;

        // Emergency: use before buff expires regardless of last GCD
        if (StatusHelper.PlayerStatusTime(true, StatusID.WindsRumination) < 3f
            && !HasBlitzReady)
        {
            return WindsReplyPvE.CanUse(out act);
        }

        if (WindsReplyPvE.CanUse(out act))
        {
            // Wind's Reply doesn't grant Formless Fist, so it can go anywhere
            // Prefer after Opo GCD but don't require it
            if (!HasBlitzReady && IsLastGCDOpo)
            {
                return true;
            }

            // During burst, also allow after Masterful Blitz
            if ((IsBurst || HasRiddleOfFire) && !HasBlitzReady && IsLastGCDMasterfulBlitz)
            {
                return true;
            }
        }

        return false;
    }
    private bool TryUseSixSidedStar(out IAction? act)
    {
        act = null;
        if (!IsInHighEndDuty || (CurrentTarget != null && CurrentTarget.GetHealthRatio() > BossHealthThreshold)) return false;

        if (CurrentTarget != null && (CurrentTarget.IsBossFromIcon() || CurrentTarget.IsBossFromTTK()))
        {
            if (CurrentTarget.GetHealthRatio() <= BossHealthThreshold && (!HasPerfectBalance && !HasFormlessFist))
            {
                return SixsidedStarPvE.CanUse(out act);
            }
        }

        return false;
    }
    private bool TryUseFormShift(out IAction? act)
    {
        act = null;
        if (HasFormlessFist || InOpoopoForm || InCoeurlForm || InRaptorForm ||
            HasPerfectBalance || InCombat && HasHostilesInRange) return false;

        return FormShiftPvE.CanUse(out act);
    }

    #endregion

    #region oGCD Methods

    private bool TryUseBuffs(out IAction? act)
    {
        return TryUseBrotherhood(out act)
                   || TryUseRiddleOfFire(out act);
    }
    private bool TryUsePerfectBalance(out IAction? act)
    {
        act = null;
        if (HasPerfectBalance) return false;

        // BMR-aware: Don't start Perfect Balance if downtime < 8s (need time for 3 GCDs + Blitz)
        // Per Balance advanced guide: PB window is 20s, but the 3 GCDs + blitz take ~8s minimum
        if (BmrActive && BmrDowntimeIn is > 0 and <= 8f)
            return false;

        // Opener: use PB when both buffs are ready
        if (CombatElapsedLessGCD(1))
        {
            if (BrotherhoodPvE.Cooldown.HasOneCharge &&
                RiddleOfFirePvE.Cooldown.HasOneCharge)
            {
                return PerfectBalancePvE.CanUse(out act, usedUp: false, skipTTKCheck: true);
            }
        }

        // During burst (BH+RoF), use after Opo when Fire's Reply already spent
        if (InBurst && !HasFiresRumination)
        {
            return IsLastGCDOpo && PerfectBalancePvE.CanUse(out act, usedUp: true, skipTTKCheck: true);
        }

        // Solar odd window: PB before RoF (2-4 GCDs ahead)
        if (SolarOddWindow && IsLastGCDOpo && IsReadySoon(RiddleOfFirePvE, 2))
        {
            return PerfectBalancePvE.CanUse(out act, usedUp: true);
        }

        // Lunar odd window: PB after RoF, on next Opo
        if (LunarOddWindow && (HasRiddleOfFire || IsReadySoon(RiddleOfFirePvE, 0)) && !HasBothNadi)
        {
            return IsLastGCDOpo && PerfectBalancePvE.Cooldown.WillHaveOneCharge(10) && PerfectBalancePvE.CanUse(out act, usedUp: true, skipTTKCheck: true);
        }

        // Pre-even window: PB when both BH+RoF coming soon
        if (IsReadySoon(BrotherhoodPvE, 2) && IsReadySoon(RiddleOfFirePvE, 2))
        {
            return IsLastGCDOpo && PerfectBalancePvE.CanUse(out act, usedUp: true, skipTTKCheck: true);
        }

        // Overcap protection: if PB has 2 charges past opener, use it to avoid waste
        if (PerfectBalancePvE.Cooldown.CurrentCharges >= 2 && !CombatElapsedLessGCD(5) && IsLastGCDOpo)
        {
            return PerfectBalancePvE.CanUse(out act, usedUp: true);
        }

        return false;
    }
    private bool TryUseBrotherhood(out IAction? act)
    {
        act = null;
        if (!CanBurst || !CanEarlyWeave || !RiddleOfFirePvE.Cooldown.WillHaveOneCharge(0.5f)) return false;

        // BMR-aware: Don't pop Brotherhood if downtime is imminent and it won't be useful
        // Brotherhood is 120s CD — if downtime < 15s, the buff window is wasted
        if (BmrActive && BmrDowntimeIn is > 0 and <= 15f)
            return false;

        // BMR-aware: Hold burst for vulnerability window if one is coming within 30s
        // Only hold if Brotherhood isn't about to overcap (i.e., it hasn't been sitting ready too long)
        if (BmrActive && BmrVulnerableIn is > 0 and <= 30f
            && BrotherhoodPvE.Cooldown.RecastTimeElapsedRaw < 5f)
            return false;

        var timeRequirement = ChosenVariation switch
        {
            OpenerVariation.DragonKick5 => PerfectBalanceStacks(1) && CombatElapsedLessGCD(10),
            OpenerVariation.DragonKick7 => HasBlitzReady && CombatElapsedLessGCD(10),
            OpenerVariation.Demolish7 => HasBlitzReady && CombatElapsedLessGCD(10),
            _ => PerfectBalanceStacks(1) && CombatElapsedLessGCD(10)
        };

        if (timeRequirement) return BrotherhoodPvE.CanUse(out act);

        // Balance: "press Brotherhood on cooldown, NEVER hold for alignment"
        if (!CombatElapsedLessGCD(10))
        {
            return BrotherhoodPvE.CanUse(out act);
        }
        return false;
    }
    private bool TryUseRiddleOfFire(out IAction? act)
    {
        act = null;
        if (!RiddleOfFirePvE.IsEnabled || CanEarlyWeave || !CanLateWeave) return false;

        // BMR-aware: Don't pop RoF if downtime is very imminent (buff would be wasted)
        // RoF is 60s CD so holding briefly for post-downtime is acceptable
        if (BmrActive && BmrDowntimeIn is > 0 and <= 10f)
            return false;

        // BMR-aware: Hold RoF briefly for vulnerability window if one is coming soon
        if (BmrActive && BmrVulnerableIn is > 0 and <= 15f
            && RiddleOfFirePvE.Cooldown.RecastTimeElapsedRaw < 3f)
            return false;

        if (RiddleOfFirePvE.CanUse(out act))
        {
            if (IsLastAbility(ActionID.BrotherhoodPvE) || HasBrotherhood)
            {
                return true;
            }

            if (LunarOddWindow)
            {
                if (IsLastAbility(ActionID.PerfectBalancePvE) && IsLastGCDOpo || !IsLastGCDOpo)
                {
                    return true;
                }
            }

            if (SolarOddWindow)
            {
                if (IsLastGCDOpo || IsLastAbility(ActionID.PerfectBalancePvE) || IsLastGCDMasterfulBlitz ||
                    HasBlitzReady)
                {
                    return true;
                }
            }
        }
        return false;
    }
    private bool TryUseRiddleOfWind(out IAction? act)
    {
        act = null;
        if (!RiddleOfWindPvE.IsEnabled || !EnoughWeaveTime) return false;

        if (RiddleOfWindPvE.CanUse(out act))
        {
            // Priority 1: During burst window
            if (InBurst && (IsLastGCDOpo || IsLastGCDMasterfulBlitz))
            {
                return true;
            }

            // Priority 2: During RoF
            if (HasRiddleOfFire && (IsLastGCDOpo || IsLastGCDMasterfulBlitz))
            {
                return true;
            }

            // Priority 3: Outside burst but both CDs are ticking - use on cooldown
            // RoW is 90s CD, holding it for burst loses more than buffing it gains
            if (IsLastGCDOpo && BrotherhoodPvE.Cooldown.IsCoolingDown && RiddleOfFirePvE.Cooldown.IsCoolingDown)
            {
                return true;
            }

            // Fallback: Use on cooldown if available and we have weave room
            // This prevents drift - losing a use of RoW (~600 auto-attack potency + 1040 Wind's Reply) is far worse
            // than missing buff alignment
            if (RiddleOfWindPvE.Cooldown.HasOneCharge && !CombatElapsedLessGCD(3))
            {
                return true;
            }
        }

        return false;
    }
    private bool TryUseForbiddenChakra(out IAction? act)
    {
        act = null;
        if (Chakra < 5 || !EnoughWeaveTime) return false;

        // AoE Check
        if (EnlightenmentPvE.CanUse(out act)) return true;

        // BMR-aware: Dump chakra before downtime (don't waste 5 stacks going into untargetable)
        if (BmrActive && BmrDowntimeIn is > 0 and <= 10f && Chakra >= 5)
        {
            return TheForbiddenChakraPvE.CanUse(out act)
                   || (!TheForbiddenChakraPvE.EnoughLevel && SteelPeakPvE.CanUse(out act));
        }

        // During Brotherhood, always spend at 5 - party generates more chakra and overcap wastes 80 potency each
        if (HasBrotherhood && Chakra >= 5)
        {
            return TheForbiddenChakraPvE.CanUse(out act)
                   || (!TheForbiddenChakraPvE.EnoughLevel && SteelPeakPvE.CanUse(out act));
        }

        // Only hold pre-burst if not already in burst window
        if (!InBurst && !IsOpenerStart && (IsReadySoon(BrotherhoodPvE, 1) || IsReadySoon(RiddleOfFirePvE, 1)))
            return false;

        if (IsOpenerStart && BlitzCount == 0)
        {
            if (ChosenVariation == OpenerVariation.Demolish7 && !InBurst) return false;
            return Chakra >= 5 && TheForbiddenChakraPvE.CanUse(out act);
        }

        if (Chakra >= 5 && TheForbiddenChakraPvE.CanUse(out act)) return true;

        // Low level fallback
        return !TheForbiddenChakraPvE.EnoughLevel && Chakra >= 5 && SteelPeakPvE.CanUse(out act);
    }

    #endregion

    #endregion
}