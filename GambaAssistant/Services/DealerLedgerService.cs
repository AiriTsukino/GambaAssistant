using GambaAssistant.Games.Blackjack;
using GambaAssistant.Models.Ledger;
using GambaAssistant.Models.Players;

namespace GambaAssistant.Services;

public sealed class DealerLedgerService
{
    private readonly BlackjackSession session;
    private readonly LogService log;
    public DealerLedgerService(BlackjackSession session, LogService log) { this.session = session; this.log = log; }
    public DealerLedger Ledger => session.DealerLedger;

    public void RecordTrade(TradeEntry entry)
    {
        Ledger.Trades.Add(entry);
        switch (entry.Classification)
        {
            case TradeClassification.BuyInBankDeposit: Ledger.TotalBuyInsDeposits += entry.Amount; break;
            case TradeClassification.CashOut: Ledger.TotalCashOuts += entry.Amount; break;
            case TradeClassification.DealerTip: Ledger.DealerTips += entry.Amount; break;
            case TradeClassification.VenueTip: Ledger.VenueTips += entry.Amount; break;
        }
        log.Add(LogCategory.Trades, $"{entry.From.Display} → {entry.To.Display}: {entry.Amount:N0} gil | {entry.Phase} | {entry.Classification}");
    }

    public void ApplySettlementDelta(long playerDelta)
    {
        Ledger.GameProfitLoss -= playerDelta;
        if (playerDelta > 0) Ledger.TotalPayouts += playerDelta;
        else if (playerDelta < 0) Ledger.TotalBets += -playerDelta;
    }

    public void ManualAdjustment(PlayerIdentity player, long amount, AdjustmentType type, string reason)
    {
        Ledger.Adjustments.Add(new AdjustmentEntry { Player = player, Amount = amount, Type = type, Reason = reason });
        Ledger.MiscAdjustments += amount;
        log.Add(LogCategory.Info, $"Manual adjustment {amount:N0} gil for {player.Display}: {type} — {reason}");
    }

    public void RecordTip(long amount, TradeClassification tipType, string note)
    {
        if (amount <= 0)
        {
            log.Add(LogCategory.Warnings, "Tip amount must be greater than 0 gil.");
            return;
        }

        if (tipType is not TradeClassification.DealerTip and not TradeClassification.VenueTip)
            tipType = TradeClassification.DealerTip;

        Ledger.Tips.Add(new TipEntry
        {
            Player = PlayerIdentity.UnknownDealer(),
            Amount = amount,
            TipType = tipType,
        });

        if (tipType == TradeClassification.DealerTip)
            Ledger.DealerTips += amount;
        else
            Ledger.VenueTips += amount;

        var label = tipType == TradeClassification.DealerTip ? "dealer" : "venue";
        log.Add(LogCategory.Info, $"Recorded {label} tip {amount:N0} gil{(string.IsNullOrWhiteSpace(note) ? string.Empty : $": {note}")}");
    }
}
