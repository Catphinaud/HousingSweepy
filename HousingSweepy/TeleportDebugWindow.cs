using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using ECommons.DalamudServices;
using Lumina.Excel.Sheets;

namespace HousingSweepy;

public class TeleportDebugWindow : Window
{
    private readonly (string CityName, uint TerritoryId)[] cityTerritories =
    [
        ("Limsa", 129),
        ("Ul'dah", 130),
        ("Gridania", 132),
        ("Foundation", 418),
        ("Kugane", 628)
    ];

    private readonly Plugin plugin;
    private uint territoryIdInput;
    private float mapXInput;
    private float mapZInput;

    public TeleportDebugWindow(Plugin plugin) : base("HousingSweepy Teleport Debug")
    {
        this.plugin = plugin;
        RespectCloseHotkey = true;
        IsOpen = false;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(520, 320),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };
    }

    public override void OnOpen()
    {
        territoryIdInput = Svc.ClientState.TerritoryType;
        mapXInput = 0f;
        mapZInput = 0f;
    }

    public override void Draw()
    {
        ImGui.TextColored(
            plugin.IsTeleporterIpcAvailable() ? new Vector4(0.06f, 0.70f, 0.50f, 1.0f) : new Vector4(0.86f, 0.22f, 0.22f, 1.0f),
            plugin.IsTeleporterIpcAvailable() ? "Teleporter IPC: Available" : "Teleporter IPC: Unavailable");

        ImGui.Separator();
        DrawCityTeleportSection();
        ImGui.Separator();

        ImGui.SetNextItemWidth(140f);
        ImGui.InputUInt("Territory ID", ref territoryIdInput);
        if (ImGui.Button("Use Current Territory")) {
            territoryIdInput = Svc.ClientState.TerritoryType;
        }

        ImGui.Separator();

        ImGui.SetNextItemWidth(120f);
        ImGui.InputFloat("Map X", ref mapXInput);
        ImGui.SetNextItemWidth(120f);
        ImGui.InputFloat("Map Z", ref mapZInput);

        ImGui.Separator();

        var territoryId = territoryIdInput < 0 ? 0u : (uint) territoryIdInput;
        DrawClosestAetheryteSection(territoryId);
        DrawAetheryteListSection(territoryId);
    }

    private void DrawCityTeleportSection()
    {
        ImGui.Text("City Teleport Debug");
        var currentTerritoryId = (uint)Svc.ClientState.TerritoryType;
        ImGui.TextDisabled($"Current Territory: {currentTerritoryId}");
        ImGui.TextDisabled("Uses current territory ID to show which city/district matches.");

        foreach (var (city, territoryId) in cityTerritories) {
            var isCurrent = currentTerritoryId == territoryId;

            if (isCurrent) {
                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.20f, 0.50f, 0.70f, 1.0f));
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.26f, 0.56f, 0.76f, 1.0f));
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.16f, 0.46f, 0.66f, 1.0f));
            }
            if (ImGui.Button($"Teleport {city}##citytp{territoryId}", new Vector2(180, 0))) {
                if (!plugin.TryTeleportToClosestAetheryte(territoryId)) {
                    Svc.Chat.PrintError($"Teleport IPC failed for {city}.");
                }
            }
            if (isCurrent) {
                ImGui.PopStyleColor(3);
            }

            ImGui.SameLine();
            ImGui.Text(isCurrent
                ? $"Territory {territoryId}  [CURRENT]"
                : $"Territory {territoryId}");
        }
    }

    private void DrawClosestAetheryteSection(uint territoryId)
    {
        ImGui.Text("Closest Aetheryte (Map Marker Distance)");

        if (plugin.TryGetClosestAetheryte(territoryId, new Vector2(mapXInput, mapZInput), out var closest)) {
            DrawAetheryteRow("Closest", closest);
        } else {
            ImGui.TextColored(new Vector4(0.86f, 0.22f, 0.22f, 1.0f), "No aetheryte resolved for this map.");
        }
    }

    private void DrawAetheryteListSection(uint territoryId)
    {
        ImGui.Separator();
        ImGui.Text("Aetherytes In Territory Map");

        var entries = plugin.GetAetherytesForTerritoryMap(territoryId);
        if (entries.Count == 0) {
            ImGui.Text("No map-matched aetherytes found.");
            return;
        }

        foreach (var aetheryte in entries) {
            DrawAetheryteRow($"#{aetheryte.RowId}", aetheryte);
        }
    }

    private void DrawAetheryteRow(string label, Aetheryte aetheryte)
    {
        var placeName = aetheryte.PlaceName.ValueNullable?.Name.ToString() ?? "Unknown";
        var cityName = GetCityNameForTerritory(aetheryte.Territory.RowId);
        ImGui.Text($"{label}: {aetheryte.RowId} - {cityName} ({placeName})");
        ImGui.SameLine();
        if (ImGui.SmallButton($"Teleport##{label}{aetheryte.RowId}")) {
            if (!plugin.TryTeleportViaIpc(aetheryte.RowId, 0)) {
                Svc.Chat.PrintError($"Teleport IPC failed for aetheryte {aetheryte.RowId}.");
            }
        }
    }

    private string GetCityNameForTerritory(uint territoryId)
    {
        foreach (var territory in plugin.ResidentialTerritories) {
            if (territory.TerritoryId == territoryId) return territory.TabLabel;
        }

        return "Unknown City";
    }
}
