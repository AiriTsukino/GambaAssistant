using GambaAssistant.Games.Blackjack;
using GambaAssistant.Models.Ledger;
using GambaAssistant.Models.Players;

namespace GambaAssistant.Services;

public sealed class TradeMonitorService
{
    private readonly BlackjackSession session;
    private readonly DealerLedgerService ledger;
    private readonly LogService log;
    public List<TradeEntry> Trades { get; } = [];
    public string DetectionStatus { get; private set; } = "Trade detection listens for visible client trade/chat log text when available. Because gil trade packets are not guaranteed through stable Dalamud APIs, manual entry remains available and bank-affecting classifications are logged.";
    public TradeMonitorService(BlackjackSession session, DealerLedgerService ledger, LogService log) { this.session = session; this.ledger = ledger; this.log = log; }

    public TradeEntry AddManualTrade(PlayerIdentity from, PlayerIdentity to, long amount, TradeClassification classification, string note = "Manual entry")
        => AddTrade(from, to, amount, classification, note, manual: true);

    public TradeEntry AddDetectedIncomingTrade(string playerName, long amount, string note = "Detected from chat/log text")
    {
        var player = FindPlayerByName(playerName);
        var dealer = session.SessionPlayers.FirstOrDefault(p => p.Status == PlayerStatus.Dealer) ?? session.SessionPlayers.FirstOrDefault();
        if (player is null || dealer is null)
        {
            var unknown = new PlayerIdentity(playerName.Trim(), string.Empty);
            log.Add(LogCategory.Warnings, $"Detected possible incoming trade from {playerName} for {amount:N0} gil, but could not match them to a tracked party player.");
            return AddTrade(unknown, PlayerIdentity.UnknownDealer(), amount, TradeClassification.NeedsReview, note, manual: false);
        }

        var classification = session.Round.Phase is BlackjackPhase.Idle or BlackjackPhase.BettingOpen or BlackjackPhase.CashOutBetweenHands
            ? TradeClassification.BuyInBankDeposit
            : TradeClassification.NeedsReview;

        return AddTrade(player.Identity, dealer.Identity, amount, classification, note, manual: false);
    }


    public TradeEntry AddDetectedOutgoingTrade(string playerName, long amount, string note = "Detected outgoing cash-out from chat/log text")
    {
        var player = FindPlayerByName(playerName);
        var dealer = session.SessionPlayers.FirstOrDefault(p => p.Status == PlayerStatus.Dealer) ?? session.SessionPlayers.FirstOrDefault();
        if (player is null || dealer is null)
        {
            var unknown = new PlayerIdentity(playerName.Trim(), string.Empty);
            log.Add(LogCategory.Warnings, $"Detected possible outgoing trade to {playerName} for {amount:N0} gil, but could not match them to a tracked party player.");
            return AddTrade(PlayerIdentity.UnknownDealer(), unknown, amount, TradeClassification.NeedsReview, note, manual: false);
        }

        var classification = session.Round.Phase is BlackjackPhase.Idle or BlackjackPhase.CashOutBetweenHands
            ? TradeClassification.CashOut
            : TradeClassification.NeedsReview;

        if (classification == TradeClassification.NeedsReview)
            log.Add(LogCategory.Warnings, $"Detected outgoing gil trade to {player.DisplayName} for {amount:N0} gil during {session.Round.Phase}; left as NeedsReview instead of lowering bank because cash-outs only auto-apply while idle or between hands.");

        return AddTrade(dealer.Identity, player.Identity, amount, classification, note, manual: false);
    }

    public void Reclassify(TradeEntry entry, TradeClassification classification, string note)
    {
        // Reclassification currently logs the audit change. Bank-affecting corrections should be entered
        // as a manual correction/deposit so the delta is explicit and undoable.
        entry.Classification = classification;
        entry.Note = note;
        log.Add(LogCategory.Trades, $"Trade {entry.Id} reclassified as {classification}: {note}");
    }

    private TradeEntry AddTrade(PlayerIdentity from, PlayerIdentity to, long amount, TradeClassification classification, string note, bool manual)
    {
        amount = Math.Max(0, amount);
        var entry = new TradeEntry
        {
            From = from,
            To = to,
            Amount = amount,
            Classification = classification,
            Phase = session.Round.Phase.ToString(),
            Note = note,
            Manual = manual
        };

        Trades.Add(entry);
        ApplyTradeEffects(entry);
        ledger.RecordTrade(entry);
        log.Add(LogCategory.Trades, $"{(manual ? "Manual" : "Detected")} trade entered: {from.Display} → {to.Display} {amount:N0} gil as {classification}.");
        return entry;
    }

    private void ApplyTradeEffects(TradeEntry entry)
    {
        switch (entry.Classification)
        {
            case TradeClassification.BuyInBankDeposit:
            case TradeClassification.Bet:
                if (TryGetPlayer(entry.From, out var depositor))
                {
                    depositor.Bank.Available += entry.Amount;
                    depositor.Bank.LastTradeAmount = entry.Amount;
                    if (depositor.Status == PlayerStatus.SittingOut)
                        depositor.Status = PlayerStatus.Playing;
                    log.Add(LogCategory.Trades, $"Bank deposit applied: {depositor.DisplayName} +{entry.Amount:N0} gil.");
                }
                break;
            case TradeClassification.CashOut:
                if (TryGetPlayer(entry.To, out var cashedOutPlayer))
                {
                    cashedOutPlayer.Bank.Available = Math.Max(0, cashedOutPlayer.Bank.Available - entry.Amount);
                    cashedOutPlayer.Bank.LastTradeAmount = entry.Amount;
                    if (cashedOutPlayer.Bank.TotalTracked == 0)
                        cashedOutPlayer.Status = PlayerStatus.CashedOut;
                    log.Add(LogCategory.Trades, $"Cash-out applied: {cashedOutPlayer.DisplayName} -{entry.Amount:N0} gil.");
                }
                break;
        }
    }

    private PlayerSessionState? FindPlayerByName(string name)
    {
        var normalized = NormalizeName(name);
        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        return session.SessionPlayers.FirstOrDefault(p =>
            NormalizeName(p.Identity.Name) == normalized
            || NormalizeName(p.DisplayName) == normalized
            || NormalizeName(p.DisplayName).StartsWith(normalized + "@")
            || normalized.StartsWith(NormalizeName(p.Identity.Name) + " ")
            || normalized.Contains(NormalizeName(p.DisplayName))
            || NormalizeName(p.DisplayName).Contains(normalized));
    }

    public PlayerSessionState? ResolveSingleActivePartyPlayer()
    {
        var candidates = session.SessionPlayers
            .Where(p => p.Status != PlayerStatus.Dealer && p.Status != PlayerStatus.LeftDisconnected && p.Status != PlayerStatus.CashedOut)
            .ToList();
        return candidates.Count == 1 ? candidates[0] : null;
    }

    private bool TryGetPlayer(PlayerIdentity identity, out PlayerSessionState player)
    {
        player = session.SessionPlayers.FirstOrDefault(p => p.Identity.Equals(identity))
            ?? session.SessionPlayers.FirstOrDefault(p => NormalizeName(p.Identity.Name) == NormalizeName(identity.Name))!;
        return player is not null;
    }

    private static string NormalizeName(string value) => value.Trim().Replace("@", " ").Replace("  ", " ").ToLowerInvariant();
}
