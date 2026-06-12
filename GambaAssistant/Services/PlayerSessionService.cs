using GambaAssistant.Games.Blackjack;
using GambaAssistant.Models.Players;

namespace GambaAssistant.Services;

public sealed class PlayerSessionService
{
    private readonly BlackjackSession session;
    private readonly LogService log;
    public PlayerSessionService(BlackjackSession session, LogService log) { this.session = session; this.log = log; }

    public void SyncParty(IEnumerable<PlayerSessionState> partyOrder)
    {
        foreach (var partyPlayer in partyOrder)
        {
            var existing = session.SessionPlayers.FirstOrDefault(p => p.Identity.Equals(partyPlayer.Identity));
            if (existing == null)
            {
                session.SessionPlayers.Add(partyPlayer);
                log.Add(LogCategory.Info, $"Added party member {partyPlayer.DisplayName} as {partyPlayer.Status}.");
            }
            else
            {
                existing.PartySlot = partyPlayer.PartySlot;
                if (existing.Status == PlayerStatus.LeftDisconnected) existing.Status = PlayerStatus.SittingOut;
            }
        }
        foreach (var existing in session.SessionPlayers.Where(p => p.Status != PlayerStatus.Dealer))
        {
            if (partyOrder.All(p => !p.Identity.Equals(existing.Identity)) && existing.Status != PlayerStatus.CashedOut)
            {
                existing.Status = PlayerStatus.LeftDisconnected;
                VoidActiveHandsForDisconnected(existing);
            }
        }
        session.SessionPlayers.Sort((a,b) => a.PartySlot.CompareTo(b.PartySlot));
    }

    public bool TryReserveBet(PlayerSessionState player, long amount, out string reason)
    {
        reason = string.Empty;
        if (session.Round.Phase != BlackjackPhase.BettingOpen) { reason = "Bets can only be confirmed while betting is open."; return false; }
        if (amount < session.Rules.MinimumBet || amount > session.Rules.MaximumBet) { reason = $"Bet must be between {session.Rules.MinimumBet:N0} and {session.Rules.MaximumBet:N0} gil."; return false; }
        if (amount > player.Bank.Available) { reason = "Bet cannot exceed available bank."; return false; }
        player.Bank.Available -= amount;
        player.Bank.ActiveBet += amount;
        player.Bank.LastBet = amount;
        player.BetConfirmed = true;
        player.Status = PlayerStatus.Playing;

        // Betting controls can be clicked more than once before the deal starts.
        // Treat each click as adding to the same confirmed hand bet instead of replacing
        // the hand. Otherwise settlement/export narration would only see the final
        // increment, even though ActiveBet and Available bank were updated correctly.
        var primaryHand = player.Hands.FirstOrDefault(h => h.HandNumber == 1);
        if (primaryHand == null)
        {
            player.Hands.Clear();
            player.Hands.Add(new BlackjackHand { HandNumber = 1, Bet = amount, OriginalBet = amount });
        }
        else
        {
            primaryHand.Bet += amount;
            primaryHand.OriginalBet += amount;
        }

        log.Add(LogCategory.RoundFlow, $"Reserved {amount:N0} gil bet for {player.DisplayName}. Total active bet: {player.Bank.ActiveBet:N0} gil.");
        return true;
    }

    public void AddBankDeposit(PlayerSessionState player, long amount)
    {
        player.Bank.Available += amount;
        player.Bank.LastTradeAmount = amount;
        log.Add(LogCategory.Trades, $"Bank deposit: {player.DisplayName} +{amount:N0} gil.");
    }

    public void VoidActiveHandsForDisconnected(PlayerSessionState player)
    {
        foreach (var hand in player.Hands.Where(h => !h.IsComplete))
        {
            hand.IsVoided = true;
            hand.IsComplete = true;
            player.Bank.Available += hand.Bet;
            player.Bank.ActiveBet -= hand.Bet;
            log.Add(LogCategory.Warnings, $"Voided active hand for disconnected player {player.DisplayName}; returned {hand.Bet:N0} gil bet to tracked bank.");
        }
    }
}
