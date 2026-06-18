using System.Text.RegularExpressions;
using Dalamud.Game.Chat;

namespace GambaAssistant.Services;

/// <summary>
/// Watches live chat/log text for dealer dice results and conservative trade notifications.
/// This intentionally stays text-based so it fails safe when game/Dalamud internals change.
/// </summary>
public sealed class ChatMonitorService : IDisposable
{
    private readonly DiceService dice;
    private readonly TradeMonitorService trades;
    private readonly LogService log;
    private string? pendingTradePlayerName;
    private string? pendingTradeDirection;
    private DateTime pendingTradeStartedUtc;
    private long pendingTradeLastAmount;

    // FFXIV/Dalamud/Chat 2 can expose dice text with slightly different formatting,
    // for example "You roll a 7.", "Airi Tsukino rolls a 7.",
    // "Random! Airi Tsukino rolls 7 (1-13)", or localized payload fragments.
    // Keep the parser conservative around the word roll/random/dice, but avoid relying
    // on one exact sentence shape.
    private static readonly Regex DiceResultAfterRangeRegex = new(@"\(\s*\d+\s*-\s*\d+\s*\)\s*(?<value>\d{1,3})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DiceResultBeforeRangeRegex = new(@"(?<value>\d{1,3})\s*\(\s*\d+\s*-\s*\d+\s*\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DiceAfterRollRegex = new(@"(?:you\s+roll|rolls?|rolled|random|dice)[^0-9]{0,48}(?<value>\d{1,3})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NameBeforeRollRegex = new(@"(?<name>[\p{L}][\p{L}'\-]*(?:\s+[\p{L}][\p{L}'\-]*)?)\s+(?:rolls?|rolled)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex IncomingTradeWithNameRegex = new(@"(?:(?<name>[\p{L}][\p{L}'\-]*(?:\s+[\p{L}][\p{L}'\-]*)?)\s+(?:trades|gives|gave|pays|paid)\s+(?:you\s+)?(?<amount>[\d,]+)\s+gil|you\s+(?:receive|received|have\s+received|obtain|obtained|gain|gained)\s+(?<amount2>[\d,]+)\s+gil\s+from\s+(?<name2>[\p{L}][\p{L}'\-]*(?:\s+[\p{L}][\p{L}'\-]*)?))", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex IncomingTradeAmountOnlyRegex = new(@"\b(?:you\s+)?(?:receive|received|have\s+received|obtain|obtained|gain|gained)\s+(?<amount>[\d,]+)\s+gil\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex OutgoingTradeWithNameRegex = new(@"\byou\s+(?:hand\s+over|gave|give|trade|traded|pay|paid)\s+(?<amount>[\d,]+)\s+gil\s+(?:to\s+)?(?<name>[\p{L}][\p{L}'\-]*(?:\s+[\p{L}][\p{L}'\-]*)?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex OutgoingTradeAmountOnlyRegex = new(@"\b(?:you\s+)?(?:hand\s+over|gave|give|trade|traded|pay|paid)\s+(?<amount>[\d,]+)\s+gil\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TradeRequestRegex = new(@"\b(?<name>[\p{L}][\p{L}'\-]*(?:\s+[\p{L}][\p{L}'\-]*)?)\s+(?:wishes|wants|would\s+like)\s+to\s+trade\s+with\s+you\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex OutgoingTradeRequestRegex = new(@"\btrade\s+request\s+sent\s+to\s+(?<name>[\p{L}][\p{L}'\-]*(?:\s+[\p{L}][\p{L}'\-]*)?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AwaitingTradeRegex = new(@"\bawaiting\s+trade\s+confirmation\s+from\s+(?<name>[\p{L}][\p{L}'\-]*(?:\s+[\p{L}][\p{L}'\-]*)?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TradeCompleteRegex = new(@"\btrade\s+complete\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public ChatMonitorService(DiceService dice, TradeMonitorService trades, LogService log)
    {
        this.dice = dice;
        this.trades = trades;
        this.log = log;
        DalamudServices.ChatGui.ChatMessage += OnChatMessage;
    }

    private void OnChatMessage(IHandleableChatMessage message)
    {
        var sender = message.Sender.ToString();
        var body = message.Message.ToString();
        InspectMessage(sender, body, GetChatTypeName(message), LooksLikeRealDiceMessage(message), GetMessagePayloadSummary(message));
    }

    private static string GetChatTypeName(IHandleableChatMessage message)
    {
        try
        {
            return message.GetType().GetProperty("Type")?.GetValue(message)?.ToString() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private void InspectMessage(string sender, string body, string chatType, bool hasGameDicePayload, string payloadSummary)
    {
        if (string.IsNullOrWhiteSpace(sender) && string.IsNullOrWhiteSpace(body))
            return;

        TryConsumeDice(sender, body, chatType, hasGameDicePayload, payloadSummary);
        TryDetectTrade(sender, body);
    }

    private void TryConsumeDice(string sender, string body, string chatType, bool hasGameDicePayload, string payloadSummary)
    {
        if (dice.Pending is null)
            return;

        var combined = string.IsNullOrWhiteSpace(sender) ? body : $"{sender} {body}";
        var normalizedCombined = StripChatNoise(combined);
        var looksLikeDice = LooksLikeDiceLine(normalizedCombined, chatType);
        if (!looksLikeDice)
            return;

        if (!hasGameDicePayload)
        {
            log.Add(LogCategory.Dice, $"Pending dice ignored; dice-looking chat text did not contain the game's dice icon/autotranslate payloads. chatType='{chatType}', sender='{sender}', body='{body}', payloads='{payloadSummary}'.");
            return;
        }

        if (!TryParseDiceResult(normalizedCombined, out var value, out var parseSource))
        {
            log.Add(LogCategory.Dice, $"Pending dice ignored; could not parse result from chatType='{chatType}', sender='{sender}', body='{body}', normalized='{normalizedCombined}'.");
            return;
        }

        var rollerName = ExtractRollerName(sender, normalizedCombined);
        var identityConfidence = IsLikelyDealerRoll(sender, normalizedCombined, rollerName, chatType);
        if (!identityConfidence)
        {
            log.Add(LogCategory.Dice, $"Pending dice ignored; roll was not identified as dealer. chatType='{chatType}', sender='{sender}', body='{body}', parsedRoller='{rollerName}', value={value}.");
            return;
        }

        if (dice.TryConsumeDealerDice(value, rollerName, allowUnidentifiedDealer: true))
            log.Add(LogCategory.Dice, $"Live dice consumed: value={value}, parse='{parseSource}', chatType='{chatType}', sender='{sender}', body='{body}'.");
    }


    private static bool LooksLikeRealDiceMessage(IHandleableChatMessage message)
    {
        // Do not trust visible chat text alone. Players can type text such as
        // "Random! 729" or "You roll a 7." manually, but real FFXIV dice
        // messages include SeString icon/autotranslate payloads in the body.
        try
        {
            var sawPayload = false;
            foreach (var payload in message.Message.Payloads)
            {
                sawPayload = true;
                var typeName = payload.GetType().Name;
                if (typeName.Equals("TextPayload", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (typeName.Contains("Icon", StringComparison.OrdinalIgnoreCase)
                    || typeName.Contains("AutoTranslate", StringComparison.OrdinalIgnoreCase)
                    || typeName.Contains("Bitmap", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return sawPayload && false;
        }
        catch
        {
            return false;
        }
    }

    private static string GetMessagePayloadSummary(IHandleableChatMessage message)
    {
        try
        {
            var types = message.Message.Payloads
                .Select(p => p.GetType().Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(6)
                .ToArray();

            return types.Length == 0 ? "none" : string.Join(", ", types);
        }
        catch
        {
            return "unavailable";
        }
    }

    private bool TryParseDiceResult(string normalizedCombined, out int value, out string parseSource)
    {
        value = 0;
        parseSource = string.Empty;

        // FFXIV party dice can arrive as:
        //   Random! (1-13) 10
        // The parenthesized values are only the dice range. Never let a generic
        // "Random" match consume the 1 from that range as the actual roll.
        // Try explicit range-aware parsing first, then generic parsing only
        // against a copy with ranges removed.
        var afterRange = DiceResultAfterRangeRegex.Match(normalizedCombined);
        if (afterRange.Success && int.TryParse(afterRange.Groups["value"].Value, out value) && IsValidDiceValue(value))
        {
            parseSource = "after-range";
            return true;
        }

        var beforeRange = DiceResultBeforeRangeRegex.Match(normalizedCombined);
        if (beforeRange.Success && int.TryParse(beforeRange.Groups["value"].Value, out value) && IsValidDiceValue(value))
        {
            parseSource = "before-range";
            return true;
        }

        var withoutRanges = RemoveDiceRanges(normalizedCombined);

        // For Random! lines, the roll value is the remaining standalone number.
        // This handles Chat 2/Dalamud payloads like "Random! (1-13) 10" even
        // when punctuation around the range is transformed by Dalamud.
        if (withoutRanges.Contains("random", StringComparison.OrdinalIgnoreCase))
        {
            var randomNumbers = Regex.Matches(withoutRanges, @"(?<![A-Za-z0-9])\d{1,3}(?![A-Za-z0-9])");
            for (var i = randomNumbers.Count - 1; i >= 0; i--)
            {
                if (int.TryParse(randomNumbers[i].Value, out value) && IsValidDiceValue(value))
                {
                    parseSource = "random-last-number";
                    return true;
                }
            }
        }

        var match = DiceAfterRollRegex.Match(withoutRanges);
        if (match.Success && int.TryParse(match.Groups["value"].Value, out value) && IsValidDiceValue(value))
        {
            parseSource = "after-roll-keyword";
            return true;
        }

        // Last-resort fallback for stripped dice lines: use the last standalone
        // number after removing ranges, never the first range number.
        var allNumbers = Regex.Matches(withoutRanges, @"(?<![A-Za-z0-9])\d{1,3}(?![A-Za-z0-9])");
        for (var i = allNumbers.Count - 1; i >= 0; i--)
        {
            if (int.TryParse(allNumbers[i].Value, out value) && IsValidDiceValue(value))
            {
                parseSource = "last-number-no-range";
                return true;
            }
        }

        return false;
    }

    private static string RemoveDiceRanges(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        // Remove common range shapes after StripChatNoise, including:
        //   (1-13), 1-13, and variants with spaces.
        var cleaned = Regex.Replace(text, @"\(\s*\d+\s*-\s*\d+\s*\)", " ");
        cleaned = Regex.Replace(cleaned, @"(?i)(random\s*!?\s*)\d+\s*-\s*\d+", "$1 ");
        // Some Dalamud payload conversions can drop the dash, resulting in
        // "Random 1 13 10". Treat the first two numbers after Random as the range
        // only when another number follows them.
        cleaned = Regex.Replace(cleaned, @"(?i)(random\s*!?\s*)\d+\s+\d+(?=\s+\d{1,3}(?![A-Za-z0-9]))", "$1 ");
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
        return cleaned;
    }

    private bool IsValidDiceValue(int value)
    {
        var sides = Math.Max(1, dice.DiceSides);
        return value >= 1 && value <= sides;
    }

    private static bool LooksLikeDiceLine(string text, string chatType)
    {
        if (!string.IsNullOrWhiteSpace(chatType) && chatType.Contains("dice", StringComparison.OrdinalIgnoreCase))
            return true;

        return text.Contains("roll", StringComparison.OrdinalIgnoreCase)
            || text.Contains("random", StringComparison.OrdinalIgnoreCase)
            || text.Contains("dice", StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractRollerName(string sender, string normalizedCombined)
    {
        if (normalizedCombined.Contains("you roll", StringComparison.OrdinalIgnoreCase))
            return GetLocalCharacterName();

        var nameMatch = NameBeforeRollRegex.Match(normalizedCombined);
        if (nameMatch.Success)
            return nameMatch.Groups["name"].Value.Trim();

        var cleanedSender = StripChatNoise(sender).Trim();
        return string.IsNullOrWhiteSpace(cleanedSender) ? GetLocalCharacterName() : cleanedSender;
    }

    private static bool IsLikelyDealerRoll(string sender, string normalizedCombined, string rollerName, string chatType)
    {
        var localName = GetLocalCharacterName();
        if (string.IsNullOrWhiteSpace(localName))
            return true;

        if (normalizedCombined.Contains("you roll", StringComparison.OrdinalIgnoreCase))
            return true;

        if (NameMatchesDealer(rollerName, localName))
            return true;

        if (NameMatchesDealer(sender, localName))
            return true;

        if (normalizedCombined.Contains(localName, StringComparison.OrdinalIgnoreCase))
            return true;

        // Some clients/chat replacements expose party dice result lines with no sender and
        // stripped player names. During a pending plugin-requested roll, consume dice chat
        // lines that cannot be attributed to anyone else so the live table does not deadlock.
        var senderMissing = string.IsNullOrWhiteSpace(StripChatNoise(sender));
        var rollerMissing = string.IsNullOrWhiteSpace(StripChatNoise(rollerName));
        var diceTyped = !string.IsNullOrWhiteSpace(chatType) && chatType.Contains("dice", StringComparison.OrdinalIgnoreCase);
        return senderMissing && (rollerMissing || diceTyped);
    }

    private static bool NameMatchesDealer(string value, string localName)
    {
        var cleanedValue = StripChatNoise(value);
        if (string.IsNullOrWhiteSpace(cleanedValue) || string.IsNullOrWhiteSpace(localName))
            return false;

        return string.Equals(cleanedValue, localName, StringComparison.OrdinalIgnoreCase)
            || cleanedValue.Contains(localName, StringComparison.OrdinalIgnoreCase)
            || localName.Contains(cleanedValue, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetLocalCharacterName()
    {
        try
        {
            return DalamudServices.PlayerState.IsLoaded ? DalamudServices.PlayerState.CharacterName.Trim() : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string StripChatNoise(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        // Remove private-use icon glyphs and common chat punctuation wrappers while preserving letters/numbers/spaces.
        var chars = value.Where(c => !char.IsControl(c) && (char.IsLetterOrDigit(c) || char.IsWhiteSpace(c) || c is '\'' or '-' or ',' or '.')).ToArray();
        return Regex.Replace(new string(chars), @"\s+", " ").Trim();
    }

    private void TryDetectTrade(string sender, string body)
    {
        var cleanedSender = StripChatNoise(sender);
        var cleanedBody = StripChatNoise(body);
        var combined = string.IsNullOrWhiteSpace(cleanedSender) ? cleanedBody : $"{cleanedSender} {cleanedBody}";

        TrackTradeConversation(combined, sender, body);

        if (!combined.Contains("gil", StringComparison.OrdinalIgnoreCase))
            return;

        var namedMatch = IncomingTradeWithNameRegex.Match(combined);
        if (namedMatch.Success)
        {
            var rawAmount = namedMatch.Groups["amount"].Success ? namedMatch.Groups["amount"].Value : namedMatch.Groups["amount2"].Value;
            if (!TryParseGilAmount(rawAmount, out var namedAmount))
                return;

            var playerName = namedMatch.Groups["name"].Success ? namedMatch.Groups["name"].Value : namedMatch.Groups["name2"].Value;
            if (string.IsNullOrWhiteSpace(playerName) && !string.IsNullOrWhiteSpace(cleanedSender))
                playerName = cleanedSender;

            if (!string.IsNullOrWhiteSpace(playerName))
            {
                trades.AddDetectedIncomingTrade(playerName, namedAmount, "Detected named incoming gil trade from chat/log text");
                return;
            }
        }

        var outgoingNamedMatch = OutgoingTradeWithNameRegex.Match(combined);
        if (outgoingNamedMatch.Success && TryParseGilAmount(outgoingNamedMatch.Groups["amount"].Value, out var outgoingNamedAmount))
        {
            trades.AddDetectedOutgoingTrade(outgoingNamedMatch.Groups["name"].Value, outgoingNamedAmount, "Detected named outgoing gil trade from chat/log text");
            pendingTradePlayerName = null;
            pendingTradeLastAmount = outgoingNamedAmount;
            return;
        }

        // Outgoing cash-out lines are often amount-only in the client log, for
        // example: "You hand over 50,000 gil." Attribute them to the active
        // outgoing trade recipient captured from the trade request/confirmation
        // sequence, regardless of Blackjack phase.
        var outgoingAmountOnlyMatch = OutgoingTradeAmountOnlyRegex.Match(combined);
        if (outgoingAmountOnlyMatch.Success && TryParseGilAmount(outgoingAmountOnlyMatch.Groups["amount"].Value, out var outgoingAmountOnly))
        {
            var outgoingPlayer = GetFreshPendingTradePlayer("outgoing");
            if (!string.IsNullOrWhiteSpace(outgoingPlayer))
            {
                trades.AddDetectedOutgoingTrade(outgoingPlayer, outgoingAmountOnly, "Detected outgoing gil amount from active trade confirmation sequence");
                pendingTradeLastAmount = outgoingAmountOnly;
                return;
            }

            var singleOutgoingPlayer = trades.ResolveSingleActivePartyPlayer();
            if (singleOutgoingPlayer is null)
            {
                log.Add(LogCategory.Warnings, $"Detected outgoing gil amount {outgoingAmountOnly:N0}, but no active outgoing trade recipient was known and there is not exactly one eligible party player. Line: sender='{sender}', body='{body}'.");
                return;
            }

            trades.AddDetectedOutgoingTrade(singleOutgoingPlayer.DisplayName, outgoingAmountOnly, "Detected outgoing gil amount; attributed to the only eligible party player");
            pendingTradeLastAmount = outgoingAmountOnly;
            return;
        }

        // Many FFXIV system trade lines only expose the amount, for example
        // "You receive 50,000 gil." with no sender. Attribute it to the active
        // incoming trade sender when available; otherwise only guess when there
        // is exactly one eligible tracked party player.
        var amountOnlyMatch = IncomingTradeAmountOnlyRegex.Match(combined);
        if (!amountOnlyMatch.Success || !TryParseGilAmount(amountOnlyMatch.Groups["amount"].Value, out var amountOnly))
        {
            if (combined.Contains("trade", StringComparison.OrdinalIgnoreCase)
                || combined.Contains("receive", StringComparison.OrdinalIgnoreCase)
                || combined.Contains("obtain", StringComparison.OrdinalIgnoreCase))
            {
                log.Add(LogCategory.Trades, $"Unmatched gil/trade chat line: sender='{sender}', body='{body}'.");
            }
            return;
        }

        if (!string.IsNullOrWhiteSpace(cleanedSender) && !NameMatchesDealer(cleanedSender, GetLocalCharacterName()))
        {
            trades.AddDetectedIncomingTrade(cleanedSender, amountOnly, "Detected incoming gil trade amount; attributed from chat sender");
            pendingTradePlayerName = null;
            pendingTradeLastAmount = amountOnly;
            return;
        }

        var pendingPlayer = GetFreshPendingTradePlayer("incoming");
        if (!string.IsNullOrWhiteSpace(pendingPlayer))
        {
            trades.AddDetectedIncomingTrade(pendingPlayer, amountOnly, "Detected incoming gil amount from active trade confirmation sequence");
            pendingTradeLastAmount = amountOnly;
            return;
        }

        var singlePlayer = trades.ResolveSingleActivePartyPlayer();
        if (singlePlayer is null)
        {
            log.Add(LogCategory.Warnings, $"Detected incoming gil amount {amountOnly:N0}, but the chat line did not include a player name and there is not exactly one eligible party player. Line: sender='{sender}', body='{body}'.");
            return;
        }

        trades.AddDetectedIncomingTrade(singlePlayer.DisplayName, amountOnly, "Detected incoming gil trade amount; attributed to the only eligible party player");
        pendingTradeLastAmount = amountOnly;
    }

    private void TrackTradeConversation(string combined, string sender, string body)
    {
        var outgoingRequestMatch = OutgoingTradeRequestRegex.Match(combined);
        if (outgoingRequestMatch.Success)
        {
            SetPendingTradePlayer(outgoingRequestMatch.Groups["name"].Value, "outgoing trade request", "outgoing", sender, body);
            return;
        }

        var requestMatch = TradeRequestRegex.Match(combined);
        if (requestMatch.Success)
        {
            SetPendingTradePlayer(requestMatch.Groups["name"].Value, "incoming trade request", "incoming", sender, body);
            return;
        }

        var awaitingMatch = AwaitingTradeRegex.Match(combined);
        if (awaitingMatch.Success)
        {
            var awaitedName = awaitingMatch.Groups["name"].Value;
            var direction = pendingTradeDirection == "outgoing" && NamesLooselyMatch(pendingTradePlayerName, awaitedName)
                ? "outgoing"
                : "incoming";
            SetPendingTradePlayer(awaitedName, $"{direction} trade confirmation", direction, sender, body);
            return;
        }

        if (TradeCompleteRegex.IsMatch(combined))
        {
            if (!string.IsNullOrWhiteSpace(pendingTradePlayerName))
            {
                var amountText = pendingTradeLastAmount > 0 ? $" after {pendingTradeLastAmount:N0} gil was detected" : string.Empty;
                var directionText = string.IsNullOrWhiteSpace(pendingTradeDirection) ? string.Empty : $" ({pendingTradeDirection})";
                log.Add(LogCategory.Trades, $"Trade sequence complete for {pendingTradePlayerName}{directionText}{amountText}.");
            }

            pendingTradePlayerName = null;
            pendingTradeDirection = null;
            pendingTradeLastAmount = 0;
        }
    }

    private void SetPendingTradePlayer(string rawName, string source, string direction, string sender, string body)
    {
        var name = StripChatNoise(rawName).Trim('.', ' ');
        if (string.IsNullOrWhiteSpace(name))
            return;

        pendingTradePlayerName = name;
        pendingTradeDirection = direction;
        pendingTradeStartedUtc = DateTime.UtcNow;
        pendingTradeLastAmount = 0;
        var amountDirection = direction == "outgoing" ? "outgoing cash-out" : "incoming buy-in";
        log.Add(LogCategory.Trades, $"Detected {source} for {name}; the next amount-only {amountDirection} gil line will be attributed to this player. sender='{sender}', body='{body}'.");
    }

    private string? GetFreshPendingTradePlayer(string expectedDirection)
    {
        if (string.IsNullOrWhiteSpace(pendingTradePlayerName))
            return null;

        if (!string.Equals(pendingTradeDirection, expectedDirection, StringComparison.OrdinalIgnoreCase))
            return null;

        if (DateTime.UtcNow - pendingTradeStartedUtc > TimeSpan.FromMinutes(2))
        {
            log.Add(LogCategory.Warnings, $"Ignored stale pending {pendingTradeDirection} trade attribution for {pendingTradePlayerName}; no gil amount was detected within 2 minutes.");
            pendingTradePlayerName = null;
            pendingTradeDirection = null;
            pendingTradeLastAmount = 0;
            return null;
        }

        return pendingTradePlayerName;
    }

    private static bool NamesLooselyMatch(string? left, string? right)
    {
        var a = StripChatNoise(left ?? string.Empty).ToLowerInvariant();
        var b = StripChatNoise(right ?? string.Empty).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
            return false;

        return a == b || a.Contains(b, StringComparison.OrdinalIgnoreCase) || b.Contains(a, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseGilAmount(string rawAmount, out long amount)
    {
        amount = 0;
        return !string.IsNullOrWhiteSpace(rawAmount)
            && long.TryParse(rawAmount.Replace(",", string.Empty), out amount)
            && amount > 0;
    }

    public void Dispose()
    {
        DalamudServices.ChatGui.ChatMessage -= OnChatMessage;
    }
}
