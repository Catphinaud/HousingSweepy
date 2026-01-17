using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Command;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using ECommons;
using ECommons.Automation;
using ECommons.Automation.NeoTaskManager;
using ECommons.Commands;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using VT = FFXIVClientStructs.FFXIV.Component.GUI.ValueType;

namespace HousingSweepy;

public sealed class Plugin : IDalamudPlugin
{
    public IDalamudPluginInterface PluginInterface { get; }

    public static Plugin Instance { get; private set; } = null!;

    private const string CommandName = "/sweepy";

    public static TaskManager TaskManager = null!;

    private readonly WindowSystem _windowSystem;
    internal readonly ExcelSheet<TerritoryType> Territories;

    // state
    private readonly WardObserver wardObserver;
    private readonly MainWindow window;
    internal readonly ExcelSheet<World> Worlds;


    internal bool _disposed;

    public bool IsScanningWards;

    public WordTerritory? LastCommittedZoneAndWorld;

    public Dictionary<uint, Dictionary<int, List<HouseInfoEntry>>> SeenHousesByTerritory = new();

    public bool StopNext;

    public Dictionary<uint, List<WardInfo>> WardsByTerritory = new();

    public Queue<int> WardsToScan = new();

    private readonly List<ResidentialTerritory> residentialTerritories = new();
    public IReadOnlyList<ResidentialTerritory> ResidentialTerritories => residentialTerritories;

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        Instance = this;
        PluginInterface = pluginInterface;

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
        PluginInterface.UiBuilder.Draw += _windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += window.MarkOpenedViaCommandToggle;
        Svc.Commands.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Toggle the HousingSweepy window.\n'/sweepy reset' to reset seen houses."
        });

        Svc.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "HousingSelectBlock", OnHousingSelectBlock);
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "HousingSelectBlock", OnHousingSelectBlock);
    }

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;

        Svc.Commands.RemoveHandler(CommandName);
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "HousingSelectBlock", OnHousingSelectBlock);
        Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "HousingSelectBlock", OnHousingSelectBlock);

        PluginInterface.UiBuilder.Draw -= _windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= window.MarkOpenedViaCommandToggle;
        ECommonsMain.Dispose();
        wardObserver.Dispose();
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
        if (args.Trim().ToLower() == "reset")
            ResetSeenHouses();
        else
            window.MarkOpenedViaCommandToggle();
    }

    public void ResetSeenHouses()
    {
        SeenHousesByTerritory.Clear();
        WardsByTerritory.Clear();

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

    internal void SelectTerritory(uint territoryId)
    {
        window.SelectTerritory(territoryId);
    }

    // Maybe one should remove this but I had it for debugging but removed the code in it...
    public void Commit()
    {
        var territoryId = (uint) Svc.ClientState.TerritoryType;
        var worldId = Svc.PlayerState.CurrentWorld.RowId;

        var key = new WordTerritory(worldId, territoryId);

        if (LastCommittedZoneAndWorld == null || !LastCommittedZoneAndWorld.Equals(key)) {
            if (LastCommittedZoneAndWorld != null && LastCommittedZoneAndWorld.WorldId != worldId) {
                SeenHousesByTerritory.Clear();
                WardsByTerritory.Clear();
            }

            LastCommittedZoneAndWorld = key;

            // Keep per-territory seen houses; update current key only.
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

    public record HouseInfoEntry(ushort HouseNumber, uint HousePrice, bool IsOwned)
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

    public Dictionary<int, List<HouseInfoEntry>> GetSeenHousesForTerritory(uint territoryId)
    {
        if (!SeenHousesByTerritory.TryGetValue(territoryId, out var seen)) {
            seen = new Dictionary<int, List<HouseInfoEntry>>();
            SeenHousesByTerritory[territoryId] = seen;
        }

        return seen;
    }

    public List<WardInfo> GetWardsForTerritory(uint territoryId)
    {
        if (!WardsByTerritory.TryGetValue(territoryId, out var wards)) {
            wards = new List<WardInfo>();
            WardsByTerritory[territoryId] = wards;
        }

        return wards;
    }

    public bool HasAnySeenHouses()
    {
        foreach (var territory in SeenHousesByTerritory.Values) {
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
