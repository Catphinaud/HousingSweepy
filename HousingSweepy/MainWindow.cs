using System.Numerics;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using ECommons.DalamudServices;

namespace HousingSweepy;

public class MainWindow : Window
{
    private readonly Plugin plugin;
    private readonly bool[] plotNumberFilter = Enumerable.Repeat(true, 30).ToArray();
    private readonly bool[] plotNumberFilterDraft = Enumerable.Repeat(true, 30).ToArray();
    private bool filterFoundEnabled;
    private bool filterFoundUserDisabled;
    private bool filterFoundSmall = true;
    private bool filterFoundMedium = true;
    private bool filterFoundLarge = true;
    private bool filterFoundSmallDraft = true;
    private bool filterFoundMediumDraft = true;
    private bool filterFoundLargeDraft = true;
    private bool filterFoundEnabledDraft = true;

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
        ImGui.SameLine();
        var rightEdge = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X;
        var filterLabel = "Filter \u25BE";
        var filterSize = new Vector2(ImGui.CalcTextSize(filterLabel).X + 22, 24);
        ImGui.SetCursorPosX(rightEdge - filterSize.X);
        DrawFoundFilterControl(filterLabel, filterSize);
        ImGui.Separator();

        var contentWidth = ImGui.GetContentRegionAvail().X;
        var wardCol = 130f;
        var sizeCol = 48f;
        var spacing = 10f;
        var needed = wardCol + (sizeCol * 3) + (spacing * 3);
        if (needed > contentWidth) {
            var overflow = needed - contentWidth;
            wardCol = MathF.Max(90f, wardCol - overflow);
            sizeCol = MathF.Max(40f, sizeCol - overflow / 3f);
        }

        var startX = ImGui.GetCursorPosX();
        var wardX = startX;
        var smallX = wardX + wardCol + spacing;
        var mediumX = smallX + sizeCol + spacing;
        var largeX = mediumX + sizeCol + spacing;

        ImGui.SetCursorPosX(wardX);
        ImGui.Text("Ward");
        ImGui.SameLine();
        ImGui.SetCursorPosX(smallX + (sizeCol - ImGui.CalcTextSize("S").X) / 2);
        ImGui.Text("S");
        ImGui.SameLine();
        ImGui.SetCursorPosX(mediumX + (sizeCol - ImGui.CalcTextSize("M").X) / 2);
        ImGui.Text("M");
        ImGui.SameLine();
        ImGui.SetCursorPosX(largeX + (sizeCol - ImGui.CalcTextSize("L").X) / 2);
        ImGui.Text("L");
        if (ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
            OpenFoundFilterPopup();
        ImGui.Separator();

        var rowHeight = 28f;
        var drawList = ImGui.GetWindowDrawList();
        var anySeen = seenWards.Values.Any(houses => houses.Count > 0);
        if (anySeen && !filterFoundUserDisabled) filterFoundEnabled = true;
        for (var ward = 0; ward <= 29; ward++) {
            var wardIndex = ward + 1;
            var seenWard = seenWards.ContainsKey(ward) && seenWards[ward].Count > 0;

            var wardHouses = seenWards.GetValueOrDefault(ward);
            var smallCount = seenWard ? (wardHouses?.Count(h => h is { TypeShort: "S", IsOwned: false }) ?? 0) : 0;
            var mediumCount = seenWard ? (wardHouses?.Count(h => h is { TypeShort: "M", IsOwned: false }) ?? 0) : 0;
            var largeCount = seenWard ? (wardHouses?.Count(h => h is { TypeShort: "L", IsOwned: false }) ?? 0) : 0;

            if (filterFoundEnabled && anySeen) {
                var matchSmall = filterFoundSmall && smallCount > 0;
                var matchMedium = filterFoundMedium && mediumCount > 0;
                var matchLarge = filterFoundLarge && largeCount > 0;
                if (!(matchSmall || matchMedium || matchLarge)) continue;
            }

            var rowTop = ImGui.GetCursorScreenPos();
            var rowBottom = new Vector2(rowTop.X + contentWidth, rowTop.Y + rowHeight);

            ImGui.InvisibleButton($"WardRow{wardIndex}", new Vector2(contentWidth, rowHeight));
            var isSelected = selectedWard == ward;
            var isHovered = ImGui.IsItemHovered();
            if (ImGui.IsItemClicked()) {
                if (selectedWard != ward) {
                    SetSelectedWard(selectedTerritoryId, ward);
                    if (plugin is { IsScanningWards: false, StopNext: false }) plugin.OpenHouseListForWard(ward);
                }
            }

            var rowColor = isSelected
                ? new Vector4(0.23f, 0.40f, 0.55f, 0.85f)
                : isHovered ? new Vector4(0.20f, 0.20f, 0.24f, 0.8f) : new Vector4(0f, 0f, 0f, 0f);
            if (rowColor.W > 0f)
                drawList.AddRectFilled(rowTop, rowBottom, ImGui.GetColorU32(rowColor), 6f);

            var textColor = isSelected ? new Vector4(1f, 1f, 1f, 1f) : new Vector4(0.86f, 0.88f, 0.92f, 1f);
            var textPos = new Vector2(rowTop.X + 8, rowTop.Y + 6);
            drawList.AddText(textPos, ImGui.GetColorU32(textColor), $"Ward {wardIndex:00}");


            var pillY = rowTop.Y + 4;
            DrawCountPill(drawList, new Vector2(rowTop.X + (smallX - startX), pillY), new Vector2(sizeCol, 20), smallCount, seenWard,
                new Vector4(0.12f, 0.58f, 0.95f, 1.0f));
            DrawCountPill(drawList, new Vector2(rowTop.X + (mediumX - startX), pillY), new Vector2(sizeCol, 20), mediumCount, seenWard,
                new Vector4(0.40f, 0.75f, 0.25f, 1.0f));
            DrawCountPill(drawList, new Vector2(rowTop.X + (largeX - startX), pillY), new Vector2(sizeCol, 20), largeCount, seenWard,
                new Vector4(0.75f, 0.28f, 0.90f, 1.0f));
        }
    }

    private void DrawFoundFilterControl(string label, Vector2 buttonSize)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6f);

        var isActive = filterFoundEnabled;
        var baseColor = isActive ? new Vector4(0.20f, 0.50f, 0.70f, 1.0f) : new Vector4(0.20f, 0.20f, 0.24f, 1.0f);

        ImGui.PushStyleColor(ImGuiCol.Button, baseColor);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered,
            new Vector4(MathF.Min(baseColor.X + 0.08f, 1f), MathF.Min(baseColor.Y + 0.08f, 1f), MathF.Min(baseColor.Z + 0.08f, 1f), 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive,
            new Vector4(MathF.Max(baseColor.X - 0.06f, 0f), MathF.Max(baseColor.Y - 0.06f, 0f), MathF.Max(baseColor.Z - 0.06f, 0f), 1f));

        if (ImGui.Button(label, buttonSize)) {
            OpenFoundFilterPopup();
        }

        ImGui.PopStyleColor(3);

        if (ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
            OpenFoundFilterPopup();

        ImGui.PopStyleVar();
        DrawFoundFilterPopup();
    }

    private void OpenFoundFilterPopup()
    {
        filterFoundSmallDraft = filterFoundSmall;
        filterFoundMediumDraft = filterFoundMedium;
        filterFoundLargeDraft = filterFoundLarge;
        filterFoundEnabledDraft = filterFoundEnabled;
        ImGui.OpenPopup("FoundFilterPopup");
    }

    private void DrawFoundFilterPopup()
    {
        if (!ImGui.BeginPopup("FoundFilterPopup")) return;

        ImGui.Text("Filter sizes with at least one open plot:");
        ImGui.Separator();

        ImGui.Checkbox("Enable filter", ref filterFoundEnabledDraft);
        ImGui.Checkbox("Small", ref filterFoundSmallDraft);
        ImGui.Checkbox("Medium", ref filterFoundMediumDraft);
        ImGui.Checkbox("Large", ref filterFoundLargeDraft);

        ImGui.Separator();

        var disableApply = filterFoundEnabledDraft && !(filterFoundSmallDraft || filterFoundMediumDraft || filterFoundLargeDraft);
        ImGui.BeginDisabled(disableApply);
        if (ImGui.Button("Apply", new Vector2(80, 24))) {
            filterFoundSmall = filterFoundSmallDraft;
            filterFoundMedium = filterFoundMediumDraft;
            filterFoundLarge = filterFoundLargeDraft;
            filterFoundEnabled = filterFoundEnabledDraft;
            filterFoundUserDisabled = !filterFoundEnabledDraft;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(80, 24))) ImGui.CloseCurrentPopup();

        ImGui.EndPopup();
    }

    private void DrawHousePanel(Dictionary<int, List<Plugin.HouseInfoEntry>> seenWards)
    {
        var selectedTerritory = plugin.ResidentialTerritories.FirstOrDefault(t => t.TerritoryId == selectedTerritoryId);
        var selectedWard = GetSelectedWard(selectedTerritoryId);
        var seenCount = CountSeenWards(seenWards);

        ImGui.Text($"{selectedTerritory.TabLabel} — Ward Details");
        ImGui.SameLine();
        var rightEdge = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X;
        var filterLabel = "Plot Filter \u25BE";
        var filterSize = new Vector2(ImGui.CalcTextSize(filterLabel).X + 22, 24);
        ImGui.SetCursorPosX(rightEdge - filterSize.X);
        DrawHouseFilterControl(filterLabel, filterSize);

        ImGui.TextColored(new Vector4(0.55f, 0.62f, 0.70f, 1.0f), $"{selectedTerritory.PlaceName} • {seenCount}/30 wards scanned");
        ImGui.Separator();

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

        if (!plotNumberFilter.Any(enabled => enabled)) {
            ImGui.Text("Select at least one plot number to show houses.");
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
        var plotIndex = (house.HouseNumber % 30) + 1;
        return plotNumberFilter[plotIndex - 1];
    }

    private void DrawHouseFilterControl(string label, Vector2 buttonSize)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6f);

        var isActive = plotNumberFilter.Any(enabled => !enabled);
        var baseColor = isActive ? new Vector4(0.20f, 0.50f, 0.70f, 1.0f) : new Vector4(0.20f, 0.20f, 0.24f, 1.0f);

        ImGui.PushStyleColor(ImGuiCol.Button, baseColor);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered,
            new Vector4(MathF.Min(baseColor.X + 0.08f, 1f), MathF.Min(baseColor.Y + 0.08f, 1f), MathF.Min(baseColor.Z + 0.08f, 1f), 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive,
            new Vector4(MathF.Max(baseColor.X - 0.06f, 0f), MathF.Max(baseColor.Y - 0.06f, 0f), MathF.Max(baseColor.Z - 0.06f, 0f), 1f));

        if (ImGui.Button(label, buttonSize)) {
            OpenHouseFilterPopup();
        }

        ImGui.PopStyleColor(3);

        if (ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
            OpenHouseFilterPopup();

        ImGui.PopStyleVar();
        DrawHouseFilterPopup();
    }

    private void OpenHouseFilterPopup()
    {
        Array.Copy(plotNumberFilter, plotNumberFilterDraft, plotNumberFilter.Length);
        ImGui.OpenPopup("HouseFilterPopup");
    }

    private void DrawHouseFilterPopup()
    {
        if (!ImGui.BeginPopup("HouseFilterPopup")) return;

        ImGui.Text("Show plot numbers (subdivision too):");
        ImGui.Separator();

        var disableNone = !plotNumberFilterDraft.Any(enabled => enabled);

        ImGui.BeginDisabled(plotNumberFilterDraft.All(enabled => enabled));
        if (ImGui.Button("Show all", new Vector2(80, 24))) {
            for (var i = 0; i < plotNumberFilterDraft.Length; i++) {
                plotNumberFilterDraft[i] = true;
            }
        }
        ImGui.EndDisabled();

        ImGui.SameLine();

        ImGui.BeginDisabled(disableNone);
        if (ImGui.Button("Show none", new Vector2(80, 24))) {
            for (var i = 0; i < plotNumberFilterDraft.Length; i++) {
                plotNumberFilterDraft[i] = false;
            }
        }
        ImGui.EndDisabled();

        ImGui.Separator();

        var columns = 3;
        ImGui.Columns(columns, "PlotNumberFilterCols", false);
        for (var i = 0; i < plotNumberFilterDraft.Length; i++) {
            var label = (i + 1).ToString();
            ImGui.Checkbox(label, ref plotNumberFilterDraft[i]);
            ImGui.NextColumn();
        }
        ImGui.Columns();

        ImGui.Separator();

        if (ImGui.Button("Apply", new Vector2(80, 24))) {
            Array.Copy(plotNumberFilterDraft, plotNumberFilter, plotNumberFilter.Length);
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(80, 24))) ImGui.CloseCurrentPopup();

        ImGui.EndPopup();
    }

    private void DrawCountPill(ImDrawListPtr drawList, Vector2 pos, Vector2 size, int count, bool isActive, Vector4 activeColor)
    {
        var idleColor = new Vector4(0.20f, 0.20f, 0.22f, 1.0f);
        var bgColor = isActive ? activeColor : idleColor;
        if (count == 0) bgColor = new Vector4(bgColor.X, bgColor.Y, bgColor.Z, 0.35f);
        var textColor = isActive ? new Vector4(1f, 1f, 1f, 1f) : new Vector4(0.55f, 0.55f, 0.60f, 1.0f);
        if (count == 0) textColor = new Vector4(textColor.X, textColor.Y, textColor.Z, 0.55f);
        var end = new Vector2(pos.X + size.X, pos.Y + size.Y);
        var rounding = 4f;

        drawList.AddRectFilled(pos, end, ImGui.GetColorU32(bgColor), rounding);
        var text = isActive ? count.ToString() : "—";
        var textSize = ImGui.CalcTextSize(text);
        var textPos = new Vector2(pos.X + (size.X - textSize.X) / 2, pos.Y + (size.Y - textSize.Y) / 2);
        drawList.AddText(textPos, ImGui.GetColorU32(textColor), text);
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

    internal void SelectTerritory(uint territoryId)
    {
        selectedTerritoryId = territoryId;
        hasSelectedTerritory = true;
    }

    private enum OpenSource
    {
        Unknown,
        CommandToggle,
        AddonSetupLifecycle
    }
}
