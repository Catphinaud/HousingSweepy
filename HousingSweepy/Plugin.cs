using System.Numerics;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Command;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using ECommons;
using ECommons.Automation;
using ECommons.Automation.NeoTaskManager;
using ECommons.Commands;
using ECommons.DalamudServices;
using ECommons.GameFunctions;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using VT = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType;

namespace HousingSweepy;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/sweepy";
    private const byte AetheryteMapMarkerDataType = 3;
    private const string ResidentialDistrictAethernetEntry = "Residential District Aethernet.";
    private const string GoToSpecifiedWardEntry = "Go to specified ward. (Review Tabs)";

    public static TaskManager TaskManager = null!;

    private readonly WindowSystem _windowSystem;

    private readonly List<ResidentialTerritory> residentialTerritories = new();
    internal readonly ExcelSheet<TerritoryType> Territories;

    // state
    private readonly WardObserver wardObserver;
    private readonly MainWindow window;
    private readonly TeleportDebugWindow teleportDebugWindow;
    private readonly TaskOverlayWindow taskOverlayWindow;
    internal readonly ExcelSheet<World> Worlds;


    internal bool _disposed;

    public bool IsScanningWards;
    public bool ShowTaskOverlay;

    public WordTerritory? LastCommittedZoneAndWorld;

    // Now stored per WorldId -> TerritoryId -> WardNumber -> House list
    public Dictionary<uint, Dictionary<uint, Dictionary<int, List<HouseInfoEntry>>>> SeenHousesByWorldAndTerritory = new();

    public bool StopNext;

    // Now stored per WorldId -> TerritoryId -> Ward list
    public Dictionary<uint, Dictionary<uint, List<WardInfo>>> WardsByWorldAndTerritory = new();

    public Queue<int> WardsToScan = new();

    private readonly ICallGateSubscriber<uint, byte, bool> teleporterTeleportIpc;
    private readonly ICallGateSubscriber<bool> teleporterChatMessageIpc;
    private readonly ICallGateSubscriber<bool> lifestreamIsBusyIpc;
    private readonly ICallGateSubscriber<string, object> lifestreamExecuteCommandIpc;

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        Instance = this;
        PluginInterface = pluginInterface;

        teleporterTeleportIpc = pluginInterface.GetIpcSubscriber<uint, byte, bool>("Teleport");
        teleporterChatMessageIpc = pluginInterface.GetIpcSubscriber<bool>("Teleport.ChatMessage");
        lifestreamIsBusyIpc = pluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy");
        lifestreamExecuteCommandIpc = pluginInterface.GetIpcSubscriber<string, object>("Lifestream.ExecuteCommand");

        ECommonsMain.Init(pluginInterface, this);
        Territories = Svc.Data.GetExcelSheet<TerritoryType>();
        Worlds = Svc.Data.GetExcelSheet<World>();
        Svc.Data.GetExcelSheet<HousingLandSet>();


        TaskManager = new TaskManager(new TaskManagerConfiguration
        {
            ShowDebug = true,
            TimeLimitMS = 15000
        });

        InitializeResidentialTerritories();

        wardObserver = new WardObserver(this);
        _windowSystem = new WindowSystem("HousingSweepy");
        _windowSystem.AddWindow(window = new MainWindow(this));
        _windowSystem.AddWindow(teleportDebugWindow = new TeleportDebugWindow(this));
        _windowSystem.AddWindow(taskOverlayWindow = new TaskOverlayWindow(this));
        PluginInterface.UiBuilder.Draw += _windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += window.MarkOpenedViaCommandToggle;
        Svc.Commands.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Toggle the HousingSweepy window.\n'/sweepy reset' to reset seen houses.\n'/sweepy tpdebug' to toggle teleport debug."
        });

        Svc.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "HousingSelectBlock", OnHousingSelectBlock);
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "HousingSelectBlock", OnHousingSelectBlock);

        Svc.Log.Information(IsTeleporterIpcAvailable()
            ? "Teleporter IPC is available."
            : "Teleporter IPC is not available.");
    }

    public IDalamudPluginInterface PluginInterface { get; }

    public static Plugin Instance { get; private set; } = null!;
    public IReadOnlyList<ResidentialTerritory> ResidentialTerritories => residentialTerritories;

    public unsafe uint? CurrentWorldId
    {
        get {
            var agentLobbyPtr = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentLobby.Instance();
            return !agentLobbyPtr->IsLoggedIn ? null : agentLobbyPtr->LobbyData.CurrentWorldId;
        }
    }

    public string? CurrentWorldName => Svc.PlayerState.CurrentWorld.ValueNullable?.Name.ToString() ?? null;

    public unsafe void OpenPlot(uint plotId, uint mapId)
    {
        plotId = Math.Clamp(plotId, 0, 62);

        var instance = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentMap.Instance();

        if (instance == null)
        {
            Svc.Chat.PrintError("Could not get AgentMap instance.");
            return;
        }

        var housingMapMarkerInfo = Svc.Data.Excel.GetSubrowSheet<HousingMapMarkerInfo>().GetSubrowOrDefault(mapId, (ushort)plotId);

        if (housingMapMarkerInfo == null)
        {
            Svc.Chat.PrintError($"No housing map marker info found for the map {mapId} with subrow id {plotId}.");
            return;
        }

        var info = housingMapMarkerInfo.Value;

        var position = new Vector3(info.X, info.Y, info.Z);

        if (info.Map.Value.TerritoryType.ValueNullable is not {} territory)
        {
            Svc.Chat.PrintError($"No territory found for the map {mapId} with subrow id {plotId}.");
            return;
        }

        uint realMapId = info.Map.RowId;
        uint realTerritoryId = territory.RowId;

        position.X += territory.Map.Value.OffsetX;
        position.Z += territory.Map.Value.OffsetY;

        instance->SetFlagMapMarker(realTerritoryId, territory.Map.RowId, position, 71296);

        if (!instance->AddMiniMapMarker(position, 71296))
        {
            Svc.Chat.PrintError("Unable to place mini map marker");
        }

        instance->OpenMap(realMapId, realTerritoryId);
    }

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;

        Svc.Commands.RemoveHandler(CommandName);
        Svc.AddonLifecycle.UnregisterListener(AddonEvent.PreFinalize, "HousingSelectBlock", OnHousingSelectBlock);
        Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "HousingSelectBlock", OnHousingSelectBlock);

        PluginInterface.UiBuilder.Draw -= _windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= window.MarkOpenedViaCommandToggle;
        ECommonsMain.Dispose();
        wardObserver.Dispose();
    }

    public bool IsTeleporterIpcAvailable()
    {
        try {
            _ = teleporterChatMessageIpc.InvokeFunc();
            return true;
        } catch {
            return false;
        }
    }

    public bool TryTeleportViaIpc(uint aetheryteId, byte subIndex)
    {
        if (!IsTeleporterIpcAvailable()) return false;

        try {
            return teleporterTeleportIpc.InvokeFunc(aetheryteId, subIndex);
        } catch (Exception ex) {
            Svc.Log.Warning(ex, "Teleporter IPC call failed.");
            return false;
        }
    }

    public bool TryGetClosestAetheryte(Level level, out Aetheryte aetheryte)
    {
        if (!level.Territory.IsValid) {
            aetheryte = default;
            return false;
        }

        if (level.Territory.Value.Aetheryte.RowId != 0 && level.Territory.Value.Aetheryte.IsValid) {
            aetheryte = level.Territory.Value.Aetheryte.Value;
            return true;
        }

        return TryGetClosestAetheryte(level.Territory.RowId, new Vector2(level.X, level.Z), out aetheryte);
    }

    public bool TryGetClosestAetheryte(uint territoryId, Vector2 mapCoords, out Aetheryte aetheryte)
    {
        var candidates = GetAetherytesForTerritoryMap(territoryId);
        if (candidates.Count == 0) {
            aetheryte = default;
            return false;
        }

        aetheryte = default;
        var currentDistance = float.MaxValue;

        foreach (var aetheryteRow in candidates) {
            if (!TryGetAetheryteMapMarker(aetheryteRow.RowId, out var marker)) continue;

            var markerCoords = new Vector2(marker.X, marker.Y);
            var distance = (markerCoords - mapCoords).LengthSquared();
            if (distance >= currentDistance) continue;

            currentDistance = distance;
            aetheryte = aetheryteRow;
        }

        if (currentDistance != float.MaxValue) return true;

        // Fallback when map markers cannot be resolved.
        aetheryte = candidates[0];
        return true;
    }

    public bool TryGetClosestAetheryte(uint territoryId, out Aetheryte aetheryte)
    {
        var territory = Territories.GetRowOrDefault(territoryId);
        if (territory == null) {
            aetheryte = default;
            return false;
        }

        if (territory.Value.Aetheryte.RowId != 0 && territory.Value.Aetheryte.IsValid) {
            aetheryte = territory.Value.Aetheryte.Value;
            return true;
        }

        var candidates = GetAetherytesForTerritoryMap(territoryId);
        if (candidates.Count == 0) {
            aetheryte = default;
            return false;
        }

        aetheryte = candidates[0];
        return true;
    }

    public bool TryTeleportToClosestAetheryte(uint territoryId)
    {
        return TryGetClosestAetheryte(territoryId, out var aetheryte) && TryTeleportViaIpc(aetheryte.RowId, 0);
    }

    public bool TryOpenResidentialDistrictAethernet(uint cityTerritoryId)
    {
        if (TaskManager.IsBusy) {
            IsScanningWards = false;
            StopNext = false;
            WardsToScan.Clear();
            TaskManager.Abort();
        }

        if (!TryGetClosestAetheryte(cityTerritoryId, out _)) return false;

        ShowTaskOverlay = true;
        if (IsHousingSelectBlockVisible()) {
            var selectNothingAttempts = new int[1];
            TaskManager.Enqueue(CloseHousingSelectBlock, "Close HousingSelectBlock");
            TaskManager.EnqueueDelay(500);
            TaskManager.Enqueue(WaitForNothingEntry, "Wait for Nothing.", new(timeLimitMS: 30000));
            TaskManager.Enqueue(() => SelectNothingEntry(selectNothingAttempts), "Select Nothing.", new(timeLimitMS: 30000));
        }

        TaskManager.Enqueue(() => TeleportToCityAetheryteIfNeeded(cityTerritoryId), "Teleport to city aetheryte");
        TaskManager.Enqueue(() => IsInTerritoryAndReady(cityTerritoryId), "Wait for city aetheryte arrival", new(timeLimitMS: 120000));
        TaskManager.Enqueue(TargetReachableAetheryte, "Target city aetheryte", new(timeLimitMS: 30000));
        TaskManager.Enqueue(LockOnTarget, "Lock on city aetheryte", new(timeLimitMS: 30000));
        TaskManager.Enqueue(EnableAutomove, "Enable automove", new(timeLimitMS: 30000));
        TaskManager.Enqueue(WaitUntilCloseToTarget, "Wait until close to city aetheryte", new(timeLimitMS: 30000));
        TaskManager.Enqueue(DisableAutomove, "Disable automove", new(timeLimitMS: 30000));
        TaskManager.Enqueue(DisableLockOn, "Disable lock on", new(timeLimitMS: 30000));
        TaskManager.Enqueue(InteractWithTargetedAetheryte, "Interact with city aetheryte", new(timeLimitMS: 30000));
        TaskManager.Enqueue(WaitForResidentialDistrictAethernet, "Wait for Residential District Aethernet", new(timeLimitMS: 30000));
        TaskManager.Enqueue(SelectResidentialDistrictAethernet, "Select Residential District Aethernet", new(timeLimitMS: 30000));
        TaskManager.Enqueue(WaitForGoToSpecifiedWard, "Wait for Go to specified ward", new(timeLimitMS: 30000));
        TaskManager.Enqueue(SelectGoToSpecifiedWard, "Select Go to specified ward", new(timeLimitMS: 30000));
        TaskManager.Enqueue(() => ShowTaskOverlay = false, "Hide task overlay");
        return true;
    }

    private bool TeleportToCityAetheryteIfNeeded(uint cityTerritoryId)
    {
        var currentTerritory = (uint)Svc.ClientState.TerritoryType;
        var alreadyNearTargetAetheryte = currentTerritory == cityTerritoryId && IsNearReachableAetheryte(30f);
        return alreadyNearTargetAetheryte
               || (EzThrottler.Throttle("HousingSweepy.TeleportToCityAetheryte", 1000)
                   && TryTeleportToClosestAetheryte(cityTerritoryId));
    }

    private static bool IsNearReachableAetheryte(float maxDistance)
    {
        var player = Svc.Objects.LocalPlayer;
        if (player == null) return false;

        return Svc.Objects.Any(obj => obj.ObjectKind == ObjectKind.Aetheryte
                                      && obj.IsTargetable
                                      && Vector3.Distance(player.Position, obj.Position) <= maxDistance);
    }

    private static bool IsInTerritoryAndReady(uint territoryId)
    {
        return Svc.Objects.LocalPlayer != null
               && Svc.ClientState.TerritoryType == territoryId
               && !Svc.Condition[ConditionFlag.BetweenAreas]
               && !Svc.Condition[ConditionFlag.BetweenAreas51];
    }

    public bool IsLifestreamIpcAvailable()
    {
        try {
            _ = lifestreamIsBusyIpc.InvokeFunc();
            return true;
        } catch {
            return false;
        }
    }

    public bool TryTeleportToHousingAddressViaLifestream(uint territoryId, int ward, int plot)
    {
        if (ward < 0 || ward > 29 || plot < 0 || plot > 59) return false;
        if (!IsLifestreamIpcAvailable()) return false;
        if (CurrentWorldName is not { Length: > 0 } worldName) return false;

        var territory = residentialTerritories.FirstOrDefault(t => t.TerritoryId == territoryId);
        if (territory.TerritoryId == 0) {
            EnsureResidentialTerritory(territoryId);
            territory = residentialTerritories.FirstOrDefault(t => t.TerritoryId == territoryId);
        }

        if (territory.TerritoryId == 0) return false;

        var address = $"{worldName}, {territory.PlaceName}, W{ward + 1}, P{plot + 1}";

        if (IsHousingSelectBlockVisible()) {
            var selectNothingAttempts = new int[1];
            TaskManager.Enqueue(CloseHousingSelectBlock, "Close HousingSelectBlock");
            TaskManager.EnqueueDelay(500);
            TaskManager.Enqueue(WaitForSelectString, "Wait for SelectString");
            TaskManager.Enqueue(() => SelectNothingEntry(selectNothingAttempts), "Select Nothing.");
            TaskManager.Enqueue(() => ExecuteLifestreamAddress(address), "Travel to housing address");
            return true;
        }

        return ExecuteLifestreamAddress(address);
    }

    private bool ExecuteLifestreamAddress(string address)
    {
        try {
            if (lifestreamIsBusyIpc.InvokeFunc()) {
                Svc.Chat.PrintError("Lifestream is busy.");
                return true;
            }

            lifestreamExecuteCommandIpc.InvokeAction(address);
            return true;
        } catch (Exception ex) {
            Svc.Log.Warning(ex, "Lifestream IPC call failed.");
            return false;
        }
    }

    private static bool IsHousingSelectBlockVisible()
    {
        var addon = Svc.GameGui.GetAddonByName("HousingSelectBlock");
        return addon.IsVisible;
    }

    private static unsafe void CloseHousingSelectBlock()
    {
        var addon = Svc.GameGui.GetAddonByName("HousingSelectBlock");
        if (addon.IsVisible) Callback.Fire((AtkUnitBase*)addon.Address, true, -1);
    }

    private static bool WaitForSelectString()
    {
        var addon = Svc.GameGui.GetAddonByName("SelectString");
        return addon.IsVisible;
    }

    private static bool WaitForNothingEntry()
    {
        return TryGetSelectStringEntry("Nothing.", out _);
    }

    private static bool TargetReachableAetheryte()
    {
        if (Svc.Objects.LocalPlayer == null) return false;
        var aetheryte = GetReachableAetheryte();
        if (aetheryte == null) return false;
        if (Svc.Targets.Target?.Address == aetheryte.Address) return true;

        if (!EzThrottler.Throttle("HousingSweepy.TargetReachableAetheryte", 200)) return false;

        Svc.Targets.Target = aetheryte;
        return true;
    }

    private static unsafe bool InteractWithTargetedAetheryte()
    {
        if (Svc.Objects.LocalPlayer == null) return false;
        var aetheryte = GetReachableAetheryte();
        if (aetheryte == null || Svc.Targets.Target?.Address != aetheryte.Address) return false;
        if (!EzThrottler.Throttle("HousingSweepy.InteractWithTargetedAetheryte", 500)) return false;

        TargetSystem.Instance()->InteractWithObject(aetheryte.Struct(), false);
        return true;
    }

    private static bool LockOnTarget()
    {
        if (Svc.Objects.LocalPlayer == null || Svc.Targets.Target == null) return false;
        if (EzThrottler.Throttle("HousingSweepy.LockOnAetheryte", 200)) {
            Chat.SendMessage("/lockon");
            return true;
        }

        return false;
    }

    private static bool EnableAutomove()
    {
        if (Svc.Objects.LocalPlayer == null) return false;
        if (EzThrottler.Throttle("HousingSweepy.EnableAutomove", 200)) {
            Chat.SendMessage("/automove on");
            return true;
        }

        return false;
    }

    private static bool WaitUntilCloseToTarget()
    {
        var player = Svc.Objects.LocalPlayer;
        var target = Svc.Targets.Target;
        return player != null && target != null && Vector3.Distance(player.Position, target.Position) <= 9f;
    }

    private static bool DisableAutomove()
    {
        if (Svc.Objects.LocalPlayer == null) return false;
        if (EzThrottler.Throttle("HousingSweepy.DisableAutomove", 200)) {
            Chat.SendMessage("/automove off");
            return true;
        }

        return false;
    }

    private static bool DisableLockOn()
    {
        if (Svc.Objects.LocalPlayer == null) return false;
        if (EzThrottler.Throttle("HousingSweepy.DisableLockOn", 200)) {
            Chat.SendMessage("/lockon off");
            return true;
        }

        return false;
    }

    private static unsafe bool SelectResidentialDistrictAethernet()
    {
        if (!TryGetSelectStringEntry(ResidentialDistrictAethernetEntry, out var entry)) return false;
        if (!EzThrottler.Throttle("HousingSweepy.SelectResidentialDistrictAethernet", 500)) return false;

        entry.Select();
        return true;
    }

    private static bool WaitForResidentialDistrictAethernet()
    {
        return TryGetSelectStringEntry(ResidentialDistrictAethernetEntry, out _);
    }

    private static unsafe bool SelectGoToSpecifiedWard()
    {
        if (!TryGetSelectStringEntry(GoToSpecifiedWardEntry, out var entry)) return false;
        if (!EzThrottler.Throttle("HousingSweepy.SelectGoToSpecifiedWard", 500)) return false;

        entry.Select();
        return true;
    }

    private static bool WaitForGoToSpecifiedWard()
    {
        return TryGetSelectStringEntry(GoToSpecifiedWardEntry, out _);
    }

    private static IGameObject? GetReachableAetheryte()
    {
        var player = Svc.Objects.LocalPlayer;
        if (player == null) return null;

        return Svc.Objects
            .Where(obj => obj.ObjectKind == ObjectKind.Aetheryte && obj.IsTargetable)
            .OrderBy(obj => Vector3.DistanceSquared(player.Position, obj.Position))
            .FirstOrDefault(obj => Vector3.Distance(player.Position, obj.Position) < 30f);
    }

    private static unsafe bool TryGetSelectStringEntry(string text, out AddonMaster.SelectString.Entry entry)
    {
        entry = default;

        var addon = Svc.GameGui.GetAddonByName("SelectString");
        if (!addon.IsVisible) return false;

        var selectString = new AddonMaster.SelectString((AddonSelectString*)addon.Address);
        if (!selectString.IsAddonReady) return false;

        foreach (var candidate in selectString.Entries) {
            if (!candidate.Text.Equals(text, StringComparison.OrdinalIgnoreCase)
                && !candidate.Text.EndsWith(text, StringComparison.OrdinalIgnoreCase)) continue;

            entry = candidate;
            return true;
        }

        return false;
    }

    private static unsafe bool SelectNothingEntry(int[] attempts)
    {
        attempts[0]++;

        if (!TryGetSelectStringEntry("Nothing.", out var entry)) return attempts[0] >= 3;
        if (!EzThrottler.Throttle("HousingSweepy.SelectNothing", 500)) return false;

        entry.Select();
        return true;
    }

    public bool TryGetCityTerritoryId(string tabLabel, out uint territoryId)
    {
        switch (tabLabel)
        {
            case "Limsa":
                territoryId = 129;
                return true;
            case "Ul'dah":
                territoryId = 130;
                return true;
            case "Gridania":
                territoryId = 132;
                return true;
            case "Foundation":
                territoryId = 418;
                return true;
            case "Kugane":
                territoryId = 628;
                return true;
            default:
                territoryId = 0;
                return false;
        }
    }

    public bool IsPlayerFarFromClosestAetheryte(uint territoryId, float minDistance)
    {
        var player = Svc.Objects.LocalPlayer;
        if (player == null) return true;

        var playerCoords = new Vector2(player.Position.X, player.Position.Z);
        if (!TryGetClosestAetheryte(territoryId, playerCoords, out var aetheryte)) return true;
        if (!TryGetAetheryteMapMarker(aetheryte.RowId, out var marker)) return true;

        var markerCoords = new Vector2(marker.X, marker.Y);
        var distance = Vector2.Distance(playerCoords, markerCoords);
        return distance > minDistance;
    }

    public List<Aetheryte> GetAetherytesForTerritoryMap(uint territoryId)
    {
        var territory = Territories.GetRowOrDefault(territoryId);
        if (territory == null) return [];

        var mapId = territory.Value.Map.RowId;
        return Svc.Data.GetExcelSheet<Aetheryte>()
            .Where(row => row.Territory.RowId == territoryId && row.Map.RowId == mapId)
            .OrderBy(row => row.RowId)
            .ToList();
    }

    private bool TryGetAetheryteMapMarker(uint aetheryteId, out MapMarker marker)
    {
        foreach (var subrowCollection in Svc.Data.Excel.GetSubrowSheet<MapMarker>()) {
            foreach (var mapMarker in subrowCollection) {
                if (mapMarker.DataType != AetheryteMapMarkerDataType) continue;
                if (mapMarker.DataKey.RowId == 0 || mapMarker.DataKey.RowId != aetheryteId) continue;

                marker = mapMarker;
                return true;
            }
        }

        marker = default;
        return false;
    }

    private void OnHousingSelectBlock(AddonEvent type, AddonArgs args)
    {
        var isClosing = type == AddonEvent.PreFinalize;

        if (isClosing)
            window.CloseIfOpenedViaAddonSetupLifecycle();
        else if (!window.IsOpen) window.MarkOpenedViaAddonSetupLifecycle();
    }

    public void OnCommand(string command, string args)
    {
        var arg = args.Trim().ToLowerInvariant();
        switch (arg) {
            case "reset":
                ResetSeenHouses();
                break;
            case "tpdebug":
            case "teleportdebug":
                teleportDebugWindow.IsOpen = !teleportDebugWindow.IsOpen;
                break;
            default:
                window.MarkOpenedViaCommandToggle();
                break;
        }
    }

    public void ResetSeenHouses()
    {
        SeenHousesByWorldAndTerritory.Clear();
        WardsByWorldAndTerritory.Clear();

        try {
            wardObserver?.ResetSweep();
        } catch (Exception ex) {
            Svc.Log.Error(ex, "Error resetting ward observer sweep");
        }

        Svc.NotificationManager.AddNotification(new Notification
        {
            Type = NotificationType.Success,
            Content = "Seen houses have been reset."
        });
    }

    public void CancelCurrentTasks()
    {
        IsScanningWards = false;
        ShowTaskOverlay = false;
        StopNext = false;
        WardsToScan.Clear();
        TaskManager.Abort();
        Chat.SendMessage("/automove off");
        Chat.SendMessage("/lockon off");
    }

    // Maybe one should remove this but I had it for debugging but removed the code in it...
    public void Commit()
    {
        var territoryId = (uint) Svc.ClientState.TerritoryType;
        var worldId = Svc.PlayerState.CurrentWorld.RowId;

        var key = new WordTerritory(worldId, territoryId);

        if (LastCommittedZoneAndWorld == null || !LastCommittedZoneAndWorld.Equals(key)) {
            LastCommittedZoneAndWorld = key;
            // Caches are world-scoped now; no need to clear on world change.
        }
    }

    public void ScanForSeenHouses()
    {
        var territoryId = (uint) Svc.ClientState.TerritoryType;
        var wards = GetWardsForTerritory(territoryId);
        StopNext = false;
        IsScanningWards = false;
        TaskManager.Abort();
        WardsToScan.Clear();

        // TaskManager.BeginStack();

        for (var wardIndex = 0; wardIndex < 30; wardIndex++) {
            var wardNumber = wardIndex + 1;
            var wardInfo = wards.FirstOrDefault(w => w.WardNumber == wardNumber);
            if (wardInfo == null) {
                QueueWardForScan(wardIndex);
            }
        }

        QueueNext();

        // TaskManager.InsertStack();
    }

    private unsafe bool EnqueueNextWard(int index)
    {
        var addon = Svc.GameGui.GetAddonByName("HousingSelectBlock");
        if (!addon.IsVisible) {
            TaskManager.Abort();
            Svc.NotificationManager.AddNotification(new Notification
                { Type = NotificationType.Error, Content = "Housing Select Block is not open. Cannot scan houses." });
            return false;
        }

        if (EzThrottler.Throttle("ScanHouse", 100)) {
            Callback.Fire((AtkUnitBase*) addon.Address, true, 1, index);

            TaskManager.InsertDelay(100);

            return true;
        }

        return false;
    }

    public bool QueueWardForScan(int wardNumber)
    {
        if (!WardsToScan.Contains(wardNumber)) {
            WardsToScan.Enqueue(wardNumber);
            return true;
        }

        return false;
    }

    public void QueueNext(bool fromCallback = false)
    {
        if (StopNext) {
            StopNext = false;
            IsScanningWards = false;

            TaskManager.Abort();
            return;
        }

        if (WardsToScan.Count > 0) {
            var nextWard = WardsToScan.Dequeue();

            if (!IsScanningWards) IsScanningWards = true;

            TaskManager.Insert(() => EnqueueNextWard(nextWard), $"Scan Ward {nextWard}");
        } else {
            IsScanningWards = false;
            if (fromCallback)
                Svc.NotificationManager.AddNotification(new Notification
                {
                    Type = NotificationType.Success,
                    Content = "Finished scanning all wards."
                });
        }
    }

    public unsafe void OpenHouseListForWard(int ward)
    {
        if (ward < 0 || ward > 29) {
            Svc.Log.Error($"Invalid ward number: {ward}");
            return;
        }

        // Check if we've already scanned this ward
        var territoryId = (uint) Svc.ClientState.TerritoryType;
        var existingWard = GetWardsForTerritory(territoryId).FirstOrDefault(w => w.WardNumber == ward);
        if (existingWard != null && existingWard.HasBeenSeen()) {
            Svc.Log.Info($"Ward {ward} has already been scanned. Skipping.");
            return;
        }

        var addon = Svc.GameGui.GetAddonByName("HousingSelectBlock");
        if (!addon.IsVisible) return;

        if (StopNext) {
            StopNext = false;
            IsScanningWards = false;

            TaskManager.Abort();
            return;
        }

        if (EzThrottler.Throttle("ScanHouse", 100)) Callback.Fire((AtkUnitBase*) addon.Address, true, 1, ward);
    }

    private uint GetWorldBucketId()
    {
        // Prefer lobby (works even while zoning), fall back to PlayerState.
        return CurrentWorldId ?? Svc.PlayerState.CurrentWorld.RowId;
    }

    public Dictionary<int, List<HouseInfoEntry>> GetSeenHousesForTerritory(uint territoryId)
    {
        var worldId = GetWorldBucketId();

        if (!SeenHousesByWorldAndTerritory.TryGetValue(worldId, out var byTerritory)) {
            byTerritory = new Dictionary<uint, Dictionary<int, List<HouseInfoEntry>>>();
            SeenHousesByWorldAndTerritory[worldId] = byTerritory;
        }

        if (!byTerritory.TryGetValue(territoryId, out var seen)) {
            seen = new Dictionary<int, List<HouseInfoEntry>>();
            byTerritory[territoryId] = seen;
        }

        return seen;
    }

    public List<WardInfo> GetWardsForTerritory(uint territoryId)
    {
        var worldId = GetWorldBucketId();

        if (!WardsByWorldAndTerritory.TryGetValue(worldId, out var byTerritory)) {
            byTerritory = new Dictionary<uint, List<WardInfo>>();
            WardsByWorldAndTerritory[worldId] = byTerritory;
        }

        if (!byTerritory.TryGetValue(territoryId, out var wards)) {
            wards = new List<WardInfo>();
            byTerritory[territoryId] = wards;
        }

        return wards;
    }

    public bool HasAnySeenHouses()
    {
        var worldId = GetWorldBucketId();
        if (!SeenHousesByWorldAndTerritory.TryGetValue(worldId, out var byTerritory)) return false;

        foreach (var territory in byTerritory.Values) {
            if (territory.Count > 0) return true;
        }

        return false;
    }

    private void InitializeResidentialTerritories()
    {
        var entries = new[]
        {
            new { TabLabel = "Ul'dah", PlaceName = "The Goblet" },
            new { TabLabel = "Limsa", PlaceName = "Mist" },
            new { TabLabel = "Gridania", PlaceName = "The Lavender Beds" },
            new { TabLabel = "Foundation", PlaceName = "Empyreum" },
            new { TabLabel = "Kugane", PlaceName = "Shirogane" }
        };

        foreach (var entry in entries) {
            var territory = Territories.FirstOrDefault(t => t.PlaceName.ValueNullable?.Name.ToString() == entry.PlaceName);
            if (territory.RowId == 0) {
                Svc.Log.Warning($"Could not find territory for {entry.PlaceName}.");
                continue;
            }

            residentialTerritories.Add(new ResidentialTerritory(territory.RowId, entry.TabLabel, entry.PlaceName));
        }
    }

    internal void EnsureResidentialTerritory(uint territoryId)
    {
        if (residentialTerritories.Any(t => t.TerritoryId == territoryId)) return;

        var territory = Territories.GetRowOrDefault(territoryId);
        if (territory == null) {
            Svc.Log.Warning($"Could not find territory for ID {territoryId}.");
            return;
        }

        var placeName = territory.Value.PlaceName.ValueNullable?.Name.ToString() ?? "Unknown";
        if (placeName == "Unknown") {
            Svc.Log.Warning($"Could not find place name for territory ID {territoryId}.");
        }

        var existingIndex = residentialTerritories.FindIndex(t => t.PlaceName == placeName);
        if (existingIndex >= 0) {
            var existing = residentialTerritories[existingIndex];
            residentialTerritories[existingIndex] = new ResidentialTerritory(territoryId, existing.TabLabel, existing.PlaceName);
            return;
        }

        var tabLabel = GetTabLabelForPlaceName(placeName);
        residentialTerritories.Add(new ResidentialTerritory(territoryId, tabLabel, placeName));
    }

    public void SelectTerritory(uint territoryId)
    {
        window.SelectTerritory(territoryId);
    }

    private static string GetTabLabelForPlaceName(string placeName)
    {
        return placeName switch
        {
            "The Goblet" => "Ul'dah",
            "Mist" => "Limsa",
            "The Lavender Beds" => "Gridania",
            "Empyreum" => "Foundation",
            "Shirogane" => "Kugane",
            _ => placeName
        };
    }

    public record HouseInfoEntry(ushort HouseNumber, uint HousePrice, bool IsOwned, string EstateOwnerName = "")
    {
        public string TypeShort => HousePrice switch
        {
            < 6_000_000 => "S",
            < 25_000_000 => "M",
            _ => "L"
        };
    }

    public class WordTerritory : IEquatable<WordTerritory>, IComparable<WordTerritory>
    {
        public readonly uint TerritoryId;
        public readonly string TerritoryName;
        public readonly uint WorldId;
        public readonly string WorldName;

        public WordTerritory(uint worldId, uint territoryId)
        {
            WorldId = worldId;
            TerritoryId = territoryId;
            WorldName = Instance.Worlds.GetRowOrDefault(worldId)?.Name.ToString() ?? "Unknown";
            TerritoryName = Instance.Territories.GetRowOrDefault(territoryId)?.PlaceName.ValueNullable?.Name.ToString() ?? "Unknown";
        }

        public int CompareTo(WordTerritory? other)
        {
            if (other == null) return 1;

            // only ids
            var worldComparison = WorldId.CompareTo(other.WorldId);
            if (worldComparison != 0) return worldComparison;

            return TerritoryId.CompareTo(other.TerritoryId);
        }

        public bool Equals(WordTerritory? other)
        {
            if (other == null) return false;

            return WorldId == other.WorldId && TerritoryId == other.TerritoryId;
        }
    }

    public readonly record struct ResidentialTerritory(uint TerritoryId, string TabLabel, string PlaceName);
}

// wards, 1-30 wards; 30 houses | 30 subdivision houses = 60 houses per ward

public class WardInfo
{
    public List<HouseInfo> Houses =
    [
        ..Enumerable.Range(1, 60).Select(houseIndex => new HouseInfo(houseIndex, false)).ToList()
    ];

    public int WardNumber;

    public WardInfo(int wardNumber)
    {
        WardNumber = wardNumber;
    }

    // Setter

    public bool HasBeenSeen()
    {
        return Houses.Any(h => h.HasBeenSeen);
    }

    public void ResetSeen()
    {
        foreach (var h in Houses) {
            h.HasBeenSeen = false;
            h.Price = 0;
        }
    }

    public void UpdateHouseInfo(ushort @ushort, HouseInfoEntry houseInfoEntry)
    {
        var house = Houses.FirstOrDefault(h => h.HouseNumber == @ushort);
        if (house != null) {
            house.HasBeenSeen = true;
            house.Price = houseInfoEntry.HousePrice;
        }
    }
}

public class HouseInfo : IEquatable<HouseInfo>
{
    public bool HasBeenSeen;
    public int HouseNumber;
    public bool IsSubdivision;
    public uint Price;

    public HouseInfo(int houseNumber, bool hasBeenSeen)
    {
        HouseNumber = houseNumber;
        IsSubdivision = houseNumber > 30;
        HasBeenSeen = hasBeenSeen;
    }

    public bool Equals(HouseInfo? other)
    {
        if (other == null) return false;

        return HouseNumber == other.HouseNumber && IsSubdivision == other.IsSubdivision;
    }
}
