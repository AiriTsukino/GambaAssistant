# GambaAssistant

GambaAssistant is a standalone Dalamud plugin for a single Final Fantasy XIV venue dealer running a visible-dice Blackjack table. Players do not need the plugin. It assists with table order, Blackjack phase flow, dealer-only overlays, session banks, a dealer ledger, conservative trade monitoring, narration, logs, undo/corrections, demo mode, and manual exports.

## Commands

- `/gambaassistant` opens the main table window.
- `/gambaassistantsettings` opens the settings window.

The plugin list Open button opens the main window. The Settings button opens the settings window. The main window also includes a Settings button.

## Basic dealer flow

Optional **Astrologian immersion** can be enabled in General Settings. During the initial deal, GambaAssistant queues `/ac "Umbral Draw"` once, then `/target "Player Name"` plus `/ac "Play I"`, `/ac "Play II"`, and `/ac "Play III"` for the first three active players. This is cosmetic only; Blackjack dice/card flow never waits for these actions to succeed.


1. Start or reset the night from the Table tab.
2. Sync party order. The local player/dealer is slot 1. Party slot 2 onward are potential players, spectators, or staff.
3. Open betting.
4. Record player buy-ins/deposits in the Trade Monitor or Players & Banks tab.
5. Confirm a valid bet for each player. Players without a confirmed bet sit out that hand.
6. Start dealing. Cards are requested through visible `/dice party 13` rolls.
7. Press Hit, Stand, Double Down, or Split for the active player hand.
8. Finish dealer turn and settle the round.
9. Cash-outs and profile/rule changes are only allowed between hands or after reset.

## Betting, bank, and cash-out model

Each player has a current-night bank. Confirming a bet reserves it immediately: available bank decreases and active bet increases. Settlement returns stakes and winnings for wins, returns stakes for pushes, and moves losses to the house/dealer ledger. V1 uses full cash-outs only.

## Blackjack model

- No hidden RNG.
- Default card source is visible `/dice party 13`.
- `1=A`, `2-10` are numeric, `11=J`, `12=Q`, `13=K`.
- No suits.
- Infinite/deckless model: every roll is independent and duplicates are valid.
- Natural Blackjack pays 3:2 by default and beats normal 21.
- Hit, Stand, Double Down, and Split are supported by the rules/state-machine layer.
- Insurance and surrender are intentionally not included in v1.

## Demo/test mode

Demo mode creates simulated players and routes all generated chat to the internal log only. It does not affect live banks, live trades, party chat, live session history, or exports.

## Logs and exports

The Log / Terminal tab records current-session events including trades, dice, chat queue, round flow, settlement, undo, warnings, and demo messages. History / Export supports manual JSON and CSV exports. Session data is retained only until Reset Night unless exported.

## Config and profiles

Global UI state is stored in the Dalamud plugin config. Venue profiles, templates, and exports live under a plugin-scoped `GambaAssistant` config folder. Profiles can hold Blackjack rules, limits, chat templates, overlay preferences, message pacing, demo preferences, export settings, and tip defaults. Profile switching is locked while a session is active.

## Developer notes

The composition root is `Plugin.cs`. Shared Dalamud services are in `DalamudServices.cs`. Domain models live under `Models/`, game interfaces under `Models/Games/`, and the v1 Blackjack implementation under `Games/Blackjack/`. Services own party syncing, player banks, trade monitoring, dice, chat queue, ledger, profiles, persistence, overlays, exports, logs, undo, and demo mode. ImGui windows and tabs live under `UI/` and use plugin-scoped styling from `UI/Components/GambaTheme.cs`.

Future games should plug in through `IGameModule`, `IGameSession`, `IGameRuleset`, `IGameAction`, `IGameStateMachine`, and `IGameRenderer` without mixing domain logic into ImGui rendering methods.

## DRT (DRT)

GambaAssistant includes an initial DRT tab. Add party players, add your current in-game target, or manually enter Name@World entrants, then start an even-player bracket. Each match begins with both players rolling `/random 10`; the higher roll goes first. The active match then starts at `/random 999` and each valid roll becomes the next maximum until someone rolls `1`. The player who rolls `1` is eliminated and the winner advances.

Only one bracket/match is active at a time. Click a bracket card to make it active. The bracket view is zoomable for larger tournaments.
