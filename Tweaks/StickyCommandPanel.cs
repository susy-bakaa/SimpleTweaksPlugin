using System;
using System.Diagnostics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using SimpleTweaksPlugin.TweakSystem;
using SimpleTweaksPlugin.Utility;

namespace SimpleTweaksPlugin.Tweaks;
[TweakName("Sticky Command Panel")]
[TweakDescription("Keep the command panel open after it has been opened.")]
[TweakAuthor("SubaruYashiro")]
[TweakAutoConfig]
public unsafe class StickyCommandPanel : UiAdjustments.SubTweak
{
    public class Configs : TweakConfig
    {
        [TweakConfigOption("Auto (Re)Open While in Duty.")]
        public bool AutoOpenInDuty = true;

        [TweakConfigOption("Auto (Re)Open Anywhere.")]
        public bool AutoOpenEverywhere = true;

        [TweakConfigOption("Only (Re)Open After First Manual Open.")]
        public bool RequireInitialOpen = true;

        [TweakConfigOption("Manual Close Unsticks Panel.")]
        public bool ManualCloseUnsticks = true;

        public string TerritoryIds = string.Empty;
    }

    public Configs Config { get; private set; }

    // A finalize caused by a game-state transition is normally accompanied by a
    // Dalamud Condition change. Wait briefly before deciding that a finalize was
    // a manual close so condition changes immediately before/after it can be seen.
    private const double ManualCloseDetectionDelaySeconds = 1.0;
    private const double ConditionBeforeCloseGraceSeconds = 1.0;

    private bool commandPanelWasOpened;
    private bool pendingCloseCheck;
    private long pendingCloseTimestamp;
    private long lastConditionChangeTimestamp;
    private ulong conditionChangeSerial;
    private ulong conditionSerialAtClose;

    protected override void Enable()
    {
        // Only inherit an already-open panel when it is inside the configured scope.
        commandPanelWasOpened = IsTerritoryAllowed() && Common.GetUnitBase("QuickPanel") != null;
        pendingCloseCheck = false;
        lastConditionChangeTimestamp = 0;
        conditionChangeSerial = 0;

        Service.Condition.ConditionChange += OnConditionChange;
        Service.ClientState.TerritoryChanged += OnTerritoryChanged;
        Service.Framework.Update += OnFrameworkUpdate;
        Service.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "QuickPanel", OnQuickPanelOpened);
        Service.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "QuickPanel", OnQuickPanelClosing);
    }

    protected override void Disable()
    {
        Service.Condition.ConditionChange -= OnConditionChange;
        Service.ClientState.TerritoryChanged -= OnTerritoryChanged;
        Service.Framework.Update -= OnFrameworkUpdate;
        Service.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "QuickPanel", OnQuickPanelOpened);
        Service.AddonLifecycle.UnregisterListener(AddonEvent.PreFinalize, "QuickPanel", OnQuickPanelClosing);

        commandPanelWasOpened = false;
        pendingCloseCheck = false;
    }

    private void OnQuickPanelOpened(AddonEvent type, AddonArgs args)
    {
        // Opening the panel outside the territory allow-list must not arm sticky
        // state for when the player later enters an allowed territory.
        if (!IsTerritoryAllowed()) return;
        commandPanelWasOpened = true;
        pendingCloseCheck = false;
    }

    private void OnQuickPanelClosing(AddonEvent type, AddonArgs args)
    {
        if (!commandPanelWasOpened || !Config.ManualCloseUnsticks) return;

        if (!IsTerritoryAllowed())
        {
            DisarmStickyState();
            return;
        }

        pendingCloseCheck = true;
        pendingCloseTimestamp = Stopwatch.GetTimestamp();
        conditionSerialAtClose = conditionChangeSerial;
    }

    private void OnConditionChange(ConditionFlag flag, bool value)
    {
        lastConditionChangeTimestamp = Stopwatch.GetTimestamp();
        conditionChangeSerial++;

        if (!IsTerritoryAllowed())
        {
            DisarmStickyState();
            return;
        }

        if (Config.RequireInitialOpen && !commandPanelWasOpened) return;
        if (ShouldAutoOpen()) CheckCommandPanelCondition();
    }

    private void OnTerritoryChanged(uint territoryId)
    {
        // Territory scope is also a sticky-state scope. Leaving it always disarms,
        // so returning later requires the player to open QuickPanel manually again.
        if (!IsTerritoryAllowed(territoryId))
        {
            DisarmStickyState();
            return;
        }

        // If moving directly between two allowed territories, retain sticky state.
        if ((!Config.RequireInitialOpen || commandPanelWasOpened) && ShouldAutoOpen()) CheckCommandPanelCondition();
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!pendingCloseCheck) return;

        var now = Stopwatch.GetTimestamp();
        var elapsedSinceClose = (now - pendingCloseTimestamp) / (double)Stopwatch.Frequency;
        if (elapsedSinceClose < ManualCloseDetectionDelaySeconds) return;

        pendingCloseCheck = false;

        if (!IsTerritoryAllowed())
        {
            DisarmStickyState();
            return;
        }

        // A condition changed after PreFinalize while we were waiting.
        var conditionChangedAfterClose = conditionChangeSerial != conditionSerialAtClose;

        // Also accept a condition change immediately before PreFinalize. Game-driven
        // UI destruction and Dalamud condition events do not have guaranteed ordering.
        var conditionChangedShortlyBeforeClose = false;
        if (lastConditionChangeTimestamp != 0 && lastConditionChangeTimestamp <= pendingCloseTimestamp)
        {
            var secondsBeforeClose = (pendingCloseTimestamp - lastConditionChangeTimestamp) / (double)Stopwatch.Frequency;
            conditionChangedShortlyBeforeClose = secondsBeforeClose <= ConditionBeforeCloseGraceSeconds;
        }

        if (!conditionChangedAfterClose && !conditionChangedShortlyBeforeClose)
        {
            // No game-state transition was observed around the close, so treat it as
            // an explicit/manual close and require another manual open to re-arm.
            commandPanelWasOpened = false;
            return;
        }

        // Game-driven close: remain armed. Re-check here as well because the relevant
        // condition event may have happened just before the addon finalized, in which
        // case OnConditionChange saw the panel still open and had nothing to reopen.
        if ((!Config.RequireInitialOpen || commandPanelWasOpened) && ShouldAutoOpen()) CheckCommandPanelCondition();
    }

    private void DisarmStickyState()
    {
        commandPanelWasOpened = false;
        pendingCloseCheck = false;
    }

    private bool ShouldAutoOpen()
    {
        return (Service.Condition[ConditionFlag.BoundByDuty] && Config.AutoOpenInDuty) || Config.AutoOpenEverywhere;
    }

    private bool IsTerritoryAllowed()
    {
        return IsTerritoryAllowed(Service.ClientState.TerritoryType);
    }

    private bool IsTerritoryAllowed(uint territoryId)
    {
        if (string.IsNullOrWhiteSpace(Config.TerritoryIds)) return true;

        var hasValidId = false;
        foreach (var value in Config.TerritoryIds.Split([',', ';', ' ', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!uint.TryParse(value, out var configuredTerritoryId)) continue;
            hasValidId = true;
            if (configuredTerritoryId == territoryId) return true;
        }

        // If the field contains no valid IDs at all, don't accidentally disable the tweak everywhere.
        return !hasValidId;
    }

    private void DrawConfig(ref bool hasChanged)
    {
        var territoryIds = Config.TerritoryIds;
        ImGui.SetNextItemWidth(350 * ImGui.GetIO().FontGlobalScale);
        if (ImGui.InputText("Territory IDs (comma/space separated; blank = anywhere)##StickyCommandPanelTerritories", ref territoryIds, 512))
        {
            Config.TerritoryIds = territoryIds;
            hasChanged = true;
        }
    }

    private void CheckCommandPanelCondition()
    {
        var unitBase = Common.GetUnitBase("QuickPanel");
        if (unitBase != null) return;
        AgentQuickPanel.Instance()->OpenPanel(AgentQuickPanel.Instance()->ActivePanel, false, false);
    }
}
