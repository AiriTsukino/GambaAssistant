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

    public void NormalizeForSave()
    {
        Rules ??= new BlackjackRules();
        Round ??= new BlackjackRound();
        DealerLedger ??= new DealerLedger();
        SessionPlayers ??= [];
        Round.Players ??= [];
        Round.DealerHand ??= new BlackjackHand { HandNumber = 0 };
        NormalizeDealerEntries();
    }

    public void NormalizeAfterLoad()
    {
        NormalizeForSave();

        for (var i = 0; i < SessionPlayers.Count; i++)
            EnsurePlayerState(SessionPlayers[i]);

        NormalizeDealerEntries();

        var restoredRoundPlayers = Round.Players.ToList();
        Round.Players.Clear();

        foreach (var roundPlayer in restoredRoundPlayers)
        {
            EnsurePlayerState(roundPlayer);
            var sessionPlayer = SessionPlayers.FirstOrDefault(p => IsSamePlayer(p.Identity, roundPlayer.Identity));
            if (sessionPlayer is null)
            {
                SessionPlayers.Add(roundPlayer);
                sessionPlayer = roundPlayer;
            }
            else
            {
                CopyPlayerState(roundPlayer, sessionPlayer);
            }

            if (Round.Players.All(p => !IsSamePlayer(p.Identity, sessionPlayer.Identity)))
                Round.Players.Add(sessionPlayer);
        }

        NormalizeDealerEntries();
        RestoreRoundPlayerStatuses();

        if (Round.ActivePlayerIndex < 0)
            Round.ActivePlayerIndex = 0;
        if (Round.Players.Count == 0)
            Round.ActivePlayerIndex = 0;
        else if (Round.ActivePlayerIndex >= Round.Players.Count)
            Round.ActivePlayerIndex = Round.Players.Count - 1;

        if (Round.ActiveHandIndex < 0)
            Round.ActiveHandIndex = 0;
        if (ActivePlayer is { } activePlayer && activePlayer.Hands.Count > 0 && Round.ActiveHandIndex >= activePlayer.Hands.Count)
            Round.ActiveHandIndex = activePlayer.Hands.Count - 1;
    }

    private void RestoreRoundPlayerStatuses()
    {
        if (Round.Phase is BlackjackPhase.Idle or BlackjackPhase.CashOutBetweenHands)
            return;

        foreach (var player in Round.Players.Where(p => p.Status is PlayerStatus.LeftDisconnected or PlayerStatus.SittingOut))
        {
            if (player.BetConfirmed
                || player.Bank.ActiveBet > 0
                || player.Hands.Any(h => !h.IsVoided && h.Cards.Count > 0))
                player.Status = PlayerStatus.Playing;
        }
    }

    private void NormalizeDealerEntries()
    {
        var dealers = SessionPlayers.Where(p => p.Status == PlayerStatus.Dealer).ToList();
        if (dealers.Count == 0)
            return;

        var dealer = dealers
            .OrderByDescending(GetDealerIdentityPriority)
            .ThenBy(p => p.PartySlot <= 0 ? int.MaxValue : p.PartySlot)
            .First();

        dealer.Status = PlayerStatus.Dealer;
        dealer.PartySlot = 1;
        dealer.BetConfirmed = false;
        dealer.Bank ??= new PlayerBank();
        dealer.Hands ??= [];
        dealer.Hands.Clear();

        foreach (var duplicate in dealers.Where(p => !ReferenceEquals(p, dealer)))
            SessionPlayers.Remove(duplicate);

        Round.Players.RemoveAll(p => p.Status == PlayerStatus.Dealer || IsSamePlayer(p.Identity, dealer.Identity));
    }

    private static int GetDealerIdentityPriority(PlayerSessionState player)
    {
        var hasName = !string.IsNullOrWhiteSpace(player.Identity.Name);
        var hasWorld = !string.IsNullOrWhiteSpace(player.Identity.World);
        var isPlaceholder = !hasName || string.Equals(player.Identity.Name, "Dealer", StringComparison.OrdinalIgnoreCase);

        return (isPlaceholder ? 0 : 2) + (hasWorld ? 1 : 0);
    }

    private static void EnsurePlayerState(PlayerSessionState player)
    {
        player.Bank ??= new PlayerBank();
        player.Hands ??= [];
        player.RoundHistory ??= [];
    }

    private static void CopyPlayerState(PlayerSessionState source, PlayerSessionState target)
    {
        target.Identity = source.Identity;
        target.PartySlot = source.PartySlot;
        target.Status = source.Status;
        target.Bank = source.Bank ?? new PlayerBank();
        target.Hands = source.Hands ?? [];
        target.RoundHistory = source.RoundHistory ?? [];
        target.BetConfirmed = source.BetConfirmed;
    }

    private static bool IsSamePlayer(PlayerIdentity a, PlayerIdentity b)
    {
        if (!string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase))
            return false;

        return string.IsNullOrWhiteSpace(a.World)
            || string.IsNullOrWhiteSpace(b.World)
            || string.Equals(a.World, b.World, StringComparison.OrdinalIgnoreCase);
    }

}
