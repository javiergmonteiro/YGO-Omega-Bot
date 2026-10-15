using BotTest.Game.AI.Knowledge;
using BotTest.Utils;
using OmegaBotWrapper;
using OmegaBotWrapper.Enum;
using OmegaBotWrapper.Heuristics;
using OmegaBotWrapper.States;
using OmegaBotWrapper.Struct;
using static BotTest.Game.AI.Knowledge.CardKnowledge;

namespace BotTest.Game.AI.Brains
{
    /// <summary>
    /// Edison-format rules any deck gets for free: what to do against specific opposing cards
    /// (Stardust, Machina Fortress, Vayu...), Royal Oppression / trap usage, target selection by threat,
    /// and fixes for framework gaps (synchro materials, costs, chain ownership).
    /// Deck brains plug their own plays in through the virtual hooks below.
    /// Rule of thumb: "what do I do against *their* card / how does the game work" lives here,
    /// "how do I play *my* cards" lives in the deck brain.
    /// </summary>
    public abstract class EdisonBotBrain : DeckBotBrain
    {
        private const int MaxActivationsPerCardPerTurn = 3;

        private static readonly Random Rng = new();

        private readonly Dictionary<int, int> _activationCount = new();
        private int _activationTurn = -1;
        private DynamicCard? _forcedAttackTarget;

        private string? _lastRepeatedLog;

        // Enemy monsters (id@zone) at our previous decision, to spot the one being summoned now.
        private HashSet<string> _seenEnemyMonsters = new();

        private List<DynamicCard> NewEnemyMonsters(List<DynamicCard> enemyMonsters) =>
            enemyMonsters.Where(card => !_seenEnemyMonsters.Contains($"{Key(card)}@{card.CurrentCoordinates.Sequence}")).ToList();

        // When each of our monsters (id@zone) was first seen (order stamp, turn): the latest one is the one just summoned.
        private Dictionary<string, (int Stamp, int Turn)> _ownFirstSeen = new();
        private int _ownSeenStamp;

        private static string ZoneKey(DynamicCard card) => $"{Key(card)}@{card.CurrentCoordinates.Sequence}";

        private async Task SnapshotOwnMonstersAsync()
        {
            var keys = (await GetBotMonsterCardsAsync()).Select(ZoneKey).ToHashSet();
            foreach (var key in keys.Where(key => !_ownFirstSeen.ContainsKey(key)))
                _ownFirstSeen[key] = (++_ownSeenStamp, CurrentHeader.CurrentTurn);
            foreach (var gone in _ownFirstSeen.Keys.Where(key => !keys.Contains(key)).ToList())
                _ownFirstSeen.Remove(gone);
        }

        // Our most recently summoned monster this turn (not seen yet counts as the newest).
        private DynamicCard? JustSummoned(List<DynamicCard> ours) =>
            ours.Where(card => card.IsFaceup())
                .Select(card => new { Card = card, Seen = _ownFirstSeen.TryGetValue(ZoneKey(card), out var seen) ? seen : (int.MaxValue, CurrentHeader.CurrentTurn) })
                .Where(x => x.Seen.Item2 == CurrentHeader.CurrentTurn)
                .OrderByDescending(x => x.Seen.Item1)
                .Select(x => (DynamicCard?)x.Card)
                .FirstOrDefault();

        // Our monsters that their resolving removal card will take anyway: Icarus tributes one of them for free.
        private List<DynamicCard> _icarusDoomed = new();

        private void SnapshotEnemyMonsters(List<DynamicCard> enemyMonsters) =>
            _seenEnemyMonsters = enemyMonsters.Select(card => $"{Key(card)}@{card.CurrentCoordinates.Sequence}").ToHashSet();

        // Cards seen in the opponent's hand (Trap Dustshoot) and the turn they were seen, for later rules.
        private List<int> _revealedEnemyHand = new();
        private int _revealedHandTurn = -1;
        private Dictionary<int, int> _revealedPublicBaseline = new();

        // Copies of a card the opponent has in public zones (GY, banished, field).
        private int EnemyPublicCount(int cardId)
        {
            var enemy = CurrentField.EnemySide;
            return (enemy.Grave ?? Array.Empty<int>())
                .Concat(enemy.Banish ?? Array.Empty<int>())
                .Concat((enemy.MonsterZones ?? Array.Empty<CardArray>()).Concat(enemy.SpellZones ?? Array.Empty<CardArray>())
                    .SelectMany(zone => zone.Cards ?? Array.Empty<int>()))
                .Count(id => id > 0 && CardIdentity.Same(id, cardId));
        }

        /// <summary>
        /// A card seen in their hand that is probably still there: no copy has shown up in public since the reveal.
        /// Once one does (activated, destroyed while set...), the one we saw is the one that left.
        /// </summary>
        protected bool StillInRevealedHand(int cardId) =>
            _revealedEnemyHand.Any(id => CardIdentity.Same(id, cardId)) &&
            EnemyPublicCount(cardId) <= (_revealedPublicBaseline.TryGetValue(CardIdentity.Canonical(cardId), out int seen) ? seen : 0);

        // Attack declared expecting Book of Moon on this monster in the declaration window.
        private DynamicCard? _moonOnAttack;

        // A target chosen while deciding to chain (e.g. Book of Moon on the attacker).
        private DynamicCard? _preferredTarget;

        // Tytannial (with a Plant to tribute) is on their field at the current idle decision.
        private bool _targetingBlocked;
        private bool _tytannialActive;

        protected readonly ChainTracker Chain = new();

        protected EdisonBotBrain(
            ICardEvaluator? cardEvaluator = null,
            IBoardEvaluator? boardEvaluator = null,
            IActionScorer? actionScorer = null)
            : base(cardEvaluator, boardEvaluator, actionScorer)
        {
        }

        /// <summary>Snapshot of the board for one idle decision.</summary>
        protected sealed class Board
        {
            public required List<DynamicCard> EnemyMonsters { get; init; }
            public required List<DynamicCard> EnemySpells { get; init; }
            public required List<DynamicCard> OwnMonsters { get; init; }
            public required List<DynamicCard> OwnSpells { get; init; }
            public required List<DynamicCard> Hand { get; init; }
            public required List<DynamicCard> Graveyard { get; init; }
            public required bool IsMain1 { get; init; }
            public required bool StardustBlocking { get; init; }
            public required bool ControlsStolenMonster { get; init; }
            public required bool TargetingBlocked { get; init; }

            /// <summary>The opponent plays Gladiator Beasts (seen in their GY, banished, field or revealed hand).</summary>
            public required bool VsGladiatorBeasts { get; init; }
        }

        // ---- Deck hooks -------------------------------------------------------------------------------

        /// <summary>Prefix for log lines.</summary>
        protected virtual string LogTag => "[Edison]";

        /// <summary>The deck's own Main Phase plays (summons, combos). Return null to fall through to the shared rules.</summary>
        protected virtual Task<IdleCmdResponse?> DecideMainPhaseAsync(DecisionContext<IdleCmdData> context, Board board) =>
            Task.FromResult<IdleCmdResponse?>(null);

        /// <summary>Deck-specific chain decision. Null = not handled, use the shared rules.</summary>
        protected virtual Task<bool?> ShouldChainDeckCardAsync(DynamicCard card) => Task.FromResult<bool?>(null);

        /// <summary>Deck-specific card selections (searches, revives...). Null = not handled.</summary>
        protected virtual Task<IList<byte>?> SelectDeckCardAsync(SelectCardData state) =>
            Task.FromResult<IList<byte>?>(null);

        /// <summary>Monster effects on field / GY the brain may activate in Main Phase, in priority order.</summary>
        protected virtual int[] MonsterEffectCards => Array.Empty<int>();

        /// <summary>Extra conditions for a monster effect in <see cref="MonsterEffectCards"/>.</summary>
        protected virtual bool CanUseMonsterEffect(int cardId, Board board, List<DynamicCard> monsterTargets) =>
            monsterTargets.Count + board.EnemySpells.Count > 0;

        /// <summary>Deck cards whose activation targets a card on the field (blocked by Tytannial).</summary>
        protected virtual int[] DeckTargetingCards => Array.Empty<int>();

        /// <summary>Monsters worth protecting from battle traps: Extra Deck monsters and big beaters (2300+ base ATK).</summary>
        protected virtual bool IsImportantMonster(DynamicCard card) =>
            card.IsExtraCard() || (card.BaseAttack > 0 ? card.BaseAttack : card.Attack) >= 2300;

        /// <summary>Our monsters with an effect when they destroy a monster by battle (attack first, never into tokens).</summary>
        protected virtual int[] OnBattleDestroyAttackers => Array.Empty<int>();

        /// <summary>Monster effects that target our own cards (never hit Machina Fortress).</summary>
        protected virtual int[] EffectsTargetingOwnCards => Array.Empty<int>();

        /// <summary>Synchro summon priority (also used when an effect picks a synchro from the Extra Deck).</summary>
        protected virtual int[] SynchroPriority => Array.Empty<int>();

        /// <summary>GY banish order for costs: first = banish first; unknown cards go before all of these.</summary>
        protected virtual int[] BanishLastOrder => Array.Empty<int>();

        /// <summary>Summonable cards the generic fallback must not normal summon (handled by the deck).</summary>
        protected virtual int[] ExcludedFromGenericSummon => Array.Empty<int>();

        /// <summary>ATK we can add in battle from hand (e.g. Kalut), for "can we beat it" checks.</summary>
        protected virtual int BattleBoostInHand(List<DynamicCard> hand) => 0;

        /// <summary>Deck veto on synchro summons right now (e.g. a monster already locked as the only attacker).</summary>
        protected virtual bool CanSynchroNow(Board board) => true;

        /// <summary>Synchro levels available in our Extra Deck (for "can a stolen monster become material").</summary>
        protected virtual int[] SynchroLevels => new[] { 5, 6, 7, 8 };

        /// <summary>Deck-specific uses for a monster we'd steal this turn (e.g. tribute it, or synchro with a tuner from hand).</summary>
        protected virtual bool CanUseStolenMonster(DynamicCard target, IdleCmdData state, Board board) => false;

        /// <summary>How much it hurts to lose this card from hand.</summary>
        protected virtual int HandValue(DynamicCard card)
        {
            if (card.IsCode(HappyToDiscard))
                return 0;
            if (card.IsCode(UpstartGoblin))
                return 1;
            if (card.IsCode(KeyCards))
                return 4;
            return 2;
        }

        /// <summary>Lower = better to give up as tribute or synchro material.</summary>
        protected virtual int MaterialCost(DynamicCard card)
        {
            if (IsStolen(card))
                return -1000;
            if ((card.Type & TokenType) != 0)
                return -500;
            return card.Attack;
        }

        // ---- Shared state helpers ---------------------------------------------------------------------

        /// <summary>The known ID of a card (alternate artworks are normalized into Alias).</summary>
        protected static int Key(DynamicCard card) => card.Alias != 0 ? card.Alias : card.ID;

        /// <summary>
        /// Resolves alternate artworks for every card in view (both fields, hands, GYs, banished) plus the offered
        /// cards, and marks the offered ones in place. Called at the start of each decision.
        /// </summary>
        protected async Task NormalizeAsync(DynamicCard[]? offered = null)
        {
            var ids = new List<int>();
            foreach (var side in new[] { CurrentField.OwnSide, CurrentField.EnemySide })
            {
                ids.AddRange(side.Hand ?? Array.Empty<int>());
                ids.AddRange(side.Grave ?? Array.Empty<int>());
                ids.AddRange(side.Banish ?? Array.Empty<int>());
                foreach (var zone in (side.MonsterZones ?? Array.Empty<CardArray>()).Concat(side.SpellZones ?? Array.Empty<CardArray>()))
                    ids.AddRange(zone.Cards ?? Array.Empty<int>());
            }
            if (offered != null)
                ids.AddRange(offered.Select(card => card.ID));

            await CardIdentity.EnsureAsync(ids);
            CardIdentity.Normalize(offered);
        }

        // ---- Attack index limitation ----------------------------------------------------------------------
        // The game ignores the attack index we send and always attacks with the first monster of AttackableCards
        // (18 of 18 requests with index >= 1 attacked with index 0; every index-0 request went through). Until Omega
        // fixes it, each battle decision is made for that first monster only.
        private static BattleCmdResponse AttackWith(DynamicCard attacker) => attacker.DeclareAttack();

        protected override async Task<List<DynamicCard>> GetEnemyMonsterCardsAsync(bool onlyFaceUp = false) =>
            CardIdentity.Normalize(await base.GetEnemyMonsterCardsAsync(onlyFaceUp));

        protected override async Task<List<DynamicCard>> GetEnemySpellCardsAsync(bool onlyFaceUp = false) =>
            CardIdentity.Normalize(await base.GetEnemySpellCardsAsync(onlyFaceUp));

        protected override async Task<List<DynamicCard>> GetBotMonsterCardsAsync(bool onlyFaceUp = false) =>
            CardIdentity.Normalize(await base.GetBotMonsterCardsAsync(onlyFaceUp));

        protected override async Task<List<DynamicCard>> GetBotHandCardsAsync() =>
            CardIdentity.Normalize(await base.GetBotHandCardsAsync());

        protected override async Task<List<DynamicCard>> GetBotGraveyardCardsAsync() =>
            CardIdentity.Normalize(await base.GetBotGraveyardCardsAsync());

        // Our own Spell & Trap zone. (DeckBotBrain's GetPlayerCardsInZoneAsync reads the *human player's* side.)
        protected async Task<List<DynamicCard>> GetOwnSpellCardsAsync() =>
            CardIdentity.Normalize(await CurrentField.GetCardsInLocation(CardLocation.SpellZone));

        // The header reports CurrentPlayer relative to the bot: 0 = bot's turn, 1 = opponent's.
        protected bool IsBotTurn => CurrentHeader.CurrentPlayer == 0;

        protected void Log(string message) => ConsoleUtils.PrintColored($"{LogTag} {message}");

        // For checks that run on every decision: only log when the outcome changes.
        protected void LogOnChange(string message)
        {
            if (message != _lastRepeatedLog)
                Log(message);
            _lastRepeatedLog = message;
        }

        // ---- Idle (Main Phase) ------------------------------------------------------------------------

        private async Task<IdleCmdResponse> DecideIdleCmdCoreAsync(DecisionContext<IdleCmdData> context, CancellationToken ct)
        {
            var response = await DecideIdleAsync(context, ct);
            Chain.SnapshotEnemyBanish(CurrentField.EnemySide.Banish);
            SnapshotEnemyMonsters(await GetEnemyMonsterCardsAsync());
            await SnapshotOwnMonstersAsync();
            // Anything but a phase change can start a chain of ours (summons, activations).
            Chain.SetOwnActionPending(response.Action is not (MainAction.ToBattlePhase or MainAction.ToEndPhase), CurrentHeader);
            return response;
        }

        private async Task<IdleCmdResponse> DecideIdleAsync(DecisionContext<IdleCmdData> context, CancellationToken ct)
        {
            await PrepareContextAsync(context, ct);
            await NormalizeAsync();
            var idle = context.StateData;
            foreach (var list in new[] { idle.SummonableCards, idle.SpecialSummonableCards, idle.ReposableCards,
                                         idle.MonsterSetableCards, idle.SpellSetableCards, idle.ActivableCards })
                CardIdentity.Normalize(list);
            ResetCountersIfNewTurn();
            var state = context.StateData;

            var board = await BuildBoardAsync();

            var response = await DecideGenericOpeningAsync(state, board);
            if (response.HasValue)
                return response.Value;

            // Already lethal on board into a set backrow: don't commit more (a Mirror Force would take everything).
            // Only if they can still have one: Mirror Force and Torrential are limited, so once used they're gone.
            bool holdBack = HasLethalOnBoard(board) && board.EnemySpells.Any(card => !card.IsFaceup()) &&
                OpponentMayStillHave(board, PunishesOverextension);
            if (holdBack)
                LogOnChange("Lethal already on board: holding the rest back in case of a backrow surprise");

            if (!holdBack)
            {
                response = await DecideMainPhaseAsync(context, board);
                if (response.HasValue)
                    return response.Value;

                response = await DecideMonsterEffectsAsync(state, board);
                if (response.HasValue)
                    return response.Value;
            }

            response = DecideSets(context, board);
            if (response.HasValue)
                return response.Value;

            // Reposition, then battle/end phase. Hide the summons the deck deliberately skipped from the generic logic.
            var filtered = state;
            filtered.SummonableCards = (state.SummonableCards ?? Array.Empty<DynamicCard>())
                .Where(card => !card.IsCode(ExcludedFromGenericSummon))
                .ToArray();
            // Monsters that take no battle damage and can't be destroyed by battle stay in attack position,
            // and so do monsters that destroy what they battle while there are targets for them.
            // Weak face-down monsters (e.g. a set Vayu) aren't flipped just to poke a face-down, unless we can pump them.
            bool nonDarkTargets = board.EnemyMonsters.Any(card => card.IsFaceup() && (card.Attribute & DarkAttribute) == 0);
            bool canPump = BattleBoostInHand(board.Hand) > 0;
            // Only monsters that punish being destroyed in battle (Machina Fortress): a fearless attacker may go to defense.
            bool onlyPunishers = board.EnemyMonsters.Count > 0 && board.EnemyMonsters.All(card => card.IsFaceup() && card.IsCode(PunishesBattleDestruction));
            int strongestEnemyAttack = board.EnemyMonsters.Where(card => card.IsFaceup()).Select(card => card.Attack).DefaultIfEmpty(0).Max();
            filtered.ReposableCards = (state.ReposableCards ?? Array.Empty<DynamicCard>())
                .Where(card => !(card.IsCode(FearlessAttackers) && card.IsAttack() && !onlyPunishers))
                .Where(card => !(card.IsCode(DestroysNonDarkInBattle) && card.IsAttack() && nonDarkTargets))
                .Where(card => !(!card.IsFaceup() && card.Attack < 1500 && !canPump))
                // With a pump in hand (Kalut) that makes it win against their strongest monster, a monster stays in
                // attack: if they attack it, the pump wins the battle (and Shura's effect triggers on their turn too).
                .Where(card => !(card.IsAttack() && canPump && card.Attack + BattleBoostInHand(board.Hand) > strongestEnemyAttack))
                .ToArray();
            return await base.OnIdleCmdAsync(context with { StateData = filtered }, ct);
        }

        /// <summary>
        /// Can the opponent still hold any of these cards? Uses the format's banlist minus the copies already
        /// seen in their GY, banished, or face-up on their field.
        /// </summary>
        protected bool OpponentMayStillHave(Board board, params int[] cardIds)
        {
            var enemy = CurrentField.EnemySide;
            var faceUp = board.EnemyMonsters.Concat(board.EnemySpells).Where(card => card.IsFaceup()).Select(card => card.ID).ToList();
            return cardIds.Any(id => CardCounter.OpponentCopiesLeft(id, enemy.Grave, enemy.Banish, faceUp) > 0);
        }

        /// <summary>
        /// Their face-up attack-position monsters can deal our LP this turn. Each of our monsters (plus any extra
        /// blockers we're about to add) stops one attack, so the direct damage is the sum of the attacks left over
        /// after their biggest ones are spent on our blockers.
        /// </summary>
        protected bool OpponentHasLethal(Board board, int extraBlockers = 0)
        {
            var attacks = board.EnemyMonsters.Where(card => card.IsFaceup() && card.IsAttack())
                .Select(card => card.Attack).OrderByDescending(attack => attack).ToList();
            int blockers = board.OwnMonsters.Count + extraBlockers;
            return attacks.Skip(blockers).Sum() >= CurrentField.OwnSide.LifePoints;
        }

        /// <summary>
        /// Don't add one more body when it only feeds Mirror Force / Torrential: we already have a monster out,
        /// they have 2+ set cards, those cards are still possible, and the extra monster neither makes lethal nor
        /// is worth risking (tuners and important monsters still come down).
        /// </summary>
        protected bool ShouldAvoidOverextending(Board board, DynamicCard card)
        {
            if (!IsBotTurn || board.OwnMonsters.Count == 0 || board.EnemySpells.Count(spell => !spell.IsFaceup()) < 2)
                return false;
            if (card.HasType((CardType)TunerType) || IsImportantMonster(card) || !OpponentMayStillHave(board, PunishesOverextension))
                return false;

            int attack = board.OwnMonsters.Where(monster => monster.IsFaceup() && monster.IsAttack()).Sum(monster => monster.Attack) + card.Attack;
            if (board.EnemyMonsters.Count == 0 && attack >= CurrentField.EnemySide.LifePoints)
                return false;

            LogOnChange($"Holding {card.ID}: 2+ set cards and Mirror Force / Torrential still possible");
            return true;
        }

        // No monsters in their way and our attack-position monsters already deal at least their LP.
        private bool HasLethalOnBoard(Board board) =>
            IsBotTurn && board.IsMain1 && board.EnemyMonsters.Count == 0 &&
            board.OwnMonsters.Where(card => card.IsFaceup() && card.IsAttack()).Sum(card => card.Attack) >= CurrentField.EnemySide.LifePoints;

        // Draw spells, steal their biggest threat, removal.
        private Task<IdleCmdResponse?> DecideGenericOpeningAsync(IdleCmdData state, Board board)
        {
            // Trap Dustshoot on our turn: Main Phase 1, before we commit or attack (the engine only offers it with 4+ cards in their hand).
            if (IsBotTurn && board.IsMain1 && TryActivate(state, TrapDustshoot, board.StardustBlocking, out var response))
                return Result(response);

            if (TryActivate(state, UpstartGoblin, board.StardustBlocking, out response))
                return Result(response);

            if (board.Hand.Any(card => card.ID != AllureOfDarkness && card.HasType(CardType.Monster) && (card.Attribute & DarkAttribute) != 0) &&
                TryActivate(state, AllureOfDarkness, board.StardustBlocking, out response))
                return Result(response);

            // Brain Control the most threatening monster (Stardust first), but only with a plan for it this turn.
            var priorityEnemy = GetPriorityEnemyMonster(board.EnemyMonsters.Where(card => card.IsFaceup()));
            if (priorityEnemy.ID > 0 &&
                ThreatScore(priorityEnemy) >= 1500 &&
                CurrentField.OwnSide.LifePoints > 1600 &&
                TryGetActivable(state, BrainControl, out _) &&
                HasPlanForStolenMonster(priorityEnemy, state, board) &&
                TryActivate(state, BrainControl, board.StardustBlocking, out response))
                return Result(response);

            // Book of Moon on a face-up Stardust in our Main Phase 1 when it's what stops our destruction this turn
            // (Heavy Storm in hand, Icarus set for the battle step).
            var stardustUp = board.EnemyMonsters.FirstOrDefault(card => card.IsCode(StardustDragon) && card.IsFaceup() && !card.IsDisabled());
            bool destructionReady = board.Hand.Any(card => card.IsCode(HeavyStorm)) ||
                board.OwnSpells.Any(card => !card.IsFaceup() && card.IsCode(IcarusAttack));
            if (IsBotTurn && board.IsMain1 && stardustUp.ID != 0 && destructionReady &&
                TryActivate(state, BookOfMoon, false, out response))
            {
                Log("Book of Moon on Stardust: unlocks our destruction this turn");
                _preferredTarget = stardustUp;
                return Result(response);
            }

            // Removal spells (skipped automatically while Stardust can negate them).
            // Heavy Storm hits our backrow too. Use it when it takes more of theirs than ours, or an even trade when
            // we're ahead in total cards (hand + field) or it clears the way for lethal.
            int ownTotal = board.Hand.Count + board.OwnMonsters.Count + board.OwnSpells.Count;
            int enemyTotal = (CurrentField.EnemySide.Hand ?? Array.Empty<int>()).Length + board.EnemyMonsters.Count + board.EnemySpells.Count;
            bool stormEdge = board.EnemySpells.Count > board.OwnSpells.Count ||
                (board.EnemySpells.Count >= 1 && board.EnemySpells.Count == board.OwnSpells.Count && (ownTotal - 1 > enemyTotal || HasLethalOnBoard(board)));
            if (stormEdge &&
                TryActivate(state, HeavyStorm, board.StardustBlocking, out response))
                return Result(response);

            // MST from hand:
            //  - always on a face-up continuous/equip/field card;
            //  - on a set card when we're about to commit something it could interrupt (a summon this turn);
            //  - otherwise only now and then, so it isn't predictable; the rest of the time it gets set for later.
            bool faceUpBackrow = board.EnemySpells.Any(card => card.IsFaceup() && IsLastingCard(card));
            bool setBackrow = board.EnemySpells.Any(card => !card.IsFaceup());
            bool commitsThisTurn = IsBotTurn && board.IsMain1 && (state.SummonableCards ?? Array.Empty<DynamicCard>()).Length > 0;
            bool mixItUp = setBackrow && IsBotTurn && Rng.NextDouble() < 0.3;
            if ((faceUpBackrow || (setBackrow && commitsThisTurn) || mixItUp) &&
                TryActivate(state, MysticalSpaceTyphoon, board.StardustBlocking, out response))
                return Result(response);

            // Book of Moon on our turn is played at attack declaration, not here (see OnBattleCmdAsync):
            // the opponent gets fewer windows to react.

            return Result(null);
        }

        /// <summary>
        /// Brain Control is only worth it with a plan for the stolen monster this turn:
        ///  - attack with it into another of their monsters it beats or trades with (not a token), or
        ///  - it makes a direct attack lethal, or
        ///  - synchro with a tuner we already control, or
        ///  - a deck-specific use (tribute, tuner from hand...).
        /// Stealing just to hold it for a turn (or in Main Phase 2) wastes the card.
        /// </summary>
        private bool HasPlanForStolenMonster(DynamicCard target, IdleCmdData state, Board board)
        {
            bool canBattle = IsBotTurn && board.IsMain1 && state.CanBattlePhase;
            var others = board.EnemyMonsters.Where(card => !SameCard(card, target)).ToList();

            if (canBattle && others.Any(card => card.IsFaceup() && (card.Type & TokenType) == 0 && card.GetRealPower() <= target.Attack))
            {
                Log($"Brain Control plan: attack with {target.ID} into one of their monsters");
                return true;
            }

            if (canBattle && others.Count == 0)
            {
                int damage = target.Attack + board.OwnMonsters.Where(card => card.IsAttack() && card.IsFaceup()).Sum(card => card.Attack);
                if (damage >= CurrentField.EnemySide.LifePoints)
                {
                    Log($"Brain Control plan: lethal with {target.ID}");
                    return true;
                }
            }

            bool tunerOnField = board.OwnMonsters.Any(card =>
                card.IsFaceup() && card.HasType((CardType)TunerType) && SynchroLevels.Contains(card.Level + target.Level) &&
                (!card.IsCode(CannotBeSynchroMaterial) || card.IsDisabled()));
            if (tunerOnField)
            {
                Log($"Brain Control plan: synchro with {target.ID}");
                return true;
            }

            if (CanUseStolenMonster(target, state, board))
            {
                Log($"Brain Control plan: deck use for {target.ID}");
                return true;
            }

            return false;
        }

        private static bool SameCard(DynamicCard a, DynamicCard b) =>
            a.ID == b.ID && a.CurrentCoordinates.Sequence == b.CurrentCoordinates.Sequence;

        /// <summary>
        /// Synchro summons by <see cref="SynchroPriority"/>: on our turn, or any time we hold a stolen monster
        /// (it becomes material). Black Rose only when clearly behind and not into an active Stardust.
        /// </summary>
        protected bool TrySynchroSummon(IdleCmdData state, Board board, out IdleCmdResponse response)
        {
            response = default;
            if (!IsBotTurn && !board.ControlsStolenMonster)
                return false;

            if (!CanSynchroNow(board))
                return false;

            if (board.OwnMonsters.Any(card => card.IsFaceup() && card.HasType((CardType)TunerType)))
                LogOnChange($"Synchro check: tuner on field, engine offers [{string.Join(",", (state.SpecialSummonableCards ?? Array.Empty<DynamicCard>()).Select(card => card.ID))}]");

            // With Royal Oppression face-up (theirs or ours), they can pay 800 to negate the synchro:
            // the materials are already gone, so we'd lose two monsters. (Vayu's GY effect can't be negated.)
            // Sometimes summon anyway as bait: every negation costs them 800, which matters more the lower they are.
            bool oppressionUp = board.OwnSpells.Concat(board.EnemySpells).Any(card => card.IsCode(RoyalOppression) && card.IsFaceup());
            int enemyLifePoints = CurrentField.EnemySide.LifePoints;
            if (oppressionUp && enemyLifePoints > 800)
            {
                bool baitWorthIt = enemyLifePoints <= 3000 || Rng.NextDouble() < 0.25;
                if (!baitWorthIt)
                {
                    LogOnChange("No synchro summon: Royal Oppression is face-up and they can pay to negate it");
                    return false;
                }

                Log($"Synchro into Royal Oppression as bait (they have {enemyLifePoints} LP)");
            }

            var options = (state.SpecialSummonableCards ?? Array.Empty<DynamicCard>())
                .Where(card => card.IsCode(SynchroPriority) || card.IsCode(BlackRoseDragon))
                .Where(card => ScoreSynchro(card.ID, board) > 0)
                .ToList();
            if (options.Count == 0)
                return false;

            var best = options.OrderByDescending(card => ScoreSynchro(card.ID, board)).First();
            Log($"Synchro summon: {best.ID} (score {ScoreSynchro(best.ID, board)} among {string.Join(",", options.Select(o => o.ID))})");
            response = best.SpecialSummon();
            return true;
        }

        /// <summary>
        /// Which synchro fits the board right now. The deck's <see cref="SynchroPriority"/> order is only a tiebreaker;
        /// the situation decides (e.g. Goyo when it can beat and steal their monster, Armed Wing into defense positions).
        /// </summary>
        protected int ScoreSynchro(int cardId, Board board)
        {
            int index = Array.IndexOf(SynchroPriority, cardId);
            int score = index < 0 ? 0 : 30 - index;

            var enemies = board.EnemyMonsters;
            var faceUp = enemies.Where(card => card.IsFaceup()).ToList();
            bool canAttackThisTurn = IsBotTurn && board.IsMain1;

            switch (cardId)
            {
                case GoyoGuardian:
                    // 2800: destroys it in battle and takes it. Worth it when there's something to steal.
                    var stealable = enemies.Where(card => card.GetRealPower() < 2800 && (card.Type & TokenType) == 0).ToList();
                    if (canAttackThisTurn && stealable.Count > 0)
                        score += 60 + stealable.Max(ThreatScore) / 100;
                    break;

                case BlackwingArmedWing:
                    // 2300, +500 and piercing against defense position.
                    if (canAttackThisTurn && enemies.Any(card => card.IsDefense() && card.GetRealPower() < 2800))
                        score += 50;
                    break;

                case BlackwingArmorMaster:
                    // A wall (and wedge counters) against what we can't beat.
                    if (faceUp.Any(card => card.GetRealPower() >= 2800))
                        score += 60;
                    break;

                case SilverwindTheAscendant:
                    // Destroys up to 2 face-up monsters with DEF lower than its ATK (no battle phase after).
                    if (faceUp.Count(card => card.Defense < 2800) >= 2)
                        score += 70;
                    break;

                case DarkEndDragon:
                    if (faceUp.Any(card => ThreatScore(card) >= 2000))
                        score += 45;
                    break;

                case RedDragonArchfiend:
                    if (canAttackThisTurn && enemies.Count(card => card.IsDefense()) >= 2)
                        score += 40;
                    break;

                case AllyOfJusticeCatastor:
                    // Destroys a non-DARK monster it battles.
                    if (canAttackThisTurn && faceUp.Any(card => (card.Attribute & DarkAttribute) == 0 && card.GetRealPower() >= 2200))
                        score += 40;
                    break;

                case BlackRoseDragon:
                    // Wipes the whole field (ours too): only when clearly behind, and not into a Stardust that negates it.
                    bool behind = board.EnemyMonsters.Count + board.EnemySpells.Count >= board.OwnMonsters.Count + board.OwnSpells.Count + 2;
                    score = behind && !board.StardustBlocking ? 120 : -100;
                    break;

                case MagicalAndroid:
                    // Gains 600 LP every End Phase: a real option when our life is low.
                    if (CurrentField.OwnSide.LifePoints <= 4000)
                        score += 30;
                    break;

                case BrionacDragonOfTheIceBarrier:
                    // Bounce what we can't otherwise answer, paying with a card we don't need.
                    bool hardThreat = faceUp.Any(card => card.IsCode(PunishesMonsterTargeting) || ThreatScore(card) >= 2500) ||
                        board.EnemySpells.Any(card => card.IsFaceup());
                    if (hardThreat && board.Hand.Any(card => HandValue(card) <= 2))
                        score += 45;
                    break;

                case StardustDragon:
                    // Protection against a backrow wall.
                    if (board.EnemySpells.Count(card => !card.IsFaceup()) >= 2)
                        score += 30;
                    break;
            }

            return score;
        }

        protected async Task<Board> BuildBoardAsync()
        {
            var enemyMonsters = await GetEnemyMonsterCardsAsync();
            _targetingBlocked = IsTargetingBlocked(enemyMonsters);
            _tytannialActive = IsTytannialActive(enemyMonsters);
            var ownSpells = await GetOwnSpellCardsAsync();
            var hand = await GetBotHandCardsAsync();
            var ownMonsters = await GetBotMonsterCardsAsync();
            return new Board
            {
                EnemyMonsters = enemyMonsters,
                EnemySpells = await GetEnemySpellCardsAsync(),
                OwnMonsters = ownMonsters,
                OwnSpells = ownSpells,
                Hand = hand,
                Graveyard = await GetBotGraveyardCardsAsync(),
                IsMain1 = (DuelPhase)CurrentHeader.CurrentPhase == DuelPhase.Main1,
                StardustBlocking = IsStardustBlocking(enemyMonsters, ownSpells, hand),
                ControlsStolenMonster = ownMonsters.Any(IsStolen),
                TargetingBlocked = _targetingBlocked,
                VsGladiatorBeasts = OpponentPlaysArchetype(GladiatorBeastPrefix),
            };
        }

        private async Task<IdleCmdResponse?> DecideMonsterEffectsAsync(IdleCmdData state, Board board)
        {
            // Monster effects never count Machina Fortress as a target unless its discard doesn't hurt us.
            bool fortressOk = await IsFortressTargetWorthItAsync(board.Hand);
            var monsterTargets = board.EnemyMonsters.Where(card => !IsFortressTrap(card, fortressOk)).ToList();

            foreach (int cardId in MonsterEffectCards)
            {
                if (!CanUseMonsterEffect(cardId, board, monsterTargets))
                    continue;

                // About to hit Machina Fortress with a monster effect: first empty the hand into the backrow
                // (spells included) so its discard costs as little as possible.
                if (TryGetActivable(state, cardId, out _) && WillTargetFortress(cardId, monsterTargets))
                {
                    var toSet = (state.SpellSetableCards ?? Array.Empty<DynamicCard>())
                        .FirstOrDefault(card => card.CurrentCoordinates.Location == (byte)CardLocation.Hand);
                    if (toSet.ID > 0)
                    {
                        Log($"Setting {toSet.ID} before targeting Machina Fortress");
                        return toSet.SetSpellTrap();
                    }
                }

                if (TryActivate(state, cardId, board.StardustBlocking, out var response))
                    return response;
            }

            return null;
        }

        // Which trap to set first when not all of them go down.
        private static readonly int[] TrapSetOrder =
        {
            SolemnJudgment, StarlightRoad, DimensionalPrison, MirrorForce, BottomlessTrapHole, RoyalOppression, TrapDustshoot, IcarusAttack
        };

        // Set traps, plus Book of Moon / MST so they can be used on the opponent's turn.
        private IdleCmdResponse? DecideSets(DecisionContext<IdleCmdData> context, Board board)
        {
            var setables = context.StateData.SpellSetableCards ?? Array.Empty<DynamicCard>();

            // Set in Main Phase 2 when there's a battle coming: cards set before attacking can be hit by
            // battle effects (e.g. Machina Fortress destroying a card when it dies) and give information away.
            if (IsBotTurn && board.IsMain1 && context.StateData.CanBattlePhase && setables.Length > 0)
            {
                LogOnChange("Holding sets for Main Phase 2");
                return null;
            }

            // Keep a Spell & Trap zone free to play spells next turn, unless we're nearly dead and need every trap.
            const int spellTrapZones = 5;
            int occupied = board.OwnSpells.Count(card => card.CurrentCoordinates.Sequence < spellTrapZones);
            if (occupied >= spellTrapZones - 1 && CurrentField.OwnSide.LifePoints > 2000)
                return null;

            // Against a possible Heavy Storm, two set cards are enough, unless something set protects the backrow
            // (Solemn Judgment to negate it, Starlight Road to stop a mass destruction).
            int setCount = board.OwnSpells.Count(card => !card.IsFaceup());
            bool backrowProtected = board.OwnSpells.Any(card => !card.IsFaceup() && card.IsCode(SolemnJudgment, StarlightRoad));
            if (setCount >= 2 && !backrowProtected && OpponentMayStillHave(board, HeavyStorm) && CurrentField.OwnSide.LifePoints > 2000)
            {
                LogOnChange("Two cards set already: holding the rest in case of Heavy Storm");
                return null;
            }

            // Best traps first: the ones that protect the backrow, then the strongest answers.
            var trap = setables
                .Where(card => card.HasType(CardType.Trap))
                .OrderBy(card => Array.FindIndex(TrapSetOrder, id => card.IsCode(id)) is int i && i >= 0 ? i : TrapSetOrder.Length)
                .FirstOrDefault();
            if (trap.ID > 0)
            {
                Log($"Setting trap: {trap.ID}");
                return trap.SetSpellTrap();
            }

            var quickPlay = setables.FirstOrDefault(card => card.IsCode(BookOfMoon, MysticalSpaceTyphoon));
            if (quickPlay.ID > 0 && (!board.IsMain1 || !context.StateData.CanBattlePhase))
            {
                Log($"Setting quick-play: {quickPlay.ID}");
                return quickPlay.SetSpellTrap();
            }

            return null;
        }

        protected bool TryActivate(IdleCmdData state, int cardId, bool stardustBlocking, out IdleCmdResponse response)
        {
            response = default;
            if (stardustBlocking && DestructionCards.Contains(cardId))
                return false;

            if (!TryGetActivable(state, cardId, out var card))
                return false;

            if (IsTargetingCard(cardId) && (_targetingBlocked || (CostlyTargeting.Contains(cardId) && _tytannialActive)))
            {
                Log($"Not activating {cardId}: Tytannial would negate it");
                return false;
            }

            _activationCount.TryGetValue(cardId, out int count);
            if (count >= MaxActivationsPerCardPerTurn)
                return false;

            _activationCount[cardId] = count + 1;
            Chain.MarkOwnActivation(card.HasType(CardType.Monster), CurrentHeader);
            MarkCardActivated(cardId);
            Log($"Activating {cardId}");
            response = card.MainActivateFromIndex(0);
            return true;
        }

        private void ResetCountersIfNewTurn()
        {
            if (CurrentHeader.CurrentTurn == _activationTurn)
                return;

            _activationTurn = CurrentHeader.CurrentTurn;
            _activationCount.Clear();
        }

        private static Task<IdleCmdResponse?> Result(IdleCmdResponse? response) => Task.FromResult(response);

        // ---- Battle ------------------------------------------------------------------------------------

        private async Task<BattleCmdResponse> DecideBattleCmdCoreAsync(DecisionContext<BattleCmdData> context, CancellationToken ct)
        {
            await PrepareContextAsync(context, ct);
            await NormalizeAsync(context.StateData.AttackableCards);
            _forcedAttackTarget = null;

            var attackers = context.StateData.AttackableCards ?? Array.Empty<DynamicCard>();
            var allAttackers = attackers;
            if (attackers.Length > 1)
            {
                // Only the first listed monster can actually attack (see AttackWith).
                LogOnChange($"Only the first attacker can be chosen ({attackers[0].ID}); the rest wait their turn in the list");
                attackers = new[] { attackers[0] };
            }

            // Icarus in our own battle step: when their monsters (a Gorz and its token...) stop our next attacker,
            // clear them now so the attacks go on, instead of waiting for their turn. Only through the first
            // activable card (the API may use the first one whatever index we send, like attacks).
            var activables = context.StateData.ActivableCards ?? Array.Empty<DynamicCard>();
            if (activables.Length > 0 && activables[0].IsCode(IcarusAttack) && attackers.Length > 0 && !attackers[0].Attacked && !_targetingBlocked)
            {
                var blockers = await GetEnemyMonsterCardsAsync();
                var ownMonsters = await GetBotMonsterCardsAsync();
                var spellsNow = await GetEnemySpellCardsAsync();
                bool firstBlocked = blockers.Count > 0 && !blockers.Any(target => GetAttackScore(attackers[0], target) > 0);
                if (firstBlocked && ownMonsters.Count >= 2 &&
                    !IsStardustBlocking(blockers, await GetOwnSpellCardsAsync(), await GetBotHandCardsAsync()) &&
                    await ShouldUseIcarus(blockers, spellsNow))
                {
                    Log("Icarus in our battle step: clearing their blockers so the attacks go on");
                    Chain.MarkOwnActivation(false, CurrentHeader, IcarusAttack);
                    return activables[0].BattleActivateFromIndex();
                }
            }

            // We saw Mirror Force in their hand and they have set cards: one attack per battle, not the whole board.
            bool mirrorKnown = StillInRevealedHand(MirrorForce) &&
                CardCounter.OpponentCopiesLeft(MirrorForce, CurrentField.EnemySide.Grave, CurrentField.EnemySide.Banish, Array.Empty<int>()) > 0;
            bool setBackrow = (await GetEnemySpellCardsAsync()).Any(card => !card.IsFaceup());
            bool alreadyAttacked = (await GetBotMonsterCardsAsync()).Any(card => card.Attacked);
            if (mirrorKnown && setBackrow && alreadyAttacked)
            {
                Log("Mirror Force seen in their hand: one attack only, the rest stay back");
                return context.StateData.CanMainPhaseTwo ? BattleAction.ToMainPhaseTwo : BattleAction.ToEndPhase;
            }

            // A battle trap seen in their hand (Dimensional Prison, Sakuretsu...) with cards set: our important monsters
            // (synchros, big beaters) don't walk into it unless the attack wins the duel. Only the first listed monster
            // can attack (API limitation), so this decides for it.
            // Holding the first attacker also holds everyone behind it (the API only attacks with the first one),
            // so with other attackers waiting it attacks anyway: the trap takes one monster either way.
            var knownBattleTrap = AttackResponseTraps.FirstOrDefault(StillInRevealedHand);
            bool othersWaiting = allAttackers.Skip(1).Any(card => !card.Attacked);
            if (knownBattleTrap != 0 && setBackrow && attackers.Length > 0 && !othersWaiting && IsImportantMonster(attackers[0]))
            {
                bool winsNow = (await GetEnemyMonsterCardsAsync()).Count == 0 && attackers[0].CanDirectAttack &&
                    attackers[0].Attack >= CurrentField.EnemySide.LifePoints;
                if (!winsNow)
                {
                    Log($"{attackers[0].ID} stays back: {knownBattleTrap} was seen in their hand");
                    return context.StateData.CanMainPhaseTwo ? BattleAction.ToMainPhaseTwo : BattleAction.ToEndPhase;
                }
            }

            // They control no cards and may hold Gorz: its token gets the damage of the attack, so poke with the
            // weakest attacker first (a smaller token, and the rest can still react after it).
            var enemySideNow = CurrentField.EnemySide;
            bool enemyEmpty = (await GetEnemyMonsterCardsAsync()).Count == 0 && (await GetEnemySpellCardsAsync()).Count == 0;
            bool gorzPossible = (enemySideNow.Hand ?? Array.Empty<int>()).Length > 0 &&
                (_revealedEnemyHand.Contains(GorzTheEmissaryOfDarkness) ||
                 CardCounter.OpponentCopiesLeft(GorzTheEmissaryOfDarkness, enemySideNow.Grave, enemySideNow.Banish, Array.Empty<int>()) > 0);
            // Only the first listed monster can attack, so we can't pick the weakest: if the first isn't the weakest
            // and nothing set answers Gorz and its token (Icarus, Mirror Force, Prison, Book of Moon), stay back
            // rather than hand them a big token. An attack that wins the duel goes anyway.
            if (enemyEmpty && gorzPossible && attackers.Length > 0 && !attackers[0].Attacked && attackers[0].CanDirectAttack)
            {
                var ready = allAttackers.Where(card => !card.Attacked && card.CanDirectAttack).ToList();
                bool firstIsWeakest = ready.All(card => card.Attack >= attackers[0].Attack);
                bool lethal = ready.Sum(card => card.Attack) >= enemySideNow.LifePoints;
                bool answerSet = (await GetOwnSpellCardsAsync()).Any(card => !card.IsFaceup() &&
                    card.IsCode(IcarusAttack, MirrorForce, DimensionalPrison, BookOfMoon));
                if (!firstIsWeakest && !answerSet && !lethal)
                {
                    Log($"{attackers[0].ID} stays back: Gorz possible, it isn't our weakest attacker and nothing set answers it");
                    return context.StateData.CanMainPhaseTwo ? BattleAction.ToMainPhaseTwo : BattleAction.ToEndPhase;
                }

                Log($"{attackers[0].ID} attacks directly, Gorz possible ({(firstIsWeakest ? "weakest attacker" : answerSet ? "answer set" : "lethal")})");
                return AttackWith(attackers[0]);
            }

            // Monsters with an effect when they destroy a monster by battle (Shura) attack first, into a non-token
            // they beat: tokens don't go to the GY, so they don't trigger anything. Others can clean up tokens.
            var battleTargets = await GetEnemyMonsterCardsAsync();
            var handNow = await GetBotHandCardsAsync();
            int boost = BattleBoostInHand(handNow);
            for (int index = 0; index < attackers.Length; index++)
            {
                var trigger = attackers[index];
                if (!trigger.IsCode(OnBattleDestroyAttackers) || trigger.Attacked)
                    continue;

                // What it destroys on its own first, then what it destroys with a damage-step pump from hand (Kalut).
                var enemySpellsNow = await GetEnemySpellCardsAsync();
                var target = battleTargets
                    .Where(card => (card.Type & TokenType) == 0)
                    .Where(card => !IsProtectedByUnion(card, enemySpellsNow))
                    .Where(card => !card.IsFaceup() || card.GetRealPower() < trigger.Attack + boost)
                    .OrderByDescending(card => card.IsFaceup() && card.GetRealPower() < trigger.Attack)
                    .ThenByDescending(card => card.IsFaceup())
                    .ThenByDescending(ThreatScore)
                    .FirstOrDefault();
                if (target.ID != 0 || target.CurrentCoordinates.Location == (byte)CardLocation.MonsterZone)
                {
                    Log($"{trigger.ID} attacks {target.ID} first (its effect needs a monster sent to the GY)");
                    _forcedAttackTarget = target;
                    return AttackWith(trigger);
                }
            }

            // Book of Moon at attack declaration: attack a blocker we can't beat face-up but whose DEF we beat,
            // and flip it face-down in the declaration window. Monsters with a battle-destroy effect go first.
            if (attackers.Length > 0 && handNow.Any(card => card.IsCode(BookOfMoon)) && !_targetingBlocked)
            {
                var ordered = Enumerable.Range(0, attackers.Length)
                    .Where(i => !attackers[i].Attacked)
                    .OrderByDescending(i => attackers[i].IsCode(OnBattleDestroyAttackers))
                    .ThenByDescending(i => attackers[i].Attack);
                // A battle-destroy monster (Shura) later in the list gets more out of the Moon: if the first attacker
                // has another attack that wins on its own, take that one and keep the Moon for the next declaration.
                bool triggerWaiting = !attackers[0].IsCode(OnBattleDestroyAttackers) &&
                    allAttackers.Skip(1).Any(card => card.IsCode(OnBattleDestroyAttackers) && !card.Attacked);
                if (triggerWaiting)
                {
                    var plainWin = battleTargets
                        .Where(card => GetAttackScore(attackers[0], card) > 0)
                        .OrderByDescending(card => GetAttackScore(attackers[0], card))
                        .FirstOrDefault();
                    if (plainWin.CurrentCoordinates.Location == (byte)CardLocation.MonsterZone)
                    {
                        Log($"{attackers[0].ID} attacks {plainWin.ID}; Book of Moon kept for the battle-destroy monster next in line");
                        _forcedAttackTarget = plainWin;
                        return AttackWith(attackers[0]);
                    }
                }

                foreach (int i in ordered)
                {
                    var attacker = attackers[i];
                    var blocker = battleTargets
                        .Where(card => card.IsFaceup() && (card.Type & TokenType) == 0)
                        .Where(card => card.GetRealPower() >= attacker.Attack && card.Defense < attacker.Attack)
                        .Where(card => ThreatScore(card) >= 1500 || attacker.IsCode(OnBattleDestroyAttackers))
                        .OrderByDescending(ThreatScore)
                        .FirstOrDefault();
                    if (blocker.ID == 0)
                        continue;

                    Log($"{attacker.ID} attacks {blocker.ID}, Book of Moon at declaration");
                    _forcedAttackTarget = blocker;
                    _moonOnAttack = blocker;
                    return AttackWith(attacker);
                }
            }

            // Monsters that steal what they destroy (Goyo) go first, into the best monster worth taking.
            var enemyMonsters = await GetEnemyMonsterCardsAsync();
            for (int index = 0; index < attackers.Length; index++)
            {
                var thief = attackers[index];
                if (!thief.IsCode(StealsOnBattle) || thief.Attacked)
                    continue;

                var target = enemyMonsters
                    .Where(card => card.IsFaceup() && (card.Type & TokenType) == 0 && card.GetRealPower() < thief.Attack)
                    .Where(card => !card.IsCode(PunishesBattleDestruction))
                    .OrderByDescending(ThreatScore)
                    .FirstOrDefault();
                if (target.ID > 0 && ThreatScore(target) >= 1000)
                {
                    Log($"{thief.ID} attacks first to steal {target.ID}");
                    _forcedAttackTarget = target;
                    return AttackWith(thief);
                }
            }

            // Monsters that destroy a non-DARK monster they battle go for the biggest non-DARK threat, whatever its ATK.
            for (int index = 0; index < attackers.Length; index++)
            {
                var destroyer = attackers[index];
                if (!destroyer.IsCode(DestroysNonDarkInBattle) || destroyer.Attacked)
                    continue;

                var target = enemyMonsters
                    .Where(card => card.IsFaceup() && (card.Attribute & DarkAttribute) == 0)
                    .OrderByDescending(ThreatScore)
                    .FirstOrDefault();
                if (target.ID > 0)
                {
                    Log($"{destroyer.ID} attacks non-DARK {target.ID} (destroyed by its effect)");
                    _forcedAttackTarget = target;
                    return AttackWith(destroyer);
                }
            }

            // Monsters that can't be destroyed by battle and take no damage always attack their best monster.
            for (int index = 0; index < attackers.Length; index++)
            {
                var attacker = attackers[index];
                if (!attacker.IsCode(FearlessAttackers) || attacker.Attacked)
                    continue;

                // Not into Machina Fortress: destroying it in battle lets it destroy one of our cards (the attacker).
                var target = GetPriorityEnemyMonster((await GetEnemyMonsterCardsAsync()).Where(card => !card.IsCode(PunishesBattleDestruction)));
                if (target.ID > 0 || target.CurrentCoordinates.Location == (byte)CardLocation.MonsterZone)
                {
                    Log($"{attacker.ID} attacks {target.ID}");
                    _forcedAttackTarget = target;
                    return AttackWith(attacker);
                }
            }

            return await DecideGenericAttackAsync(context, attackers, battleTargets);
        }

        // Generic attack choice. Unlike the framework's base logic, negated monsters still count as monsters
        // (with Plasma on our side every opponent monster is negated, and the base logic then saw an empty field).
        private Task<BattleCmdResponse> DecideGenericAttackAsync(DecisionContext<BattleCmdData> context, DynamicCard[] attackers, List<DynamicCard> enemyMonsters)
        {
            var ready = Enumerable.Range(0, attackers.Length).Where(i => !attackers[i].Attacked).ToList();
            BattleCmdResponse Done() => context.StateData.CanMainPhaseTwo ? BattleAction.ToMainPhaseTwo : BattleAction.ToEndPhase;

            if (enemyMonsters.Count == 0)
            {
                int direct = ready.Where(i => attackers[i].CanDirectAttack).OrderByDescending(i => attackers[i].Attack).DefaultIfEmpty(-1).First();
                if (direct < 0)
                    return Task.FromResult(Done());
                Log($"{attackers[direct].ID} attacks directly");
                return Task.FromResult(AttackWith(attackers[direct]));
            }

            var best = ready
                .SelectMany(i => enemyMonsters.Select(target => new { Index = i, Target = target, Score = GetAttackScore(attackers[i], target) }))
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => attackers[x.Index].Attack)
                .FirstOrDefault();
            if (best != null)
            {
                Log($"{attackers[best.Index].ID} attacks {best.Target.ID}");
                _forcedAttackTarget = best.Target;
                return Task.FromResult(AttackWith(attackers[best.Index]));
            }

            // A face-down defender (unknown DEF): poke it with the strongest attacker.
            var faceDown = enemyMonsters.FirstOrDefault(card => !card.IsFaceup() && card.IsDefense());
            if (faceDown.CurrentCoordinates.Location == (byte)CardLocation.MonsterZone && ready.Count > 0)
            {
                int strongest = ready.OrderByDescending(i => attackers[i].Attack).First();
                Log($"{attackers[strongest].ID} attacks a face-down monster");
                _forcedAttackTarget = faceDown;
                return Task.FromResult(AttackWith(attackers[strongest]));
            }

            return Task.FromResult(Done());
        }

        // ---- Chain -------------------------------------------------------------------------------------

        private async Task<int> DecideSelectChainCoreAsync(DecisionContext<SelectChainData> context, CancellationToken ct)
        {
            await PrepareContextAsync(context, ct);
            await NormalizeAsync(context.StateData.SelectableCards);
            RegisterChainDescriptions(context.StateData.Descriptions ?? Array.Empty<int>());

            if (context.StateData.Forced)
            {
                Chain.MarkOwnAction(CurrentHeader);
                return await base.OnSelectChainAsync(context, ct);
            }

            var enemyMonsters = await GetEnemyMonsterCardsAsync();
            var enemySpells = await GetEnemySpellCardsAsync();
            var cards = context.StateData.SelectableCards ?? Array.Empty<DynamicCard>();

            for (int i = 0; i < cards.Length; i++)
            {
                if (await ShouldChain(cards[i], enemyMonsters, enemySpells))
                {
                    Log($"Chaining {cards[i].ID}");
                    Chain.MarkOwnActivation(cards[i].HasType(CardType.Monster), CurrentHeader, Key(cards[i]));
                    Chain.SnapshotEnemyBanish(CurrentField.EnemySide.Banish);
                    SnapshotEnemyMonsters(enemyMonsters);
                    await SnapshotOwnMonstersAsync();
                    return i;
                }
            }

            Chain.MarkPassed();
            Chain.SnapshotEnemyBanish(CurrentField.EnemySide.Banish);
            SnapshotEnemyMonsters(enemyMonsters);
            await SnapshotOwnMonstersAsync();
            return -1;
        }

        private async Task<bool> ShouldChain(DynamicCard card, List<DynamicCard> enemyMonsters, List<DynamicCard> enemySpells)
        {
            var ownSpells = await GetOwnSpellCardsAsync();
            var hand = await GetBotHandCardsAsync();
            // A lethal attack coming: use anything that might stop it, even destruction into Stardust
            // (if Stardust negates, it tributes itself and the attack is gone too).
            var incoming = CurrentField.EnemySide;
            bool lethalIncoming = !IsBotTurn && incoming.HasBattlingMonster &&
                incoming.BattlingMonster.Attack >= CurrentField.OwnSide.LifePoints && !CurrentField.OwnSide.HasBattlingMonster;
            if (lethalIncoming)
                Log("Lethal attack incoming: any answer goes");

            if (!lethalIncoming && IsStardustBlocking(enemyMonsters, ownSpells, hand) && card.IsCode(DestructionCards))
                return false;

            if (IsTargetingCard(card.ID) &&
                (IsTargetingBlocked(enemyMonsters) || (card.IsCode(CostlyTargeting) && IsTytannialActive(enemyMonsters))))
                return false;

            int lifePoints = CurrentField.OwnSide.LifePoints;
            bool stardustInEnemyGrave = (CurrentField.EnemySide.Grave ?? Array.Empty<int>()).Any(id => CardIdentity.Same(id, StardustDragon));

            // D.D. Crow is saved for Stardust in the GY.
            if (card.IsCode(DDCrow))
                return stardustInEnemyGrave;

            if (card.IsCode(RoyalOppression))
                return ShouldUseRoyalOppression(card, lifePoints);

            // Goyo's steal: not on a monster that punishes being destroyed in battle (Machina Fortress).
            if (card.IsCode(StealsOnBattle))
            {
                int lastInTheirGrave = (CurrentField.EnemySide.Grave ?? Array.Empty<int>()).LastOrDefault();
                if (lastInTheirGrave != 0 && PunishesBattleDestruction.Any(id => CardIdentity.Same(lastInTheirGrave, id)))
                {
                    Log($"Not stealing {lastInTheirGrave} with {card.ID}: it punishes being destroyed in battle");
                    return false;
                }
            }

            // A stolen monster's self-tributing effect (Stardust) only sends it back to its owner's GY: let it go instead.
            if (card.IsCode(DontUseWhenStolen) && card.CurrentCoordinates.Controller == ChainTracker.Bot && IsStolen(card))
            {
                Log($"Not using stolen {card.ID}'s effect: it would only go back to its owner");
                return false;
            }

            var deckDecision = await ShouldChainDeckCardAsync(card);
            if (deckDecision.HasValue)
                return deckDecision.Value;

            // Monster effects (floaters, triggers, Stardust...) are always worth using.
            if (card.HasType(CardType.Monster))
                return true;

            // Starlight Road only answers mass destruction of our cards: safe on any turn.
            if (card.IsCode(StarlightRoad))
                return true;

            // Book of Moon on our turn: only in the declaration window of an attack we set up for it.
            if (card.IsCode(BookOfMoon) && IsBotTurn)
            {
                var defender = CurrentField.EnemySide;
                if (_moonOnAttack.HasValue && defender.HasBattlingMonster &&
                    defender.BattlingMonster.ID == _moonOnAttack.Value.ID && defender.BattlingMonster.IsFaceup())
                {
                    _preferredTarget = defender.BattlingMonster;
                    _moonOnAttack = null;
                    Log($"Book of Moon on the attacked {defender.BattlingMonster.ID}");
                    return true;
                }
                return false;
            }

            // Trap Dustshoot on their turn: as soon as it's legal (it's first offered in their Draw/Standby Phase,
            // when their hand is biggest). On our own turn it's activated from Main Phase 1 instead (idle).
            if (card.IsCode(TrapDustshoot))
                return !IsBotTurn && !Chain.AnsweredCurrentChain(CurrentHeader);

            // Book of Moon on a face-up Stardust in their Draw/Standby Phase when our set destruction cards (Mirror Force,
            // Bottomless, Solemn...) would otherwise be negated: face-down, it can't stop them this turn.
            if (card.IsCode(BookOfMoon) && !IsBotTurn && (DuelPhase)CurrentHeader.CurrentPhase is DuelPhase.Draw or DuelPhase.Standby &&
                !Chain.AnsweredCurrentChain(CurrentHeader))
            {
                var stardust = enemyMonsters.FirstOrDefault(monster => monster.IsCode(StardustDragon) && monster.IsFaceup() && !monster.IsDisabled());
                bool lockedAnswers = ownSpells.Any(spell => !spell.IsFaceup() && spell.IsCode(DestructionCards));
                if (stardust.ID != 0 && lockedAnswers)
                {
                    Log("Book of Moon on Stardust: our set destruction cards work this turn");
                    _preferredTarget = stardust;
                    return true;
                }
            }

            // Icarus in response to their removal (Bottomless on our summon, Mirror Force, Torrential, Prison...):
            // tribute the monster they're taking anyway and destroy two of their cards. On either turn.
            if (card.IsCode(IcarusAttack) && CurrentHeader.LastChainPlayer == ChainTracker.Opponent &&
                !Chain.AlreadyChained(Key(card), CurrentHeader))
            {
                var doomed = await DoomedByTheirRemovalAsync(enemySpells);
                var others = enemyMonsters.Where(monster => !IsProtectedByUnion(monster, enemySpells))
                    .Concat(enemySpells.Where(spell => !spell.IsFaceup() || IsLastingCard(spell)))
                    .Select(RemovalValue).OrderByDescending(value => value).Take(2).ToList();
                if (doomed.Count > 0 && (others.Count >= 2 || others.Sum() >= 1500))
                {
                    Log($"Icarus in response to their removal: tributing {doomed[0].ID}, which goes anyway");
                    _icarusDoomed = doomed;
                    return true;
                }
            }

            // Other spells/traps only on the opponent's turn so we don't hit our own plays.
            if (IsBotTurn)
                return false;

            var enemySide = CurrentField.EnemySide;
            DynamicCard? attacker = enemySide.HasBattlingMonster ? enemySide.BattlingMonster : null;

            // We already answered this chain (e.g. Royal Oppression on their summon): don't pile on more cards
            // unless something of ours is about to die in battle.
            bool ourMonsterDying = CurrentField.OwnSide.HasBattlingMonster && attacker.HasValue &&
                attacker.Value.Attack >= CurrentField.OwnSide.BattlingMonster.GetRealPower();
            // Never a second copy of the same card on one chain (two Dimensional Prisons, two Bottomless).
            if (Chain.AlreadyChained(Key(card), CurrentHeader))
            {
                Log($"Not chaining {card.ID}: a copy is already on this chain");
                return false;
            }

            // Already answered this chain: no more spells/traps. Exceptions: Icarus can still tribute a monster that's
            // dying in battle anyway, and a lethal attack lets a second, different answer through in case the first fails.
            bool icarusOnDyingMonster = card.IsCode(IcarusAttack) && ourMonsterDying;
            if (Chain.AnsweredCurrentChain(CurrentHeader) && !icarusOnDyingMonster && !lethalIncoming && !card.HasType(CardType.Monster))
            {
                Log($"Not chaining {card.ID}: already answered this chain");
                return false;
            }

            // Against Gladiator Beasts, Bottomless waits for a key piece (Bestiari, Gyzarus, Heraklinos):
            // the rest tag out anyway.
            if (card.IsCode(BottomlessTrapHole) && OpponentPlaysArchetype(GladiatorBeastPrefix) &&
                !enemyMonsters.Any(monster => monster.IsFaceup() && monster.IsCode(GladiatorKeyMonsters)))
            {
                Log("Holding Bottomless for Bestiari / Gyzarus");
                return false;
            }

            return card.ID switch
            {
                DimensionalPrison => ShouldUsePrison(attacker, enemyMonsters, lifePoints),
                MirrorForce => attacker.HasValue && (enemyMonsters.Count(m => m.IsAttack()) >= 2 || attacker.Value.Attack >= 1800 || attacker.Value.Attack >= lifePoints),
                SolemnJudgment => SolemnOnSummonThreat(enemyMonsters) ||
                    (lifePoints >= 4000 && IsWorthSolemn(enemyMonsters) && !HasCheaperMonsterAnswer(ownSpells, header: CurrentHeader)),
                IcarusAttack => await ShouldUseIcarus(enemyMonsters, enemySpells),
                MysticalSpaceTyphoon => ShouldChainMst(enemySpells),
                BookOfMoon => UseBookOfMoonOnAttacker(attacker, enemyMonsters),
                _ => true
            };
        }

        // Royal Oppression: negate everything the opponent does, never our own plays, never what it can't negate (Vayu).
        private bool ShouldUseRoyalOppression(DynamicCard card, int lifePoints)
        {
            var header = CurrentHeader;

            // Keep it face-down until the opponent actually goes for a special summon: once face-up,
            // they can pay 800 to negate *our* summons too.
            if (!card.IsFaceup())
                return !IsBotTurn && ChainTracker.Source(header) == ChainTracker.Opponent;

            // We must be able to pay and stay alive.
            if (lifePoints <= 800)
                return false;

            if (Chain.OpponentJustUsedUnnegatable(CurrentField.EnemySide.Banish, out int unnegatable))
            {
                Log($"Opponent used {unnegatable}: Royal Oppression can't negate it, skipping");
                return false;
            }

            bool negate = ChainTracker.Source(header) switch
            {
                ChainTracker.Opponent => true,
                ChainTracker.Bot => false,
                // The API gave no source: fall back to whether our last decision started this.
                _ => !Chain.OwnActionPendingNow(header)
            };
            if (negate)
                Log($"Royal Oppression: negating (LastChainPlayer={header.LastChainPlayer} LastSummonPlayer={header.LastSummonPlayer})");
            return negate;
        }

        // MST on their turn: their End Phase (clears a set card before our turn, too late for them to set another),
        // or a face-up spell/trap they just started using.
        private bool ShouldChainMst(List<DynamicCard> enemySpells)
        {
            bool endPhase = (DuelPhase)CurrentHeader.CurrentPhase == DuelPhase.End;
            if (endPhase && enemySpells.Any(card => !card.IsFaceup()))
                return true;
            return CurrentHeader.LastChainPlayer == ChainTracker.Opponent && enemySpells.Any(card => card.IsFaceup() && IsLastingCard(card));
        }

        // Book of Moon on their turn: wait for an attack declaration. Flipped then, the monster can't attack
        // and can't be flip summoned back until their next turn. Target that attacker.
        private bool UseBookOfMoonOnAttacker(DynamicCard? attacker, List<DynamicCard> enemyMonsters)
        {
            if (!attacker.HasValue || !attacker.Value.IsFaceup())
                return false;

            // Plasma on their field is the better target whatever attacks (unless the attack itself kills us):
            // face-down it stops negating our monsters and loses the monster it took.
            bool lethalHit = !CurrentField.OwnSide.HasBattlingMonster && attacker.Value.Attack >= CurrentField.OwnSide.LifePoints;
            var priority = enemyMonsters.FirstOrDefault(card => card.IsFaceup() && !card.IsDisabled() && card.IsCode(BookOfMoonPriority));
            if (!lethalHit && priority.ID != 0)
            {
                Log($"Book of Moon on {priority.ID} instead of the attacker {attacker.Value.ID}");
                _preferredTarget = priority;
                return true;
            }

            // Only when the attack costs something worth a card: lethal or a big hit going direct, or a valuable
            // face-up monster of ours about to die. Not to save a set monster (Vayu is happy in the GY) or a small hit.
            var attacking = attacker.Value;
            var ownSide = CurrentField.OwnSide;
            bool worthIt;
            if (!ownSide.HasBattlingMonster)
            {
                worthIt = attacking.Attack >= ownSide.LifePoints || attacking.Attack >= 2000;
            }
            else
            {
                var defender = ownSide.BattlingMonster;
                bool dies = defender.IsDefense() ? attacking.Attack > defender.Defense : attacking.Attack >= defender.Attack;
                bool valuable = defender.IsFaceup() && (IsImportantMonster(defender) || defender.Attack >= 1700);
                worthIt = dies && valuable;
            }

            if (!worthIt)
            {
                Log($"Book of Moon kept: the attack of {attacking.ID} isn't worth a card");
                return false;
            }

            _preferredTarget = attacker;
            return true;
        }

        // Prison is the main answer to Stardust: keep it for Stardust unless the hit is lethal.
        private static bool ShouldUsePrison(DynamicCard? attacker, List<DynamicCard> enemyMonsters, int lifePoints)
        {
            if (!attacker.HasValue)
                return false;

            var attackingCard = attacker.Value;
            if (attackingCard.IsCode(StardustDragon))
                return true;

            bool lethal = attackingCard.Attack >= lifePoints;
            if (IsStardustActive(enemyMonsters))
                return lethal;

            return lethal || attackingCard.Attack >= 1900;
        }

        // The monster being summoned has an effect that fires on summon (Black Rose wiping the field, a Monarch,
        // Bestiari): only Solemn stops it, so pay the LP whatever our total, and before any cheaper answer.
        private bool SolemnOnSummonThreat(List<DynamicCard> enemyMonsters)
        {
            var summoning = NewEnemyMonsters(enemyMonsters).FirstOrDefault(card => card.IsCode(SolemnPriorityTargets));
            if (summoning.ID == 0)
                return false;

            Log($"Solemn on the summon of {summoning.ID}: its effect fires on summon");
            return true;
        }

        // Which of our monsters a removal card they're resolving takes: Bottomless the one just summoned, Mirror Force
        // our attack-position monsters, Torrential all of them, Prison / Sakuretsu the one battling.
        private async Task<List<DynamicCard>> DoomedByTheirRemovalAsync(List<DynamicCard> enemySpells)
        {
            var resolving = enemySpells.Where(spell => spell.IsFaceup() && !IsLastingCard(spell)).ToList();
            var ours = await GetBotMonsterCardsAsync();
            var doomed = new List<DynamicCard>();
            if (resolving.Any(spell => spell.IsCode(TorrentialTribute)))
                doomed.AddRange(ours);
            if (resolving.Any(spell => spell.IsCode(MirrorForce)))
                doomed.AddRange(ours.Where(monster => monster.IsAttack()));
            if (resolving.Any(spell => spell.IsCode(BottomlessTrapHole)) && JustSummoned(ours) is DynamicCard summoned && summoned.Attack >= 1500)
                doomed.Add(summoned);
            if (resolving.Any(spell => spell.IsCode(DimensionalPrison, 56120475 /* Sakuretsu Armor */)) && CurrentField.OwnSide.HasBattlingMonster)
                doomed.AddRange(ours.Where(monster => monster.CurrentCoordinates.Sequence == CurrentField.OwnSide.BattlingMonster.CurrentCoordinates.Sequence));
            return doomed.DistinctBy(monster => monster.CurrentCoordinates.Sequence).ToList();
        }

        // Solemn costs half our LP. Against a monster summon (not a chain link), keep it while a cheaper answer to
        // monsters is set: Bottomless (on the summon), Dimensional Prison / Mirror Force (on its attack).
        private static bool HasCheaperMonsterAnswer(List<DynamicCard> ownSpells, BotStateHeader header)
        {
            bool isSummon = header.LastChainPlayer != ChainTracker.Opponent;
            return isSummon && ownSpells.Any(card => !card.IsFaceup() && card.IsCode(BottomlessTrapHole, DimensionalPrison, MirrorForce));
        }

        // Without knowing what's being summoned, only pay half LP when the board is already threatening.
        // On a summon, only the monster being summoned counts (a big monster already on their field doesn't make
        // a Lonefire worth half our LP).
        private bool IsWorthSolemn(List<DynamicCard> enemyMonsters)
        {
            bool isSummon = CurrentHeader.LastChainPlayer != ChainTracker.Opponent;
            var judged = isSummon ? NewEnemyMonsters(enemyMonsters) : enemyMonsters;
            return judged.Any(monster => monster.IsCode(StardustDragon) || monster.Attack >= 2400);
        }

        // Icarus is a 2-for-1, not a reflex. Use it when:
        //  - one of our monsters is about to die in battle anyway (tribute it in response), or
        //  - we're behind (more cards on their side, or a monster we can't beat), or
        //  - they have 2+ backrows to clear.
        // The tribute itself is picked by MaterialCost (least useful first).
        private async Task<bool> ShouldUseIcarus(List<DynamicCard> enemyMonsters, List<DynamicCard> enemySpells)
        {
            if (enemyMonsters.Count + enemySpells.Count < 2)
                return false;

            var ownSide = CurrentField.OwnSide;
            var enemySide = CurrentField.EnemySide;
            if (ownSide.HasBattlingMonster && enemySide.HasBattlingMonster &&
                enemySide.BattlingMonster.Attack >= ownSide.BattlingMonster.GetRealPower())
                return true;

            // Otherwise compare what it takes (the two most valuable targets) with what it costs:
            // Icarus itself plus the monster we tribute (the cheapest one, by MaterialCost; half its value,
            // since it's usually a body that already did its job).
            var targets = enemyMonsters.Where(card => !IsProtectedByUnion(card, enemySpells)).Concat(enemySpells)
                .Where(card => card.CurrentCoordinates.Location != (byte)CardLocation.SpellZone || !card.IsFaceup() || IsLastingCard(card))
                .Select(RemovalValue)
                .OrderByDescending(value => value)
                .Take(2)
                .ToList();
            if (targets.Count < 2)
                return false;

            var ownMonsters = await GetBotMonsterCardsAsync();
            var tribute = ownMonsters.OrderBy(MaterialCost).FirstOrDefault();
            int tributeValue = tribute.ID == 0 || IsStolen(tribute) || (tribute.Type & TokenType) != 0 ? 0 : ThreatScore(tribute) / 2;
            int gain = targets.Sum();
            int cost = 1500 + tributeValue;
            LogOnChange($"Icarus check: gain={gain} cost={cost}");
            return gain >= cost;
        }

        // ---- Card selection ----------------------------------------------------------------------------

        private async Task<IList<byte>?> DecideSelectCardCoreAsync(DecisionContext<SelectCardData> context, CancellationToken ct)
        {
            await PrepareContextAsync(context, ct);
            await NormalizeAsync(context.StateData.SelectableCards);
            var state = context.StateData;
            var selectable = state.SelectableCards ?? Array.Empty<DynamicCard>();

            if (state.SelectionHint == (int)DHint.ATTACKTARGET && _forcedAttackTarget.HasValue)
            {
                var target = _forcedAttackTarget.Value;
                _forcedAttackTarget = null;
                // Match by zone, not ID: their face-down monsters reach the bot without an ID.
                int targetIndex = Array.FindIndex(selectable, card =>
                    card.CurrentCoordinates.Controller == ChainTracker.Opponent &&
                    card.CurrentCoordinates.Sequence == target.CurrentCoordinates.Sequence);
                if (targetIndex >= 0)
                    return new List<byte> { (byte)targetIndex };
            }

            // Costs and materials (Icarus tribute, synchro materials picked one by one): give up the least useful card.
            if (state.SelectionHint is (int)DHint.RELEASE or (int)DHint.TRIBUTE or (int)DHint.SMATERIAL && selectable.Length > 0)
            {
                if (_icarusDoomed.Count > 0)
                {
                    var doomedSeqs = _icarusDoomed.Select(card => card.CurrentCoordinates.Sequence).ToHashSet();
                    _icarusDoomed = new();
                    int doomedIndex = Array.FindIndex(selectable, card => card.CurrentCoordinates.Controller == ChainTracker.Bot &&
                        card.CurrentCoordinates.Location == (byte)CardLocation.MonsterZone && doomedSeqs.Contains(card.CurrentCoordinates.Sequence));
                    if (doomedIndex >= 0)
                    {
                        Log($"{(DHint)state.SelectionHint}: {selectable[doomedIndex].ID} (it goes anyway)");
                        return new List<byte> { (byte)doomedIndex };
                    }
                }

                var cheapest = selectable
                    .Select((card, index) => new { Card = card, Index = index })
                    .OrderBy(x => MaterialCost(x.Card) - (IsBotTurn && x.Card.Attacked ? 3000 : 0))
                    .Take(Math.Max(state.Minimum, 1))
                    .Select(x => (byte)x.Index)
                    .ToList();
                Log($"{(DHint)state.SelectionHint}: {string.Join(",", cheapest.Select(i => selectable[i].ID))}");
                return cheapest;
            }

            var deckSelection = await SelectDeckCardAsync(state);
            if (deckSelection != null)
                return deckSelection;

            // Synchro picked from the Extra Deck by an effect (e.g. Vayu): same situational choice as a synchro summon.
            if (selectable.Length > 0 && selectable.All(card => card.CurrentCoordinates.Location == (byte)CardLocation.Extra))
            {
                var board = await BuildBoardAsync();
                var best = selectable
                    .Select((card, index) => new { Card = card, Index = index })
                    .OrderByDescending(x => ScoreSynchro(x.Card.ID, board))
                    .First();
                Log($"Extra Deck pick: {best.Card.ID} (score {ScoreSynchro(best.Card.ID, board)})");
                return new List<byte> { (byte)best.Index };
            }

            // Banishing from our own hand as a cost (Allure of Darkness): the card we'd miss least, never a game-changer
            // like Dark Armed Dragon unless it's the only option.
            if (state.SelectionHint == (int)DHint.REMOVE && selectable.Length > 0 &&
                selectable.All(card => card.CurrentCoordinates.Location == (byte)CardLocation.Hand && card.CurrentCoordinates.Controller == ChainTracker.Bot))
            {
                var banish = selectable
                    .Select((card, index) => new { Card = card, Index = index })
                    .OrderBy(x => HandValue(x.Card))
                    .ThenBy(x => Array.FindIndex(BanishLastOrder, id => x.Card.IsCode(id)))
                    .Take(Math.Max(state.Minimum, 1))
                    .Select(x => (byte)x.Index)
                    .ToList();
                Log($"Banishing from hand: {string.Join(",", banish.Select(i => selectable[i].ID))}");
                return banish;
            }

            // Banishing from our own GY as a cost (e.g. Dark Armed Dragon): follow the deck's banish order.
            if (state.SelectionHint == (int)DHint.REMOVE && selectable.Length > 0 &&
                selectable.All(card => card.CurrentCoordinates.Location == (byte)CardLocation.Grave && card.Owner == 0))
            {
                var banish = selectable
                    .Select((card, index) => new { Card = card, Index = index })
                    .OrderBy(x => Array.FindIndex(BanishLastOrder, id => x.Card.IsCode(id)))
                    .Take(Math.Max(state.Minimum, 1))
                    .Select(x => (byte)x.Index)
                    .ToList();
                Log($"Banishing from GY: {string.Join(",", banish.Select(i => selectable[i].ID))}");
                return banish;
            }

            // Choosing from the opponent's hand (Trap Dustshoot): hand traps first, then combo starters, then threat.
            if (selectable.Length > 0 && selectable.All(card => card.CurrentCoordinates.Location == (byte)CardLocation.Hand))
            {
                // Ours if every offered card is in our hand (by ID, counting copies).
                var ownHandIds = (await GetBotHandCardsAsync()).Select(card => card.ID).ToList();
                bool fromTheirHand = selectable.Any(card => !ownHandIds.Remove(card.ID));
                if (fromTheirHand)
                {
                    var revealed = await RevealHandCardsAsync(selectable);
                    var board = await BuildBoardAsync();
                    var best = revealed
                        .Select((card, index) => new { Card = card, Index = index })
                        .OrderByDescending(x => HandPickScore(x.Card, board))
                        .Take(Math.Max(state.Minimum, 1))
                        .ToList();
                    Log($"Their hand (monsters): {string.Join(",", revealed.Select(card => card.ID))} -> taking {string.Join(",", best.Select(x => x.Card.ID))}");
                    return best.Select(x => (byte)x.Index).ToList();
                }
            }

            // Discard costs from hand (e.g. Brionac): the cards we miss the least.
            if ((state.SelectionHint == (int)DHint.DISCARD ||
                 (state.SelectionHint == (int)DHint.TOGRAVE && selectable.All(card => card.CurrentCoordinates.Location == (byte)CardLocation.Hand))) &&
                selectable.Length > 0)
            {
                var discard = selectable
                    .Select((card, index) => new { Card = card, Index = index })
                    .OrderBy(x => HandValue(x.Card))
                    .Take(Math.Max(state.Minimum, 1))
                    .Select(x => (byte)x.Index)
                    .ToList();
                Log($"Discarding {string.Join(",", discard.Select(i => selectable[i].ID))}");
                return discard;
            }

            // The game asks for targets with several hints (Gale uses FACEUP, Brain Control CONTROL...).
            // Whenever enemy cards are offered, pick by threat; own-card selections fall through.
            if (state.SelectionHint is (int)DHint.TARGET or (int)DHint.DESTROY or (int)DHint.REMOVE or (int)DHint.FACEUP
                or (int)DHint.FACEDOWN or (int)DHint.CONTROL or (int)DHint.POSCHANGE or (int)DHint.RTOHAND
                or (int)DHint.TOGRAVE or (int)DHint.TODECK)
            {
                var picks = await PickEnemyTargetsAsync(state, selectable);
                if (picks != null)
                    return picks;
            }

            return await base.OnSelectCardAsync(context, ct);
        }

        private async Task<IList<byte>?> PickEnemyTargetsAsync(SelectCardData state, DynamicCard[] selectable)
        {
            // Diagnostic: confirms the controller convention of the selection data (1 = opponent).
            Log($"Target options ({(DHint)state.SelectionHint}): " + string.Join(" ", selectable.Select(card =>
                $"{card.ID}@c{card.CurrentCoordinates.Controller}/l{card.CurrentCoordinates.Location}/s{card.CurrentCoordinates.Sequence}")));

            if (_preferredTarget.HasValue)
            {
                var preferred = _preferredTarget.Value;
                _preferredTarget = null;
                if (selectable.TryGetByIDWithZone(preferred.ID, preferred.CurrentCoordinates.Sequence, out _, out int preferredIndex))
                {
                    Log($"Targeting {preferred.ID} (chosen when chaining)");
                    return new List<byte> { (byte)preferredIndex };
                }
            }

            var enemyMonsterCards = await GetEnemyMonsterCardsAsync();
            var enemySpellCards = await GetEnemySpellCardsAsync();
            var enemyCards = enemyMonsterCards.Concat(enemySpellCards).ToList();

            // With no monster beating ours, multi-target removal (Icarus) goes for a backrow wall first.
            int ownBest = (await GetBotMonsterCardsAsync()).Select(card => card.Attack).DefaultIfEmpty(0).Max();
            bool preferBackrow = state.Minimum >= 2 &&
                enemySpellCards.Count(card => !card.IsFaceup()) >= 2 &&
                !enemyMonsterCards.Any(card => card.IsFaceup() && card.GetRealPower() >= ownBest);
            byte preferredLocation = (byte)(preferBackrow ? CardLocation.SpellZone : CardLocation.MonsterZone);

            var hand = await GetBotHandCardsAsync();
            bool avoidPunishers = Chain.LastActivationWasMonsterEffect && !await IsFortressTargetWorthItAsync(hand);
            // Destruction on a union-protected monster just destroys the union: target the union, not the monster.
            bool destroying = state.SelectionHint == (int)DHint.DESTROY;
            var protectedMonsters = destroying
                ? enemyMonsterCards.Where(card => IsProtectedByUnion(card, enemySpellCards)).ToList()
                : new List<DynamicCard>();

            var picks = selectable
                .Select((card, index) => new { Card = card, Index = index })
                .Where(x => IsEnemyCard(x.Card, enemyCards))
                .Where(x => !protectedMonsters.Any(p => p.CurrentCoordinates.Sequence == x.Card.CurrentCoordinates.Sequence &&
                                                        x.Card.CurrentCoordinates.Location == (byte)CardLocation.MonsterZone))
                // A face-up normal/quick-play spell or normal trap is just resolving: destroying it does nothing.
                .Where(x => x.Card.CurrentCoordinates.Location != (byte)CardLocation.SpellZone || !x.Card.IsFaceup() || IsLastingCard(x.Card))
                .Where(x => !avoidPunishers || !x.Card.IsCode(PunishesMonsterTargeting))
                // Single-target removal on monsters keeps going for the biggest monster threat; multi-target removal
                // (Icarus) takes the most valuable cards, backrow included.
                .OrderByDescending(x => state.Minimum >= 2 ? 0 : (x.Card.CurrentCoordinates.Location == preferredLocation ? 1 : 0))
                .ThenByDescending(x => RemovalValue(x.Card))
                .Take(Math.Max(state.Minimum, 1))
                .Select(x => (byte)x.Index)
                .ToArray();

            if (picks.Length >= state.Minimum && picks.Length > 0)
            {
                Log($"Targeting {picks.Length} enemy card(s)");
                return picks;
            }

            return null;
        }

        private async Task<IList<byte>?> DecideSelectTributeCoreAsync(DecisionContext<SelectTributeData> context, CancellationToken ct)
        {
            await PrepareContextAsync(context, ct);
            await NormalizeAsync(context.StateData.SelectableCards);
            var cards = context.StateData.SelectableCards ?? Array.Empty<DynamicCard>();

            var picks = cards
                .Select((card, index) => new { Card = card, Index = index })
                .OrderBy(x => MaterialCost(x.Card))
                .Take(Math.Max(context.StateData.Minimum, 1))
                .Select(x => (byte)x.Index)
                .ToList();

            Log($"Tributing {string.Join(",", picks.Select(i => cards[i].ID))}");
            return picks;
        }

        private async Task<IList<byte>?> DecideSelectSumCoreAsync(DecisionContext<SelectSumData> context, CancellationToken ct)
        {
            await PrepareContextAsync(context, ct);
            await NormalizeAsync(context.StateData.SelectableCards);
            var state = context.StateData;
            var cards = state.SelectableCards ?? Array.Empty<DynamicCard>();
            int mandatoryLevels = (state.MandatoryCards ?? Array.Empty<DynamicCard>()).Sum(card => card.OpParam1);

            // The framework's default ignores the mandatory tuner and never tries single-card combos,
            // so search every subset and keep the cheapest one that hits the level sum.
            var best = FindCheapestSum(cards, state, state.Sum - mandatoryLevels)
                ?? FindCheapestSum(cards, state, state.Sum);

            if (best != null)
            {
                Log($"Synchro materials: {string.Join(",", best.Select(i => cards[i].ID))}");
                return best;
            }

            return await base.OnSelectSumAsync(context, ct);
        }

        // ---- Safety net ---------------------------------------------------------------------------------
        // A bug in our own rules must not kill the bot mid-duel: log it and fall back to the framework's answer.

        public override async Task<IdleCmdResponse> OnIdleCmdAsync(DecisionContext<IdleCmdData> context, CancellationToken ct = default)
        {
            try { return await DecideIdleCmdCoreAsync(context, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log($"[ERROR] OnIdleCmdAsync failed, using the default answer: {ex}");
                return await base.OnIdleCmdAsync(context, ct);
            }
        }

        public override async Task<BattleCmdResponse> OnBattleCmdAsync(DecisionContext<BattleCmdData> context, CancellationToken ct = default)
        {
            try { return await DecideBattleCmdCoreAsync(context, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log($"[ERROR] OnBattleCmdAsync failed, using the default answer: {ex}");
                return await base.OnBattleCmdAsync(context, ct);
            }
        }

        public override async Task<int> OnSelectChainAsync(DecisionContext<SelectChainData> context, CancellationToken ct = default)
        {
            try { return await DecideSelectChainCoreAsync(context, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log($"[ERROR] OnSelectChainAsync failed, using the default answer: {ex}");
                return await base.OnSelectChainAsync(context, ct);
            }
        }

        public override async Task<IList<byte>?> OnSelectCardAsync(DecisionContext<SelectCardData> context, CancellationToken ct = default)
        {
            try { return await DecideSelectCardCoreAsync(context, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log($"[ERROR] OnSelectCardAsync failed, using the default answer: {ex}");
                return await base.OnSelectCardAsync(context, ct);
            }
        }

        public override async Task<IList<byte>?> OnSelectTributeAsync(DecisionContext<SelectTributeData> context, CancellationToken ct = default)
        {
            try { return await DecideSelectTributeCoreAsync(context, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log($"[ERROR] OnSelectTributeAsync failed, using the default answer: {ex}");
                return await base.OnSelectTributeAsync(context, ct);
            }
        }

        public override async Task<IList<byte>?> OnSelectSumAsync(DecisionContext<SelectSumData> context, CancellationToken ct = default)
        {
            try { return await DecideSelectSumCoreAsync(context, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log($"[ERROR] OnSelectSumAsync failed, using the default answer: {ex}");
                return await base.OnSelectSumAsync(context, ct);
            }
        }

        public override async Task<CardPosition> OnSelectPositionAsync(DecisionContext<SelectPositionData> context, CancellationToken ct = default)
        {
            try { return await DecideSelectPositionCoreAsync(context, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log($"[ERROR] OnSelectPositionAsync failed, using the default answer: {ex}");
                return await base.OnSelectPositionAsync(context, ct);
            }
        }

        // Summon position: monsters that can't be destroyed by battle (Armor Master) stall in defense when the only
        // opposing monsters punish being destroyed in battle. Everything else keeps the default.
        private async Task<CardPosition> DecideSelectPositionCoreAsync(DecisionContext<SelectPositionData> context, CancellationToken ct)
        {
            await PrepareContextAsync(context, ct);
            await NormalizeAsync();
            var positions = context.StateData.Positions ?? Array.Empty<byte>();
            int cardId = CardIdentity.Canonical(context.StateData.CardID);
            var enemies = await GetEnemyMonsterCardsAsync();

            bool fearless = FearlessAttackers.Contains(cardId);
            bool onlyPunishers = enemies.Count > 0 && enemies.All(card => card.IsFaceup() && card.IsCode(PunishesBattleDestruction));
            if (fearless && onlyPunishers && positions.Contains((byte)CardPosition.FaceUpDefence))
            {
                Log($"{cardId} in defense: attacking only helps their Machina Fortress");
                return CardPosition.FaceUpDefence;
            }

            // No battle left for us this turn (turn 1, Main Phase 2): an indestructible monster waits in defense, so they
            // can't crash a Machina Fortress into it to trigger its effect. Nothing is lost: it couldn't attack anyway.
            var phase = (DuelPhase)context.Header.CurrentPhase;
            bool noBattleThisTurn = context.Header.CurrentTurn == 1 || phase == DuelPhase.Main2;
            if (fearless && noBattleThisTurn && positions.Contains((byte)CardPosition.FaceUpDefence))
            {
                Log($"{cardId} in defense: it can't attack this turn");
                return CardPosition.FaceUpDefence;
            }

            return await base.OnSelectPositionAsync(context, ct);
        }

        public override Task<bool> OnSelectEffectYnAsync(DecisionContext<SelectEffectYnData> context, CancellationToken ct = default)
        {
            Log($"Effect yes/no: card {context.StateData.EffectCard.ID} desc {context.StateData.Description} -> yes");
            Chain.MarkOwnAction(context.Header);
            return Task.FromResult(true);
        }

        public override Task<bool> OnSelectYesNoAsync(DecisionContext<SelectYesNoData> context, CancellationToken ct = default)
        {
            Log("Yes/no prompt -> yes");
            Chain.MarkOwnAction(context.Header);
            return Task.FromResult(true);
        }

        private IList<byte>? FindCheapestSum(DynamicCard[] cards, SelectSumData state, int target)
        {
            if (target <= 0 || cards.Length == 0 || cards.Length > 16)
                return null;

            int min = Math.Max(state.Minimum, 1);
            int max = state.Maximum > 0 ? state.Maximum : cards.Length;
            List<int>? best = null;
            int bestCost = int.MaxValue;

            for (int mask = 1; mask < (1 << cards.Length); mask++)
            {
                var indexes = Enumerable.Range(0, cards.Length).Where(i => (mask & (1 << i)) != 0).ToList();
                if (indexes.Count < min || indexes.Count > max)
                    continue;

                int s1 = indexes.Sum(i => cards[i].OpParam1);
                int s2 = indexes.Sum(i => cards[i].OpParam2 != 0 ? cards[i].OpParam2 : cards[i].OpParam1);
                bool matches = s1 == target || s2 == target || (!state.MustBeExactValue && (s1 >= target || s2 >= target));
                if (!matches)
                    continue;

                int cost = indexes.Sum(i => MaterialCost(cards[i]));
                if (cost < bestCost)
                {
                    bestCost = cost;
                    best = indexes;
                }
            }

            return best?.Select(i => (byte)i).ToList();
        }

        // ---- Machina Fortress --------------------------------------------------------------------------

        // Targeting Machina Fortress with a monster effect lets them see our hand and discard our best card.
        // Do it only when the need outweighs that cost:
        //   cost      = value of the best card in hand (they pick what we lose)
        //   necessity = 3 if none of our monsters beats it in battle (counting battle boosts in hand)
        //             + 2 if it's dangerous (we have no monsters, or two hits kill us)
        private static bool IsFortressTrap(DynamicCard card, bool fortressIsNecessary) =>
            card.IsCode(PunishesMonsterTargeting) && card.IsFaceup() && !fortressIsNecessary;

        protected async Task<bool> IsFortressTargetWorthItAsync(List<DynamicCard> hand)
        {
            if (hand.Count == 0)
                return true;

            var fortress = (await GetEnemyMonsterCardsAsync()).FirstOrDefault(card => card.IsCode(PunishesMonsterTargeting) && card.IsFaceup());
            if (fortress.ID == 0)
                return true;

            var ownMonsters = await GetBotMonsterCardsAsync();
            int ownBest = ownMonsters.Select(card => card.Attack).DefaultIfEmpty(0).Max() +
                (ownMonsters.Count > 0 ? BattleBoostInHand(hand) : 0);

            bool unbeatable = ownBest <= fortress.GetRealPower();
            bool dangerous = ownMonsters.Count == 0 || CurrentField.OwnSide.LifePoints <= fortress.Attack * 2;
            int necessity = (unbeatable ? 3 : 0) + (dangerous ? 2 : 0);
            int cost = hand.Max(HandValue);

            LogOnChange($"Machina Fortress: necessity={necessity} cost={cost}");
            return necessity >= cost;
        }

        // Would this monster effect end up targeting Machina Fortress?
        private bool WillTargetFortress(int effectCardId, List<DynamicCard> monsterTargets)
        {
            if (EffectsTargetingOwnCards.Contains(effectCardId))
                return false;

            var best = GetPriorityEnemyMonster(monsterTargets.Where(card => card.IsFaceup()));
            return best.IsCode(PunishesMonsterTargeting);
        }

        // ---- Threat / Stardust -------------------------------------------------------------------------

        protected static bool IsStardustActive(IEnumerable<DynamicCard> enemyMonsters) =>
            enemyMonsters.Any(card => card.IsCode(StardustDragon) && card.IsFaceup() && !card.IsDisabled());

        protected static DynamicCard GetPriorityEnemyMonster(IEnumerable<DynamicCard> enemyMonsters) =>
            enemyMonsters
                .OrderByDescending(ThreatScore)
                .FirstOrDefault();

        // How badly we want this monster gone: stats plus known effect threats. Uses base ATK: a temporary pump
        // (Card Trooper's +500s for one turn) doesn't make a card worth more. "Can we beat it now" uses current stats.
        protected static int ThreatScore(DynamicCard card)
        {
            int attack = card.BaseAttack > 0 ? card.BaseAttack : card.Attack;
            int score = card.IsFaceup() ? Math.Max(attack, card.Defense / 2) : 1000;
            if (ThreatBonus.TryGetValue(Key(card), out int bonus) && !card.IsDisabled())
                score += bonus;
            if (card.IsExtraCard())
                score += 300;
            // Tokens are judged by their stats like anything else (a Gorz token can hit hard); 0/0 bodies are harmless.
            if (card.IsFaceup() && card.Attack == 0 && card.Defense == 0)
                score -= 1000;
            return score;
        }

        // Destruction is pointless into Stardust, unless Royal Oppression (set or active) or D.D. Crow
        // can punish its revival from the GY.
        protected bool IsStardustBlocking(IEnumerable<DynamicCard> enemyMonsters, IEnumerable<DynamicCard> ownSpells, IEnumerable<DynamicCard> hand)
        {
            if (!IsStardustActive(enemyMonsters))
                return false;

            bool hasOppression = ownSpells.Any(card => card.IsCode(RoyalOppression)) && CurrentField.OwnSide.LifePoints > 800;
            bool hasCrow = hand.Any(card => card.IsCode(DDCrow));
            return !(hasOppression || hasCrow);
        }

        // How much removing this card is worth, on one scale for monsters and backrow:
        // a set backrow card is unknown but usually a real answer (1500); tokens / 0-0 bodies are near worthless.
        protected static int RemovalValue(DynamicCard card)
        {
            if (card.CurrentCoordinates.Location == (byte)CardLocation.SpellZone)
                return card.IsFaceup() ? 1200 + (ThreatBonus.TryGetValue(Key(card), out int bonus) ? bonus : 0) : 1500;
            return ThreatScore(card);
        }

        // A monster with a union monster equipped (e.g. Machina Gearframe on Machina Fortress): destroying the monster
        // only destroys the union instead. Destruction (Icarus, battle) should go for the union, not the monster.
        protected static bool IsProtectedByUnion(DynamicCard monster, List<DynamicCard> enemySpells)
        {
            if (monster.EquipCards == null || monster.EquipCards.Length == 0)
                return false;

            return monster.EquipCards.Any(equip => enemySpells.Any(spell =>
                spell.CurrentCoordinates.Sequence == equip.Sequence && spell.IsFaceup() && spell.HasType(CardType.Monster)));
        }

        // A face-up card in the Spell & Trap zone that stays there (continuous, equip, field, or an equipped union monster).
        // Face-up normal / quick-play spells and normal traps are only there while they resolve.
        protected static bool IsLastingCard(DynamicCard card) =>
            card.HasType(CardType.Continuous) || card.HasType(CardType.Equip) || card.HasType(CardType.Field) || card.HasType(CardType.Monster);

        // ---- Matchups -----------------------------------------------------------------------------------

        /// <summary>
        /// Whether the opponent plays an archetype, by card name (format versions included): any card of theirs seen
        /// in the GY, banished, on the field, or revealed from hand.
        /// </summary>
        protected bool OpponentPlaysArchetype(string namePrefix)
        {
            var enemy = CurrentField.EnemySide;
            var seen = (enemy.Grave ?? Array.Empty<int>())
                .Concat(enemy.Banish ?? Array.Empty<int>())
                .Concat((enemy.MonsterZones ?? Array.Empty<CardArray>()).Concat(enemy.SpellZones ?? Array.Empty<CardArray>())
                    .SelectMany(zone => zone.Cards ?? Array.Empty<int>()))
                .Concat(_revealedEnemyHand);
            return seen.Any(id => id > 0 && (CardIdentity.NameOf(id)?.StartsWith(namePrefix, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        // ---- Opponent's hand ----------------------------------------------------------------------------

        /// <summary>Monster IDs the opponent showed from their hand (Trap Dustshoot).</summary>
        protected IReadOnlyList<int> RevealedEnemyHand => _revealedEnemyHand;

        // The selection data shows their hand hidden (ID -3). Trap Dustshoot has just revealed it to us, so read each
        // offered card by position: first from our perspective, then from theirs (only here, where it's public info).
        // The whole revealed hand is remembered for later rules (e.g. a known Gorz).
        private async Task<List<DynamicCard>> RevealHandCardsAsync(DynamicCard[] offered)
        {
            var result = new List<DynamicCard>();
            foreach (var card in offered)
            {
                var known = card;
                if (known.ID <= 0)
                {
                    try { known = await CurrentField.GetBotFieldCard(card.CurrentCoordinates.Controller, (int)CardLocation.Hand, card.CurrentCoordinates.Sequence); }
                    catch (Exception) { }
                }
                if (known.ID <= 0)
                {
                    try { known = await CurrentField.GetPlayerFieldCard(0, (int)CardLocation.Hand, card.CurrentCoordinates.Sequence); }
                    catch (Exception) { }
                }
                result.Add(known.ID > 0 ? known : card);
            }

            var resultArray = result.ToArray();
            await CardIdentity.EnsureAsync(resultArray.Select(card => card.ID));
            CardIdentity.Normalize(resultArray);
            result = resultArray.ToList();

            try
            {
                var revealedIds = (await CurrentField.GetPlayerCardsInLocation(CardLocation.Hand, 0)).Select(card => card.ID).ToList();
                await CardIdentity.EnsureAsync(revealedIds.Concat(result.Select(card => card.ID)));
                _revealedEnemyHand = revealedIds.Select(CardIdentity.Canonical).ToList();
                _revealedHandTurn = CurrentHeader.CurrentTurn;
                _revealedPublicBaseline = _revealedEnemyHand.Distinct().ToDictionary(id => id, EnemyPublicCount);
                Log($"Revealed hand: {string.Join(",", _revealedEnemyHand)}");
            }
            catch (Exception)
            {
                _revealedEnemyHand = result.Select(card => card.ID).Where(id => id > 0).ToList();
            }

            return result;
        }

        // Value of taking this monster out of their hand: known hand traps / combo starters, threat, stats,
        // whether they could normal summon it right away, and whether it's live right now.
        private int HandPickScore(DynamicCard card, Board board)
        {
            int score = HandPriority.TryGetValue(Key(card), out int priority) ? priority : 0;

            // Gorz punishes our attacks: worth even more while we have attackers on the field.
            if (card.IsCode(GorzTheEmissaryOfDarkness) && board.OwnMonsters.Any(own => own.IsFaceup() && own.IsAttack()))
                score += 2000;

            // Destiny HERO - Plasma is summoned by tributing 3 monsters: live (and urgent) only if they have them.
            if (card.IsCode(DestinyHeroPlasma) && board.EnemyMonsters.Count >= 3)
                score += 2500;

            // Debris Dragon revives from their GY: with nothing there it's a dead card.
            if (card.IsCode(DebrisDragon) && (CurrentField.EnemySide.Grave ?? Array.Empty<int>()).Length == 0)
                score -= priority;
            if (ThreatBonus.TryGetValue(Key(card), out int threat))
                score += threat;
            score += Math.Max(card.Attack, 0) / 10;
            if (card.Level is > 0 and <= 4)
                score += 300;
            return score;
        }

        // ---- Tytannial ----------------------------------------------------------------------------------

        // Tytannial negates anything that targets a card on the field by tributing a face-up Plant.
        // If it has another Plant to tribute (Fluff Tokens...), targeting just loses the card.
        // With only Tytannial itself to tribute, the trade is acceptable.
        protected static bool IsTargetingBlocked(IEnumerable<DynamicCard> enemyMonsters)
        {
            var monsters = enemyMonsters.ToList();
            var tytannial = monsters.FirstOrDefault(card => card.IsCode(NegatesFieldTargeting) && card.IsFaceup() && !card.IsDisabled());
            if (tytannial.ID == 0)
                return false;

            return monsters.Any(card => card.IsFaceup() && (card.Race & PlantRace) != 0 && !SameCard(card, tytannial));
        }

        protected static bool IsTytannialActive(IEnumerable<DynamicCard> enemyMonsters) =>
            enemyMonsters.Any(card => card.IsCode(NegatesFieldTargeting) && card.IsFaceup() && !card.IsDisabled());

        protected bool IsTargetingCard(int cardId) => TargetsOnField.Contains(cardId) || DeckTargetingCards.Contains(cardId);

        // Owner is bot-relative: a monster we control that the opponent owns was stolen (Brain Control).
        protected static bool IsStolen(DynamicCard card) => card.Owner == 1;

        // Selection data is bot-relative: controller 1 = opponent. Matching by ID doesn't work: their face-down cards
        // reach the bot with no ID, and our enemy lists are read from the player's side, where they have one.
        protected static bool IsEnemyCard(DynamicCard card, List<DynamicCard> enemyCards) =>
            card.CurrentCoordinates.Controller == ChainTracker.Opponent;
    }
}
