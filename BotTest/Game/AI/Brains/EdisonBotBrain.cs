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

        // Synchros summoned with Vayu's effect have their effects negated while on the field (card -> turn picked).
        private readonly Dictionary<int, int> _vayuSummoned = new();

        /// <summary>Negated by the engine, or summoned with Vayu (its effects stay negated).</summary>
        protected bool IsNegated(DynamicCard card) => card.IsDisabled() || _vayuSummoned.ContainsKey(Key(card));

        // Trap Stun: every trap is negated for the rest of the turn it resolves. Seen face-up on their field, or a new
        // copy in their GY / banished since the turn started.
        private int _trapStunTurn = -1;
        // Copies of Trap Stun in their GY / banished at the last look (-1 = not seen yet this game).
        private int _trapStunSeenCount = -1;

        private void UpdateTrapStun(IEnumerable<DynamicCard> enemySpells)
        {
            int turn = CurrentHeader.CurrentTurn;
            var enemy = CurrentField.EnemySide;
            bool faceUp = enemySpells.Any(card => card.IsFaceup() && card.IsCode(TrapStun));

            // A missing GY reads as "unknown", not as empty: an empty baseline made an old copy look new.
            if (enemy.Grave == null)
            {
                if (faceUp && _trapStunTurn != turn)
                    MarkTrapStun(turn, "face-up on their field");
                return;
            }

            int used = enemy.Grave.Concat(enemy.Banish ?? Array.Empty<int>()).Count(id => CardIdentity.Same(id, TrapStun));
            bool newCopy = _trapStunSeenCount >= 0 && used > _trapStunSeenCount;
            _trapStunSeenCount = used;
            if (_trapStunTurn != turn && (faceUp || newCopy))
                MarkTrapStun(turn, faceUp ? "face-up on their field" : $"new copy in GY/banished ({used})");
        }

        private void MarkTrapStun(int turn, string why)
        {
            _trapStunTurn = turn;
            Log($"Trap Stun resolved ({why}): no more traps this turn");
        }

        protected bool TrapsStunned => _trapStunTurn == CurrentHeader.CurrentTurn;

        // Their Threatening Roar on our turn: no attacks left this turn. Seen as a new copy in their GY.
        private int _enemyRoarTurn = -1;
        private int _enemyRoarSeenCount = -1;

        private void UpdateEnemyRoar()
        {
            var grave = CurrentField.EnemySide.Grave;
            if (grave == null)
                return;
            int used = grave.Count(id => CardIdentity.Same(id, ThreateningRoar));
            bool newCopy = _enemyRoarSeenCount >= 0 && used > _enemyRoarSeenCount;
            _enemyRoarSeenCount = used;
            if (newCopy && IsBotTurn && _enemyRoarTurn != CurrentHeader.CurrentTurn)
            {
                _enemyRoarTurn = CurrentHeader.CurrentTurn;
                Log("Their Threatening Roar resolved: no attacks this turn");
            }
        }

        protected bool AttacksLockedByRoar => _enemyRoarTurn == CurrentHeader.CurrentTurn;

        // Honest seen in their hand and not shown in public since (their copies left by the banlist count).
        protected bool HonestKnownInTheirHand => StillInRevealedHand(Honest) &&
            CardCounter.OpponentCopiesLeft(Honest, CurrentField.EnemySide.Grave, CurrentField.EnemySide.Banish, Array.Empty<int>()) > 0;

        private async Task SnapshotOwnMonstersAsync()
        {
            UpdateTrapStun(await GetEnemySpellCardsAsync());
            UpdateEnemyRoar();
            var keys = (await GetBotMonsterCardsAsync()).Select(ZoneKey).ToHashSet();
            var idsOnField = keys.Select(key => int.Parse(key.Split('@')[0])).ToHashSet();
            foreach (var gone in _vayuSummoned.Where(x => x.Value != CurrentHeader.CurrentTurn && !idsOnField.Contains(x.Key)).Select(x => x.Key).ToList())
                _vayuSummoned.Remove(gone);
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

        // Enemy monsters a multi-target removal must take first (e.g. the tuner pair Icarus breaks up).
        private List<DynamicCard> _priorityTargets = new();

        // Tytannial (with a Plant to tribute) is on their field at the current idle decision.
        private bool _targetingBlocked;
        private bool _tytannialActive;

        protected readonly ChainTracker Chain = new();

        // Turn-keyed memory would match the same turn number in the next game (2026-10-15: a Trap Stun from game 1
        // turn 9 locked our traps in game 2 turn 9; a game-1 reveal held back an attack in game 3).
        protected override void OnNewDuel()
        {
            Log("New game: clearing what was remembered from the previous one");
            _activationCount.Clear();
            _activationTurn = -1;
            _forcedAttackTarget = null;
            _seenEnemyMonsters = new();
            _ownFirstSeen = new();
            _vayuSummoned.Clear();
            _trapStunTurn = -1;
            _trapStunSeenCount = -1;
            _icarusDoomed = new();
            _revealedEnemyHand = new();
            _revealedHandTurn = -1;
            _revealedPublicBaseline = new();
            _moonOnAttack = null;
            _preferredTarget = null;
            _priorityTargets = new();
            _baitRollTurn = -1;
            _lastHandLog = null;
            Chain.Reset();
        }

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

        // Monsters that get rid of a face-down monster they attack (Ehren): they go first, into a face-down one.
        protected virtual int[] FaceDownAttackers => Array.Empty<int>();

        /// <summary>Monster effects that target our own cards (never hit Machina Fortress).</summary>
        protected virtual int[] EffectsTargetingOwnCards => Array.Empty<int>();

        /// <summary>Synchro summon priority (also used when an effect picks a synchro from the Extra Deck).</summary>
        protected virtual int[] SynchroPriority => Array.Empty<int>();

        /// <summary>GY banish order for costs: first = banish first; unknown cards go before all of these.</summary>
        protected virtual int[] BanishLastOrder => Array.Empty<int>();

        /// <summary>Summonable cards the generic fallback must not normal summon (handled by the deck).</summary>
        protected virtual int[] ExcludedFromGenericSummon => Array.Empty<int>();

        /// <summary>Settable monsters the generic fallback must not set (handled by the deck).</summary>
        protected virtual int[] ExcludedFromGenericSet => Array.Empty<int>();

        /// <summary>Activable cards the generic fallback must not activate (the deck decides when they're worth it).</summary>
        protected virtual int[] ExcludedFromGenericActivation => Array.Empty<int>();

        /// <summary>Face-down monsters the generic fallback must not flip (their flip effect is the deck's call).</summary>
        protected virtual int[] ExcludedFromGenericFlip => Array.Empty<int>();

        /// <summary>ATK we can add in battle from hand (e.g. Kalut), for "can we beat it" checks.</summary>
        protected virtual int BattleBoostInHand(List<DynamicCard> hand) => 0;

        /// <summary>Whether the pump from <see cref="BattleBoostInHand"/> can be used on this monster (Kalut: Blackwings only).</summary>
        protected virtual bool CanBeBoosted(DynamicCard card) => false;

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
            // On our Main Phase 1, a monster in defense (Blizzard's revive, a set monster) won't attack this turn:
            // it's the material to give up, so the attack-position one still swings.
            if (IsBotTurn && (DuelPhase)CurrentHeader.CurrentPhase == DuelPhase.Main1 && card.IsDefense())
                return card.Attack - 1500;
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
        private string? _lastHandLog;

        protected void LogOnChange(string message)
        {
            if (message != _lastRepeatedLog)
                Log(message);
            _lastRepeatedLog = message;
        }

        // Diagnostics: "id:ATK" plus position (A/D) and "set" when face-down, e.g. "44508094:2500A 65192027:set".
        protected static string DescribeCards(IEnumerable<DynamicCard> cards) =>
            string.Join(" ", cards.Select(card => !card.IsFaceup()
                ? $"{card.ID}:set"
                : card.HasType(CardType.Monster) ? $"{card.ID}:{card.Attack}{(card.IsAttack() ? "A" : "D")}" : $"{card.ID}:up"));

        protected static string DescribeIds(DynamicCard[]? cards) =>
            string.Join(",", (cards ?? Array.Empty<DynamicCard>()).Select(card => card.ID));

        private string? _lastBoardLog;

        // LP and both fields, once per change: the logs had no LP and "why didn't it do X" needs the board.
        private void LogBoard(Board board)
        {
            string boardLog = $"Board (turn {CurrentHeader.CurrentTurn}, LP {CurrentField.OwnSide.LifePoints} vs {CurrentField.EnemySide.LifePoints}): " +
                $"ours [{DescribeCards(board.OwnMonsters)}] [{DescribeCards(board.OwnSpells)}] | " +
                $"theirs [{DescribeCards(board.EnemyMonsters)}] [{DescribeCards(board.EnemySpells)}]";
            if (boardLog != _lastBoardLog)
                Log(boardLog);
            _lastBoardLog = boardLog;
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

            // The hand never shows up in the logs otherwise, and "why didn't it play anything" needs it.
            string handLog = $"Hand (turn {CurrentHeader.CurrentTurn}): {string.Join(",", board.Hand.Select(card => card.ID))}";
            if (handLog != _lastHandLog)
                Log(handLog);
            _lastHandLog = handLog;
            LogBoard(board);

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

            var judgmentWipe = DecideJudgmentDragon(state, board);
            if (judgmentWipe.HasValue)
                return judgmentWipe.Value;

            response = DecideSets(context, board);
            if (response.HasValue)
                return response.Value;

            // Reposition, then battle/end phase. Hide the summons the deck deliberately skipped from the generic logic.
            var filtered = state;
            filtered.SummonableCards = (state.SummonableCards ?? Array.Empty<DynamicCard>())
                .Where(card => !card.IsCode(ExcludedFromGenericSummon))
                .ToArray();
            filtered.MonsterSetableCards = (state.MonsterSetableCards ?? Array.Empty<DynamicCard>())
                .Where(card => !card.IsCode(ExcludedFromGenericSet))
                .ToArray();
            filtered.ActivableCards = (state.ActivableCards ?? Array.Empty<DynamicCard>())
                .Where(card => !card.IsCode(ExcludedFromGenericActivation))
                .ToArray();
            // Monsters that take no battle damage and can't be destroyed by battle stay in attack position,
            // and so do monsters that destroy what they battle while there are targets for them.
            // Weak face-down monsters (e.g. a set Vayu) aren't flipped just to poke a face-down, unless we can pump them.
            bool nonDarkTargets = board.EnemyMonsters.Any(card => card.IsFaceup() && (card.Attribute & DarkAttribute) == 0);
            bool canPump = BattleBoostInHand(board.Hand) > 0;
            // Only monsters that punish being destroyed in battle (Machina Fortress): a fearless attacker may go to defense.
            bool onlyPunishers = board.EnemyMonsters.Count > 0 && board.EnemyMonsters.All(card => card.IsFaceup() && card.IsCode(PunishesBattleDestruction));
            int strongestEnemyAttack = board.EnemyMonsters.Where(card => card.IsFaceup()).Select(card => card.Attack).DefaultIfEmpty(0).Max();
            // Main Phase 1 with a battle ahead and our attackers' ATK reaching their LP: nobody goes to defense first
            // (2026-10-17: Bora went to defense facing Ancient Fairy Dragon with them at 1100 and a 0-DEF token to pierce).
            bool lethalReach = IsBotTurn && board.IsMain1 && state.CanBattlePhase &&
                board.OwnMonsters.Where(card => card.IsFaceup() && card.IsAttack()).Sum(card => card.Attack) >= CurrentField.EnemySide.LifePoints;
            filtered.ReposableCards = (state.ReposableCards ?? Array.Empty<DynamicCard>())
                .Where(card => !(lethalReach && card.IsFaceup() && card.IsAttack()))
                .Where(card => !(!card.IsFaceup() && card.IsCode(ExcludedFromGenericFlip)))
                .Where(card => !(card.IsCode(FearlessAttackers) && !IsNegated(card) && card.IsAttack() && !onlyPunishers))
                // A face-up defender doesn't switch to attack just to be exposed: if something of theirs beats it and it
                // has no real monster to beat (tokens don't count), it stays in defense.
                .Where(card => !(card.IsFaceup() && card.IsDefense() && card.Attack < strongestEnemyAttack &&
                    !board.EnemyMonsters.Any(enemy => enemy.IsFaceup() && (enemy.Type & TokenType) == 0 && enemy.GetRealPower() < card.Attack)))
                .Where(card => !(card.IsCode(DestroysNonDarkInBattle) && card.IsAttack() && nonDarkTargets))
                .Where(card => !(!card.IsFaceup() && card.Attack < 1500 && !canPump))
                // With a pump in hand (Kalut) that makes it win against their strongest monster, a monster stays in
                // attack: if they attack it, the pump wins the battle (and Shura's effect triggers on their turn too).
                .Where(card => !(card.IsAttack() && canPump && card.Attack + BattleBoostInHand(board.Hand) > strongestEnemyAttack))
                .Where(card => !(card.IsFaceup() && card.IsAttack() && KeepsAttackAsBluff(card, strongestEnemyAttack)))
                .ToArray();
            // No rule played anything: what the engine still offered, so "why did it pass with X in hand" has an answer
            // (2026-10-21: Brain Control, Black Whirlwind and Sirocco stayed in hand with nothing logged).
            LogOnChange($"No rule played anything ({(DuelPhase)CurrentHeader.CurrentPhase}). Engine offered: activate [{DescribeIds(state.ActivableCards)}] " +
                $"summon [{DescribeIds(state.SummonableCards)}] special [{DescribeIds(state.SpecialSummonableCards)}] " +
                $"set monster [{DescribeIds(state.MonsterSetableCards)}] set spell [{DescribeIds(state.SpellSetableCards)}] " +
                $"reposition [{DescribeIds(state.ReposableCards)}]" +
                (holdBack ? " (holding back: lethal on board)" : ""));
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

            // Upstart gives them 1000 LP: in Main Phase 1, with enough damage on board to finish them, it waits for Main Phase 2.
            bool upstartBreaksLethal = IsBotTurn && board.IsMain1 && board.OwnMonsters
                .Where(card => card.IsFaceup() && card.IsAttack()).Sum(card => card.Attack) >= CurrentField.EnemySide.LifePoints;
            if (upstartBreaksLethal)
                LogOnChange("Upstart Goblin waits: our attackers could finish them");
            else if (TryActivate(state, UpstartGoblin, board.StardustBlocking, out response))
                return Result(response);

            if (board.Hand.Any(card => card.ID != AllureOfDarkness && card.HasType(CardType.Monster) && (card.Attribute & DarkAttribute) != 0) &&
                TryActivate(state, AllureOfDarkness, board.StardustBlocking, out response))
                return Result(response);

            // Brain Control the most threatening monster (Stardust first), but only with a plan for it this turn.
            // Tytannial cornered (the only Plant of theirs) is a plan on its own: she tributes herself to negate it,
            // or we take the monster.
            var priorityEnemy = GetPriorityEnemyMonster(board.EnemyMonsters.Where(card => card.IsFaceup() && !ThoughtRulerProtects(card, board.EnemyMonsters)));
            if (priorityEnemy.ID > 0 &&
                ThreatScore(priorityEnemy) >= 1500 &&
                CurrentField.OwnSide.LifePoints > 1600 &&
                TryGetActivable(state, BrainControl, out _) &&
                (HasPlanForStolenMonster(priorityEnemy, state, board) || (board.IsMain1 && TytannialCornered)) &&
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

        private int _baitRollTurn = -1;
        private bool _baitRoll;

        /// <summary>A face-up Royal Oppression on either side: anyone can pay 800 to negate a special summon.</summary>
        protected static bool OppressionUp(Board board) =>
            board.OwnSpells.Concat(board.EnemySpells).Any(card => card.IsCode(RoyalOppression) && card.IsFaceup());

        /// <summary>
        /// Our special summons (from the hand, or by an effect) would just be negated and destroyed: a face-up Royal
        /// Oppression and the opponent can afford 800 (2026-10-17: they paid twice with our own Oppression and destroyed
        /// Dark Armed Dragon and Blizzard). Below 3000 LP every payment hurts them, so it's allowed as bait.
        /// </summary>
        protected bool OppressionStopsOurSpecialSummons(Board board) =>
            OppressionUp(board) && CurrentField.EnemySide.LifePoints > 3000;

        // The materials can attack first and the synchro still comes in Main Phase 2, when the attacks are safe and
        // each one already wins: nothing set on their side (no Mirror Force / Torrential on the materials) and every
        // monster of theirs is beaten by our weakest attacker (2026-10-16: Armor Master in Main Phase 1
        // dealt nothing and left a token; Bora piercing + Gale on the tokens first was 1700 and a clean field).
        // Monsters that recruit, search or draw when they die: their effect fires anyway, so Goyo taking one only gives
        // us a weak body (Javier, 2026-11-01: "los que al morir reclutan o buscan, tampoco revivirlos con Goyo").
        private static readonly HashSet<string> FloatsOnDeathNames = new()
        {
            "Sangan", "Pyramid Turtle", "Apprentice Magician", "XX-Saber Emmersblade", "Goblin Zombie", "Dandylion", "Card Trooper"
        };

        // Javier (2026-11-01): with a face-up Thought Ruler Archfiend of theirs, a Spell/Trap of ours doesn't target their
        // Psychics (Thought Ruler included): they pay 1000 to negate and destroy it. Unless they can't pay (1000 LP or less).
        protected bool ThoughtRulerProtects(DynamicCard target, IEnumerable<DynamicCard> enemyMonsters) =>
            target.IsFaceup() && target.HasRace(CardRace.Psychic) && CurrentField.EnemySide.LifePoints > 1000 &&
            enemyMonsters.Any(card => card.IsFaceup() && card.IsCode(ThoughtRulerArchfiend) && !card.IsDisabled());

        // IDs too: an opponent card's name may not be looked up yet (2026-11-01: Goyo took a Pyramid Turtle anyway).
        private static readonly int[] FloatsOnDeathIds = { 77044671, 63665875, 26202165, 9156135, 15341821, CardTrooper };

        protected static bool FloatsOnDeath(int id) => FloatsOnDeathIds.Any(known => CardIdentity.Same(id, known)) ||
            (CardIdentity.NameOf(id) is { } name && FloatsOnDeathNames.Contains(name));

        // A direct hit from these costs us a card from hand (Spirit Reaper, Reaper on the Nightmare, X-Saber Airbellum
        // discard; X-Saber Urbellum puts one on top of the deck, only while we hold 4 or more).
        protected static bool DiscardsOnDirectHit(DynamicCard card, int handCount)
        {
            if (handCount == 0)
                return false;
            if (card.IsCode(SpiritReaper) || CardIdentity.NameOf(card.ID) is "Reaper on the Nightmare" or "X-Saber Airbellum")
                return true;
            return handCount >= 4 && (CardIdentity.Same(card.ID, XSaberUrbellumId) || CardIdentity.NameOf(card.ID) == "X-Saber Urbellum");
        }
        private const int XSaberUrbellumId = 80108118;

        // Krebons negates an attack on it for 800 LP: no use attacking it to steal it (Javier, 2026-11-01: Goyo made for
        // it and both attacks were negated).
        protected bool NegatesAttacksOnIt(DynamicCard card) => card.IsFaceup() && !card.IsDisabled() &&
            CardIdentity.NameOf(card.ID) == "Krebons" && CurrentField.EnemySide.LifePoints > 800;

        // Not worth stealing with Goyo: it floats when it dies, or it negates the attack.
        protected bool BadStealTarget(DynamicCard card) => FloatsOnDeath(card.ID) || NegatesAttacksOnIt(card);

        protected bool ShouldAttackBeforeSynchro(IdleCmdData state, Board board, List<DynamicCard> synchroOptions)
        {
            if (!IsBotTurn || !board.IsMain1 || !state.CanBattlePhase)
                return false;

            var attackers = board.OwnMonsters.Where(card => card.IsFaceup() && card.IsAttack() && !card.Attacked).ToList();
            if (attackers.Count == 0)
                return false;

            // Empty field: the materials always attack first and the synchro waits for Main Phase 2, even with a set card
            // of theirs and even if it costs damage (Javier, 2026-11-01: Armor Master in Main 1 into a set card was
            // banished by Dimensional Prison; keep the resources).
            if (board.EnemyMonsters.Count == 0)
                return true;

            if (board.EnemySpells.Any(card => !card.IsFaceup()))
                return false;

            // Cyber Valley ends the Battle Phase when attacked: the synchro comes first (2026-10-19: Gale attacked it
            // and Dark End came out in Main Phase 2 with no damage dealt).
            if (board.EnemyMonsters.Any(card => card.IsFaceup() && CardIdentity.NameOf(card.ID) == "Cyber Valley"))
                return false;

            var weakest = attackers.OrderBy(card => card.Attack).First();
            return board.EnemyMonsters.All(enemy => enemy.IsFaceup() && GetAttackScore(weakest, enemy) > 0);
        }

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
            int enemyLifePoints = CurrentField.EnemySide.LifePoints;
            if (OppressionUp(board) && enemyLifePoints > 800)
            {
                // Rolled once per turn: re-rolling on every decision made the bait far more likely than 25%.
                if (_baitRollTurn != CurrentHeader.CurrentTurn)
                {
                    _baitRollTurn = CurrentHeader.CurrentTurn;
                    _baitRoll = Rng.NextDouble() < 0.25;
                }
                bool baitWorthIt = enemyLifePoints <= 3000 || _baitRoll;
                if (!baitWorthIt)
                {
                    LogOnChange("No synchro summon: Royal Oppression is face-up and they can pay to negate it");
                    return false;
                }

                LogOnChange($"Synchro into Royal Oppression as bait (they have {enemyLifePoints} LP)");
            }

            var options = (state.SpecialSummonableCards ?? Array.Empty<DynamicCard>())
                .Where(card => card.IsCode(SynchroPriority) || card.IsCode(BlackRoseDragon))
                .Where(card => ScoreSynchro(card.ID, board) > 0)
                .ToList();
            if (options.Count == 0)
                return false;

            if (ShouldAttackBeforeSynchro(state, board, options))
            {
                LogOnChange("Synchro waits for Main Phase 2: the materials attack first");
                return false;
            }

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
            // Javier: Goyo came down after their Roar had closed the battle and stole nothing.
            bool canAttackThisTurn = IsBotTurn && board.IsMain1 && !AttacksLockedByRoar;

            switch (cardId)
            {
                case GoyoGuardian:
                    // 2800: destroys it in battle and takes it. Worth it when there's something to steal.
                    var stealable = enemies.Where(card => card.GetRealPower() < 2800 && (card.Type & TokenType) == 0 && !BadStealTarget(card)).ToList();
                    if (canAttackThisTurn && stealable.Count > 0)
                        score += 60 + stealable.Max(ThreatScore) / 100;
                    // Javier: a 2800 Tytannial can't be stolen, but Goyo trades with her (her effect doesn't stop battle).
                    else if (canAttackThisTurn && IsTytannialActive(faceUp.Where(card => card.IsAttack() && card.Attack <= 2800)))
                        score += 50;
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
                    // Javier: it targets nothing, so it's the answer to a Tytannial with Plants to tribute.
                    if (canAttackThisTurn && IsTargetingBlocked(faceUp) &&
                        faceUp.Any(card => card.IsCode(NegatesFieldTargeting) && (card.Attribute & DarkAttribute) == 0))
                        score += 30;
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
                    // Javier: it's LIGHT, so with Honest in hand it carries Honest into their threats.
                    if (board.Hand.Any(card => card.IsCode(Honest)))
                        score += 20;
                    break;

                case BrionacDragonOfTheIceBarrier:
                    // Bounce what we can't otherwise answer, paying with a card we don't need.
                    // A Spirit Reaper dies to the target and no battle gets past it (Javier: Brionac is the answer).
                    bool hardThreat = faceUp.Any(card => card.IsCode(PunishesMonsterTargeting) || ThreatScore(card) >= 2500 || card.IsCode(SpiritReaper)) ||
                        board.EnemySpells.Any(card => card.IsFaceup());
                    // Only their set backrow stands in the way (their monsters are small): bounce a set card rather than
                    // make a Goyo that walks into it (2026-10-17: Goyo over Brionac into a set Mirror Force).
                    bool onlyBackrow = !hardThreat && board.EnemySpells.Any(card => !card.IsFaceup()) &&
                        faceUp.All(card => card.GetRealPower() < 1900);
                    // Tytannial negates its bounce (Javier: Brionac into her only in an emergency or for lethal).
                    if (IsTytannialActive(faceUp))
                        break;
                    if (hardThreat && board.Hand.Any(card => HandValue(card) <= 2))
                        score += 45;
                    else if (onlyBackrow && canAttackThisTurn && board.Hand.Any(card => HandValue(card) <= 2))
                        score += 60;
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

        // A Judgment Dragon we control without a deck rule for it (stolen with Brain Control, 2026-10-23: the Blackwings bot
        // held one that won the game). Its wipe (1000 LP) when it then hits for the win, or when they have 2+ cards more.
        private IdleCmdResponse? DecideJudgmentDragon(IdleCmdData state, Board board)
        {
            if (!IsBotTurn || MonsterEffectCards.Any(id => CardIdentity.Same(id, JudgmentDragon)) ||
                CurrentField.OwnSide.LifePoints <= 1000 || board.StardustBlocking)
                return null;
            var judgment = board.OwnMonsters.Where(card => card.IsCode(JudgmentDragon) && card.IsFaceup()).ToList();
            if (judgment.Count == 0)
                return null;

            int theirs = board.EnemyMonsters.Count + board.EnemySpells.Count;
            int ours = board.OwnMonsters.Count(card => !card.IsCode(JudgmentDragon)) + board.OwnSpells.Count;
            bool wins = board.IsMain1 && theirs > 0 && judgment.Any(card => card.IsAttack() && !card.Attacked && card.Attack >= CurrentField.EnemySide.LifePoints);
            if (!wins && theirs - ours < 2)
                return null;
            if (!TryActivate(state, JudgmentDragon, false, out var response))
                return null;
            Log($"Judgment Dragon's wipe: {(wins ? "then it hits directly for the win" : "they have 2+ cards more")}");
            return response;
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

        // Traps that go down first and aren't held back by our Heavy Storm in hand (Threatening Roar for Lightsworn).
        protected virtual int[] PriorityTraps => Array.Empty<int>();

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

            int setCount = board.OwnSpells.Count(card => !card.IsFaceup());
            bool desperate = CurrentField.OwnSide.LifePoints <= 2000;

            // Heavy Storm seen in their hand (Dustshoot): one card at most until they use it.
            if (setCount >= 1 && !desperate && StillInRevealedHand(HeavyStorm))
            {
                LogOnChange("Heavy Storm seen in their hand: one set card until they use it");
                return null;
            }

            // Our own Heavy Storm in hand: keep the backrow small so casting it next turn costs us little.
            bool priorityTrap = setables.Any(card => card.HasType(CardType.Trap) && card.IsCode(PriorityTraps) &&
                !board.OwnSpells.Any(set => !set.IsFaceup() && set.IsCode(card.ID)));
            if (setCount >= 1 && !desperate && !priorityTrap && board.Hand.Any(card => card.IsCode(HeavyStorm)))
            {
                LogOnChange("Holding our sets: we have Heavy Storm in hand");
                return null;
            }

            // Against a possible Heavy Storm, two set cards are enough, or as many as they have set (then their Storm
            // costs them as much as us), unless something set protects the backrow (Solemn Judgment to negate it,
            // Starlight Road to stop a mass destruction).
            int setCap = Math.Max(2, board.EnemySpells.Count(card => !card.IsFaceup()));
            bool backrowProtected = board.OwnSpells.Any(card => !card.IsFaceup() && card.IsCode(SolemnJudgment, StarlightRoad));
            if (setCount >= setCap && !backrowProtected && OpponentMayStillHave(board, HeavyStorm) && !desperate)
            {
                LogOnChange($"{setCount} cards set already: holding the rest in case of Heavy Storm");
                return null;
            }

            // Best traps first: the ones that protect the backrow, then the strongest answers.
            var trap = setables
                .Where(card => card.HasType(CardType.Trap))
                .Where(card => !card.IsCode(OneSetCopyAtATime) || !board.OwnSpells.Any(set => !set.IsFaceup() && set.IsCode(card.ID)))
                .OrderByDescending(card => card.IsCode(PriorityTraps))
                .ThenBy(card => Array.FindIndex(TrapSetOrder, id => card.IsCode(id)) is int i && i >= 0 ? i : TrapSetOrder.Length)
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

            if (TrapsStunned && card.HasType(CardType.Trap))
                return false;

            // Tytannial cornered (no other Plant to tribute): burning a card to make her tribute herself is worth it,
            // costly targeting included (Javier, 2026-10-17).
            if (IsTargetingCard(cardId) && _targetingBlocked)
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

            // Javier: Ehren always goes first into a face-down monster, before anything else attacks it.
            for (int index = 0; index < attackers.Length; index++)
            {
                var flipper = attackers[index];
                if (!flipper.IsCode(FaceDownAttackers) || flipper.Attacked)
                    continue;
                var faceDown = (await GetEnemyMonsterCardsAsync()).FirstOrDefault(card => !card.IsFaceup() &&
                    card.CurrentCoordinates.Location == (byte)CardLocation.MonsterZone);
                if (faceDown.CurrentCoordinates.Location == (byte)CardLocation.MonsterZone)
                {
                    Log($"{flipper.ID} attacks their face-down monster first");
                    _forcedAttackTarget = faceDown;
                    return AttackWith(flipper);
                }
            }

            // Monsters that steal what they destroy (Goyo) go first, into the best monster worth taking.
            var enemyMonsters = await GetEnemyMonsterCardsAsync();
            bool honestKnown = HonestKnownInTheirHand;
            for (int index = 0; index < attackers.Length; index++)
            {
                var thief = attackers[index];
                if (!thief.IsCode(StealsOnBattle) || thief.Attacked)
                    continue;

                var target = enemyMonsters
                    .Where(card => card.IsFaceup() && (card.Type & TokenType) == 0 && card.GetRealPower() < thief.Attack)
                    .Where(card => !card.IsCode(PunishesBattleDestruction) && !BadStealTarget(card))
                    // Javier: Goyo attacked a Celestia with Honest already revealed in their hand.
                    .Where(card => !(honestKnown && (card.Attribute & LightAttribute) != 0))
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
                if (!attacker.IsCode(FearlessAttackers) || attacker.Attacked || IsNegated(attacker))
                    continue;

                // Not into Machina Fortress: destroying it in battle lets it destroy one of our cards (the attacker).
                var target = GetPriorityEnemyMonster((await GetEnemyMonsterCardsAsync()).Where(card => !card.IsCode(PunishesBattleDestruction) &&
                    !(card.IsCode(SpiritReaper) && card.IsFaceup() && card.IsDefense())));
                if (target.ID > 0 || target.CurrentCoordinates.Location == (byte)CardLocation.MonsterZone)
                {
                    Log($"{attacker.ID} attacks {target.ID}");
                    _forcedAttackTarget = target;
                    return AttackWith(attacker);
                }
            }

            bool fortressUse = attackers.Any(card => card.IsCode(GoyoGuardian)) && await StolenFortressHasUseAsync();
            // Javier: trading one of ours for their Fortress is fine while we keep more monsters than they do
            // (Gorz 2700 never hit a 2500 Fortress and the game was lost, 2026-10-27).
            var ourMonsters = await GetBotMonsterCardsAsync();
            bool fieldAdvantage = ourMonsters.Count > battleTargets.Count;
            // Javier: Fortress' effect only hits our other cards: with nothing else on our field, trading on a tie is fine.
            bool onlyCard = ourMonsters.Count == 1 && (await GetOwnSpellCardsAsync()).Count == 0;
            return await DecideGenericAttackAsync(context, attackers, allAttackers, battleTargets, boost, fortressUse, fieldAdvantage, onlyCard);
        }

        // Javier: Goyo only takes a Machina Fortress (and only crashes into it) when we'd use it right away: a synchro
        // with a tuner of ours, or a tribute for a monster in hand. Otherwise Goyo just trades for a Fortress they revive.
        private async Task<bool> StolenFortressHasUseAsync()
        {
            const int fortressLevel = 7;
            var own = (await GetBotMonsterCardsAsync()).Where(card => !card.IsCode(GoyoGuardian)).ToList();
            bool synchro = own.Any(card => card.IsFaceup() && card.HasType((CardType)TunerType) && SynchroLevels.Contains(card.Level + fortressLevel) &&
                (!card.IsCode(CannotBeSynchroMaterial) || card.IsDisabled()));
            return synchro || CanTributeStolenMonster(await GetBotHandCardsAsync(), own);
        }

        /// <summary>
        /// Javier (same as Icarus, for every deck): our monster-effect removal skips a Machina Fortress unless it's worth
        /// its discard, and when destroying, a monster with a union equipped (only the union would die: hit the union).
        /// </summary>
        protected async Task<Func<DynamicCard, bool>> AvoidedMonsterEffectTargetsAsync(bool destroying)
        {
            var enemySpells = await GetEnemySpellCardsAsync();
            var enemyMonsters = await GetEnemyMonsterCardsAsync();
            bool avoidFortress = !await IsFortressTargetWorthItAsync(await GetBotHandCardsAsync());
            var protectedZones = destroying
                ? enemyMonsters.Where(card => IsProtectedByUnion(card, enemySpells)).Select(card => (int)card.CurrentCoordinates.Sequence).ToHashSet()
                : new HashSet<int>();
            return card => card.CurrentCoordinates.Location == (byte)CardLocation.MonsterZone &&
                ((avoidFortress && card.IsCode(PunishesMonsterTargeting)) || protectedZones.Contains(card.CurrentCoordinates.Sequence));
        }

        /// <summary>Deck-specific: traps set one copy at a time (two set copies, the wrong one got chained, Javier).</summary>
        protected virtual int[] OneSetCopyAtATime => Array.Empty<int>();

        /// <summary>Deck-specific: an attacker we'd rather trade on equal ATK with this target than keep.</summary>
        protected virtual bool TradesOnTie(DynamicCard attacker, DynamicCard target) => false;

        /// <summary>Deck-specific: a monster in hand we'd tribute a stolen monster for (the others on our field also count).</summary>
        protected virtual bool CanTributeStolenMonster(List<DynamicCard> hand, List<DynamicCard> ownMonsters) => false;

        // Against Blackwings with cards in their hand: Kalut gives a battling Blackwing +1400, so an attack into a face-up
        // attack-position Blackwing that wins by less loses our monster instead (Javier: clear them 1-for-1 with effects).
        private bool KalutTurnsIt(DynamicCard attacker, DynamicCard target)
        {
            if (!target.IsFaceup() || !target.IsAttack() || IsStolen(attacker) || target.IsDisabled())
                return false;
            if (!(CardIdentity.NameOf(target.ID)?.StartsWith("Blackwing", StringComparison.OrdinalIgnoreCase) ?? false))
                return false;
            var enemy = CurrentField.EnemySide;
            bool kalutPossible = (enemy.Hand ?? Array.Empty<int>()).Length > 0 &&
                CardCounter.OpponentCopiesLeft(KalutMoonShadow, enemy.Grave, enemy.Banish, Array.Empty<int>()) > 0;
            bool turns = kalutPossible && attacker.Attack <= target.Attack + 1400;
            if (turns)
                LogOnChange($"{attacker.ID} doesn't attack {target.ID}: Kalut in their hand would win that battle");
            return turns;
        }

        // Generic attack choice. Unlike the framework's base logic, negated monsters still count as monsters
        // (with Plasma on our side every opponent monster is negated, and the base logic then saw an empty field).
        private Task<BattleCmdResponse> DecideGenericAttackAsync(DecisionContext<BattleCmdData> context, DynamicCard[] attackers,
            DynamicCard[] allAttackers, List<DynamicCard> enemyMonsters, int boost, bool fortressUse, bool fieldAdvantage, bool onlyCard)
        {
            var ready = Enumerable.Range(0, attackers.Length).Where(i => !attackers[i].Attacked).ToList();
            BattleCmdResponse Done()
            {
                // A monster that could attack and didn't (2026-10-17: Armor Master stayed put with nothing logged).
                if (ready.Count > 0)
                    LogOnChange($"No attack with [{DescribeCards(ready.Select(i => attackers[i]))}] into [{DescribeCards(enemyMonsters)}]: no attack rule matched");
                return context.StateData.CanMainPhaseTwo ? BattleAction.ToMainPhaseTwo : BattleAction.ToEndPhase;
            }

            if (enemyMonsters.Count == 0)
            {
                int direct = ready.Where(i => attackers[i].CanDirectAttack).OrderByDescending(i => attackers[i].Attack).DefaultIfEmpty(-1).First();
                if (direct < 0)
                    return Task.FromResult(Done());
                Log($"{attackers[direct].ID} attacks directly");
                return Task.FromResult(AttackWith(attackers[direct]));
            }

            // A stolen monster goes back at the End Phase: trading it is free, so ties count too (2026-10-16: a stolen
            // Stardust hit a token instead of trading with the Machina Fortress that then dealt lethal). Same for a
            // deck's throwaway body (Wulf), tokens included (Javier: Wulf trades with a Gorz token).
            foreach (int i in ready)
            {
                bool stolen = IsStolen(attackers[i]);
                var trade = enemyMonsters
                    .Where(card => card.IsFaceup() && card.IsAttack() && card.Attack == attackers[i].Attack)
                    .Where(card => stolen ? (card.Type & TokenType) == 0 : TradesOnTie(attackers[i], card) ||
                        (onlyCard && card.IsCode(PunishesBattleDestruction)))
                    .OrderByDescending(ThreatScore)
                    .FirstOrDefault();
                if (trade.ID > 0 && enemyMonsters.All(card => GetAttackScore(attackers[i], card) <= 0 || ThreatScore(card) < ThreatScore(trade)))
                {
                    Log($"{(stolen ? "Stolen " : "")}{attackers[i].ID} trades with {trade.ID}");
                    _forcedAttackTarget = trade;
                    return Task.FromResult(AttackWith(attackers[i]));
                }
            }

            var best = ready
                .SelectMany(i => enemyMonsters.Select(target => new { Index = i, Target = target, Score = GetAttackScore(attackers[i], target) }))
                .Where(x => x.Score > 0)
                // Destroying Machina Fortress in battle lets it destroy the attacker: not with a monster we need
                // (2026-10-16: Dark Armed Dragon), unless it's a stolen one. Goyo does crash into it when we'd use the
                // Fortress it takes (Javier): then the trade is ours.
                .Where(x => !x.Target.IsCode(PunishesBattleDestruction) || !IsImportantMonster(attackers[x.Index]) || IsStolen(attackers[x.Index]) ||
                    (fieldAdvantage && !attackers[x.Index].IsCode(GoyoGuardian)) ||
                    (attackers[x.Index].IsCode(GoyoGuardian) && !attackers[x.Index].IsDisabled() && fortressUse))
                .Where(x => !KalutTurnsIt(attackers[x.Index], x.Target))
                .OrderByDescending(x => x.Score)
                // Every attack-position target scores the same: hit the most dangerous one, not the leftmost
                // (2026-10-16: Stratos instead of Plasma).
                .ThenByDescending(x => ThreatScore(x.Target))
                .ThenByDescending(x => attackers[x.Index].Attack)
                .FirstOrDefault();
            if (best != null)
            {
                Log($"{attackers[best.Index].ID} attacks {best.Target.ID}");
                _forcedAttackTarget = best.Target;
                return Task.FromResult(AttackWith(attackers[best.Index]));
            }

            // Nothing it beats alone, but a damage-step pump in hand (Kalut) makes it win: attack, the pump fires then
            // (2026-10-16: Armed Wing 2300 + Kalut never attacked Tytannial 2800).
            if (ready.Count > 0 && boost > 0 && CanBeBoosted(attackers[ready[0]]))
            {
                var attacker = attackers[ready[0]];
                var pumped = enemyMonsters
                    .Where(card => card.IsFaceup() && !card.IsCode(PunishesBattleDestruction))
                    .Where(card => card.GetRealPower() >= attacker.Attack && card.GetRealPower() < attacker.Attack + boost)
                    .OrderByDescending(ThreatScore)
                    .FirstOrDefault();
                if (pumped.ID > 0)
                {
                    Log($"{attacker.ID} attacks {pumped.ID}, counting on the {boost} pump in hand");
                    _forcedAttackTarget = pumped;
                    return Task.FromResult(AttackWith(attacker));
                }
            }

            // The first in the list can't win, but someone behind it can (only the first one can attack, and staying
            // back holds everyone): bounce off a defender whose DEF is barely above its ATK, so the next one gets
            // its turn (2026-10-16: Blizzard blocked Shura, which beat their defender).
            bool someoneBehindWins = allAttackers.Skip(1).Any(card => !card.Attacked && enemyMonsters.Any(target => GetAttackScore(card, target) > 0));
            if (ready.Count > 0 && someoneBehindWins)
            {
                var attacker = attackers[ready[0]];
                int lifePoints = CurrentField.OwnSide.LifePoints;
                var cushion = enemyMonsters
                    .Where(card => card.IsFaceup() && card.IsDefense() && card.Defense >= attacker.Attack)
                    .Where(card => card.Defense - attacker.Attack <= 500 && card.Defense - attacker.Attack < lifePoints - 1000)
                    .OrderBy(card => card.Defense - attacker.Attack)
                    .FirstOrDefault();
                if (cushion.ID > 0)
                {
                    Log($"{attacker.ID} bounces off {cushion.ID} ({cushion.Defense - attacker.Attack} damage) so the next attacker gets its turn");
                    _forcedAttackTarget = cushion;
                    return Task.FromResult(AttackWith(attacker));
                }
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

            // Every chain window with something to answer: what was offered and what's attacking (2026-10-21: four set
            // traps stayed down against two direct attacks and the log had no line at all for those windows).
            if (cards.Length > 0)
            {
                var enemySide = CurrentField.EnemySide;
                string attack = enemySide.HasBattlingMonster
                    ? $", attacking: {enemySide.BattlingMonster.ID}:{enemySide.BattlingMonster.Attack}" +
                      (CurrentField.OwnSide.HasBattlingMonster ? $" into {CurrentField.OwnSide.BattlingMonster.ID}" : " directly")
                    : "";
                Log($"Chain window (turn {CurrentHeader.CurrentTurn}, {(DuelPhase)CurrentHeader.CurrentPhase}, LastChainPlayer={CurrentHeader.LastChainPlayer}, " +
                    $"LP {CurrentField.OwnSide.LifePoints}{attack}): offered [{DescribeIds(cards)}], theirs [{DescribeCards(enemyMonsters)}]");
            }

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

            if (cards.Length > 0)
                Log("Chain window: passing");
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

            // Their Heavy Storm on the chain (it stays face-up while it resolves).
            bool theirStormResolving = CurrentHeader.LastChainPlayer == ChainTracker.Opponent &&
                enemySpells.Any(spell => spell.IsFaceup() && spell.IsCode(HeavyStorm));

            // Solemn negating a spell destroys nothing, so Stardust can't stop it.
            // Mirror Force on Stardust's own attack still works: negating it tributes Stardust, so the attack is gone
            // (2026-10-21: Mirror Force stayed set while Stardust and Tytannial hit for 5300).
            // Only when the hit hurts: a lone Stardust into 8000 LP isn't worth Mirror Force, it comes back in the End
            // Phase (Javier, 2026-11-01). Worth it when what attacks this turn leaves us at 4000 or less.
            int incomingDamage = enemyMonsters.Where(m => m.IsFaceup() && m.IsAttack()).Sum(m => m.Attack);
            bool stardustAttacking = !IsBotTurn && incoming.HasBattlingMonster && incoming.BattlingMonster.IsCode(StardustDragon) &&
                !CurrentField.OwnSide.HasBattlingMonster && CurrentField.OwnSide.LifePoints - incomingDamage <= 4000;
            if (!lethalIncoming && IsStardustBlocking(enemyMonsters, ownSpells, hand) && card.IsCode(DestructionCards) &&
                !(theirStormResolving && card.IsCode(SolemnJudgment)) && !(stardustAttacking && card.IsCode(MirrorForce)))
            {
                LogOnChange($"Not chaining {card.ID}: Stardust would negate it");
                return false;
            }

            UpdateTrapStun(enemySpells);
            if (TrapsStunned && card.HasType(CardType.Trap))
            {
                Log($"Not chaining {card.ID}: Trap Stun negates traps this turn");
                return false;
            }

            if (theirStormResolving)
            {
                // A face-down Oppression can't stop a Storm: chaining it only takes the chain away from Solemn.
                if (card.IsCode(RoyalOppression) && !card.IsFaceup())
                    return false;

                // Solemn saves the backrow when the Storm costs us more than it costs them (2026-10-16: four times
                // the whole backrow, Solemn included, went to a Storm Solemn could have negated).
                if (card.IsCode(SolemnJudgment) && !Chain.AlreadyChained(Key(card), CurrentHeader))
                {
                    int oursLost = ownSpells.Count(spell => spell.CurrentCoordinates.Sequence != card.CurrentCoordinates.Sequence);
                    int theirsLost = enemySpells.Count(spell => !(spell.IsFaceup() && spell.IsCode(HeavyStorm)));
                    bool worthIt = oursLost >= 2 && oursLost > theirsLost && CurrentField.OwnSide.LifePoints >= 2000;
                    if (worthIt)
                        Log($"Solemn on Heavy Storm: saves {oursLost} of ours (they'd lose {theirsLost})");
                    return worthIt;
                }
            }

            if (IsTargetingCard(card.ID) && IsTargetingBlocked(enemyMonsters))
            {
                LogOnChange($"Not chaining {card.ID}: their targeting protection (Tytannial) would negate it");
                return false;
            }

            int lifePoints = CurrentField.OwnSide.LifePoints;
            bool stardustInEnemyGrave = (CurrentField.EnemySide.Grave ?? Array.Empty<int>()).Any(id => CardIdentity.Same(id, StardustDragon));

            // D.D. Crow is saved for Stardust in the GY.
            if (card.IsCode(DDCrow))
                return stardustInEnemyGrave;

            if (card.IsCode(RoyalOppression))
                return ShouldUseRoyalOppression(card, lifePoints, enemyMonsters, enemySpells);

            // Goyo's steal: not on a monster that punishes being destroyed in battle (Machina Fortress), unless we'd
            // tribute it or synchro with it right away (Javier).
            if (card.IsCode(StealsOnBattle))
            {
                int lastInTheirGrave = (CurrentField.EnemySide.Grave ?? Array.Empty<int>()).LastOrDefault();
                if (lastInTheirGrave != 0 && PunishesBattleDestruction.Any(id => CardIdentity.Same(lastInTheirGrave, id)) &&
                    !await StolenFortressHasUseAsync())
                {
                    Log($"Not stealing {lastInTheirGrave} with {card.ID}: it punishes being destroyed in battle");
                    return false;
                }
                if (lastInTheirGrave != 0 && FloatsOnDeath(lastInTheirGrave))
                {
                    Log($"Not stealing {lastInTheirGrave} with {card.ID}: its effect when it dies fires anyway");
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

            // The engine also offers Main Phase monster effects in the window right after our own summon: they pass
            // the same check as in the Main Phase (2026-10-17: Dark Armed Dragon fired into an empty field and
            // destroyed itself).
            int mainPhaseEffect = Array.Find(MonsterEffectCards, id => card.IsCode(id));
            if (mainPhaseEffect != 0 && IsBotTurn)
            {
                var board = await BuildBoardAsync();
                bool fortressOk = await IsFortressTargetWorthItAsync(board.Hand);
                var monsterTargets = board.EnemyMonsters.Where(target => !IsFortressTrap(target, fortressOk)).ToList();
                if (!CanUseMonsterEffect(mainPhaseEffect, board, monsterTargets))
                {
                    Log($"Not chaining {card.ID}: its Main Phase condition fails");
                    return false;
                }
            }

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

                // Their Heavy Storm takes Icarus anyway: half the time, use it on the way out (risky, so not always).
                bool stormResolving = enemySpells.Any(spell => spell.IsFaceup() && spell.IsCode(HeavyStorm));
                if (stormResolving && others.Count >= 2 && Rng.NextDouble() < 0.5)
                {
                    Log("Icarus in response to Heavy Storm: it would be destroyed anyway");
                    return true;
                }
            }

            // Other spells/traps only on the opponent's turn so we don't hit our own plays.
            if (IsBotTurn)
                return false;

            var enemySide = CurrentField.EnemySide;
            // The battle info outlives the attacker: after Prison banishes it, the next chain still reports it.
            DynamicCard? attacker = enemySide.HasBattlingMonster && StillOnField(enemySide.BattlingMonster, enemyMonsters)
                ? enemySide.BattlingMonster : null;

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

            // Their Judgment Dragon wipes the field in their Main Phase: Book of Moon would be destroyed anyway, so flip JD
            // face-down in response; it can't attack this turn (2026-10-24: Book stayed set and JD hit for 3000).
            var enemyJudgment = enemyMonsters.FirstOrDefault(monster => monster.IsFaceup() && monster.IsCode(JudgmentDragon) && !monster.IsDisabled());
            bool judgmentWipe = card.IsCode(BookOfMoon) && !IsBotTurn && enemyJudgment.ID != 0 && CurrentHeader.LastChainPlayer == 1 &&
                (DuelPhase)CurrentHeader.CurrentPhase == DuelPhase.Main1;
            if (judgmentWipe)
            {
                Log($"Book of Moon on their Judgment Dragon: its wipe destroys Book anyway, face-down it can't attack");
                _preferredTarget = enemyJudgment;
                return true;
            }

            // Thought Ruler negates a Spell/Trap aimed at a Psychic attacker (Prison, Book of Moon).
            if (card.IsCode(DimensionalPrison, BookOfMoon) && attacker.HasValue && ThoughtRulerProtects(attacker.Value, enemyMonsters) &&
                !(attacker.Value.Attack >= lifePoints && !CurrentField.OwnSide.HasBattlingMonster))
            {
                Log($"Not chaining {card.ID}: Thought Ruler would pay 1000 to negate it on {attacker.Value.ID}");
                return false;
            }

            bool use = card.ID switch
            {
                DimensionalPrison => ShouldUsePrison(attacker, enemyMonsters, lifePoints) && !StrongerAttackerWaiting(attacker, enemyMonsters, lifePoints),
                // Not on a lone Card Trooper: it draws them a card when destroyed, a +1 for them (Javier).
                // Not when the only attacker is a monster with a union equipped (Gearframe on Machina Fortress): only the
                // union dies and the attack goes on (2026-10-29).
                // Spirit Reaper (and X-Saber Airbellum) hitting us directly makes us discard: Mirror Force kills it when we
                // hold cards (Javier).
                MirrorForce => attacker.HasValue && !LoneCardTrooper(attacker.Value, enemyMonsters, lifePoints) &&
                    !(IsProtectedByUnion(attacker.Value, enemySpells) && enemyMonsters.Count(m => m.IsAttack()) == 1) &&
                    // Drill Warrior halved to 1200 for a direct attack is still a 2400 synchro that keeps coming back (Javier).
                    (enemyMonsters.Count(m => m.IsAttack()) >= 2 || attacker.Value.Attack >= 1800 || attacker.Value.Attack >= lifePoints ||
                     CardIdentity.NameOf(attacker.Value.ID) == "Drill Warrior" ||
                     (DiscardsOnDirectHit(attacker.Value, hand.Count) &&
                      !CurrentField.OwnSide.HasBattlingMonster)),
                SolemnJudgment => SolemnOnSummonThreat(enemyMonsters) ||
                    (lifePoints >= 4000 && IsWorthSolemn(enemyMonsters) && !HasCheaperMonsterAnswer(ownSpells, header: CurrentHeader)),
                IcarusAttack => await ShouldUseIcarus(enemyMonsters, enemySpells),
                MysticalSpaceTyphoon => ShouldChainMst(enemySpells),
                BookOfMoon => UseBookOfMoonOnAttacker(attacker, enemyMonsters),
                _ => true
            };
            if (!use)
                Log($"Not chaining {card.ID}: its own condition fails (attacker {(attacker.HasValue ? $"{attacker.Value.ID}:{attacker.Value.Attack}" : "none")}, LP {lifePoints})");
            return use;
        }

        // Is the opponent special summoning right now, as far as we can tell? The API doesn't say which card started
        // the chain, so this goes by what's on their field:
        //  - a summon window (no chain of theirs) with a new monster that only arrives by special summon
        //    (Extra Deck, or a hand monster that summons itself: Quickdraw, Cyber Dragon, Gorz...);
        //  - a chain of theirs while they control a monster whose effect special summons (Lonefire, Debris...)
        //    or just turned face-up a spell/trap that special summons (Monster Reborn, Call of the Haunted...).
        private string? SpecialSummonUnderway(List<DynamicCard> enemyMonsters, List<DynamicCard> enemySpells)
        {
            var header = CurrentHeader;
            static bool Named(DynamicCard card, string[] names) =>
                CardIdentity.NameOf(card.ID) is string name && names.Any(n => name.StartsWith(n, StringComparison.OrdinalIgnoreCase));

            if (header.LastChainPlayer != ChainTracker.Opponent && header.LastSummonPlayer == ChainTracker.Opponent)
            {
                // A monster of theirs left the field in the same window: that was a tribute, so a monster that can also be
                // normal summoned (Machina Fortress, Cyber Dragon...) came by tribute summon (2026-10-17: flipped on a
                // Fortress tributing two tokens, then they paid 800 to negate our summons).
                var nowKeys = enemyMonsters.Select(ZoneKey).ToHashSet();
                bool tributed = _seenEnemyMonsters.Any(key => !nowKeys.Contains(key));
                var special = NewEnemyMonsters(enemyMonsters).FirstOrDefault(m => m.IsExtraCard() ||
                    (Named(m, SelfSpecialSummoners) && !(tributed && Named(m, AlsoTributeSummoned))));
                return special.ID != 0 ? $"special summon of {special.ID}" : null;
            }

            if (header.LastChainPlayer == ChainTracker.Opponent)
            {
                var monster = enemyMonsters.FirstOrDefault(m => m.IsFaceup() && Named(m, SpecialSummonEffectMonsters));
                if (monster.ID != 0)
                    return $"{monster.ID} can special summon";
                var spell = enemySpells.FirstOrDefault(s => s.IsFaceup() && Named(s, SpecialSummonSpellTraps));
                if (spell.ID != 0)
                    return $"{spell.ID} special summons";

                // Their Standby Phase with Drill Warrior banished: the chain is its return (2026-10-19: Oppression let it
                // through and flipped on the summon window after, when there was nothing left to negate).
                if ((DuelPhase)header.CurrentPhase == DuelPhase.Standby)
                {
                    int returning = (CurrentField.EnemySide.Banish ?? Array.Empty<int>()).FirstOrDefault(id =>
                        CardIdentity.NameOf(id) is string name &&
                        ReturnFromBanishInStandby.Any(n => name.StartsWith(n, StringComparison.OrdinalIgnoreCase)));
                    if (returning != 0)
                        return $"{returning} returns from banishment";
                }
            }
            return null;
        }

        // Royal Oppression: negate everything the opponent does, never our own plays, never what it can't negate (Vayu).
        private bool ShouldUseRoyalOppression(DynamicCard card, int lifePoints, List<DynamicCard> enemyMonsters, List<DynamicCard> enemySpells)
        {
            var header = CurrentHeader;

            // Keep it face-down until the opponent actually goes for a special summon: once face-up,
            // they can pay 800 to negate *our* summons too (2026-10-17: flipped on a Gearframe search, which it
            // can't negate, and later they paid twice to destroy our Dark Armed Dragon and Blizzard with it).
            if (!card.IsFaceup())
            {
                if (IsBotTurn || ChainTracker.Source(header) != ChainTracker.Opponent)
                    return false;
                string? reason = SpecialSummonUnderway(enemyMonsters, enemySpells);
                Log($"Royal Oppression (face-down) window: LastChainPlayer={header.LastChainPlayer} LastSummonPlayer={header.LastSummonPlayer} " +
                    $"new=[{string.Join(",", NewEnemyMonsters(enemyMonsters).Select(m => m.ID))}] -> {reason ?? "nothing it can negate, stays down"}");
                return reason != null;
            }

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

        // A stronger monster of theirs in attack position that hasn't attacked yet: Prison waits for it unless this hit
        // is lethal (2026-10-17: Prison took Ancient Fairy Dragon, 2100, and Machina Fortress hit for 2500 right after).
        private static bool StrongerAttackerWaiting(DynamicCard? attacker, List<DynamicCard> enemyMonsters, int lifePoints)
        {
            if (!attacker.HasValue || attacker.Value.Attack >= lifePoints || attacker.Value.IsCode(StardustDragon))
                return false;
            var current = attacker.Value;
            return enemyMonsters.Any(card => card.IsFaceup() && card.IsAttack() && !card.Attacked &&
                !SameCard(card, current) && card.Attack > current.Attack);
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

        // Whether a monster from the battle info is still on their field (same zone, same card). Prison or Icarus
        // can remove the attacker while the API keeps reporting it as battling (2026-10-15: Icarus tributed Gale
        // "to save it" from a Stardust already banished, and only hit two Fluff Tokens).
        private static bool StillOnField(DynamicCard monster, List<DynamicCard> enemyMonsters) =>
            enemyMonsters.Any(card => card.CurrentCoordinates.Sequence == monster.CurrentCoordinates.Sequence &&
                CardIdentity.Same(card.ID, monster.ID));

        // Icarus is a 2-for-1, not a reflex. Use it when:
        //  - one of our monsters is about to die in battle anyway (tribute it in response), or
        //  - we're behind (more cards on their side, or a monster we can't beat), or
        //  - they have 2+ backrows to clear.
        // The tribute itself is picked by MaterialCost (least useful first).
        private static (DynamicCard Tuner, DynamicCard Other)? FindBlackRosePair(List<DynamicCard> enemyMonsters)
        {
            var faceUp = enemyMonsters.Where(card => card.IsFaceup() && card.Level > 0).ToList();
            foreach (var tuner in faceUp.Where(card => card.HasType((CardType)TunerType)))
            {
                var other = faceUp.FirstOrDefault(card => card.CurrentCoordinates.Sequence != tuner.CurrentCoordinates.Sequence &&
                    !card.HasType((CardType)TunerType) && tuner.Level + card.Level == 7);
                if (other.Level > 0)
                    return (tuner, other);
            }
            return null;
        }

        private static bool LoneCardTrooper(DynamicCard attacker, List<DynamicCard> enemyMonsters, int lifePoints) =>
            attacker.IsCode(CardTrooper) && attacker.Attack < lifePoints &&
            enemyMonsters.Where(monster => monster.IsAttack()).All(monster => monster.IsCode(CardTrooper));

        private async Task<bool> ShouldUseIcarus(List<DynamicCard> enemyMonsters, List<DynamicCard> enemySpells)
        {
            if (enemyMonsters.Count + enemySpells.Count < 2)
                return false;

            var ownSide = CurrentField.OwnSide;
            var enemySide = CurrentField.EnemySide;
            if (ownSide.HasBattlingMonster && enemySide.HasBattlingMonster && StillOnField(enemySide.BattlingMonster, enemyMonsters) &&
                enemySide.BattlingMonster.Attack >= ownSide.BattlingMonster.GetRealPower())
                return true;

            // A tuner and a non-tuner of theirs that add up to level 7 are a Black Rose waiting to wipe the field:
            // break the pair up first when the wipe would cost us 2+ cards (2026-10-16: Debris + Dandylion looked
            // cheap at 1300 of value, then Black Rose took Sirocco, Icarus and Prison).
            if (!IsBotTurn && FindBlackRosePair(enemyMonsters) is { } pair)
            {
                int oursAtRisk = (await GetBotMonsterCardsAsync()).Count + (await GetOwnSpellCardsAsync()).Count - 1;
                bool blackRoseLeft = CardCounter.OpponentCopiesLeft(BlackRoseDragon, enemySide.Grave, enemySide.Banish,
                    enemyMonsters.Where(card => card.IsFaceup()).Select(card => card.ID)) > 0;
                if (blackRoseLeft && oursAtRisk >= 2)
                {
                    Log($"Icarus on {pair.Tuner.ID} + {pair.Other.ID}: level 7 for Black Rose ({oursAtRisk} of ours at risk)");
                    _priorityTargets = new List<DynamicCard> { pair.Tuner, pair.Other };
                    return true;
                }
            }

            // Otherwise compare what it takes (the two most valuable targets) with what it costs:
            // Icarus itself plus the monster we tribute (the cheapest one, by MaterialCost; half its value,
            // since it's usually a body that already did its job).
            // A face-up Lightsworn Druid makes their Lightsworn untargetable: Icarus fell on our own Bora and Sirocco
            // (2026-10-24).
            bool druidShield = enemyMonsters.Any(card => card.IsFaceup() && card.IsCode(LightswornDruid) && !card.IsDisabled());
            var targets = enemyMonsters.Where(card => !IsProtectedByUnion(card, enemySpells))
                .Where(card => !druidShield || !card.IsFaceup() || !card.IsCode(LightswornNames)).Concat(enemySpells)
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
                Log($"Extra Deck pick: {best.Card.ID} (score {ScoreSynchro(best.Card.ID, board)}), effects negated (Vayu)");
                _vayuSummoned[Key(best.Card)] = CurrentHeader.CurrentTurn;
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
                $"{card.ID}@c{card.CurrentCoordinates.Controller}/l{card.CurrentCoordinates.Location}/s{card.CurrentCoordinates.Sequence}")) +
                // Their whole field next to the options: shows which cards the engine left out (2026-10-17: Brionac
                // wasn't offered Turbo Warrior).
                $" | their field [{DescribeCards(await GetEnemyMonsterCardsAsync())}] [{DescribeCards(await GetEnemySpellCardsAsync())}]");

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

            var priorityTargets = _priorityTargets;
            _priorityTargets = new();
            // Our Spell/Trap: their Psychics under Thought Ruler go last (it would be negated).
            bool spellTrapTargeting = !Chain.LastActivationWasMonsterEffect;

            var picks = selectable
                .Select((card, index) => new { Card = card, Index = index })
                .Where(x => IsEnemyCard(x.Card, enemyCards))
                .Where(x => !protectedMonsters.Any(p => p.CurrentCoordinates.Sequence == x.Card.CurrentCoordinates.Sequence &&
                                                        x.Card.CurrentCoordinates.Location == (byte)CardLocation.MonsterZone))
                // A face-up normal/quick-play spell or normal trap is just resolving: destroying it does nothing.
                .Where(x => x.Card.CurrentCoordinates.Location != (byte)CardLocation.SpellZone || !x.Card.IsFaceup() || IsLastingCard(x.Card))
                .Where(x => !avoidPunishers || !x.Card.IsCode(PunishesMonsterTargeting))
                .OrderBy(x => spellTrapTargeting && x.Card.CurrentCoordinates.Location == (byte)CardLocation.MonsterZone &&
                    ThoughtRulerProtects(x.Card, enemyMonsterCards))
                // Monsters marked when deciding to chain (the synchro pair Icarus breaks up) go first.
                .ThenByDescending(x => x.Card.CurrentCoordinates.Location == (byte)CardLocation.MonsterZone &&
                    priorityTargets.Any(p => p.CurrentCoordinates.Sequence == x.Card.CurrentCoordinates.Sequence))
                // Tokens last: Dark Armed Dragon took two Fluff Tokens and left the set backrow (2026-10-21).
                .ThenBy(x => (x.Card.Type & TokenType) != 0)
                // Single-target removal on monsters keeps going for the biggest monster threat; multi-target removal
                // (Icarus) takes the most valuable cards, backrow included.
                .ThenByDescending(x => state.Minimum >= 2 ? 0 : (x.Card.CurrentCoordinates.Location == preferredLocation ? 1 : 0))
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

            // No battle left for us this turn (turn 1, Main Phase 2) and a Machina Fortress on their side: an indestructible
            // monster waits in defense, so they can't crash the Fortress into it to trigger its effect. Otherwise it stays in
            // attack: in defense, Ehren sent Armor Master back to the deck (2026-10-23).
            var phase = (DuelPhase)context.Header.CurrentPhase;
            bool noBattleThisTurn = context.Header.CurrentTurn == 1 || phase == DuelPhase.Main2;
            bool fortressThere = enemies.Any(card => card.IsFaceup() && card.IsCode(PunishesBattleDestruction));
            if (fearless && noBattleThisTurn && fortressThere && positions.Contains((byte)CardPosition.FaceUpDefence))
            {
                Log($"{cardId} in defense: it can't attack this turn");
                return CardPosition.FaceUpDefence;
            }

            return await base.OnSelectPositionAsync(context, ct);
        }

        public override async Task<bool> OnSelectEffectYnAsync(DecisionContext<SelectEffectYnData> context, CancellationToken ct = default)
        {
            var effectCard = context.StateData.EffectCard;
            // An effect that special summons (Blizzard's revive, Shura's floater) into a face-up Royal Oppression:
            // they pay 800, negate it and destroy our monster. Skip it unless every payment really hurts them.
            try
            {
                await PrepareContextAsync(context, ct);
                if (effectCard.IsCode(OurSpecialSummonEffects) && CurrentField.EnemySide.LifePoints > 3000 &&
                    (await GetOwnSpellCardsAsync()).Concat(await GetEnemySpellCardsAsync()).Any(card => card.IsCode(RoyalOppression) && card.IsFaceup()))
                {
                    Log($"Effect yes/no: card {effectCard.ID} -> no (its special summon would be negated by Royal Oppression)");
                    return false;
                }
                // Goyo's steal comes as a yes/no after the battle: not on a monster whose death effect fires anyway.
                int lastInTheirGrave = (CurrentField.EnemySide.Grave ?? Array.Empty<int>()).LastOrDefault();
                if (effectCard.IsCode(StealsOnBattle) && lastInTheirGrave != 0 && FloatsOnDeath(lastInTheirGrave))
                {
                    Log($"Effect yes/no: card {effectCard.ID} -> no (not stealing {lastInTheirGrave}: its effect when it dies fires anyway)");
                    return false;
                }
            }
            catch (Exception e)
            {
                Log($"[error] Effect yes/no check failed, answering yes: {e.Message}");
            }

            Log($"Effect yes/no: card {effectCard.ID} desc {context.StateData.Description} -> yes");
            Chain.MarkOwnAction(context.Header);
            return true;
        }

        /// <summary>A face-up attacker the deck keeps in attack even though something of theirs beats it (a bluff).</summary>
        protected virtual bool KeepsAttackAsBluff(DynamicCard card, int strongestEnemyAttack) => false;

        /// <summary>Our monsters whose effect special summons (Royal Oppression negates and destroys them).</summary>
        protected virtual int[] OurSpecialSummonEffects => Array.Empty<int>();

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
        protected static bool IsFortressTrap(DynamicCard card, bool fortressIsNecessary) =>
            card.IsCode(PunishesMonsterTargeting) && card.IsFaceup() && !fortressIsNecessary;

        protected async Task<bool> IsFortressTargetWorthItAsync(List<DynamicCard> hand)
        {
            if (hand.Count == 0)
                return true;

            var fortress = (await GetEnemyMonsterCardsAsync()).FirstOrDefault(card => card.IsCode(PunishesMonsterTargeting) && card.IsFaceup());
            if (fortress.ID == 0)
                return true;

            // Beating it in battle only counts with a monster we can afford to lose to its effect: important monsters
            // don't attack it (2026-10-16: DAD skipped its effect on Fortress, attacked it, and was destroyed).
            var ownMonsters = await GetBotMonsterCardsAsync();
            var expendable = ownMonsters.Where(card => !IsImportantMonster(card) || IsStolen(card)).ToList();
            int ownBest = expendable.Select(card => card.Attack).DefaultIfEmpty(0).Max() +
                (expendable.Count > 0 ? BattleBoostInHand(hand) : 0);

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

            // Wulf only comes out when a Lightsworn mills it: in their hand it's a dead card, back in the deck it's live (Javier).
            if (card.IsCode(LightswornWulf))
                score -= 10000;

            // Destiny HERO - Plasma is summoned by tributing 3 monsters: live (and urgent) only if they have them.
            if (card.IsCode(DestinyHeroPlasma) && board.EnemyMonsters.Count >= 3)
                score += 2500;

            // Debris Dragon revives from their GY: with nothing there it's a dead card.
            if (card.IsCode(DebrisDragon) && (CurrentField.EnemySide.Grave ?? Array.Empty<int>()).Length == 0)
                score -= priority;

            // With nothing in their GY, GY-based plays are slow: the searchers that start the engine come first
            // (Gearframe, Stratos). Machina Force is discard fodder to revive Machina Fortress from the GY:
            // without a Fortress there it does little this turn.
            var theirGrave = CurrentField.EnemySide.Grave ?? Array.Empty<int>();
            if (theirGrave.Length == 0 && card.IsCode(Searchers))
                score += 1500;
            if (card.IsCode(MachinaForce) && !theirGrave.Any(id => CardIdentity.Same(id, MachinaFortress)))
                score -= 1500;
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

        /// <summary>A face-up Tytannial that can only tribute herself to negate: any targeting makes her leave.</summary>
        protected bool TytannialCornered => _tytannialActive && !_targetingBlocked;

        protected bool IsTargetingCard(int cardId) => TargetsOnField.Contains(cardId) || DeckTargetingCards.Contains(cardId);

        // Owner is bot-relative: a monster we control that the opponent owns was stolen (Brain Control).
        protected static bool IsStolen(DynamicCard card) => card.Owner == 1;

        // Selection data is bot-relative: controller 1 = opponent. Matching by ID doesn't work: their face-down cards
        // reach the bot with no ID, and our enemy lists are read from the player's side, where they have one.
        protected static bool IsEnemyCard(DynamicCard card, List<DynamicCard> enemyCards) =>
            card.CurrentCoordinates.Controller == ChainTracker.Opponent;
    }
}
