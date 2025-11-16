using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace HousingSweepy;

public class MainWindow : Window
{
    private readonly Plugin plugin;
    private bool hideSmallHouses;

    private OpenSource lastOpenSource = OpenSource.Unknown;

    private int selectedWard = -1;

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
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6.0f);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8, 8));
        var actionBtnSize = new Vector2(180, 34);

        if (plugin.IsScanningWards) {
            var stopColor = new Vector4(0.86f, 0.22f, 0.22f, 1.0f);
            ImGui.PushStyleColor(ImGuiCol.Button, stopColor);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered,
                new Vector4(MathF.Min(stopColor.X + 0.06f, 1f), MathF.Min(stopColor.Y + 0.06f, 1f), MathF.Min(stopColor.Z + 0.06f, 1f), 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive,
                new Vector4(MathF.Max(stopColor.X - 0.05f, 0f), MathF.Max(stopColor.Y - 0.05f, 0f), MathF.Max(stopColor.Z - 0.05f, 0f), 1f));

            if (ImGui.Button("Stop Scanning", actionBtnSize)) {
                plugin.IsScanningWards = false;
                plugin.StopNext = true;
                Plugin.TaskManager.Abort();
            }

            ImGui.PopStyleColor(3);
        } else {
            ImGui.BeginDisabled(plugin.IsScanningWards);
            if (ImGui.Button($"Scan for Seen Houses", actionBtnSize)) plugin.ScanForSeenHouses();

            ImGui.EndDisabled();
        }

        ImGui.SameLine();

        ImGui.BeginDisabled(plugin.SeenHouses.Count == 0);
        if (ImGui.Button("Reset Seen Houses", actionBtnSize)) plugin.ResetSeenHouses();

        ImGui.EndDisabled();

        ImGui.PopStyleVar(2);
        DrawWardSelection();
    }

    private void DrawWardSelection()
    {
        var seenWards = plugin.SeenHouses;

        var colorSeen = new Vector4(0.06f, 0.70f, 0.50f, 1.0f); // teal (high contrast green)
        var colorMedium = new Vector4(0.12f, 0.58f, 0.95f, 1.0f); // bright azure (blue)
        var colorLarge = new Vector4(0.75f, 0.28f, 0.90f, 1.0f); // magenta/purple (distinct from blue/green)
        var colorNone = new Vector4(0.40f, 0.40f, 0.40f, 1.0f); // subdued gray (fallback)

        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6.0f);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(8, 6));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(6, 6));

        // Left: wards list
        ImGui.BeginChild("WardsChild", new Vector2(220, 0), true);

        var wardButtonSize = new Vector2(46, 30);

        // Use 3 columns for wards
        ImGui.Columns(3, "WardsCols", false);

        for (var ward = 0; ward <= 29; ward++) {
            var wardIndex = ward + 1;
            var seenWard = seenWards.ContainsKey(ward) && seenWards[ward].Count > 0;

            var hasMedium = seenWard && (seenWards.GetValueOrDefault(ward)?.Exists(h => h is { TypeShort: "M", IsOwned: false }) ?? false);
            var hasLarge = seenWard && (seenWards.GetValueOrDefault(ward)?.Exists(h => h is { TypeShort: "L", IsOwned: false }) ?? false);

            var color = hasLarge ? colorLarge : hasMedium ? colorMedium : seenWard ? colorSeen : colorNone;

            ImGui.PushStyleColor(ImGuiCol.Button, color);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(color.X + 0.06f, color.Y + 0.06f, color.Z + 0.06f, 1.0f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive,
                new Vector4(MathF.Max(color.X - 0.05f, 0f), MathF.Max(color.Y - 0.05f, 0f), MathF.Max(color.Z - 0.05f, 0f), 1.0f));

            var label = $"{wardIndex:00}##Ward{wardIndex}";
            if (ImGui.Button(label, wardButtonSize))
                if (selectedWard != ward) {
                    selectedWard = ward;

                    if (plugin is { IsScanningWards: false, StopNext: false }) plugin.OpenHouseListForWard(ward);
                }

            ImGui.PopStyleColor(3);

            ImGui.NextColumn();
        }

        ImGui.Columns();
        ImGui.EndChild();

        // Right: houses for selected ward
        ImGui.SameLine();
        ImGui.BeginChild("HousesChild", new Vector2(0, 0), true);

        if (selectedWard < 0)
            ImGui.Text("Select a ward to view houses.");
        else
            DrawHousesInWard(selectedWard);

        ImGui.EndChild();

        ImGui.PopStyleVar(3);
    }

    private void DrawHousesInWard(int ward)
    {
        if (ImGui.Checkbox("Hide Small Houses", ref hideSmallHouses)) {
            // ignore
        }

        ImGui.Text($"Houses in Ward {ward + 1}:");

        List<Plugin.HouseInfoEntry>? seen = plugin.SeenHouses.GetValueOrDefault(ward);
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
            if (hideSmallHouses && (house.TypeShort == "S" || house.IsOwned)) continue;

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

    private enum OpenSource
    {
        Unknown,
        CommandToggle,
        AddonSetupLifecycle
    }
}
