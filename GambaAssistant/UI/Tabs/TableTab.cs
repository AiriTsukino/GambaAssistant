using Dalamud.Bindings.ImGui;
using GambaAssistant.Games.Blackjack;
using GambaAssistant.Models.Players;
using GambaAssistant.Services;
using GambaAssistant.UI.Components;

namespace GambaAssistant.UI.Tabs;

public sealed class TableTab
{
    private readonly Configuration config;
    private readonly BlackjackSession session;
    private readonly ProfileService profiles;
    private readonly PartyService party;
    private readonly PlayerSessionService players;
    private readonly DealerLedgerService dealerLedger;
    private readonly DiceService dice;
    private readonly ChatQueueService chat;
    private readonly UndoService undo;
    private readonly LogService log;
    private readonly Action openSettings;
    private readonly Dictionary<string, long> customBets = new();
    private readonly Dictionary<string, long> quickDeposits = new();
    private int pendingInitialDealCards;
    private bool dealerTurnStarted;
    private bool allBustRoundOverAnnounced;
    private double nextLivePartySyncTime;

    public TableTab(Configuration config, BlackjackSession session, ProfileService profiles, PartyService party, PlayerSessionService players, DealerLedgerService dealerLedger, DiceService dice, ChatQueueService chat, UndoService undo, LogService log, Action openSettings)
    {
        this.config = config;
        this.session = session;
        this.profiles = profiles;
        this.party = party;
        this.players = players;
        this.dealerLedger = dealerLedger;
        this.dice = dice;
        this.chat = chat;
        this.undo = undo;
        this.log = log;
        this.openSettings = openSettings;
    }

    public void Draw()
    {
        AutoSyncLiveParty();
        DrawStatusHeader();
        DrawRoundControls();
        DrawDealerCard();
        DrawPlayerTable();
    }


    private void AutoSyncLiveParty()
    {
        if (chat.DemoMode)
            return;

        var now = ImGui.GetTime();
        if (now < nextLivePartySyncTime)
            return;

        nextLivePartySyncTime = now + 1.0;
        players.SyncParty(party.GetPartyTableOrder());
    }

    private void DrawStatusHeader()
    {
        UiHelpers.Card("Table Status", () =>
        {
            ImGui.Columns(3, "##table-status-columns", false);
            ImGui.TextColored(GambaTheme.Gold, $"Round {session.Round.RoundNumber}");
            ImGui.Text($"Phase: {session.Round.Phase}");
            ImGui.NextColumn();
            ImGui.Text($"Chat/Dice: {chat.Status}");
            ImGui.Text($"Dice pending/queued: {dice.QueuedCount}");
            if (dice.Pending is not null)
                ImGui.TextDisabled($"Waiting for visible dealer dice: {dice.Pending.Purpose}");
            ImGui.TextDisabled(chat.DemoMode
                ? "Party sync: Demo mode isolated"
                : $"Party sync: {party.LastLivePartyMemberCount} live party member(s)");
            ImGui.NextColumn();
            var overlayEnabled = config.Overlay.Enabled;
            if (ImGui.Checkbox("Enable overlay", ref overlayEnabled))
                config.Overlay.Enabled = overlayEnabled;

            var compactOverlay = config.Overlay.Compact;
            if (ImGui.Checkbox("Compact overlay", ref compactOverlay))
                config.Overlay.Compact = compactOverlay;

            if (ImGui.Button("Settings")) openSettings();
            ImGui.Columns(1);
        });
    }

    private void DrawRoundControls()
    {
        UiHelpers.Card("Dealer Controls", () =>
        {
            if (ImGui.Button("Pause")) chat.Pause();
            ImGui.SameLine();
            if (ImGui.Button("Resume")) chat.Resume();
            ImGui.SameLine();
            if (ImGui.Button("Panic Stop"))
            {
                chat.PanicClear();
                dice.ClearPendingAndQueued();
                pendingInitialDealCards = 0;
                dealerTurnStarted = false;
                allBustRoundOverAnnounced = false;
            }
            UiHelpers.Tooltip("Stops queued party messages and dice commands, clears pending dice rolls, then pauses automation so the dealer can recover safely.");

            ImGui.Separator();

            if (ImGui.Button("Sync Party Now"))
            {
                players.SyncParty(party.GetPartyTableOrder());
                nextLivePartySyncTime = ImGui.GetTime() + 1.0;
            }
            UiHelpers.Tooltip("Live mode automatically syncs the FFXIV party list about once per second. This button forces an immediate refresh. Demo mode remains isolated.");

            ImGui.SameLine();
            if (UiHelpers.DisabledAwareButton("Open Betting", session.Round.Phase is BlackjackPhase.Idle or BlackjackPhase.CashOutBetweenHands, "Betting can only open between hands."))
            {
                dealerTurnStarted = false;
                allBustRoundOverAnnounced = false;
                session.Round.Phase = BlackjackPhase.BettingOpen;
                chat.EnqueueParty($"Betting is open for next round. Table limits: {session.Rules.MinimumBet:N0}-{session.Rules.MaximumBet:N0} gil.");
                log.Add(LogCategory.RoundFlow, $"Betting opened for internal Round {session.Round.RoundNumber}.");
            }

            ImGui.SameLine();
            if (UiHelpers.DisabledAwareButton("Start Dealing", session.Round.Phase == BlackjackPhase.BettingOpen && session.SessionPlayers.Any(p => p.BetConfirmed), "Confirm at least one valid bet first."))
                StartDealing();

            ImGui.SameLine();
            if (UiHelpers.DisabledAwareButton("Settle Round", session.Round.Phase == BlackjackPhase.Settlement, "Settlement is available once player/dealer resolution is complete."))
                SettleRound();

            ImGui.SameLine();
            var hasUndo = undo.Actions.Count > 0;
            if (UiHelpers.DisabledAwareButton($"Undo Last ({undo.Actions.Count})", hasUndo, "There are no reversible actions in the current undo stack."))
                undo.TryUndoLast();

            var nextUndo = undo.Actions.FirstOrDefault();
            if (nextUndo is not null)
                ImGui.TextDisabled($"Next undo: {nextUndo.Label}");

            if (UiHelpers.ConfirmingButton("Reset Night", "Confirm Reset Night", "This clears the current-night banks, ledger, logs, round history, and active hands. Exports are not automatic."))
            {
                session.ResetNight();
                log.Clear();
                undo.Clear();
                pendingInitialDealCards = 0;
                dealerTurnStarted = false;
                allBustRoundOverAnnounced = false;
            }
        });
    }



    private void DrawDealerCard()
    {
        UiHelpers.Card("Dealer Hand / Bank", () =>
        {
            var ledger = dealerLedger.Ledger;
            ImGui.TextColored(GambaTheme.Gold, $"Dealer tracked gil: {ledger.ExpectedDealerGil:N0} gil");
            ImGui.TextDisabled($"Starting {ledger.StartingGil:N0} + game/tips/adjustments/deposits - cash-outs");
            if (ledger.ActualEndingGil.HasValue)
                ImGui.TextDisabled($"Actual ending gil entered: {ledger.ActualEndingGil.Value:N0} gil | Difference: {ledger.Difference.GetValueOrDefault():N0} gil");
            ImGui.Separator();
            ImGui.Text($"Cards: {session.Round.DealerHand.CardText}");
            ImGui.Text($"Total: {session.Round.DealerHand.TotalText}");
            if (session.Round.Phase == BlackjackPhase.DealerTurn)
                ImGui.TextDisabled(dealerTurnStarted ? "Dealer automation is resolving." : "Dealer turn is ready.");
            else if (session.Round.Phase == BlackjackPhase.Settlement && AllPlayerHandsAreTerminalLosses())
                ImGui.TextDisabled("All active players are bust/void. Dealer hand is skipped for this round.");
        });
    }

    private void DrawPlayerTable()
    {
        UiHelpers.Card("Party Table", () =>
        {
            foreach (var p in session.SessionPlayers.OrderBy(p => p.PartySlot))
                DrawPlayerRow(p);
        });
    }

    private void DrawPlayerRow(PlayerSessionState p)
    {
        ImGui.PushID(p.Identity.ToString());
        ImGui.Separator();

        ImGui.TextColored(p.Status == PlayerStatus.Dealer ? GambaTheme.Gold : GambaTheme.Text, $"{p.PartySlot}. {p.DisplayName}");
        ImGui.SameLine();
        ImGui.TextDisabled($"Status: {p.Status}");
        ImGui.SameLine();
        ImGui.TextDisabled($"Bank: {p.Bank.Available:N0} + Active {p.Bank.ActiveBet:N0} = {p.Bank.TotalTracked:N0}");

        if (p.Status == PlayerStatus.Dealer)
        {
            ImGui.PopID();
            return;
        }

        if (session.Round.Phase == BlackjackPhase.BettingOpen)
            DrawBettingControls(p);

        DrawHands(p);
        DrawActiveHandControls(p);
        ImGui.PopID();
    }

    private void DrawBettingControls(PlayerSessionState p)
    {
        ImGui.Indent(12f);
        ImGui.TextDisabled("Bet controls");
        if (UiHelpers.DisabledAwareButton("Min Bet", p.Bank.Available >= session.Rules.MinimumBet, "Player does not have enough available bank for the table minimum."))
            ReserveBetWithUndo(p, session.Rules.MinimumBet, "Min bet");
        ImGui.SameLine();
        if (UiHelpers.DisabledAwareButton("Last Bet", p.Bank.LastBet > 0 && p.Bank.Available >= p.Bank.LastBet, "No last bet is available, or the player lacks available bank."))
            ReserveBetWithUndo(p, p.Bank.LastBet, "Last bet");
        ImGui.SameLine();
        if (UiHelpers.DisabledAwareButton("Recent Trade", p.Bank.LastTradeAmount > 0 && p.Bank.Available >= p.Bank.LastTradeAmount, "No recent trade is available, or the player lacks available bank."))
            ReserveBetWithUndo(p, p.Bank.LastTradeAmount, "Recent trade bet");

        var betKey = p.Identity.ToString();
        customBets.TryGetValue(betKey, out var customBet);
        ImGui.SetNextItemWidth(150);
        if (UiHelpers.InputGil($"Custom##custom-bet-{betKey}", ref customBet))
            customBets[betKey] = customBet;
        ImGui.SameLine();
        if (ImGui.Button($"Bet##bet-{betKey}"))
            ReserveBetWithUndo(p, customBet, "Custom bet");

        quickDeposits.TryGetValue(betKey, out var quickDeposit);
        ImGui.SetNextItemWidth(150);
        if (UiHelpers.InputGil($"Quick bank add##quick-bank-{betKey}", ref quickDeposit))
            quickDeposits[betKey] = quickDeposit;
        ImGui.SameLine();
        if (ImGui.Button($"Add Bank##quick-add-bank-{betKey}"))
        {
            players.AddBankDeposit(p, quickDeposit);
            log.Add(LogCategory.Trades, $"Quick bank add from Table tab: {p.DisplayName} +{quickDeposit:N0} gil.");
        }
        UiHelpers.Tooltip("Use this as a live fallback if the client does not expose a trade message for automatic detection.");
        ImGui.Unindent(12f);
    }

    private void DrawHands(PlayerSessionState p)
    {
        if (p.Hands.Count == 0)
        {
            ImGui.TextDisabled("No active hand.");
            return;
        }

        ImGui.Indent(12f);
        foreach (var h in p.Hands)
        {
            var marker = session.Round.Phase == BlackjackPhase.PlayerTurns && session.ActivePlayer == p && session.ActiveHand == h ? "▶ " : "  ";
            ImGui.Text($"{marker}Hand {h.HandNumber}: {h.CardText} = {h.TotalText} | Bet {h.Bet:N0} | {HandStatus(h)}");
        }
        ImGui.Unindent(12f);
    }

    private void DrawActiveHandControls(PlayerSessionState p)
    {
        var active = session.Round.Phase == BlackjackPhase.PlayerTurns && session.ActivePlayer == p;
        if (!active || session.ActiveHand is not { } hand)
            return;

        var sm = new BlackjackStateMachine(session);
        var hitEnabled = IsActiveUnfinishedHand(p, hand);
        var hitReason = hitEnabled ? string.Empty : "Hit is only available for the active unfinished hand.";
        var standEnabled = hitEnabled;
        var standReason = standEnabled ? string.Empty : "Stand is only available for the active unfinished hand.";
        var doubleEnabled = sm.CanDouble(hand, out var doubleReason);
        var splitEnabled = sm.CanSplit(hand, out var splitReason);

        ImGui.Indent(12f);
        ImGui.TextColored(GambaTheme.Gold, "Active hand actions");

        if (UiHelpers.DisabledAwareButton("Hit", hitEnabled, hitReason))
            RequestCard(p, hand, "Hit");

        ImGui.SameLine();
        if (UiHelpers.DisabledAwareButton("Stand", standEnabled, standReason))
            Stand(hand);

        ImGui.SameLine();
        if (UiHelpers.DisabledAwareButton("Double Down", doubleEnabled, doubleReason))
            DoubleDown(p, hand);

        ImGui.SameLine();
        if (UiHelpers.DisabledAwareButton("Split", splitEnabled, splitReason))
            Split(p, hand);

        ImGui.Unindent(12f);
    }

    private bool IsActiveUnfinishedHand(PlayerSessionState player, BlackjackHand hand)
        => session.Round.Phase == BlackjackPhase.PlayerTurns
           && session.ActivePlayer == player
           && session.ActiveHand == hand
           && !hand.IsComplete
           && !hand.IsBusted
           && !hand.IsVoided;

    private string HandStatus(BlackjackHand hand)
    {
        if (hand.IsVoided) return "Voided";
        if (hand.IsBusted) return "Bust";
        if (IsNaturalByRules(hand)) return "Natural Blackjack";
        if (hand.IsComplete) return "Complete";
        return "Acting";
    }

    private bool IsNaturalByRules(BlackjackHand hand)
        => hand.IsNaturalBlackjack || (session.Rules.SplitTwentyOneCountsAsNatural && hand.IsSplitHand && hand.IsTwoCardTwentyOne);

    private void ReserveBetWithUndo(PlayerSessionState p, long amount, string label)
    {
        var beforeAvailable = p.Bank.Available;
        var beforeActive = p.Bank.ActiveBet;
        var beforeLastBet = p.Bank.LastBet;
        var beforeConfirmed = p.BetConfirmed;
        var beforeHands = p.Hands.Select(CloneHand).ToList();

        if (!players.TryReserveBet(p, amount, out var reason))
        {
            log.Add(LogCategory.Warnings, reason);
            return;
        }

        AnnounceBetPlaced(p);

        undo.Push($"{label} for {p.DisplayName}", () =>
        {
            p.Bank.Available = beforeAvailable;
            p.Bank.ActiveBet = beforeActive;
            p.Bank.LastBet = beforeLastBet;
            p.BetConfirmed = beforeConfirmed;
            p.Hands.Clear();
            p.Hands.AddRange(beforeHands.Select(CloneHand));
        });
    }


    private void AnnounceBetPlaced(PlayerSessionState player)
    {
        if (session.Round.Phase != BlackjackPhase.BettingOpen || player.Status == PlayerStatus.Dealer)
            return;

        var message = ApplyTemplate("BetPlaced", new Dictionary<string, string>
        {
            ["player"] = player.DisplayName,
            ["amount"] = player.Bank.ActiveBet.ToString("N0"),
            ["bet"] = player.Bank.ActiveBet.ToString("N0"),
            ["bank"] = player.Bank.Available.ToString("N0"),
        });

        chat.EnqueueParty(message);
        log.Add(LogCategory.ChatQueue, $"Queued bet announcement for {player.DisplayName}: {player.Bank.ActiveBet:N0} gil.");
    }

    private void StartDealing()
    {
        dealerTurnStarted = false;
        allBustRoundOverAnnounced = false;
        chat.EnqueueParty("Bets closed. Dealing next round.");
        var roundPlayers = session.SessionPlayers
            .Where(p => p.BetConfirmed)
            .OrderBy(p => p.PartySlot)
            .ToList();

        session.Round = new BlackjackRound
        {
            RoundNumber = session.Round.RoundNumber,
            Phase = BlackjackPhase.Dealing,
            Players = roundPlayers,
            DealerHand = new BlackjackHand { HandNumber = 0 },
            ActivePlayerIndex = 0,
            ActiveHandIndex = 0
        };

        pendingInitialDealCards = (roundPlayers.Count * 2) + 1;

        QueueAstrologianInitialDealFlavor(roundPlayers);

        // Deal in table order: one card to each active player, one visible dealer card,
        // then the second card to each active player. The dealer's final/reveal card is
        // still rolled later during Dealer Turn per the venue rules.
        foreach (var p in session.Round.Players)
            RequestCard(p, p.Hands[0], "Initial card 1", CountInitialDealCardResolved);

        RequestDealerCard("Dealer visible card", CountInitialDealCardResolved, continueDealerTurn: false);

        foreach (var p in session.Round.Players)
            RequestCard(p, p.Hands[0], "Initial card 2", CountInitialDealCardResolved);

        log.Add(LogCategory.RoundFlow, "Started table-order initial deal using visible /dice results.");
    }

    private void QueueAstrologianInitialDealFlavor(IReadOnlyList<PlayerSessionState> roundPlayers)
    {
        if (!config.General.AstrologianImmersionEnabled)
            return;

        if (chat.DemoMode)
            log.Add(LogCategory.Demo, "AST immersion enabled: demo mode will queue/log AST action commands only; no real action commands are sent.");

        var eligiblePlayers = roundPlayers
            .Where(p => p.Status != PlayerStatus.Dealer)
            .OrderBy(p => p.PartySlot)
            .Take(3)
            .ToList();

        if (eligiblePlayers.Count == 0)
            return;

        // This is strictly cosmetic. Blackjack state progression never waits for these
        // action commands and game-side failures/cooldowns are ignored by design.
        chat.EnqueueCommand("/ac \"Umbral Draw\"");

        var playActions = new[] { "Play I", "Play II", "Play III" };
        for (var i = 0; i < eligiblePlayers.Count; i++)
        {
            var player = eligiblePlayers[i];
            if (config.General.AstrologianUseTargetCommand)
                chat.EnqueueCommand($"/target \"{GetTargetableCharacterName(player)}\"");

            chat.EnqueueCommand($"/ac \"{playActions[i]}\"");
        }

        if (roundPlayers.Count > 3)
            log.Add(LogCategory.ChatQueue, $"AST immersion queued for the first 3 active players; {roundPlayers.Count - 3} additional player(s) ignored for AST actions.");
        else
            log.Add(LogCategory.ChatQueue, $"AST immersion queued for {eligiblePlayers.Count} active player(s).");
    }

    private static string GetTargetableCharacterName(PlayerSessionState player)
    {
        var displayName = player.DisplayName;
        var atIndex = displayName.IndexOf('@');
        return atIndex > 0 ? displayName[..atIndex] : displayName;
    }

    private void CountInitialDealCardResolved()
    {
        if (pendingInitialDealCards <= 0) return;
        pendingInitialDealCards--;
        if (pendingInitialDealCards == 0)
            CompleteInitialDealAndStartPlayerTurns();
    }

    private void CompleteInitialDealAndStartPlayerTurns()
    {
        session.Round.Phase = BlackjackPhase.PlayerTurns;
        MoveToFirstUnfinishedPlayerHandOrDealerTurn();
        log.Add(LogCategory.RoundFlow, "Initial deal complete. Player turns started.");
        if (session.Round.Phase == BlackjackPhase.PlayerTurns && session.ActivePlayer is not null)
        {
            chat.EnqueueParty("Initial deal complete.");
            AnnounceActivePlayerTurn();
        }
        BeginDealerTurnIfReady();
    }

    private void RequestCard(PlayerSessionState p, BlackjackHand hand, string reason, Action? afterApply = null)
    {
        dice.RequestRoll($"{reason} for {p.DisplayName} Hand {hand.HandNumber}", c =>
        {
            hand.AddCard(c);
            hand.Actions.Add(reason);

            if (IsNaturalByRules(hand))
            {
                chat.EnqueueParty($"🎉 {p.DisplayName} has a Natural Blackjack! {hand.CardText} = 21.");
                log.Add(LogCategory.RoundFlow, $"{p.DisplayName} has a Natural Blackjack on {hand.CardText}.");
            }
            else
            {
                chat.EnqueueParty($"{p.DisplayName} receives {c}. Hand: {hand.CardText} = {hand.TotalText}.");
            }

            if (hand.IsBusted)
                chat.EnqueueParty($"{p.DisplayName} busts with {hand.BestTotal}.");

            var wasPlayerTurnBeforeAfterApply = session.Round.Phase == BlackjackPhase.PlayerTurns;
            afterApply?.Invoke();

            // Initial-deal callbacks can transition Dealing -> PlayerTurns inside
            // afterApply. CompleteInitialDealAndStartPlayerTurns already announces
            // the first active turn, so do not let the same dice callback announce it
            // again after the phase changes.
            if (!wasPlayerTurnBeforeAfterApply)
                return;

            if (session.Round.Phase == BlackjackPhase.PlayerTurns && hand.IsComplete)
            {
                new BlackjackStateMachine(session).AdvanceToNextHand();
                if (session.Round.Phase == BlackjackPhase.PlayerTurns && session.ActivePlayer is not null)
                    AnnounceActivePlayerTurn();
                BeginDealerTurnIfReady();
            }
            else if (session.Round.Phase == BlackjackPhase.PlayerTurns && session.ActivePlayer == p && session.ActiveHand == hand && !hand.IsComplete)
            {
                AnnounceActivePlayerTurn();
            }
        });
    }

    private void Stand(BlackjackHand hand)
    {
        var beforeComplete = hand.IsComplete;
        var beforeActions = hand.Actions.ToList();
        undo.Push($"Stand hand {hand.HandNumber}", () =>
        {
            hand.IsComplete = beforeComplete;
            hand.Actions.Clear();
            hand.Actions.AddRange(beforeActions);
        });

        hand.IsComplete = true;
        hand.Actions.Add("Stand");
        chat.EnqueueParty($"{session.ActivePlayer?.DisplayName ?? "Player"} stands on {hand.TotalText}.");
        var previousPlayer = session.ActivePlayer;
        new BlackjackStateMachine(session).AdvanceToNextHand();
        if (session.Round.Phase == BlackjackPhase.PlayerTurns && session.ActivePlayer is not null && session.ActivePlayer != previousPlayer)
            AnnounceActivePlayerTurn();
        BeginDealerTurnIfReady();
    }

    private void DoubleDown(PlayerSessionState p, BlackjackHand hand)
    {
        var sm = new BlackjackStateMachine(session);
        if (!sm.CanDouble(hand, out var r)) { log.Add(LogCategory.Warnings, r); return; }

        if (hand.OriginalBet <= 0)
            hand.OriginalBet = hand.Bet;

        p.Bank.Available -= hand.Bet;
        p.Bank.ActiveBet += hand.Bet;
        hand.Bet *= 2;
        hand.IsDoubled = true;
        RequestCard(p, hand, "Double Down", () => hand.IsComplete = true);
    }

    private void Split(PlayerSessionState p, BlackjackHand hand)
    {
        var sm = new BlackjackStateMachine(session);
        if (!sm.CanSplit(hand, out var r)) { log.Add(LogCategory.Warnings, r); return; }
        if (hand.OriginalBet <= 0)
            hand.OriginalBet = hand.Bet;

        var card = hand.Cards[1];
        hand.Cards.RemoveAt(1);
        var newHand = new BlackjackHand { HandNumber = p.Hands.Count + 1, Bet = hand.Bet, OriginalBet = hand.OriginalBet, IsSplitHand = true };
        newHand.Cards.Add(card);
        hand.IsSplitHand = true;
        p.Bank.Available -= hand.Bet;
        p.Bank.ActiveBet += hand.Bet;
        p.Hands.Add(newHand);
        log.Add(LogCategory.RoundFlow, $"Split {p.DisplayName} hand.");
        AnnounceActivePlayerTurn();
    }

    private void AnnounceActivePlayerTurn()
    {
        if (session.Round.Phase != BlackjackPhase.PlayerTurns || session.ActivePlayer is not { } player || session.ActiveHand is not { } hand || hand.IsComplete)
            return;

        var options = BuildLegalOptions(hand);
        var message = ApplyTemplate("PlayerTurnOptions", new Dictionary<string, string>
        {
            ["player"] = player.DisplayName,
            ["hand"] = hand.CardText,
            ["total"] = hand.TotalText,
            ["bet"] = hand.Bet.ToString("N0"),
            ["bank"] = player.Bank.Available.ToString("N0"),
            ["options"] = options,
        });

        chat.EnqueueParty(message);
        log.Add(LogCategory.RoundFlow, $"Turn options for {player.DisplayName}: {options}.");
    }

    private string BuildLegalOptions(BlackjackHand hand)
    {
        var options = new List<string>();
        var sm = new BlackjackStateMachine(session);

        // Hit and Stand use the same legality rules as the UI buttons: they are
        // available only during Player Turns for the active unfinished hand.
        // Double and Split have additional rules handled by the state machine.
        if (session.Round.Phase == BlackjackPhase.PlayerTurns && session.ActiveHand == hand && !hand.IsComplete)
        {
            options.Add("Hit");
            options.Add("Stand");
        }

        if (sm.CanDouble(hand, out _))
            options.Add("Double");
        if (sm.CanSplit(hand, out _))
            options.Add("Split");

        return options.Count == 0 ? "No legal actions" : string.Join(", ", options);
    }

    private string ApplyTemplate(string key, IReadOnlyDictionary<string, string> values)
    {
        var profile = profiles.ActiveProfile;
        if (!profile.ChatTemplates.TryGetValue(key, out var template) || string.IsNullOrWhiteSpace(template))
            template = ChatTemplateDefaults.CreateFormal().TryGetValue(key, out var fallback) ? fallback : "{player}: Options: {options}.";

        foreach (var (name, value) in values)
            template = template.Replace("{" + name + "}", value, StringComparison.OrdinalIgnoreCase);
        return template;
    }

    private void MoveToFirstUnfinishedPlayerHandOrDealerTurn()
    {
        for (var pi = 0; pi < session.Round.Players.Count; pi++)
        {
            var player = session.Round.Players[pi];
            for (var hi = 0; hi < player.Hands.Count; hi++)
            {
                if (!player.Hands[hi].IsComplete)
                {
                    session.Round.ActivePlayerIndex = pi;
                    session.Round.ActiveHandIndex = hi;
                    return;
                }
            }
        }

        session.Round.Phase = BlackjackPhase.DealerTurn;
    }

    private void BeginDealerTurnIfReady()
    {
        if (session.Round.Phase != BlackjackPhase.DealerTurn || dealerTurnStarted)
            return;

        if (AllPlayerHandsAreTerminalLosses())
        {
            dealerTurnStarted = false;
            session.Round.DealerHand.IsComplete = true;
            session.Round.Phase = BlackjackPhase.Settlement;
            AnnounceAllBustRoundOver();
            return;
        }

        dealerTurnStarted = true;
        log.Add(LogCategory.RoundFlow, "All player hands complete. Dealer turn started.");
        chat.EnqueueParty("All player hands are complete. Dealer turn begins.");

        if (session.Round.DealerHand.Cards.Count < 2)
            RequestDealerCard("Dealer reveal card", continueDealerTurn: true);
        else
            ContinueDealerTurn();
    }

    private void AnnounceAllBustRoundOver()
    {
        if (allBustRoundOverAnnounced) return;
        allBustRoundOverAnnounced = true;

        var message = "All active players have bust. Round over; dealer hand is skipped.";
        if (chat.DemoMode)
            log.Add(LogCategory.Demo, message);
        else
            chat.EnqueueParty(message);
        log.Add(LogCategory.RoundFlow, message);
    }

    private bool AllPlayerHandsAreTerminalLosses()
    {
        var activeHands = session.Round.Players.SelectMany(p => p.Hands).Where(h => !h.IsVoided).ToList();
        return activeHands.Count > 0 && activeHands.All(h => h.IsBusted);
    }

    private void RequestDealerCard(string reason, Action? afterApply = null, bool continueDealerTurn = true)
    {
        dice.RequestRoll(reason, c =>
        {
            session.Round.DealerHand.AddCard(c);
            session.Round.DealerHand.Actions.Add(reason);
            chat.EnqueueParty($"Dealer draws {c}. Dealer hand: {session.Round.DealerHand.CardText} = {session.Round.DealerHand.TotalText}.");
            afterApply?.Invoke();
            if (continueDealerTurn)
                ContinueDealerTurn();
        });
    }

    private void ContinueDealerTurn()
    {
        if (session.Round.Phase != BlackjackPhase.DealerTurn)
            return;

        var dealer = session.Round.DealerHand;
        if (dealer.IsNaturalBlackjack)
        {
            session.Round.Phase = BlackjackPhase.Settlement;
            log.Add(LogCategory.RoundFlow, "Dealer has Natural Blackjack. Round is ready for settlement.");
            return;
        }

        if (ShouldDealerDraw(dealer))
        {
            RequestDealerCard("Dealer draw", continueDealerTurn: true);
            return;
        }

        dealer.IsComplete = true;
        session.Round.Phase = BlackjackPhase.Settlement;
        if (dealer.IsBusted)
        {
            chat.EnqueueParty($"Dealer busts with {dealer.BestTotal}. Round is ready for settlement.");
            log.Add(LogCategory.RoundFlow, $"Dealer busts with {dealer.BestTotal}. Round is ready for settlement.");
        }
        else
        {
            chat.EnqueueParty($"Dealer stands on {dealer.TotalText}. Round is ready for settlement.");
            log.Add(LogCategory.RoundFlow, $"Dealer stands on {dealer.TotalText}. Round is ready for settlement.");
        }
    }

    private bool ShouldDealerDraw(BlackjackHand dealer)
    {
        if (dealer.IsBusted) return false;
        if (dealer.BestTotal < 17) return true;
        if (dealer.BestTotal == 17 && dealer.IsSoft && !session.Rules.DealerStandsOnSoft17) return true;
        return false;
    }

    private void SettleRound()
    {
        if (AllPlayerHandsAreTerminalLosses())
            AnnounceAllBustRoundOver();

        var settlement = new BlackjackSettlementService(session.Rules);
        foreach (var p in session.Round.Players)
        {
            foreach (var h in p.Hands)
            {
                var entry = settlement.Settle(p, h, session.Round.DealerHand, session.Round.RoundNumber);
                p.RoundHistory.Add(entry);
                chat.EnqueueParty($"{p.DisplayName}: {h.Bet:N0} gil bet, {h.CardText} vs dealer {session.Round.DealerHand.CardText} → {entry.Outcome}. Bank: {p.Bank.Available:N0} gil.");
            }
        }
        session.Round.RoundNumber++;
        session.Round.Phase = BlackjackPhase.CashOutBetweenHands;
        pendingInitialDealCards = 0;
        dealerTurnStarted = false;
        allBustRoundOverAnnounced = false;
        foreach (var p in session.SessionPlayers)
        {
            p.BetConfirmed = false;
            p.Hands.Clear();
        }
    }

    private static BlackjackHand CloneHand(BlackjackHand source) => new()
    {
        HandNumber = source.HandNumber,
        Cards = source.Cards.ToList(),
        Bet = source.Bet,
        OriginalBet = source.OriginalBet,
        IsComplete = source.IsComplete,
        IsBusted = source.IsBusted,
        IsDoubled = source.IsDoubled,
        IsSplitHand = source.IsSplitHand,
        IsVoided = source.IsVoided,
        Actions = source.Actions.ToList()
    };
}
