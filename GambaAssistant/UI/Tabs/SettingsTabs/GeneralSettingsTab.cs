using Dalamud.Bindings.ImGui;
using GambaAssistant.Games.Blackjack;
using GambaAssistant.UI.Components;

namespace GambaAssistant.UI.Tabs.SettingsTabs;

public sealed class GeneralSettingsTab
{
    private readonly Configuration config;
    private readonly BlackjackSession session;

    public GeneralSettingsTab(Configuration config, BlackjackSession session)
    {
        this.config = config;
        this.session = session;
    }

    private static void DrawDisabledWrapped(string text)
    {
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + Math.Max(180f, ImGui.GetContentRegionAvail().X));
        ImGui.TextDisabled(text);
        ImGui.PopTextWrapPos();
    }

    public void Draw()
    {
        UiHelpers.InfoBox("Dealer-Only Operation", "Only the dealer/host runs GambaAssistant. Players do not need the plugin.");

        UiHelpers.Card("Automation Timing", () =>
        {
            var delay = config.General.ChatQueueDelaySeconds;
            if (ImGui.InputFloat("Chat queue delay seconds", ref delay))
                config.General.ChatQueueDelaySeconds = Math.Max(0f, delay);

            var undo = config.General.UndoLimit;
            if (ImGui.InputInt("Undo history limit", ref undo))
                config.General.UndoLimit = Math.Clamp(undo, 1, 50);
        });

        UiHelpers.Card("Astrologian Immersion", () =>
        {
            ImGui.TextWrapped("Optional visual flavor only. Blackjack dice/card flow does not wait for these actions to complete.");

            var beneficOnTurn = config.General.AstrologianImmersionEnabled;
            if (ImGui.Checkbox("Cast Benefic on active player's turn", ref beneficOnTurn))
                config.General.AstrologianImmersionEnabled = beneficOnTurn;
            UiHelpers.Tooltip("Targets the active Blackjack player and casts Benefic once when their hand becomes active.");

            var astralDrawOnDeal = config.General.AstrologianAstralDrawOnDealEnabled;
            if (ImGui.Checkbox("Cast Astral Draw when dealing starts", ref astralDrawOnDeal))
                config.General.AstrologianAstralDrawOnDealEnabled = astralDrawOnDeal;
            UiHelpers.Tooltip("Casts Astral Draw once when the initial Blackjack deal starts.");

            var keepBattleModeOn = config.General.AstrologianKeepBattleModeOnEnabled;
            if (ImGui.Checkbox("Keep battle mode on during Blackjack", ref keepBattleModeOn))
                config.General.AstrologianKeepBattleModeOnEnabled = keepBattleModeOn;
            UiHelpers.Tooltip("Periodically refreshes battle mode while a Blackjack round is active.");
        });

        UiHelpers.Card("Exports", () =>
        {
            ImGui.TextWrapped("Optional custom folder for manual JSON/CSV exports. Leave blank to use the default GambaAssistant Exports folder inside Dalamud pluginConfigs.");
            var exportPath = config.General.ExportDirectory ?? string.Empty;
            ImGui.SetNextItemWidth(-1f);
            if (ImGui.InputText("##exportPath", ref exportPath, 512))
                config.General.ExportDirectory = exportPath;

            var activePath = string.IsNullOrWhiteSpace(config.General.ExportDirectory)
                ? "Default: pluginConfigs/GambaAssistant/Exports"
                : $"Custom: {config.General.ExportDirectory}";
            ImGui.TextDisabled(activePath);

            if (ImGui.Button("Use Default Export Folder"))
                config.General.ExportDirectory = string.Empty;
        });

        UiHelpers.Card("Session Status", () =>
        {
            ImGui.Text(session.IsActive ? "Session active: Yes" : "Session active: No");
        });
    }
}
