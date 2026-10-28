using BotTest.Game.Field;
using BotTest.Utils;
using OmegaBotWrapper;
using OmegaBotWrapper.Enum;
using OmegaBotWrapper.Heuristics;
using OmegaBotWrapper.States;
using OmegaBotWrapper.Struct;

namespace BotTest.Game.AI.Brains
{
    /// <summary>
    /// Shared base for real deck brains. It keeps the current behavior-oriented
    /// helpers from BotTest while moving state and field access into the brain model.
    /// </summary>
    public abstract class DeckBotBrain : DefaultBotBrain
    {
        protected enum PendingActionKind
        {
            None,
            Search,
            Reveal,
            Discard,
            SendToGrave,
            Revive,
            MaterialSelection,
            TargetSelection
        }

        protected readonly struct PendingActionContext
        {
            public MainAction? Action { get; init; }

            public int CardId { get; init; }

            public int Description { get; init; }

            public PendingActionKind Kind { get; init; }

            public int ContextCode { get; init; }
        }

        private DuelBotWrapper? _trackedBot;
        private GameField? _gameField;
        private BotStateHeader _currentHeader;
        private int _trackedTurn = -1;
        private readonly HashSet<int> _activatedThisTurn = new();
        private readonly List<int> _chainDescriptions = new();
        private DynamicCard? _selectedDefenderCard;

        protected PendingActionContext PendingAction { get; private set; }

        protected DeckBotBrain(
            ICardEvaluator? cardEvaluator = null,
            IBoardEvaluator? boardEvaluator = null,
            IActionScorer? actionScorer = null)
            : base(cardEvaluator, boardEvaluator, actionScorer)
        {
        }

        public override async Task<BattleCmdResponse> OnBattleCmdAsync(DecisionContext<BattleCmdData> context, CancellationToken ct = default)
        {
            await PrepareContextAsync(context, ct);
            ConsoleUtils.PrintColored("OnBattleCmd - Generic attack evaluation");
            var attackableCardArray = context.StateData.AttackableCards ?? Array.Empty<DynamicCard>();

            var attackableCards = attackableCardArray
                .Where(card => !card.Attacked && !card.IsDisabled())
                .OrderByDescending(card => card.Attack)
                .ToList();

            if (!attackableCards.Any())
            {
                ConsoleUtils.PrintColored("No attackable cards available.");
                _selectedDefenderCard = null;
                return context.StateData.CanMainPhaseTwo ? BattleAction.ToMainPhaseTwo : BattleAction.ToEndPhase;
            }

            var opponentMonsters = (await GetPlayerCardsInZoneAsync(CardLocation.MonsterZone))
                .Where(card => !card.IsDisabled())
                .ToList();

            if (!opponentMonsters.Any())
            {
                var directAttacker = attackableCards
                    .Where(card => card.CanDirectAttack)
                    .OrderByDescending(card => card.Attack)
                    .FirstOrDefault();

                if (directAttacker.ID > 0 &&
                    attackableCardArray.TryGetByID(directAttacker.ID, out _, out int index))
                {
                    ConsoleUtils.PrintColored($"Direct Attack with Card ID: {directAttacker.ID}, Attack: {directAttacker.Attack}, Index: {index}");
                    _selectedDefenderCard = null;
                    return attackableCardArray[index].DeclareAttack();
                }

                ConsoleUtils.PrintColored("No direct attack possible.");
                _selectedDefenderCard = null;
                return context.StateData.CanMainPhaseTwo ? BattleAction.ToMainPhaseTwo : BattleAction.ToEndPhase;
            }

            var bestBattle = attackableCards
                .SelectMany(attacker => opponentMonsters.Select(target => new
                {
                    Attacker = attacker,
                    Target = target,
                    AttackerPower = attacker.GetRealPower(),
                    Score = GetAttackScore(attacker, target)
                }))
                .Where(option => option.Score > 0)
                .OrderByDescending(option => option.Score)
                .ThenByDescending(option => option.AttackerPower)
                .FirstOrDefault();

            if (bestBattle != null &&
                attackableCardArray.TryGetByID(bestBattle.Attacker.ID, out _, out int attackerIndex))
            {
                ConsoleUtils.PrintColored($"Attacking target ID {bestBattle.Target.ID} with attacker ID {bestBattle.Attacker.ID}");
                _selectedDefenderCard = bestBattle.Target;
                return attackableCardArray[attackerIndex].DeclareAttack();
            }

            var facedownDefenseTarget = opponentMonsters
                .Where(target => !target.IsFaceup() && target.IsDefense())
                .OrderBy(target => target.CurrentCoordinates.Sequence)
                .FirstOrDefault();

            if (facedownDefenseTarget.ID > 0)
            {
                var opportunisticAttacker = attackableCards
                    .OrderByDescending(card => card.Attack)
                    .FirstOrDefault();

                if (opportunisticAttacker.ID > 0 &&
                    attackableCardArray.TryGetByID(opportunisticAttacker.ID, out _, out int opportunisticAttackerIndex))
                {
                    ConsoleUtils.PrintColored($"Taking the opportunity to attack facedown defense target ID {facedownDefenseTarget.ID} with attacker ID {opportunisticAttacker.ID}");
                    _selectedDefenderCard = facedownDefenseTarget;
                    return attackableCardArray[opportunisticAttackerIndex].DeclareAttack();
                }
            }

            ConsoleUtils.PrintColored("No advantageous attack targets found.");
            _selectedDefenderCard = null;
            return context.StateData.CanMainPhaseTwo ? BattleAction.ToMainPhaseTwo : BattleAction.ToEndPhase;
        }

        public override async Task<IdleCmdResponse> OnIdleCmdAsync(DecisionContext<IdleCmdData> context, CancellationToken ct = default)
        {
            await PrepareContextAsync(context, ct);
            ConsoleUtils.PrintColored("[DECK BOT BRAIN] OnIdleCmd");

            var opponentMonsters = (await GetPlayerCardsInZoneAsync(CardLocation.MonsterZone))
                .Where(card => !card.IsDisabled())
                .ToList();

            var repositionToDefense = (context.StateData.ReposableCards ?? Array.Empty<DynamicCard>())
                .Where(card => card.IsFaceup() && card.IsAttack() && ShouldRepositionToDefense(card, opponentMonsters))
                .OrderBy(card => card.Attack)
                .FirstOrDefault();

            if (repositionToDefense.ID > 0)
            {
                ConsoleUtils.PrintColored($"Repositioning to defense: {repositionToDefense.ID}");
                return repositionToDefense.Reposition();
            }

            var repositionToAttack = (context.StateData.ReposableCards ?? Array.Empty<DynamicCard>())
                .Where(card => card.IsDefense() && ShouldRepositionToAttack(card, opponentMonsters))
                .OrderByDescending(card => card.Attack)
                .FirstOrDefault();

            if (repositionToAttack.ID > 0)
            {
                ConsoleUtils.PrintColored($"Repositioning to attack: {repositionToAttack.ID}");
                return repositionToAttack.Reposition();
            }

            return await base.OnIdleCmdAsync(context, ct);
        }

        public override async Task<int> OnSelectChainAsync(DecisionContext<SelectChainData> context, CancellationToken ct = default)
        {
            await PrepareContextAsync(context, ct);
            RegisterChainDescriptions(context.StateData.Descriptions ?? Array.Empty<int>());
            return await base.OnSelectChainAsync(context, ct);
        }

        public override async Task<IList<byte>?> OnSelectCardAsync(DecisionContext<SelectCardData> context, CancellationToken ct = default)
        {
            await PrepareContextAsync(context, ct);
            ConsoleUtils.PrintColored($"[DECK BOT BRAIN] [OnSelectCard] => {(DHint)context.StateData.SelectionHint}");

            if (context.StateData.SelectionHint == (int)DHint.TARGET)
            {
                byte[] botCards = context.StateData.SelectableCards
                    .Select((card, index) => new { Card = card, Index = index })
                    .OrderByDescending(x => x.Card.Attack)
                    .Take(context.StateData.Minimum)
                    .Select(x => (byte)x.Index)
                    .ToArray();

                if (botCards.Any())
                {
                    ConsoleUtils.PrintColored($"Selected {botCards.Length} cards for TARGET.");
                    return botCards;
                }
            }

            if (context.StateData.SelectionHint == (int)DHint.ATTACKTARGET)
            {
                ConsoleUtils.PrintColored("OnSelectCard - Selecting Defender");

                if (_selectedDefenderCard.HasValue)
                {
                    var selectedDefender = _selectedDefenderCard.Value;
                    if (context.StateData.SelectableCards.TryGetByIDWithZone(selectedDefender.ID, selectedDefender.CurrentCoordinates.Sequence, out _, out int selectedIndex))
                    {
                        ConsoleUtils.PrintColored($"Selecting defender by card code and sequence: {selectedDefender.ID} / {selectedDefender.CurrentCoordinates.Sequence}");
                        return new List<byte> { (byte)selectedIndex };
                    }
                }
            }

            if (context.StateData.SelectionHint is (int)DHint.DESTROY or (int)DHint.REMOVE)
            {
                string actionName = context.StateData.SelectionHint == (int)DHint.DESTROY ? "DESTROY" : "REMOVE";
                ConsoleUtils.PrintColored($"OnSelectCard - Selecting Card to {actionName}");

                byte[] playerCards = context.StateData.SelectableCards
                    .Select((card, index) => new { Card = card, Index = index })
                    .Where(x => x.Card.Owner == 1)
                    .Take(context.StateData.Minimum)
                    .Select(x => (byte)x.Index)
                    .ToArray();

                if (playerCards.Any())
                {
                    ConsoleUtils.PrintColored($"Selected {playerCards.Length} cards for {actionName}.");
                    return playerCards;
                }
            }

            ConsoleUtils.PrintColored("No specific defender index available, using default selection.");
            return await base.OnSelectCardAsync(context, ct);
        }

        public override async Task<int> OnAnnounceCardAsync(DecisionContext<AnnounceCardData> context, CancellationToken ct = default)
        {
            ConsoleUtils.PrintColored("[DECK BOT BRAIN] OnAnnounceCard");
            var cards = await DuelBotWrapper.GetMatchingCards(context.StateData.AppliableFilters ?? Array.Empty<int>());
            return BotActionUtilities.SelectRandomAnnouncedCard(cards, preferAlias: true);
        }

        protected async Task<GameField> PrepareContextAsync<TState>(DecisionContext<TState> context, CancellationToken ct = default)
        {
            if (_trackedBot != context.Bot || _gameField == null)
            {
                _trackedBot = context.Bot;
                _gameField = new GameField(context.Bot);
                _trackedTurn = -1;
                _activatedThisTurn.Clear();
                _chainDescriptions.Clear();
                PendingAction = default;
            }

            var header = context.Header;
            _currentHeader = header;

            // The same brain plays every game of a match and turns restart at 1: a lower turn means a new game.
            if (_trackedTurn > 0 && header.CurrentTurn < _trackedTurn)
                OnNewDuel();

            if (header.CurrentTurn != _trackedTurn)
            {
                _trackedTurn = header.CurrentTurn;
                _activatedThisTurn.Clear();
                _chainDescriptions.Clear();
                PendingAction = default;
            }

            await RefreshFieldAsync();
            return _gameField;
        }

        /// <summary>A new game of the match started: clear everything remembered from the previous one.</summary>
        protected virtual void OnNewDuel()
        {
        }

        protected GameField CurrentField =>
            _gameField ?? throw new InvalidOperationException("The deck brain field is not initialized. Call PrepareContextAsync first.");

        protected BotStateHeader CurrentHeader => _currentHeader;

        protected virtual Task RefreshFieldAsync() => CurrentField.GetBotField();

        protected virtual async Task<List<DynamicCard>> GetPlayerCardsInZoneAsync(CardLocation location, bool isOwnSide = true) =>
            (await CurrentField.GetPlayerCardsInZone(location, isOwnSide)).ToList();

        protected virtual Task<int> GetPlayerSpellZoneCountAsync(CardLocation location = CardLocation.SpellZone) =>
            CurrentField.GetPlayerSpellZoneCount(location);

        protected virtual Task<bool> HasCardInBotMonsterZoneAsync(int cardId) =>
            CurrentField.HasCardInBotMonsterZone(cardId);

        protected virtual Task<bool> HasCardInBotSpellZoneAsync(int cardId) =>
            CurrentField.HasCardInBotSpellZone(cardId);

        protected virtual Task<List<DynamicCard>> GetBotHandCardsAsync() =>
            CurrentField.GetBotHandCards();

        protected virtual Task<List<DynamicCard>> GetBotGraveyardCardsAsync() =>
            CurrentField.GetBotGraveyardCards();

        protected virtual Task<List<DynamicCard>> GetBotMonsterCardsAsync(bool onlyFaceUp = false) =>
            CurrentField.GetBotMonsterCards(onlyFaceUp);

        protected virtual Task<List<DynamicCard>> GetEnemyMonsterCardsAsync(bool onlyFaceUp = false) =>
            CurrentField.GetEnemyMonsterCards(onlyFaceUp);

        protected virtual Task<List<DynamicCard>> GetEnemySpellCardsAsync(bool onlyFaceUp = false) =>
            CurrentField.GetEnemySpellCards(onlyFaceUp);

        protected void SetPendingAction(PendingActionKind kind, int cardId = 0, MainAction? action = null, int description = 0, int contextCode = 0)
        {
            PendingAction = new PendingActionContext
            {
                Kind = kind,
                CardId = cardId,
                Action = action,
                Description = description,
                ContextCode = contextCode
            };
        }

        protected void ClearPendingAction() => PendingAction = default;

        protected void MarkCardActivated(int cardId)
        {
            if (cardId > 0)
                _activatedThisTurn.Add(cardId);
        }

        protected bool WasCardActivatedThisTurn(int cardId) => _activatedThisTurn.Contains(cardId);

        protected void RegisterChainDescriptions(IEnumerable<int> descriptions)
        {
            _chainDescriptions.Clear();
            _chainDescriptions.AddRange(descriptions);
        }

        protected bool IsFirstTurnOrMain2(BotStateHeader header) =>
            header.CurrentTurn <= 1 || (DuelPhase)header.CurrentPhase == DuelPhase.Main2;

        protected bool PendingContextIs(int contextCode) => PendingAction.ContextCode == contextCode;

        protected static bool TryGetSummonable(IdleCmdData state, int cardId, out DynamicCard card) =>
            (state.SummonableCards ?? Array.Empty<DynamicCard>()).TryGetByID(cardId, out card);

        protected static bool TryGetSpecialSummon(IdleCmdData state, int cardId, out DynamicCard card) =>
            (state.SpecialSummonableCards ?? Array.Empty<DynamicCard>()).TryGetByID(cardId, out card);

        protected static bool TryGetSpellSettable(IdleCmdData state, int cardId, out DynamicCard card) =>
            (state.SpellSetableCards ?? Array.Empty<DynamicCard>()).TryGetByID(cardId, out card);

        protected static bool TryGetMonsterSettable(IdleCmdData state, int cardId, out DynamicCard card) =>
            (state.MonsterSetableCards ?? Array.Empty<DynamicCard>()).TryGetByID(cardId, out card);

        protected static bool TryGetActivable(IdleCmdData state, int cardId, out DynamicCard card) =>
            (state.ActivableCards ?? Array.Empty<DynamicCard>()).TryGetByID(cardId, out card);

        protected static bool TryGetReposable(IdleCmdData state, int cardId, out DynamicCard card) =>
            (state.ReposableCards ?? Array.Empty<DynamicCard>()).TryGetByID(cardId, out card);

        protected IList<byte>? PreferCardSelection(SelectCardData state, params int[] preferredIds)
        {
            var selected = state.SelectableCards
                .Select((card, index) => new { Card = card, Index = index })
                .Where(x => x.Card.IsCode(preferredIds))
                .Take(state.Minimum)
                .Select(x => (byte)x.Index)
                .ToArray();

            return selected.Length >= state.Minimum ? selected : null;
        }

        protected virtual async Task<DynamicCard> GetBestEnemyMonster(bool onlyFaceUp = false) =>
            await CurrentField.GetBestEnemyMonster(onlyFaceUp);

        protected virtual async Task<DynamicCard> GetBestEnemySpell(bool onlyFaceUp = false) =>
            await CurrentField.GetBestEnemySpell(onlyFaceUp);

        protected virtual async Task<DynamicCard> GetBestEnemyCard(bool onlyFaceUp = false) =>
            await CurrentField.GetBestEnemyCard(onlyFaceUp);

        protected static bool CanBuildLevel8Synchro(IdleCmdData state)
        {
            return (state.SpecialSummonableCards ?? Array.Empty<DynamicCard>()).Any(card => card.IsCode(69248256, 83755611, 43202238));
        }

        protected static bool CanBuildLevel10Synchro(IdleCmdData state)
        {
            return (state.SpecialSummonableCards ?? Array.Empty<DynamicCard>()).Any(card => card.IsCode(96633955, 84815190, 47710198));
        }

        protected static bool ShouldRepositionToAttack(DynamicCard card, IEnumerable<DynamicCard> opponentMonsters)
        {
            var opponents = opponentMonsters.ToList();
            if (!opponents.Any())
                return true;

            return opponents.Any(opponent => GetAttackScore(card, opponent) > 0);
        }

        protected static bool ShouldRepositionToDefense(DynamicCard card, IEnumerable<DynamicCard> opponentMonsters)
        {
            int strongestOpponentAttack = opponentMonsters
                .Where(opponent => opponent.IsFaceup() && opponent.IsAttack())
                .Select(opponent => opponent.Attack)
                .DefaultIfEmpty(0)
                .Max();

            return strongestOpponentAttack > card.Attack;
        }

        protected static int GetAttackScore(DynamicCard attacker, DynamicCard defender)
        {
            // Jain gains 300 in the Damage Step when it attacks a monster: 1800 into 1800 wins (Javier).
            int attackerPower = attacker.Attack + (attacker.IsCode(Knowledge.CardKnowledge.LightswornPaladinJain) ? 300 : 0);
            int defenderPower = defender.GetRealPower();

            if (attackerPower <= defenderPower)
                return 0;

            int score = 10_000 + defenderPower;

            if (defender.IsFaceup() && defender.IsAttack())
                score += 2_000;
            else if (defender.IsDefense())
                score += 1_000;

            score += attackerPower - defenderPower;
            return score;
        }
    }
}
