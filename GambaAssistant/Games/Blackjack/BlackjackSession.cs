using GambaAssistant.Models.Games;
using GambaAssistant.Models.Ledger;
using GambaAssistant.Models.Players;

namespace GambaAssistant.Games.Blackjack;

[Serializable]
public sealed class BlackjackSession : IGameSession
{
    public string GameName => "Blackjack";
    public bool IsActive => Round.Phase != BlackjackPhase.Idle;
    public BlackjackRules Rules { get; set; } = new();
    public BlackjackRound Round { get; set; } = new();
    public DealerLedger DealerLedger { get; set; } = new();
    public List<PlayerSessionState> SessionPlayers { get; set; } = [];
    public bool ChatPaused { get; set; }
    public string QueueStatus { get; set; } = "Idle";
    public PlayerSessionState? ActivePlayer => ActivePlayerIndexInRange ? Round.Players[Round.ActivePlayerIndex] : null;
    public BlackjackHand? ActiveHand => ActivePlayer is { } p && Round.ActiveHandIndex >= 0 && Round.ActiveHandIndex < p.Hands.Count ? p.Hands[Round.ActiveHandIndex] : null;
    private bool ActivePlayerIndexInRange => Round.ActivePlayerIndex >= 0 && Round.ActivePlayerIndex < Round.Players.Count;

    public void ResetNight()
    {
        Round = new BlackjackRound();
        DealerLedger = new DealerLedger();
        SessionPlayers.Clear();
        ChatPaused = false;
        QueueStatus = "Idle";
    }
}
