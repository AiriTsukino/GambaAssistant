using System.Diagnostics;
using System.Numerics;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using GambaAssistant.Games.Blackjack;
using GambaAssistant.Games.DeathRoll;
using GambaAssistant.Services;
using GambaAssistant.UI.Components;
using GambaAssistant.UI.Tabs;
using GambaAssistant.UI.Tabs.SettingsTabs;

namespace GambaAssistant.UI;

public sealed class MainWindow : Window
{
    private readonly TableTab table;
    private readonly PlayersTab players;
    private readonly DealerLedgerTab ledger;
    private readonly RulesTab rules;
    private readonly TradeMonitorTab trades;
    private readonly HistoryExportTab history;
    private readonly LogTerminalTab logTab;
    private readonly DemoModeTab demo;
    private readonly OverlaySettingsTab overlaySettings;
    private readonly DeathRollTournamentTab deathRoll;
    private readonly Action openSettings;
    private int selectedTab;
    private int selectedBlackjackTab;
    private int selectedDrtTab;

    public MainWindow(Configuration config, BlackjackSession session, ProfileService profiles, PartyService party, PlayerSessionService playerService, DealerLedgerService ledgerService, TradeMonitorService tradeMonitor, DiceService dice, ChatQueueService chat, OverlayService overlays, UndoService undo, DemoModeService demoMode, ExportService exports, DeathRollTournamentService deathRollService, LogService log, Action openSettings, Action<bool> setDrtBracketWindowOpen)
        : base("GambaAssistant###GambaAssistantMainWindow", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoFocusOnAppearing)
    {
        this.openSettings = openSettings;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(900, 620), MaximumSize = new Vector2(float.MaxValue, float.MaxValue) };
        table = new TableTab(config, session, profiles, party, playerService, ledgerService, dice, chat, undo, log, openSettings);
        players = new PlayersTab(session, playerService);
        ledger = new DealerLedgerTab(ledgerService);
        rules = new RulesTab(session, profiles);
        trades = new TradeMonitorTab(session, tradeMonitor);
        history = new HistoryExportTab(session, exports);
        logTab = new LogTerminalTab(log);
        demo = new DemoModeTab(config, demoMode, log);
        overlaySettings = new OverlaySettingsTab(config, session);
        deathRoll = new DeathRollTournamentTab(config, deathRollService, chat, log, setDrtBracketWindowOpen);
    }

    public override void PreDraw() => GambaTheme.Push();
    public override void PostDraw() => GambaTheme.Pop();

    public override void Draw()
    {
        DrawHeader();

        var navWidth = 176f;
        ImGui.BeginChild("##GambaMainNav", new Vector2(navWidth, 0), true, ImGuiWindowFlags.NoScrollbar);
        DrawNavItem(0, "Blackjack");
        DrawNavItem(1, "DRT");
        DrawNavItem(2, "Log / Terminal");

        ImGui.Dummy(new Vector2(1f, 50f));
        DrawActiveSectionNav();

        ImGui.EndChild();

        ImGui.SameLine();
        ImGui.BeginChild("##GambaMainContent", Vector2.Zero, false, ImGuiWindowFlags.NoScrollbar);
        DrawMainPanel();
        ImGui.EndChild();
    }

    private void DrawMainPanel()
    {
        var panelSize = ImGui.GetContentRegionAvail();

        switch (selectedTab)
        {
            case 0: UiHelpers.Panel($"Blackjack - {GetBlackjackTabName(selectedBlackjackTab)}", DrawSelectedBlackjackTab, panelSize); break;
            case 1: UiHelpers.Panel($"DRT - {GetDrtTabName(selectedDrtTab)}", DrawSelectedDrtTab, panelSize); break;
            case 2: UiHelpers.Panel("Log / Terminal", logTab.Draw, panelSize); break;
        }
    }

    private void DrawSelectedBlackjackTab()
    {
        switch (selectedBlackjackTab)
        {
            case 0: table.Draw(); break;
            case 1: players.Draw(); break;
            case 2: ledger.Draw(); break;
            case 3: rules.Draw(); break;
            case 4: trades.Draw(); break;
            case 5: history.Draw(); break;
            case 6: overlaySettings.Draw(); break;
            case 7: demo.Draw(); break;
        }
    }

    private void DrawSelectedDrtTab()
    {
        switch (selectedDrtTab)
        {
            case 0: deathRoll.Draw(); break;
            case 1: deathRoll.DrawBracketTab(); break;
            case 2: deathRoll.DrawSettingsTab(); break;
        }
    }

    public void DrawDrtBracketWindow()
    {
        deathRoll.DrawDetachedBracket();
    }

    private void DrawHeader()
    {
        ImGui.TextColored(GambaTheme.Gold, "GambaAssistant");
        DrawToolbarButtonsOnTabRow();
        ImGui.Separator();
    }

    private void DrawNavItem(int index, string label)
    {
        if (UiHelpers.VerticalNavItem(label, selectedTab == index, new Vector2(-1f, 34f)))
            selectedTab = index;
    }

    private void DrawActiveSectionNav()
    {
        if (selectedTab == 0)
        {
            ImGui.TextColored(GambaTheme.Gold, "Blackjack Tabs");
            DrawBlackjackSubNavItem(0, "Table");
            DrawBlackjackSubNavItem(1, "Players & Banks");
            DrawBlackjackSubNavItem(2, "Dealer Ledger");
            DrawBlackjackSubNavItem(3, "Rules");
            DrawBlackjackSubNavItem(4, "Trade Monitor");
            DrawBlackjackSubNavItem(5, "History / Export");
            DrawBlackjackSubNavItem(6, "Overlay");
            DrawBlackjackSubNavItem(7, "Demo / Test");
        }
        else if (selectedTab == 1)
        {
            ImGui.TextColored(GambaTheme.Gold, "DRT Tabs");
            DrawDrtSubNavItem(0, "Tournament");
            DrawDrtSubNavItem(1, "Bracket");
            DrawDrtSubNavItem(2, "Settings");
        }
    }

    private void DrawBlackjackSubNavItem(int index, string label)
    {
        if (UiHelpers.VerticalNavItem(label, selectedBlackjackTab == index, new Vector2(-1f, 28f)))
            selectedBlackjackTab = index;
    }

    private void DrawDrtSubNavItem(int index, string label)
    {
        if (UiHelpers.VerticalNavItem(label, selectedDrtTab == index, new Vector2(-1f, 28f)))
            selectedDrtTab = index;
    }

    private static string GetBlackjackTabName(int index) => index switch
    {
        0 => "Table",
        1 => "Players & Banks",
        2 => "Dealer Ledger",
        3 => "Rules",
        4 => "Trade Monitor",
        5 => "History / Export",
        6 => "Overlay",
        7 => "Demo / Test",
        _ => "Table",
    };

    private static string GetDrtTabName(int index) => index switch
    {
        0 => "Tournament",
        1 => "Bracket",
        2 => "Settings",
        _ => "Tournament",
    };

    private void DrawToolbarButtonsOnTabRow()
    {
        const string supportLabel = "      Support##gamba-kofi-support";
        const string settingsLabel = "Settings##gamba-main-top-settings";
        const float buttonGap = 8f;
        const float settingsWidth = 94f;

        var supportWidth = MathF.Max(116f, ImGui.CalcTextSize("Support").X + 52f);
        var totalWidth = settingsWidth + buttonGap + supportWidth;

        ImGui.SameLine();
        var available = ImGui.GetContentRegionAvail().X;
        if (available > totalWidth + 8f)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + available - totalWidth);

        if (ImGui.Button(settingsLabel, new Vector2(settingsWidth, 0)))
            openSettings();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Open GambaAssistant settings");

        ImGui.SameLine(0f, buttonGap);

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
