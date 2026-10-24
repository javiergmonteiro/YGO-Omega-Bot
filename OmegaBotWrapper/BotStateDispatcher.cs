using OmegaBotWrapper.Enum;
using OmegaBotWrapper.States;

namespace OmegaBotWrapper
{
    /// <summary>
    /// Resolves bot states to handlers, preserving the existing behavior while keeping DuelBotWrapper lean.
    /// </summary>
    internal sealed class BotStateDispatcher
    {
        private delegate Task<byte[]?> StateHandler(CancellationToken ct);

        private readonly DuelBotWrapper _bot;
        private readonly IReadOnlyDictionary<BotState, StateHandler> _handlers;

        public BotStateDispatcher(DuelBotWrapper bot)
        {
            _bot = bot;
            _handlers = new Dictionary<BotState, StateHandler>
            {
                [BotState.SelectBattleCmd] = HandleBattleCmdAsync,
                [BotState.SelectIdleCmd] = HandleIdleCmdAsync,
                [BotState.SelectCard] = HandleSelectCardAsync,
                [BotState.SelectUnselectCard] = HandleSelectUnselectCardAsync,
                [BotState.SelectChain] = HandleSelectChainAsync,
                [BotState.SelectCounter] = HandleSelectCounterAsync,
                [BotState.SelectDisfield] = HandleSelectDisfieldAsync,
                [BotState.SelectEffectYn] = HandleSelectEffectYnAsync,
                [BotState.SelectOption] = HandleSelectOptionAsync,
                [BotState.SelectPlace] = HandleSelectPlaceAsync,
                [BotState.SelectPosition] = HandleSelectPositionAsync,
                [BotState.SelectSum] = HandleSelectSumAsync,
                [BotState.SelectTribute] = HandleSelectTributeAsync,
                [BotState.AnnounceAttribute] = HandleAnnounceAttributeAsync,
                [BotState.AnnounceCard] = HandleAnnounceCardAsync,
                [BotState.AnnounceNumber] = HandleAnnounceNumberAsync,
                [BotState.AnnounceRace] = HandleAnnounceRaceAsync,
                [BotState.AnnounceCardFilter] = HandleAnnounceCardFilterAsync,
                [BotState.SelectYesNo] = HandleSelectYesNoAsync,
                [BotState.RockPaperScissors] = HandleRockPaperScissorsAsync,
                [BotState.SortCard] = HandleSortCardAsync,
                [BotState.WaitingForDeck] = NoResponseAsync,
                [BotState.None] = NoResponseAsync
            };
        }

        public Task<byte[]?> DispatchAsync(BotState state, CancellationToken ct = default)
        {
            if (_handlers.TryGetValue(state, out var handler))
                return handler(ct);

            BotLogger.Warn(_bot.BotID, $"[{state}] No dispatcher registered, skipping.");
            return Task.FromResult<byte[]?>(null);
        }

        private static Task<byte[]?> NoResponseAsync(CancellationToken ct) =>
            Task.FromResult<byte[]?>(null);

        private async Task<byte[]?> HandleBattleCmdAsync(CancellationToken ct)
        {
            if (_bot.OnBattleCmdAction == null)
            {
                BotLogger.Warn(_bot.BotID, $"[{BotState.SelectBattleCmd}] No handler registered, skipping.");
                return null;
            }

            var battleState = await _bot.GetBotStateAs<BattleCmdState>(ct);
            _bot.LogBattleCmdState(battleState.CurrentStateData);
            var battleResponse = BotResponseValidator.ValidateBattleCmd(
                await _bot.OnBattleCmdAction(battleState.CurrentStateData),
                battleState.CurrentStateData, _bot.BotID);
            BotLogger.Log(_bot.BotID, "BattleCmd", $"Response -> Action={battleResponse.Action}({(int)battleResponse.Action}) Index={battleResponse.Index}");
            return BotResponseWriter.WriteBattleCmd(battleResponse);
        }

        private async Task<byte[]?> HandleIdleCmdAsync(CancellationToken ct)
        {
            if (_bot.OnIdleCmdAction == null)
            {
                BotLogger.Warn(_bot.BotID, $"[{BotState.SelectIdleCmd}] No handler registered, skipping.");
                return null;
            }

            var idleState = await _bot.GetBotStateAs<IdleCmdState>(ct);
            var idleResponse = BotResponseValidator.ValidateIdleCmd(
                await _bot.OnIdleCmdAction(idleState.CurrentStateData),
                idleState.CurrentStateData, _bot.BotID);
            return BotResponseWriter.WriteIdleCmd(idleResponse);
        }

        private async Task<byte[]?> HandleSelectCardAsync(CancellationToken ct)
        {
            if (_bot.OnSelectCardAction == null)
            {
                BotLogger.Warn(_bot.BotID, $"[{BotState.SelectCard}] No handler registered, skipping.");
                return null;
            }

            var selectCardState = await _bot.GetBotStateAs<SelectCardState>(ct);
            var selectCardData = selectCardState.CurrentStateData;
            var selectCardResponse = BotResponseValidator.ValidateCardSelection(
                await _bot.OnSelectCardAction(selectCardData) ?? Array.Empty<byte>(),
                selectCardData.Minimum, selectCardData.Maximum, selectCardData.SelectableCards?.Length ?? 0,
                "SelectCard", _bot.BotID);
            return BotResponseWriter.WriteCardSelection(selectCardResponse);
        }

        private async Task<byte[]?> HandleSelectUnselectCardAsync(CancellationToken ct)
        {
            if (_bot.OnSelectUnselectCardAction == null)
            {
                BotLogger.Warn(_bot.BotID, $"[{BotState.SelectUnselectCard}] No handler registered, skipping.");
                return null;
            }

            var selectUnselectCardState = await _bot.GetBotStateAs<SelectUnselectCardState>(ct);
            var selectUnselectCardData = selectUnselectCardState.CurrentStateData;
            var selectUnselectCardResponse = BotResponseValidator.ValidateCardSelection(
                await _bot.OnSelectUnselectCardAction(selectUnselectCardData) ?? Array.Empty<byte>(),
                selectUnselectCardData.Minimum, selectUnselectCardData.Maximum, selectUnselectCardData.SelectableCards?.Length ?? 0,
                "SelectUnselectCard", _bot.BotID);
            return BotResponseWriter.WriteCardSelection(selectUnselectCardResponse);
        }

        private async Task<byte[]?> HandleSelectChainAsync(CancellationToken ct)
        {
            if (_bot.OnSelectChainAction == null)
            {
                BotLogger.Warn(_bot.BotID, $"[{BotState.SelectChain}] No handler registered, skipping.");
                return null;
            }

            var selectChainState = await _bot.GetBotStateAs<SelectChainState>(ct);
            var selectChainResponse = BotResponseValidator.ValidateSelectChain(
                await _bot.OnSelectChainAction(selectChainState.CurrentStateData),
                selectChainState.CurrentStateData, _bot.BotID);
            return BotResponseWriter.WriteSelectChain(selectChainResponse);
        }

        private async Task<byte[]?> HandleSelectCounterAsync(CancellationToken ct)
        {
            if (_bot.OnSelectCounterAction == null)
            {
                BotLogger.Warn(_bot.BotID, $"[{BotState.SelectCounter}] No handler registered, skipping.");
                return null;
            }

            var selectCounterState = await _bot.GetBotStateAs<SelectCounterState>(ct);
            var selectCounterResponse = BotResponseValidator.ValidateSelectCounter(
                await _bot.OnSelectCounterAction(selectCounterState.CurrentStateData),
                selectCounterState.CurrentStateData, _bot.BotID);
            return BotResponseWriter.WriteSelectCounter(selectCounterResponse);
        }

        private async Task<byte[]?> HandleSelectDisfieldAsync(CancellationToken ct)
        {
            if (_bot.OnSelectDisfieldAction == null)
            {
                BotLogger.Warn(_bot.BotID, $"[{BotState.SelectDisfield}] No handler registered, skipping.");
                return null;
            }

            var selectDisfieldState = await _bot.GetBotStateAs<SelectDisfieldState>(ct);
            var disfieldData = selectDisfieldState.CurrentStateData;
            BotLogger.Log(_bot.BotID, "SelectDisfield", $"CardID={disfieldData.CardID} Player={disfieldData.Player} Location=0x{disfieldData.Location:X2} Filter=0x{disfieldData.Filter:X8}");
            var selectDisfieldResponse = BotResponseValidator.ValidateZoneSelection(
                await _bot.OnSelectDisfieldAction(disfieldData),
                disfieldData.Filter, disfieldData.Player, (byte)disfieldData.Location,
                "SelectDisfield", _bot.BotID);
            LogZoneSelectionResponse("SelectDisfield", selectDisfieldResponse);
            return BotResponseWriter.WriteZoneSelection(selectDisfieldResponse);
        }

        private async Task<byte[]?> HandleSelectEffectYnAsync(CancellationToken ct)
        {
            if (_bot.OnSelectEffectYnAction == null)
            {
                BotLogger.Warn(_bot.BotID, $"[{BotState.SelectEffectYn}] No handler registered, skipping.");
                return null;
            }

            var selectEffectYnState = await _bot.GetBotStateAs<SelectEffectYnState>(ct);
            var selectEffectYnResponse = await _bot.OnSelectEffectYnAction(selectEffectYnState.CurrentStateData);
            return BotResponseWriter.WriteBoolean(selectEffectYnResponse);
        }

        private async Task<byte[]?> HandleSelectOptionAsync(CancellationToken ct)
        {
            if (_bot.OnSelectOptionAction == null)
            {
                BotLogger.Warn(_bot.BotID, $"[{BotState.SelectOption}] No handler registered, skipping.");
                return null;
            }

            var selectOptionState = await _bot.GetBotStateAs<SelectOptionState>(ct);
            var selectedOptionResponse = BotResponseValidator.ValidateSelectOption(
                await _bot.OnSelectOptionAction(selectOptionState.CurrentStateData),
                selectOptionState.CurrentStateData, _bot.BotID);
            return BotResponseWriter.WriteSelectOption(selectedOptionResponse);
        }

        private async Task<byte[]?> HandleSelectPlaceAsync(CancellationToken ct)
        {
            if (_bot.OnSelectPlaceAction == null)
            {
                BotLogger.Warn(_bot.BotID, $"[{BotState.SelectPlace}] No handler registered, skipping.");
                return null;
            }

            var selectPlaceState = await _bot.GetBotStateAs<SelectPlaceState>(ct);
            var placeData = selectPlaceState.CurrentStateData;
            BotLogger.Log(_bot.BotID, "SelectPlace", $"CardID={placeData.CardID} Player={placeData.Player} Location=0x{placeData.Location:X2} Filter=0x{placeData.Filter:X8}");
            var selectPlaceResponse = BotResponseValidator.ValidateZoneSelection(
                await _bot.OnSelectPlaceAction(placeData),
                placeData.Filter, placeData.Player, (byte)placeData.Location,
                "SelectPlace", _bot.BotID);
            LogZoneSelectionResponse("SelectPlace", selectPlaceResponse);
            return BotResponseWriter.WriteZoneSelection(selectPlaceResponse);
        }

        private async Task<byte[]?> HandleSelectPositionAsync(CancellationToken ct)
        {
            if (_bot.OnSelectPositionAction == null)
            {
                BotLogger.Warn(_bot.BotID, $"[{BotState.SelectPosition}] No handler registered, skipping.");
                return null;
            }

            var selectPositionState = await _bot.GetBotStateAs<SelectPositionState>(ct);
            var selectPositionResponse = BotResponseValidator.ValidateSelectPosition(
                await _bot.OnSelectPositionAction(selectPositionState.CurrentStateData),
                selectPositionState.CurrentStateData, _bot.BotID);
            return BotResponseWriter.WriteSelectPosition(selectPositionResponse);
        }

        private async Task<byte[]?> HandleSelectSumAsync(CancellationToken ct)
        {
            if (_bot.OnSelectSumAction == null)
            {
                BotLogger.Warn(_bot.BotID, $"[{BotState.SelectSum}] No handler registered, skipping.");
                return null;
            }

            var selectSumState = await _bot.GetBotStateAs<SelectSumState>(ct);
            var selectSumData = selectSumState.CurrentStateData;
            var selectSumResponse = BotResponseValidator.ValidateCardSelection(
                await _bot.OnSelectSumAction(selectSumData) ?? Array.Empty<byte>(),
                selectSumData.Minimum, selectSumData.Maximum, selectSumData.SelectableCards?.Length ?? 0,
                "SelectSum", _bot.BotID);
            return BotResponseWriter.WriteCardSelection(selectSumResponse);
        }

        private async Task<byte[]?> HandleSelectTributeAsync(CancellationToken ct)
        {
            if (_bot.OnSelectTributeAction == null)
            {
                BotLogger.Warn(_bot.BotID, $"[{BotState.SelectTribute}] No handler registered, skipping.");
                return null;
            }

            var selectTributeState = await _bot.GetBotStateAs<SelectTributeState>(ct);
            var selectTributeData = selectTributeState.CurrentStateData;
            var selectTributeResponse = BotResponseValidator.ValidateCardSelection(
                await _bot.OnSelectTributeAction(selectTributeData) ?? Array.Empty<byte>(),
                selectTributeData.Minimum, selectTributeData.Maximum, selectTributeData.SelectableCards?.Length ?? 0,
                "SelectTribute", _bot.BotID);
            return BotResponseWriter.WriteCardSelection(selectTributeResponse);
        }

        private async Task<byte[]?> HandleAnnounceAttributeAsync(CancellationToken ct)
        {
            if (_bot.OnAnnounceAttributeAction == null)
            {
                BotLogger.Warn(_bot.BotID, $"[{BotState.AnnounceAttribute}] No handler registered, skipping.");
                return null;
            }

            var announceAttributeState = await _bot.GetBotStateAs<AnnounceAttributeState>(ct);
            var announceAttributeResponse = BotResponseValidator.ValidateAnnounceAttribute(
                await _bot.OnAnnounceAttributeAction(announceAttributeState.CurrentStateData),
                announceAttributeState.CurrentStateData, _bot.BotID);
            return BotResponseWriter.WriteAnnounceAttribute(announceAttributeResponse);
        }

        private async Task<byte[]?> HandleAnnounceCardAsync(CancellationToken ct)
        {
            if (_bot.OnAnnounceCardAction == null)
            {
                BotLogger.Warn(_bot.BotID, $"[{BotState.AnnounceCard}] No handler registered, skipping.");
                return null;
            }

            var announceCardState = await _bot.GetBotStateAs<AnnounceCardState>(ct);
            var announceCardResponse = BotResponseValidator.ValidateAnnounceCard(
                await _bot.OnAnnounceCardAction(announceCardState.CurrentStateData),
                "AnnounceCard", _bot.BotID);
            return BotResponseWriter.WriteAnnounceCard(announceCardResponse);
        }

        private async Task<byte[]?> HandleAnnounceNumberAsync(CancellationToken ct)
        {
            if (_bot.OnAnnounceNumberAction == null)
            {
                BotLogger.Warn(_bot.BotID, $"[{BotState.AnnounceNumber}] No handler registered, skipping.");
                return null;
            }

            var announceNumberState = await _bot.GetBotStateAs<AnnounceNumberState>(ct);
            var announceNumberResponse = BotResponseValidator.ValidateAnnounceNumber(
                await _bot.OnAnnounceNumberAction(announceNumberState.CurrentStateData),
                announceNumberState.CurrentStateData, _bot.BotID);
            // Handlers pick a value, but the engine expects its index in SelectableOptions: answering the value 3 to
            // options [3,2,1] (Card Trooper) ended the duel.
            int announceNumberIndex = Math.Max(0, Array.IndexOf(announceNumberState.CurrentStateData.SelectableOptions ?? Array.Empty<int>(), announceNumberResponse));
            return BotResponseWriter.WriteAnnounceNumber(announceNumberIndex);
        }

        private async Task<byte[]?> HandleAnnounceRaceAsync(CancellationToken ct)
        {
            if (_bot.OnAnnounceRaceAction == null)
            {
                BotLogger.Warn(_bot.BotID, $"[{BotState.AnnounceRace}] No handler registered, skipping.");
                return null;
            }

            var announceRaceState = await _bot.GetBotStateAs<AnnounceRaceState>(ct);
            var announceRaceResponse = BotResponseValidator.ValidateAnnounceRace(
                await _bot.OnAnnounceRaceAction(announceRaceState.CurrentStateData),
                announceRaceState.CurrentStateData, _bot.BotID);
            return BotResponseWriter.WriteAnnounceRace(announceRaceResponse);
        }

        private async Task<byte[]?> HandleAnnounceCardFilterAsync(CancellationToken ct)
        {
            if (_bot.OnAnnounceCardFilterAction == null)
            {
                BotLogger.Warn(_bot.BotID, $"[{BotState.AnnounceCardFilter}] No handler registered, skipping.");
                return null;
            }

            var announceCardFilterState = await _bot.GetBotStateAs<AnnounceCardFilterState>(ct);
            var announceCardFilterResponse = BotResponseValidator.ValidateAnnounceCard(
                await _bot.OnAnnounceCardFilterAction(announceCardFilterState.CurrentStateData),
                "AnnounceCardFilter", _bot.BotID);
            return BotResponseWriter.WriteAnnounceCard(announceCardFilterResponse);
        }

        private async Task<byte[]?> HandleSelectYesNoAsync(CancellationToken ct)
        {
            if (_bot.OnSelectYesNoAction == null)
            {
                BotLogger.Warn(_bot.BotID, $"[{BotState.SelectYesNo}] No handler registered, skipping.");
                return null;
            }

            var selectYesNoState = await _bot.GetBotStateAs<SelectYesNoState>(ct);
            var selectYesNoResponse = await _bot.OnSelectYesNoAction(selectYesNoState.CurrentStateData);
            return BotResponseWriter.WriteBoolean(selectYesNoResponse);
        }

        private async Task<byte[]?> HandleRockPaperScissorsAsync(CancellationToken ct)
        {
            if (_bot.OnSelectRockPaperScissors == null)
            {
                BotLogger.Warn(_bot.BotID, $"[{BotState.RockPaperScissors}] No handler registered, skipping.");
                return null;
            }

            var rpsResponse = BotResponseValidator.ValidateRockPaperScissors(
                await _bot.OnSelectRockPaperScissors(), _bot.BotID);
            return BotResponseWriter.WriteRockPaperScissors(rpsResponse);
        }

        private async Task<byte[]?> HandleSortCardAsync(CancellationToken ct)
        {
            if (_bot.OnSortCardAction == null)
            {
                BotLogger.Warn(_bot.BotID, $"[{BotState.SortCard}] No handler registered, skipping.");
                return null;
            }

            var sortCardState = await _bot.GetBotStateAs<SortCardState>(ct);
            var sortCardResponse = BotResponseValidator.ValidateSortCard(
                await _bot.OnSortCardAction(sortCardState.CurrentStateData),
                sortCardState.CurrentStateData, _bot.BotID);
            return BotResponseWriter.WriteSortCard(sortCardResponse);
        }

        private void LogZoneSelectionResponse(string stateName, int response)
        {
            BotLogger.Log(_bot.BotID, stateName,
                $"Response=0x{response:X8} (bytes: {response & 0xFF},{(response >> 8) & 0xFF},{(response >> 16) & 0xFF},{(response >> 24) & 0xFF})");
        }
    }
}
