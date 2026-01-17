using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using ECommons.DalamudServices;

namespace HousingSweepy;

public class MainWindow : Window
{
    private readonly Plugin plugin;
    private bool showSmallHouses = true;
    private bool showMediumHouses = true;
    private bool showLargeHouses = true;

    private OpenSource lastOpenSource = OpenSource.Unknown;

    private readonly Dictionary<uint, int> selectedWardByTerritory = new();
    private uint selectedTerritoryId;
    private bool hasSelectedTerritory;

    public MainWindow(Plugin plugin) : base("HousingSweepy")
    {
        this.plugin = plugin;
        RespectCloseHotkey = true;
        IsOpen = false;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(596, 669),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };
    }

    public void MarkOpenedViaCommandToggle()
    {
        lastOpenSource = OpenSource.CommandToggle;
        IsOpen = true;
    }

    public void MarkOpenedViaAddonSetupLifecycle()
    {
        lastOpenSource = OpenSource.AddonSetupLifecycle;
        IsOpen = true;
    }

    public void CloseIfOpenedViaAddonSetupLifecycle()
    {
        if (lastOpenSource == OpenSource.AddonSetupLifecycle) IsOpen = false;
    }

    public override void OnClose()
    {
        lastOpenSource = OpenSource.Unknown;
        base.OnClose();
    }

    public override void Draw()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8, 8));
        DrawHeader();
        DrawMainLayout();
        ImGui.PopStyleVar();
    }

    private void DrawHeader()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 10.0f);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(10, 6));

        var actionBtnSize = new Vector2(180, 36);

        var headerFlags = ImGuiTableFlags.SizingStretchProp;
        if (ImGui.BeginTable("HeaderTable", 2, headerFlags)) {
            ImGui.TableSetupColumn("Title", ImGuiTableColumnFlags.WidthStretch, 1);
            ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed, actionBtnSize.X * 2 + 12);
            ImGui.TableNextRow();

            ImGui.TableSetColumnIndex(0);
            ImGui.Text("HousingSweepy");
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.55f, 0.62f, 0.70f, 1.0f), plugin.IsScanningWards ? "Scanning…" : "Idle");

            ImGui.TableSetColumnIndex(1);

            if (plugin.IsScanningWards) {
                var stopColor = new Vector4(0.86f, 0.22f, 0.22f, 1.0f);
                ImGui.PushStyleColor(ImGuiCol.Button, stopColor);
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered,
                    new Vector4(MathF.Min(stopColor.X + 0.06f, 1f), MathF.Min(stopColor.Y + 0.06f, 1f), MathF.Min(stopColor.Z + 0.06f, 1f), 1f));
                ImGui.PushStyleColor(ImGuiCol.ButtonActive,
                    new Vector4(MathF.Max(stopColor.X - 0.05f, 0f), MathF.Max(stopColor.Y - 0.05f, 0f), MathF.Max(stopColor.Z - 0.05f, 0f), 1f));

                if (ImGui.Button("Stop Scan", actionBtnSize)) {
                    plugin.IsScanningWards = false;
                    plugin.StopNext = true;
                    Plugin.TaskManager.Abort();
                }

                ImGui.PopStyleColor(3);
            } else {
                ImGui.BeginDisabled(plugin.IsScanningWards);
                if (ImGui.Button("Scan Seen Houses", actionBtnSize)) plugin.ScanForSeenHouses();
                ImGui.EndDisabled();
            }

            ImGui.SameLine();

            ImGui.BeginDisabled(!plugin.HasAnySeenHouses());
            if (ImGui.Button("Reset Seen", actionBtnSize)) plugin.ResetSeenHouses();
            ImGui.EndDisabled();

            ImGui.EndTable();
        }

        ImGui.PopStyleVar(2);
    }

    private void DrawMainLayout()
    {
        if (plugin.ResidentialTerritories.Count == 0) {
            ImGui.Text("No residential territories found.");
            return;
        }

        EnsureSelectedTerritory();
        var seenWards = plugin.GetSeenHousesForTerritory(selectedTerritoryId);

        var mainFlags = ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.BordersInnerV;
        if (!ImGui.BeginTable("MainLayout", 3, mainFlags)) return;

        ImGui.TableSetupColumn("Territories", ImGuiTableColumnFlags.WidthFixed, 180);
        ImGui.TableSetupColumn("Wards", ImGuiTableColumnFlags.WidthFixed, 320);
        ImGui.TableSetupColumn("Houses", ImGuiTableColumnFlags.WidthStretch, 1);
        ImGui.TableNextRow();

        ImGui.TableSetColumnIndex(0);
        ImGui.BeginChild("TerritoryPanel", new Vector2(0, 0), true);
        DrawTerritoryList();
        ImGui.EndChild();

        ImGui.TableSetColumnIndex(1);
        ImGui.BeginChild("WardPanel", new Vector2(0, 0), true);
        DrawWardList(seenWards);
        ImGui.EndChild();

        ImGui.TableSetColumnIndex(2);
        ImGui.BeginChild("HousePanel", new Vector2(0, 0), true);
        DrawHousePanel(seenWards);
        ImGui.EndChild();

        ImGui.EndTable();
    }

    private void DrawTerritoryList()
    {
        ImGui.Text("Territories");
        ImGui.Separator();

        foreach (var territory in plugin.ResidentialTerritories) {
            var seenWards = plugin.GetSeenHousesForTerritory(territory.TerritoryId);
            var seenCount = CountSeenWards(seenWards);
            DrawTerritoryEntry(territory, seenCount);
        }
    }

    private void DrawTerritoryEntry(Plugin.ResidentialTerritory territory, int seenCount)
    {
        var isSelected = territory.TerritoryId == selectedTerritoryId;
        var accent = isSelected ? new Vector4(0.20f, 0.50f, 0.70f, 1.0f) : new Vector4(0.18f, 0.18f, 0.20f, 1.0f);
        var textColor = isSelected ? new Vector4(1f, 1f, 1f, 1f) : new Vector4(0.80f, 0.82f, 0.86f, 1.0f);

        ImGui.PushStyleColor(ImGuiCol.Button, accent);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered,
            new Vector4(MathF.Min(accent.X + 0.08f, 1f), MathF.Min(accent.Y + 0.08f, 1f), MathF.Min(accent.Z + 0.08f, 1f), 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive,
            new Vector4(MathF.Max(accent.X - 0.06f, 0f), MathF.Max(accent.Y - 0.06f, 0f), MathF.Max(accent.Z - 0.06f, 0f), 1f));
        ImGui.PushStyleColor(ImGuiCol.Text, textColor);

        if (ImGui.Button($"{territory.TabLabel}##Territory{territory.TerritoryId}", new Vector2(-1, 30))) {
            selectedTerritoryId = territory.TerritoryId;
        }

        ImGui.PopStyleColor(4);

        ImGui.TextColored(new Vector4(0.55f, 0.62f, 0.70f, 1.0f), territory.PlaceName);
        var progress = seenCount / 30f;
        ImGui.ProgressBar(progress, new Vector2(-1, 6), "");
        ImGui.Text($"{seenCount}/30 wards");

        if (seenCount < 30) {
            ImGui.TextColored(new Vector4(0.86f, 0.52f, 0.13f, 1.0f), "Not done");
        } else {
            ImGui.TextColored(new Vector4(0.06f, 0.70f, 0.50f, 1.0f), "Done");
        }

        ImGui.Separator();
    }

    private void DrawWardList(Dictionary<int, List<Plugin.HouseInfoEntry>> seenWards)
    {
        var selectedTerritory = plugin.ResidentialTerritories.FirstOrDefault(t => t.TerritoryId == selectedTerritoryId);
        var selectedWard = GetSelectedWard(selectedTerritoryId);

        ImGui.Text("Wards");
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.55f, 0.62f, 0.70f, 1.0f), selectedTerritory.TabLabel);
        ImGui.Separator();

        var tableFlags = ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit;
        if (!ImGui.BeginTable("WardListTable", 2, tableFlags)) return;

        ImGui.TableSetupColumn("Ward", ImGuiTableColumnFlags.WidthFixed, 120);
        ImGui.TableSetupColumn("Sizes", ImGuiTableColumnFlags.WidthFixed, 140);
        ImGui.TableHeadersRow();

        var pillSize = new Vector2(36, 22);

        for (var ward = 0; ward <= 29; ward++) {
            var wardIndex = ward + 1;
            var seenWard = seenWards.ContainsKey(ward) && seenWards[ward].Count > 0;

            var wardHouses = seenWards.GetValueOrDefault(ward);
            var hasSmall = seenWard && (wardHouses?.Exists(h => h is { TypeShort: "S", IsOwned: false }) ?? false);
            var hasMedium = seenWard && (wardHouses?.Exists(h => h is { TypeShort: "M", IsOwned: false }) ?? false);
            var hasLarge = seenWard && (wardHouses?.Exists(h => h is { TypeShort: "L", IsOwned: false }) ?? false);

            ImGui.TableNextRow();

            ImGui.TableSetColumnIndex(0);
            var isSelected = selectedWard == ward;
            if (ImGui.Selectable($"Ward {wardIndex:00}##WardRow{wardIndex}", isSelected, ImGuiSelectableFlags.SpanAllColumns, new Vector2(0, 26))) {
                if (selectedWard != ward) {
                    SetSelectedWard(selectedTerritoryId, ward);
                    if (plugin is { IsScanningWards: false, StopNext: false }) plugin.OpenHouseListForWard(ward);
                }
            }

            ImGui.TableSetColumnIndex(1);
            DrawSizePill("S", hasSmall, new Vector4(0.12f, 0.58f, 0.95f, 1.0f), pillSize);
            ImGui.SameLine();
            DrawSizePill("M", hasMedium, new Vector4(0.40f, 0.75f, 0.25f, 1.0f), pillSize);
            ImGui.SameLine();
            DrawSizePill("L", hasLarge, new Vector4(0.75f, 0.28f, 0.90f, 1.0f), pillSize);
        }

        ImGui.EndTable();
    }

    private void DrawHousePanel(Dictionary<int, List<Plugin.HouseInfoEntry>> seenWards)
    {
        var selectedTerritory = plugin.ResidentialTerritories.FirstOrDefault(t => t.TerritoryId == selectedTerritoryId);
        var selectedWard = GetSelectedWard(selectedTerritoryId);
        var seenCount = CountSeenWards(seenWards);

        ImGui.Text($"{selectedTerritory.TabLabel} — Ward Details");
        ImGui.TextColored(new Vector4(0.55f, 0.62f, 0.70f, 1.0f), $"{selectedTerritory.PlaceName} • {seenCount}/30 wards scanned");
        ImGui.Separator();

        DrawSizeFilters();

        if (selectedWard < 0) {
            ImGui.Spacing();
            ImGui.Text("Pick a ward to see houses.");
            return;
        }

        DrawHousesInWard(selectedWard, seenWards);
    }

    private void DrawHousesInWard(int ward, Dictionary<int, List<Plugin.HouseInfoEntry>> seenWards)
    {
        ImGui.Text($"Ward {ward + 1} Houses");

        if (!showSmallHouses && !showMediumHouses && !showLargeHouses) {
            ImGui.Text("Select at least one size filter to show houses.");
            return;
        }

        List<Plugin.HouseInfoEntry>? seen = seenWards.GetValueOrDefault(ward);
        if (seen == null || seen.Count == 0) {
            ImGui.Text("No seen houses in this ward.");
            return;
        }

        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6.0f);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(4, 4));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(4, 6));

        var ordered = new List<Plugin.HouseInfoEntry>(seen);
        // Sort: unowned first, then by price ascending
        ordered.Sort((a, b) => {
            var ownedComparison = a.IsOwned.CompareTo(b.IsOwned);
            if (ownedComparison != 0) return ownedComparison;

            var priceComparison = a.HousePrice.CompareTo(b.HousePrice);
            if (priceComparison != 0) return priceComparison;

            return String.Compare(a.TypeShort, b.TypeShort, StringComparison.Ordinal);
        });

        var btnSize = new Vector2(78, 28);

        var pale = new Vector4(0.12f, 0.58f, 0.95f, 1.0f); // bright azure (blue)
        var gold = new Vector4(0.75f, 0.28f, 0.90f, 1.0f); // magenta/purple (distinct from blue/green)
        var red = new Vector4(0.40f, 0.40f, 0.40f, 1.0f); // subdued gray (fallback)

        ImGui.Columns(4, $"HouseCols{ward}", false);

        foreach (var house in ordered) {
            if (!ShouldShowHouse(house)) continue;

            var houseNumber = house.HouseNumber + 1;
            var isSubdivision = houseNumber > 30;
            var houseLabel = $"{house.TypeShort} {houseNumber:00}";

            var houseColor = house.IsOwned ? red : house.TypeShort == "L" ? gold : house.TypeShort == "M" ? new Vector4(0.6f, 0.8f, 0.3f, 1.0f) : pale;

            ImGui.PushStyleColor(ImGuiCol.Button, houseColor);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered,
                new Vector4(MathF.Min(houseColor.X + 0.06f, 1f), MathF.Min(houseColor.Y + 0.06f, 1f), MathF.Min(houseColor.Z + 0.06f, 1f), 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive,
                new Vector4(MathF.Max(houseColor.X - 0.05f, 0f), MathF.Max(houseColor.Y - 0.05f, 0f), MathF.Max(houseColor.Z - 0.05f, 0f), 1f));

            if (ImGui.Button(houseLabel, btnSize)) {
                // Handle house selection logic here
            }

            ImGui.PopStyleColor(3);

            if (ImGui.IsItemHovered()) {
                ImGui.BeginTooltip();
                ImGui.Text($"{houseLabel}{(isSubdivision ? " (Subdivision)" : "")}");
                ImGui.Text($"Price: {house.HousePrice:N0} gil");
                ImGui.EndTooltip();
            }

            ImGui.NextColumn();
        }

        ImGui.Columns();
        ImGui.PopStyleVar(3);
    }

    private int GetSelectedWard(uint territoryId)
    {
        if (!selectedWardByTerritory.TryGetValue(territoryId, out var selectedWard)) {
            selectedWard = -1;
            selectedWardByTerritory[territoryId] = selectedWard;
        }

        return selectedWard;
    }

    private void SetSelectedWard(uint territoryId, int ward)
    {
        selectedWardByTerritory[territoryId] = ward;
    }

    private int CountSeenWards(Dictionary<int, List<Plugin.HouseInfoEntry>> seenWards)
    {
        var count = 0;
        for (var ward = 0; ward < 30; ward++) {
            if (seenWards.TryGetValue(ward, out var houses) && houses.Count > 0) count++;
        }

        return count;
    }

    private bool ShouldShowHouse(Plugin.HouseInfoEntry house)
    {
        return (showSmallHouses && house.TypeShort == "S")
               || (showMediumHouses && house.TypeShort == "M")
               || (showLargeHouses && house.TypeShort == "L");
    }

    private void DrawFilterToggle(string label, ref bool isEnabled, Vector4 activeColor, Vector2 buttonSize)
    {
        var idleColor = new Vector4(0.22f, 0.22f, 0.25f, 1.0f);
        var textColor = new Vector4(1f, 1f, 1f, 1f);
        var bgColor = isEnabled ? activeColor : idleColor;

        ImGui.PushStyleColor(ImGuiCol.Button, bgColor);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered,
            new Vector4(MathF.Min(bgColor.X + 0.08f, 1f), MathF.Min(bgColor.Y + 0.08f, 1f), MathF.Min(bgColor.Z + 0.08f, 1f), 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive,
            new Vector4(MathF.Max(bgColor.X - 0.06f, 0f), MathF.Max(bgColor.Y - 0.06f, 0f), MathF.Max(bgColor.Z - 0.06f, 0f), 1f));
        ImGui.PushStyleColor(ImGuiCol.Text, textColor);

        if (ImGui.Button(label, buttonSize)) isEnabled = !isEnabled;

        ImGui.PopStyleColor(4);
    }

    private void DrawSizeFilters()
    {
        ImGui.Text("Show sizes:");
        ImGui.SameLine();

        var buttonSize = new Vector2(86, 28);
        DrawFilterToggle("Small", ref showSmallHouses, new Vector4(0.12f, 0.58f, 0.95f, 1.0f), buttonSize);
        ImGui.SameLine();
        DrawFilterToggle("Medium", ref showMediumHouses, new Vector4(0.40f, 0.75f, 0.25f, 1.0f), buttonSize);
        ImGui.SameLine();
        DrawFilterToggle("Large", ref showLargeHouses, new Vector4(0.75f, 0.28f, 0.90f, 1.0f), buttonSize);
    }

    private void DrawSizePill(string label, bool isActive, Vector4 activeColor, Vector2 size)
    {
        var idleColor = new Vector4(0.20f, 0.20f, 0.22f, 1.0f);
        var bgColor = isActive ? activeColor : idleColor;
        var textColor = isActive ? new Vector4(1f, 1f, 1f, 1f) : new Vector4(0.65f, 0.65f, 0.70f, 1.0f);

        var drawList = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        var end = new Vector2(pos.X + size.X, pos.Y + size.Y);
        var rounding = size.Y / 2;

        drawList.AddRectFilled(pos, end, ImGui.GetColorU32(bgColor), rounding);
        var textSize = ImGui.CalcTextSize(label);
        var textPos = new Vector2(pos.X + (size.X - textSize.X) / 2, pos.Y + (size.Y - textSize.Y) / 2);
        drawList.AddText(textPos, ImGui.GetColorU32(textColor), label);

        ImGui.Dummy(size);
    }

    private void EnsureSelectedTerritory()
    {
        if (hasSelectedTerritory) return;

        var currentTerritoryId = (uint) Svc.ClientState.TerritoryType;
        var currentMatch = plugin.ResidentialTerritories.FirstOrDefault(t => t.TerritoryId == currentTerritoryId);

        if (currentMatch.TerritoryId != 0)
            selectedTerritoryId = currentMatch.TerritoryId;
        else
            selectedTerritoryId = plugin.ResidentialTerritories[0].TerritoryId;

        hasSelectedTerritory = true;
    }

    private enum OpenSource
    {
        Unknown,
        CommandToggle,
        AddonSetupLifecycle
    }
}
