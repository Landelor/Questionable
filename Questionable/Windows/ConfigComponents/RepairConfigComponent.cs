using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin;
using static Questionable.Configuration;

namespace Questionable.Windows.ConfigComponents;

internal sealed class RepairConfigComponent : ConfigComponent
{
    public RepairConfigComponent(IDalamudPluginInterface pluginInterface, Configuration configuration)
        : base(pluginInterface, configuration)
    {
    }

    public override void DrawTab()
    {
        using var tab = ImRaii.TabItem("Repairs###Repairs");
        if (!tab)
            return;

        var repairConfig = Configuration.Repairs;

        bool enabled = repairConfig.Enabled;
        if (ImGui.Checkbox("Enable Automatic Repairs", ref enabled))
        {
            repairConfig.Enabled = enabled;
            Save();
        }

        using (ImRaii.Disabled(!repairConfig.Enabled))
        {
            ImGui.Spacing();

            int threshold = repairConfig.DurabilityThreshold;
            if (ImGui.SliderInt("Durability Threshold (%)", ref threshold, 0, 100,
                    $"{threshold}%%"))
            {
                repairConfig.DurabilityThreshold = threshold;
                Save();
            }

            ImGui.SameLine();
            using (ImRaii.PushFont(UiBuilder.IconFont))
                ImGui.TextDisabled(FontAwesomeIcon.InfoCircle.ToIconString());

            if (ImGui.IsItemHovered())
            {
                using var tooltip = ImRaii.Tooltip();
                ImGui.Text("Repairs will be triggered when any equipped item reaches this durability percentage or below.");
            }

            ImGui.Spacing();

            string[] repairMethods = ["Self Repair (Dark Matter)", "Repair NPC"];
            int currentMethod = (int)repairConfig.RepairMethod;

            if (ImGui.Combo("Repair Method", ref currentMethod, repairMethods, repairMethods.Length))
            {
                repairConfig.RepairMethod = (ERepairMethod)currentMethod;
                Save();
            }

            ImGui.SameLine();
            using (ImRaii.PushFont(UiBuilder.IconFont))
                ImGui.TextDisabled(FontAwesomeIcon.InfoCircle.ToIconString());

            if (ImGui.IsItemHovered())
            {
                using var tooltip = ImRaii.Tooltip();
                ImGui.Text("Self Repair: Uses Dark Matter from your inventory");
                ImGui.Text("Repair NPC: Travels to the nearest repair NPC");
            }

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            using var color = ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudYellow);
            ImGui.TextWrapped("Note: Repair functionality will interrupt questing to perform repairs when needed, then resume the current quest.");
        }
    }
}