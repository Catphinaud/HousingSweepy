using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;

namespace HousingSweepy;

public class TaskOverlayWindow : Window
{
    private readonly Plugin plugin;

    public TaskOverlayWindow(Plugin plugin) : base(
        "HousingSweepy Task Overlay",
        ImGuiWindowFlags.NoTitleBar
        | ImGuiWindowFlags.NoResize
        | ImGuiWindowFlags.NoMove
        | ImGuiWindowFlags.NoCollapse
        | ImGuiWindowFlags.NoSavedSettings
        | ImGuiWindowFlags.NoFocusOnAppearing
        | ImGuiWindowFlags.AlwaysAutoResize,
        true)
    {
        this.plugin = plugin;
        IsOpen = true;
        RespectCloseHotkey = false;
    }

    public override bool DrawConditions()
    {
        return plugin.ShowTaskOverlay && Plugin.TaskManager is { IsBusy: true, MaxTasks: > 0 };
    }

    public override void PreDraw()
    {
        var viewportSize = ImGuiHelpers.MainViewport.Size;
        ImGuiHelpers.SetNextWindowPosRelativeMainViewport(new Vector2(0, viewportSize.Y - 44));
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(viewportSize.X, 0),
            MaximumSize = new Vector2(viewportSize.X, float.MaxValue)
        };
    }

    public override void Draw()
    {
        var queued = Plugin.TaskManager.NumQueuedTasks;
        var max = Math.Max(Plugin.TaskManager.MaxTasks, 1);
        var complete = Math.Clamp(max - queued, 0, max);
        var progress = complete / (float)max;
        var label = $"HousingSweepy: {complete}/{max}";

        ImGui.PushStyleColor(ImGuiCol.PlotHistogram, new Vector4(0.20f, 0.50f, 0.70f, 1.0f));
        ImGui.ProgressBar(progress, new Vector2(MathF.Max(200f, ImGui.GetContentRegionAvail().X - 90f), 22), label);
        ImGui.PopStyleColor();

        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(80, 22))) {
            plugin.CancelCurrentTasks();
        }

        if (ImGui.IsWindowHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Right)) {
            plugin.CancelCurrentTasks();
        }
    }
}
