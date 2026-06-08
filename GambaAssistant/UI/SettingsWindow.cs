using System.Diagnostics;
using System.Numerics;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using GambaAssistant.Games.Blackjack;
using GambaAssistant.Services;
using GambaAssistant.UI.Components;
using GambaAssistant.UI.Tabs.SettingsTabs;

namespace GambaAssistant.UI;

public sealed class SettingsWindow : Window
{
    private readonly GeneralSettingsTab general;
    private readonly ProfileSettingsTab profiles;
    private readonly ChatTemplateSettingsTab templates;
    private int selectedTab;

    public SettingsWindow(Configuration config, BlackjackSession session, ProfileService profileService, PersistenceService persistence, LogService log)
        : base("GambaAssistant Settings###GambaAssistantSettingsWindow", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoFocusOnAppearing)
    {
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(860, 560), MaximumSize = new Vector2(float.MaxValue, float.MaxValue) };
        general = new GeneralSettingsTab(config, session);
        profiles = new ProfileSettingsTab(profileService, session);
        templates = new ChatTemplateSettingsTab(profileService, session);
    }

    public override void PreDraw() => GambaTheme.Push();
    public override void PostDraw() => GambaTheme.Pop();

    public override void Draw()
    {
        DrawHeader();

        var navWidth = 176f;
        ImGui.BeginChild("##GambaSettingsNav", new Vector2(navWidth, 0), true, ImGuiWindowFlags.NoScrollbar);
        DrawNavItem(0, "General");
        DrawNavItem(1, "Venue Profiles");
        DrawNavItem(2, "Blackjack Rules");
        DrawNavItem(3, "Chat Templates");
        ImGui.EndChild();

        ImGui.SameLine();
        ImGui.BeginChild("##GambaSettingsContent", Vector2.Zero, false, ImGuiWindowFlags.NoScrollbar);
        DrawSettingsPanel();
        ImGui.EndChild();
    }

    private void DrawSettingsPanel()
    {
        var panelSize = ImGui.GetContentRegionAvail();

        switch (selectedTab)
        {
            case 0: UiHelpers.Panel("General", general.Draw, panelSize); break;
            case 1: UiHelpers.Panel("Venue Profiles", profiles.Draw, panelSize); break;
            case 2: UiHelpers.Panel("Blackjack Rules", profiles.DrawRules, panelSize); break;
            case 3: UiHelpers.Panel("Chat Templates", templates.Draw, panelSize); break;
        }
    }

    private void DrawHeader()
    {
        ImGui.TextColored(GambaTheme.Gold, "GambaAssistant Settings");
        DrawSupportButtonRightAligned();
        ImGui.Separator();
    }

    private void DrawNavItem(int index, string label)
    {
        if (UiHelpers.VerticalNavItem(label, selectedTab == index, new Vector2(-1f, 34f)))
            selectedTab = index;
    }

    private static void DrawSupportButtonRightAligned()
    {
        const string supportLabel = "      Support##gamba-settings-kofi-support";
        var supportWidth = MathF.Max(116f, ImGui.CalcTextSize("Support").X + 52f);
        var available = ImGui.GetContentRegionAvail().X;
        ImGui.SameLine();
        if (available > supportWidth + 8f)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + available - supportWidth);

        GambaTheme.PushKofiButton();
        var supportClicked = ImGui.Button(supportLabel, new Vector2(supportWidth, 0));
        GambaTheme.PopKofiButton();

        DrawKofiCupIcon(ImGui.GetItemRectMin(), ImGui.GetItemRectMax());

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Support me on Ko-Fi");

        if (supportClicked)
            OpenSupportLink();
    }

    private static void OpenSupportLink()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://ko-fi.com/airitsukino",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Warning(ex, "GambaAssistant failed to open Ko-Fi link.");
        }
    }

    private static void DrawKofiCupIcon(Vector2 min, Vector2 max)
    {
        var draw = ImGui.GetWindowDrawList();
        var centerY = (min.Y + max.Y) * 0.5f;
        var cupMin = new Vector2(min.X + 10f, centerY - 6f);
        var cupMax = new Vector2(min.X + 25f, centerY + 6f);
        var color = ImGui.GetColorU32(new Vector4(0.96f, 0.91f, 1.00f, 1f));
        var shadow = ImGui.GetColorU32(new Vector4(0.20f, 0.07f, 0.36f, 0.9f));
        var heart = ImGui.GetColorU32(new Vector4(0.78f, 0.28f, 1.00f, 1f));

        draw.AddRectFilled(cupMin + new Vector2(1f, 1f), cupMax + new Vector2(1f, 1f), shadow, 3f);
        draw.AddRectFilled(cupMin, cupMax, color, 3f);
        draw.AddRect(new Vector2(cupMax.X - 1f, centerY - 4f), new Vector2(cupMax.X + 6f, centerY + 4f), color, 4f, 0, 2f);
        draw.AddCircleFilled(new Vector2(cupMin.X + 5f, centerY - 1f), 2f, heart);
        draw.AddCircleFilled(new Vector2(cupMin.X + 8f, centerY - 1f), 2f, heart);
        draw.AddTriangleFilled(new Vector2(cupMin.X + 3f, centerY), new Vector2(cupMin.X + 10f, centerY), new Vector2(cupMin.X + 6.5f, centerY + 4f), heart);
    }
}
