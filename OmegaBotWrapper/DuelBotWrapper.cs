using OmegaBotWrapper.Struct;
using OmegaBotWrapper.Enum;
using OmegaBotWrapper.States;
using OmegaBotWrapper.Heuristics;

namespace OmegaBotWrapper
{
    /// <summary>
    /// Global wrapper to access the duel bot functionality
    /// </summary>
    public class DuelBotWrapper
    {
        private readonly OmegaApiClient _api;
        private readonly BotStateDispatcher _dispatcher;

        /// <summary>
        /// Which bot this instance is connected to
        /// </summary>
        public int BotID { get; private set; }
        /// <summary>
        /// Name of the deck the bot is currently assigned to
        /// </summary>
        public string DeckName { get; private set; }
        /// <summary>
        /// Deck code (hash) of the deck the bot is currently assigned to
        /// </summary>
        public string DeckHash { get; private set; }
        /// <summary>
        /// Creates a new bot wrapper with given deck
        /// </summary>
        public DuelBotWrapper(string deckName, string deckHash, OmegaApiClient? api = null)
        {
            DeckName = deckName;
            DeckHash = deckHash;
            BotID = -1;
            _api = api ?? OmegaApiClient.Default;
            _dispatcher = new BotStateDispatcher(this);
        }
        /// <summary>
        /// Creates a new bot wrapper backed by a heuristic brain while preserving the existing action pipeline.
        /// </summary>
        public DuelBotWrapper(string deckName, string deckHash, IBotBrain botBrain, OmegaApiClient? api = null)
            : this(new BotBrainActionsAdapter(botBrain, deckName, deckHash), api)
        {
        }
        /// <summary>
        /// Creates a new bot wrapper with given set of actions
        /// </summary>
        public DuelBotWrapper(IBotActions botActions, OmegaApiClient? api = null)
        {
            botActions.BotWrapper = this;
            DeckName = botActions.DeckName ?? string.Empty;
            DeckHash = botActions.DeckHash ?? string.Empty;
            BotID = -1;
            _api = api ?? OmegaApiClient.Default;
            _dispatcher = new BotStateDispatcher(this);
            botActions.RegisterActions(this);
        }
        /// <summary>
        /// Connects the bot to the given client bot slot
        /// </summary>
        /// <param name="slot">Slot of the bot that this instance will control or -1 for first available</param>
        /// <returns>True if the bot was found and connection was realized or False in case there was no slot available or the given slot was already taken or non-existant</returns>
        public async Task<bool> Connect(int slot = -1, CancellationToken ct = default)
        {
            var botList = await _api.GetBotList(ct);
            if (botList.BotCount <= 0)
                return false;
            if (slot >= 0)
            {
                if (botList.BotCount <= slot || botList.Bots[slot].DeckIndex != 0)
                    return false;
                BotID = botList.Bots[slot].BotID;
            }
            else
            {
                foreach (var bot in botList.Bots)
                {
                    if (bot.DeckIndex != 0)
                        continue;
                    var botState = await _api.GetBotState(bot.BotID, ct);
                    if (botState.CurrentStateHeader.CurrentStateCode != (byte)BotState.WaitingForDeck || botState.CurrentStateData.IsReady)
                        continue;
                    BotID = bot.BotID;
                    break;
                }
            }
            if (BotID < 0)
                return false;
            return await _api.SetBotData(BotID, DeckName, DeckHash.ToSafeB64(), ct);
        }
        /// <summary>
        /// Starts all the bot procedures
        /// </summary>
        /// <returns>Will only return once the bot has finished executing</returns>
        public async Task Start(CancellationToken ct = default)
        {
            try
            {
                while (!ct.IsCancellationRequested && await Heartbeat(ct))
                {
                    await Task.Delay(Const.HEARTBEAT_DELAY, ct);
                }
            }
            catch (OperationCanceledException)
            {
                BotLogger.Log(BotID, "System", "Bot stopped by cancellation.");
            }
            catch (Exception ex)
            {
                BotLogger.Error(BotID, "Heartbeat failed", ex);
            }
            finally
            {
                BotLogger.Log(BotID, "System", "Bot has finished execution");
            }
        }
        /// <summary>
        /// Action to be taken at battle phase, should return the action and its index (or 0 if not applicable)
        /// </summary>
        public Func<BattleCmdData, Task<BattleCmdResponse>>? OnBattleCmdAction { get; set; }
        /// <summary>
        /// Action to be taken at main phase, should return the action and its index (or 0 if not applicable)
        /// </summary>
        public Func<IdleCmdData, Task<IdleCmdResponse>>? OnIdleCmdAction { get; set; }
        /// <summary>
        /// Action to be taken when selecting something to chain, should return the index of the selected chain to activate or, if allowed, -1 for none
        /// </summary>
        public Func<SelectChainData, Task<int>>? OnSelectChainAction { get; set; }
        /// <summary>
        /// Action to be taken when selecting card(s), should return the indexes that was selected from the selectable options
        /// </summary>
        public Func<SelectCardData, Task<IList<byte>?>>? OnSelectCardAction { get; set; }
        /// <summary>
        /// Action to be taken when selecting tribute(s), should return the indexes that was selected from the selectable options
        /// </summary>
        public Func<SelectTributeData, Task<IList<byte>?>>? OnSelectTributeAction { get; set; }
        /// <summary>
        /// Action to be taken when selecting a sum (eg. synchro/ritual), should return the indexes that was selected from the selectable options
        /// </summary>
        public Func<SelectSumData, Task<IList<byte>?>>? OnSelectSumAction { get; set; }
        /// <summary>
        /// Action to be taken when announcing attribute(s), should return the attribute(s) selected
        /// </summary>
        public Func<AnnounceAttributeData, Task<IList<CardAttribute>>>? OnAnnounceAttributeAction { get; set; }
        /// <summary>
        /// Action to be taken when announcing race(s), should return the race(s) selected
        /// </summary>
        public Func<AnnounceRaceData, Task<IList<CardRace>>>? OnAnnounceRaceAction { get; set; }
        /// <summary>
        /// Action to be taken when announcing a number, should return the index of the number selected from the selectable options
        /// </summary>
        public Func<AnnounceNumberData, Task<int>>? OnAnnounceNumberAction { get; set; }
        /// <summary>
        /// Action to be taken when announcing a card, should return the card id that was selected
        /// </summary>
        public Func<AnnounceCardData, Task<int>>? OnAnnounceCardAction { get; set; }
        /// <summary>
        /// Action to be taken when announcing a card with filter, should return the card id that was selected
        /// </summary>
        public Func<AnnounceCardFilterData, Task<int>>? OnAnnounceCardFilterAction { get; set; }
        /// <summary>
        /// Action to be taken when selecting/unselecting card(s), should return the indexes that was selected from the selectable options
        /// </summary>
        public Func<SelectUnselectCardData, Task<IList<byte>?>>? OnSelectUnselectCardAction { get; set; }
        /// <summary>
        /// Action to be taken when selecting counter(s), should return the amount of counters picked from each of the indexes that was present on the selectable options
        /// </summary>
        public Func<SelectCounterData, Task<IList<short>>>? OnSelectCounterAction { get; set; }
        /// <summary>
        /// Action to be taken when selecting field zone for disabling, should return the integer representing the zone selected (use the 'Zones' class helper for values)
        /// </summary>
        public Func<SelectDisfieldData, Task<int>>? OnSelectDisfieldAction { get; set; }
        /// <summary>
        /// Action to be taken when selecting field zone, should return the integer representing the zone selected (use the 'Zones' class helper for values)
        /// </summary>
        public Func<SelectPlaceData, Task<int>>? OnSelectPlaceAction { get; set; }
        /// <summary>
        /// Action to be taken when selecting yes or no for an effect, should return true or false
        /// </summary>
        public Func<SelectEffectYnData, Task<bool>>? OnSelectEffectYnAction { get; set; }
        /// <summary>
        /// Action to be taken when selecting yes or no, should return true or false
        /// </summary>
        public Func<SelectYesNoData, Task<bool>>? OnSelectYesNoAction { get; set; }
        /// <summary>
        /// Action to be taken when selecting an option, should return the index of the selected option
        /// </summary>
        public Func<SelectOptionData, Task<int>>? OnSelectOptionAction { get; set; }
        /// <summary>
        /// Action to be taken when selecting a rock paper scissors option, should return 1 = scissors, 2 = rock, 3 = paper 
        /// </summary>
        public Func<Task<byte>>? OnSelectRockPaperScissors { get; set; }
        /// <summary>
        /// Action to be taken when selecting position, should return the position selected
        /// </summary>
        public Func<SelectPositionData, Task<CardPosition>>? OnSelectPositionAction { get; set; }
        /// <summary>
        /// Action to be taken when sorting cards, should return the indexes of the selectable cards in the desired order
        /// </summary>
        public Func<SortCardData, Task<IList<byte>>>? OnSortCardAction { get; set; }
        public BotStateHeader CurrentStateHeader;
        /// <summary>
        /// Main loop of actions from the bot
        /// </summary>
        /// <returns>Will return True everytime the bot finishes one loop without error, or False otherwise</returns>
        private async Task<bool> Heartbeat(CancellationToken ct = default)
        {
            CurrentStateHeader = await _api.GetBotStateHeader(BotID, ct);
            var state = (BotState)CurrentStateHeader.CurrentStateCode;
            await TraceUncommonStateAsync(state, ct);
            var payload = await _dispatcher.DispatchAsync(state, ct);
            if (payload == null)
                return true;
            TraceResponse(state, payload);
            return await SendResponse(payload, ct);
        }

        // Trace of every decision the game asks for, so no prompt goes by unnoticed (e.g. a battle replay).
        private static readonly BotState[] CommonStates =
        {
            BotState.None, BotState.WaitingForDeck, BotState.SelectIdleCmd, BotState.SelectBattleCmd,
            BotState.SelectPlace, BotState.SelectChain
        };

        private string? _lastTrace;

        private async Task TraceUncommonStateAsync(BotState state, CancellationToken ct)
        {
            if (state == BotState.SelectBattleCmd)
            {
                await TraceEmptyBattleCmdAsync(ct);
                return;
            }

            if (CommonStates.Contains(state))
                return;

            string key = $"{(int)state}:{CurrentStateHeader.CurrentTurn}:{CurrentStateHeader.CurrentPhase}";
            if (key == _lastTrace)
                return;
            _lastTrace = key;

            try
            {
                var raw = await _api.GetBotStateAs<Newtonsoft.Json.Linq.JObject>(BotID, ct);
                string json = raw.ToString(Newtonsoft.Json.Formatting.None);
                BotLogger.Log(BotID, "Trace", $"State {state}({(int)state}) raw: {(json.Length > 700 ? json[..700] + "..." : json)}");
            }
            catch (Exception ex)
            {
                BotLogger.Log(BotID, "Trace", $"State {state}({(int)state}) raw unavailable: {ex.Message}");
            }
        }

        // A battle command with nobody able to attack: keep the raw data (to see what happened to a replayed attacker).
        private async Task TraceEmptyBattleCmdAsync(CancellationToken ct)
        {
            try
            {
                var raw = await _api.GetBotStateAs<Newtonsoft.Json.Linq.JObject>(BotID, ct);
                var attackable = raw["CurrentStateData"]?["AttackableCards"] as Newtonsoft.Json.Linq.JArray;
                if (attackable != null && attackable.Count > 0)
                    return;

                string json = raw.ToString(Newtonsoft.Json.Formatting.None);
                string key = $"battle:{CurrentStateHeader.CurrentTurn}:{json.GetHashCode()}";
                if (key == _lastTrace)
                    return;
                _lastTrace = key;
                BotLogger.Log(BotID, "Trace", $"Empty SelectBattleCmd raw: {(json.Length > 900 ? json[..900] + "..." : json)}");
            }
            catch (Exception ex)
            {
                BotLogger.Log(BotID, "Trace", $"Empty SelectBattleCmd raw unavailable: {ex.Message}");
            }
        }

        private void TraceResponse(BotState state, byte[] payload) =>
            BotLogger.Log(BotID, "Trace", $"Answered {state}({(int)state}) turn {CurrentStateHeader.CurrentTurn} " +
                $"phase 0x{CurrentStateHeader.CurrentPhase:X} with [{Convert.ToHexString(payload)}]");
        /// <summary>
        /// Queries information about the bot side of the field
        /// </summary>
        /// <returns>Structure containing the data</returns>
        public async Task<FieldInfo> GetBotFieldInfo() =>
            await _api.GetBotFieldInfo(BotID);

        public static async Task<FieldInfo> GetPlayerFieldInfo() =>
            await OmegaApiClient.Default.GetPlayerFieldInfo();

        public static async Task<DynamicCard> GetPlayerFieldCardInfo(int controller, int location, int sequence) =>
            await OmegaApiClient.Default.GetPlayerFieldCardInfo(controller, location, sequence);

        public async Task<DynamicCard> GetBotFieldCardInfo(int controller, int location, int sequence) =>
            await _api.GetBotFieldCardInfo(BotID, controller, location, sequence);

        public static async Task<DeckData> GetPlayerDeck() =>
            await OmegaApiClient.Default.GetPlayerDeck();

        internal Task<T> GetBotStateAs<T>(CancellationToken ct = default) =>
            _api.GetBotStateAs<T>(BotID, ct);

        internal async Task<bool> SendResponse(byte[] data, CancellationToken ct = default)
        {
            string encoded = data.Base64Encode().ToSafeB64();
            return await _api.SendBotResponse(BotID, encoded, ct);
        }

        public async Task<bool> SendResponse(BinaryWriter pkt)
        {
            byte[] data = ((MemoryStream)pkt.BaseStream).ToArray();
            return await SendResponse(data);
        }

        public static async Task<DBCardList> GetMatchingCards(params int[] filters) =>
            await OmegaApiClient.Default.GetMatchingCards(filters);
        /// <summary>
        /// While on end screen on rematchable duel it will send the rematch request as if you tapped rematch
        /// </summary>
        /// <returns>Rematch Duel</returns>
        public static async Task<string> RematchDuel() =>
            await OmegaApiClient.Default.RematchDuel();

        internal void LogBattleCmdState(BattleCmdData data)
        {
            BotLogger.Log(BotID, "BattleCmd", $"Attackable={data.AttackableCards?.Length ?? 0} Activable={data.ActivableCards?.Length ?? 0} CanM2={data.CanMainPhaseTwo} CanEnd={data.CanEndPhase}");
            if (data.AttackableCards != null)
            {
                for (int i = 0; i < data.AttackableCards.Length; i++)
                {
                    var c = data.AttackableCards[i];
                    int attackIdx = (c.ActionIndex != null && c.ActionIndex.Length > (int)Enum.BattleAction.Attack)
                        ? c.ActionIndex[(int)Enum.BattleAction.Attack]
                        : -1;
                    BotLogger.Log(BotID, "BattleCmd", $"  [{i}] ID={c.ID} ATK={c.Attack} Zone={c.CurrentCoordinates.Sequence} ActionIndex[Attack]={attackIdx} Attacked={c.Attacked} DirectOK={c.CanDirectAttack}");
                }
            }
        }
    }
}
