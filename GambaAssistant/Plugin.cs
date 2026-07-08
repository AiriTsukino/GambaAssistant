using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using GambaAssistant.Games.Blackjack;
using GambaAssistant.Games.DeathRoll;
using GambaAssistant.Services;
using GambaAssistant.UI;
using Dalamud.Plugin;

namespace GambaAssistant;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/gambaassistant";
    private const string SettingsCommandName = "/gambaassistantsettings";
    private readonly WindowSystem windowSystem = new("GambaAssistant");
    private readonly Configuration config;
    private readonly BlackjackSession session;
    private readonly LogService log;
    private readonly PersistenceService persistence;
    private readonly ProfileService profiles;
    private readonly ChatQueueService chatQueue;
    private readonly PartyService party;
    private readonly PlayerSessionService players;
    private readonly DealerLedgerService ledger;
    private readonly TradeMonitorService tradeMonitor;
    private readonly DiceService dice;
    private readonly ChatMonitorService chatMonitor;
    private readonly OverlayService overlays;
    private readonly UndoService undo;
    private readonly DemoModeService demo;
    private readonly ExportService exports;
    private readonly DeathRollTournamentService deathRoll;
    private readonly MainWindow mainWindow;
    private readonly SettingsWindow settingsWindow;
    private DeathRollBracketWindow? drtBracketWindow;
    private DateTimeOffset nextBlackjackAutosaveAt = DateTimeOffset.MinValue;
    private DateTimeOffset nextDeathRollAutosaveAt = DateTimeOffset.MinValue;

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        DalamudServices.Initialize(pluginInterface);
        config = DalamudServices.PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        log = new LogService();
        persistence = new PersistenceService(config);
        profiles = new ProfileService(config, persistence, log);
        session = persistence.LoadBlackjackSession() ?? new BlackjackSession();
        if (session.Rules is null)
            session.Rules = profiles.ActiveProfile.BlackjackRules;
        chatQueue = new ChatQueueService(config, log);
        party = new PartyService(log);
        players = new PlayerSessionService(session, log);
        ledger = new DealerLedgerService(session, log);
        tradeMonitor = new TradeMonitorService(session, ledger, log);
        dice = new DiceService(session, chatQueue, log);
        chatMonitor = new ChatMonitorService(dice, tradeMonitor, log);
        overlays = new OverlayService(config, session, log);
        undo = new UndoService(config, log);
        demo = new DemoModeService(session, chatQueue, log);
        exports = new ExportService(persistence, config, session, log);
        var restoredDeathRollTournament = persistence.LoadDeathRollTournament();
        deathRoll = new DeathRollTournamentService(config, party, chatQueue, log, restoredDeathRollTournament);

        mainWindow = new MainWindow(config, session, profiles, party, players, ledger, tradeMonitor, dice, chatQueue, overlays, undo, demo, exports, deathRoll, log, OpenSettingsWindow, SetDrtBracketWindowOpen) { IsOpen = config.WindowVisible };
        settingsWindow = new SettingsWindow(config, session, profiles, persistence, log) { IsOpen = config.SettingsWindowVisible };
        drtBracketWindow = new DeathRollBracketWindow(mainWindow) { IsOpen = config.DeathRoll.BracketWindowOpen };
        windowSystem.AddWindow(mainWindow);
        windowSystem.AddWindow(settingsWindow);
        windowSystem.AddWindow(drtBracketWindow);

        DalamudServices.CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand) { HelpMessage = "Toggle GambaAssistant table window." });
        DalamudServices.CommandManager.AddHandler(SettingsCommandName, new CommandInfo(OnSettingsCommand) { HelpMessage = "Toggle GambaAssistant settings window." });
        DalamudServices.PluginInterface.UiBuilder.Draw += DrawUi;
        DalamudServices.PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        DalamudServices.PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        persistence.SaveNow();
        persistence.SaveBlackjackSession(session);
        persistence.SaveDeathRollTournament(deathRoll.Tournament);
    }

    private void OnCommand(string command, string arguments) => ToggleMainUi();
    private void OnSettingsCommand(string command, string arguments) => ToggleConfigUi();
    private void OpenSettingsWindow() { config.SettingsWindowVisible = true; settingsWindow.IsOpen = true; persistence.SaveNow(); }

    private void SetDrtBracketWindowOpen(bool isOpen)
    {
        config.DeathRoll.BracketWindowOpen = isOpen;
        if (drtBracketWindow is not null)
            drtBracketWindow.IsOpen = isOpen;
        persistence.SaveNow();
    }

    private void ToggleMainUi() { config.WindowVisible = !config.WindowVisible; mainWindow.IsOpen = config.WindowVisible; persistence.SaveNow(); }
    private void ToggleConfigUi() { config.SettingsWindowVisible = !config.SettingsWindowVisible; settingsWindow.IsOpen = config.SettingsWindowVisible; persistence.SaveNow(); }

    private void DrawUi()
    {
        windowSystem.Draw();
        overlays.Draw();
        var saveVisibility = false;
        if (config.WindowVisible != mainWindow.IsOpen || config.SettingsWindowVisible != settingsWindow.IsOpen)
        {
            config.WindowVisible = mainWindow.IsOpen;
            config.SettingsWindowVisible = settingsWindow.IsOpen;
            saveVisibility = true;
        }

        if (drtBracketWindow is not null && config.DeathRoll.BracketWindowOpen != drtBracketWindow.IsOpen)
        {
            config.DeathRoll.BracketWindowOpen = drtBracketWindow.IsOpen;
            saveVisibility = true;
        }

        if (saveVisibility)
            persistence.SaveNow();

        AutosaveBlackjackSession();
        AutosaveDeathRollTournament();
    }

    private void AutosaveBlackjackSession()
    {
        if (!config.General.BlackjackSessionAutosaveEnabled)
            return;

        var now = DateTimeOffset.UtcNow;
        if (now < nextBlackjackAutosaveAt)
            return;

        nextBlackjackAutosaveAt = now.AddSeconds(1);
        persistence.SaveBlackjackSession(session);
    }

    private void AutosaveDeathRollTournament()
    {
        if (!config.General.DeathRollSessionAutosaveEnabled)
            return;

        var now = DateTimeOffset.UtcNow;
        if (now < nextDeathRollAutosaveAt)
            return;

        nextDeathRollAutosaveAt = now.AddSeconds(1);
        persistence.SaveDeathRollTournament(deathRoll.Tournament);
    }

    public void Dispose()
    {
        persistence.SaveBlackjackSession(session);
        persistence.SaveDeathRollTournament(deathRoll.Tournament);
        persistence.SaveNow();
        DalamudServices.PluginInterface.UiBuilder.Draw -= DrawUi;
        DalamudServices.PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        DalamudServices.PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        DalamudServices.CommandManager.RemoveHandler(CommandName);
        DalamudServices.CommandManager.RemoveHandler(SettingsCommandName);
        windowSystem.RemoveAllWindows();
        deathRoll.Dispose();
        chatMonitor.Dispose();
        dice.Dispose();
        chatQueue.Dispose();
        persistence.Dispose();
    }
}
