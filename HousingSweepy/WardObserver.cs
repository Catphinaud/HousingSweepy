using Dalamud.Hooking;
using Dalamud.Utility.Signatures;
using ECommons.DalamudServices;

namespace HousingSweepy;

// Borrowed from https://github.com/zhudotexe/FFXIV_PaissaHouse

public record LandIdent(short LandId, short WardNumber, short TerritoryTypeId, short WorldId);

public class HouseInfoEntry
{
    public uint HousePrice;
    public HousingFlags InfoFlags;
}

[Flags]
public enum HousingFlags : byte
{
    PlotOwned = 1 << 0,
    VisitorsAllowed = 1 << 1,
    HasSearchComment = 1 << 2,
    HouseBuilt = 1 << 3,
    OwnedByFC = 1 << 4
}

public class HousingWardInfo
{
    public required HouseInfoEntry[] HouseInfoEntries;
    public required LandIdent LandIdent;

    public static unsafe HousingWardInfo Read(IntPtr dataPtr)
    {
        using var unmanagedMemoryStream = new UnmanagedMemoryStream((byte*) dataPtr.ToPointer(), 2664L);
        using var binaryReader = new BinaryReader(unmanagedMemoryStream);
        // wardInfo.LandIdent = LandIdent.ReadFromBinaryReader(binaryReader);
        var landIdent = new LandIdent(
            binaryReader.ReadInt16(),
            binaryReader.ReadInt16(),
            binaryReader.ReadInt16(),
            binaryReader.ReadInt16()
        );
        var houseInfoEntries = new HouseInfoEntry[60];

        for (var i = 0; i < 60; i++) {
            var infoEntry = new HouseInfoEntry
            {
                HousePrice = binaryReader.ReadUInt32(),
                InfoFlags = (HousingFlags) binaryReader.ReadByte()
            };
            // for (var j = 0; j < 3; j++) infoEntry.HouseAppeals[j] = binaryReader.ReadSByte();
            binaryReader.ReadBytes(3); // skip appeals for now
            //infoEntry.EstateOwnerName = Encoding.UTF8.GetString(binaryReader.ReadBytes(32)).TrimEnd(new char[1]);
            binaryReader.ReadBytes(32); // skip owner name
            houseInfoEntries[i] = infoEntry;

            // if a house is unowned, the ownerName can be literally anything, so set it to empty string
            if ((infoEntry.InfoFlags & HousingFlags.PlotOwned) == 0) {
                // infoEntry.EstateOwnerName = "";
            }
        }

        // 0x2440 Purchase Type
        binaryReader.ReadByte();
        // 0x2441 - padding byte?
        binaryReader.ReadByte();
        // 0x2442 Tenant Type
        binaryReader.ReadByte();
        // 0x2443 - padding byte?
        binaryReader.ReadByte();
        // 0x2444 - 0x2447 appear to be padding bytes

        return new HousingWardInfo
        {
            LandIdent = landIdent,
            HouseInfoEntries = houseInfoEntries
        };
    }
}

public unsafe class WardObserver
{
    private readonly Plugin plugin;
    [Signature("40 55 53 41 54 41 55 41 57 48 8D AC 24 ?? ?? ?? ?? B8", DetourName = nameof(OnHousingWardInfo))]
#pragma warning disable CS0649 // Field is never assigned to, and will always have its default value
    private Hook<HandleHousingWardInfoDelegate>? housingWardInfoHook;
#pragma warning restore CS0649 // Field is never assigned to, and will always have its default value

    public WardObserver(Plugin plugin)
    {
        this.plugin = plugin;
        Svc.Hook.InitializeFromAttributes(this);
        housingWardInfoHook?.Enable();
    }

    public int CurrentTerritoryTypeId { get; set; } = -1;


    public int DistrictId { get; private set; }
    public int WorldId { get; private set; }
    public DateTime SweepTime { get; private set; }
    public HashSet<int> SeenWardNumbers { get; } = new();

    public void Dispose()
    {
        housingWardInfoHook?.Dispose();
    }


    public void OnHousingWardInfo(
        void* agentBase,
        IntPtr dataPtr
    )
    {
        housingWardInfoHook!.Original(agentBase, dataPtr);

        if (CurrentTerritoryTypeId != Svc.ClientState.TerritoryType) {
            CurrentTerritoryTypeId = Svc.ClientState.TerritoryType;
            Svc.Log.Debug($"Updated CurrentTerritoryTypeId to {CurrentTerritoryTypeId}");
            plugin.Commit();

            Svc.Chat.Print($"Updated CurrentDistrict to {plugin.Territories.GetRow((uint) CurrentTerritoryTypeId).PlaceName.Value.Name}");
        }

        var wardInfo = HousingWardInfo.Read(dataPtr);
        Svc.Log.Debug($"Got HousingWardInfo for ward: {wardInfo.LandIdent.WardNumber} territory: {wardInfo.LandIdent.TerritoryTypeId}");

        // if the current wardinfo is for a different district than the last swept one, print the header
        // or if the last sweep was > 10m ago
        if (ShouldStartNewSweep(wardInfo))
            // reset last sweep info to the current sweep
            StartDistrictSweep(wardInfo);

        // if we've seen this ward already, ignore it
        if (ContainsSweep(wardInfo)) {
            Svc.Log.Debug($"Skipped processing HousingWardInfo for ward: {wardInfo.LandIdent.WardNumber} because we have seen it already");
        } else {
            // add the ward to this sweep
            AddSweep(wardInfo);

            var territoryId = (uint) wardInfo.LandIdent.TerritoryTypeId;
            plugin.SelectTerritory(territoryId);
            var seen = new List<Plugin.HouseInfoEntry>();
            var seenByTerritory = plugin.GetSeenHousesForTerritory(territoryId);
            if (seenByTerritory.ContainsKey(wardInfo.LandIdent.WardNumber)) seenByTerritory.Remove(wardInfo.LandIdent.WardNumber);

            seenByTerritory.Add(wardInfo.LandIdent.WardNumber, seen);

            var houseList = seen;
            for (ushort i = 0; i < wardInfo.HouseInfoEntries.Length; i++) {
                var houseInfoEntry = wardInfo.HouseInfoEntries[i];
                if (!houseList.Exists(h => h.HouseNumber == i))
                    houseList.Add(new Plugin.HouseInfoEntry(i, houseInfoEntry.HousePrice, (houseInfoEntry.InfoFlags & HousingFlags.PlotOwned) != 0));
            }

            var wards = plugin.GetWardsForTerritory(territoryId);
            var wi = wards.Find(w => w.WardNumber == wardInfo.LandIdent.WardNumber);
            if (wi == null) {
                wi = new WardInfo(wardInfo.LandIdent.WardNumber);
                wards.Add(wi);
            }

            if (wi != null)
                for (ushort i = 0; i < wardInfo.HouseInfoEntries.Length; i++) {
                    var houseInfoEntry = wardInfo.HouseInfoEntries[i];
                    wi.UpdateHouseInfo(i, houseInfoEntry);
                }


            Svc.Log.Debug($"Done processing HousingWardInfo for ward: {wardInfo.LandIdent.WardNumber}");
        }

        plugin.QueueNext(true);
    }


    /// <summary>
    ///     Returns whether or not a received WardInfo should start a new sweep.
    /// </summary>
    public bool ShouldStartNewSweep(HousingWardInfo wardInfo)
        => wardInfo.LandIdent.WorldId != WorldId
           || wardInfo.LandIdent.TerritoryTypeId != DistrictId
           || SweepTime < DateTime.Now - TimeSpan.FromMinutes(10);

    /// <summary>
    ///     Sets the housing state to a sweep of the district of the given WardInfo.
    /// </summary>
    public void StartDistrictSweep(HousingWardInfo wardInfo)
    {
        WorldId = wardInfo.LandIdent.WorldId;
        DistrictId = wardInfo.LandIdent.TerritoryTypeId;
        SeenWardNumbers.Clear();
        SweepTime = DateTime.Now;
    }

    /// <summary>
    ///     Returns whether the ward represented by the given wardinfo has been seen in the current sweep.
    /// </summary>
    public bool ContainsSweep(HousingWardInfo wardInfo)
        => SeenWardNumbers.Contains(wardInfo.LandIdent.WardNumber);

    /// <summary>
    ///     Adds sweep information for the given wardinfo to the current sweep.
    /// </summary>
    public void AddSweep(HousingWardInfo wardInfo)
    {
        if (ContainsSweep(wardInfo)) return;

        SeenWardNumbers.Add(wardInfo.LandIdent.WardNumber);

        // add open houses to the internal list
        for (ushort i = 0; i < wardInfo.HouseInfoEntries.Length; i++) {
            var houseInfoEntry = wardInfo.HouseInfoEntries[i];
            if ((houseInfoEntry.InfoFlags & HousingFlags.PlotOwned) == 0) {
                // OpenHouses.Add(new OpenHouse((ushort) wardInfo.LandIdent.WardNumber, i, houseInfoEntry));
            }
        }
    }

    /// <summary>
    ///     Resets the state such that no wards have been seen.
    /// </summary>
    public void ResetSweep()
    {
        WorldId = -1;
        DistrictId = -1;
        SeenWardNumbers.Clear();
    }

    private delegate void HandleHousingWardInfoDelegate(
        void* agentBase,
        IntPtr housingWardInfoPtr
    );
}
