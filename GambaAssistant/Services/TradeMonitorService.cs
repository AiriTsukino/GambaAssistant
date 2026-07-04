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
    public string DetectionStatus { get; private set; } = "Trade detection listens for visible client trade/chat log text and applies incoming gil as bank deposits and outgoing gil as cash-outs at any time. Manual entry remains available as a fallback when the client does not expose a stable trade line.";
    public TradeMonitorService(BlackjackSession session, DealerLedgerService ledger, LogService log) { this.session = session; this.ledger = ledger; this.log = log; }

    public TradeEntry AddManualTrade(PlayerIdentity from, PlayerIdentity to, long amount, TradeClassification classification, string note = "Manual entry")
        => AddTrade(from, to, amount, classification, note, manual: true);

    public TradeEntry AddDetectedIncomingTrade(string playerName, long amount, string note = "Detected from chat/log text")
    {
        var player = FindPlayerByName(playerName) ?? RehydrateTradePlayer(playerName);
        var dealer = session.SessionPlayers.FirstOrDefault(p => p.Status == PlayerStatus.Dealer) ?? session.SessionPlayers.FirstOrDefault();
        if (player is null || dealer is null)
        {
            var unknown = CreateIdentityFromDisplay(playerName);
            log.Add(LogCategory.Warnings, $"Detected possible incoming trade from {playerName} for {amount:N0} gil, but could not match them to a tracked table player.");
            return AddTrade(unknown, PlayerIdentity.UnknownDealer(), amount, TradeClassification.NeedsReview, note, manual: false);
        }

        return AddTrade(player.Identity, dealer.Identity, amount, TradeClassification.BuyInBankDeposit, note, manual: false);
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

        // 0.1.81: outgoing gil is a cash-out whenever it is detected. This lets
        // players cash out mid-round or between hands without phase-specific
        // bookkeeping blocking the bank update.
        return AddTrade(dealer.Identity, player.Identity, amount, TradeClassification.CashOut, note, manual: false);
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
                    if (depositor.Status is PlayerStatus.SittingOut or PlayerStatus.CashedOut or PlayerStatus.LeftDisconnected)
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

    public PlayerSessionState? ResolveSingleActivePartyPlayer() => ResolveSingleTrackedTradeCandidate();

    public PlayerSessionState? ResolveSingleTrackedTradeCandidate()
    {
        // Trade log lines often lose the player name and may still arrive after the
        // player has left the party. Keep disconnected tracked players eligible for
        // amount-only attribution, but only when there is exactly one safe candidate.
        var candidates = session.SessionPlayers
            .Where(p => p.Status != PlayerStatus.Dealer && p.Status != PlayerStatus.SpectatorStaff)
            .ToList();

        var activeOrFunded = candidates
            .Where(p => p.Status != PlayerStatus.CashedOut || p.Bank.TotalTracked > 0)
            .ToList();

        if (activeOrFunded.Count == 1)
            return activeOrFunded[0];

        return candidates.Count == 1 ? candidates[0] : null;
    }

    private bool TryGetPlayer(PlayerIdentity identity, out PlayerSessionState player)
    {
        var match = session.SessionPlayers.FirstOrDefault(p => p.Identity.Equals(identity))
            ?? session.SessionPlayers.FirstOrDefault(p => NormalizeName(p.Identity.Name) == NormalizeName(identity.Name))
            ?? session.SessionPlayers.FirstOrDefault(p => NamesLooselyMatch(p.DisplayName, identity.Display));

        if (match is null)
        {
            player = null!;
            return false;
        }

        player = match;
        return true;
    }


    private PlayerSessionState? RehydrateTradePlayer(string playerName)
    {
        var identity = CreateIdentityFromDisplay(playerName);
        if (string.IsNullOrWhiteSpace(identity.Name))
            return null;

        if (session.SessionPlayers.Any(p => p.Status == PlayerStatus.Dealer && NamesLooselyMatch(p.DisplayName, identity.Display)))
            return null;

        var nextSlot = Math.Max(2, session.SessionPlayers.Where(p => p.Status != PlayerStatus.Dealer).Select(p => p.PartySlot).DefaultIfEmpty(1).Max() + 1);
        var player = new PlayerSessionState
        {
            Identity = identity,
            PartySlot = nextSlot,
            Status = PlayerStatus.Playing
        };

        session.SessionPlayers.Add(player);
        log.Add(LogCategory.Trades, $"Re-added trade player {player.DisplayName} from detected trade text.");
        return player;
    }

    private static PlayerIdentity CreateIdentityFromDisplay(string display)
    {
        var cleaned = display.Trim();
        var atIndex = cleaned.IndexOf('@', StringComparison.Ordinal);
        if (atIndex > 0)
        {
            var name = cleaned[..atIndex].Trim();
            var world = cleaned[(atIndex + 1)..].Trim();
            return new PlayerIdentity(name, world);
        }

        var worldName = GetLocalWorldName();
        return new PlayerIdentity(cleaned, worldName);
    }

    private static string GetLocalWorldName()
    {
        try
        {
            return DalamudServices.PlayerState.IsLoaded
                ? DalamudServices.PlayerState.HomeWorld.Value.Name.ExtractText()
                : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool NamesLooselyMatch(string left, string right)
    {
        var a = NormalizeName(left);
        var b = NormalizeName(right);
        return !string.IsNullOrWhiteSpace(a)
            && !string.IsNullOrWhiteSpace(b)
            && (a == b || a.Contains(b, StringComparison.OrdinalIgnoreCase) || b.Contains(a, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeName(string value) => value.Trim().Replace("@", " ").Replace("  ", " ").ToLowerInvariant();
}
