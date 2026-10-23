using BotTest.Game.AI.Brains;
using BotTest.Game.AI.Knowledge;
using OmegaBotWrapper;
using OmegaBotWrapper.Enum;
using OmegaBotWrapper.Heuristics;
using OmegaBotWrapper.States;
using OmegaBotWrapper.Struct;
using static BotTest.Game.AI.Bots.MasterDuel.Lightsworn.LightswornCardId;

namespace BotTest.Game.AI.Bots.MasterDuel.Lightsworn
{
    /// <summary>
    /// Lightsworn strategy for Edison (pure build). Mill fast to reach 4 Lightsworn names for Judgment Dragon,
    /// clear their backrow before it lands, and watch the deck count. Everything about opposing cards comes from
    /// <see cref="EdisonBotBrain"/>. Plan written by Javier: "lightsworn deck.md" / docs/reglas/lightsworn.md.
    /// </summary>
    public sealed class LightswornBotBrain : EdisonBotBrain
    {
        private static readonly int[] LightswornMonsters = { Celestia, Wulf, Garoth, Jain, Lyla, Ehren, Druid, Lumina, Ryko };

        // Lightsworn that mill in our End Phase (Ryko and Wulf don't).
        private static readonly int[] EndPhaseMillers = { Celestia, Garoth, Jain, Lyla, Ehren, Druid, Lumina, JudgmentDragon };

        // Gorz bait: with one of these in hand there's a real play, so we don't sit on an empty field.
        private static readonly int[] StrongPlays = { Ryko, Lumina, Jain };

        // Cards they can answer a summon with: Judgment Dragon waits while one may be set.
        private static readonly int[] SummonPunishers = { BottomlessTrapHole, TorrentialTribute };

        private const int LightAttribute = 0x10;

        // 15 cards or fewer: careful with the mills. At the floor, every optional mill stops (0 cards = we lose).
        private const int DeckCaution = 15;
        private const int DeckFloor = 6;

        private static readonly Random Rng = new();

        // Which of our effects the next selection belongs to (removal targets, Tragoedia's discard).
        private int _pendingEffect;
        private DynamicCard? _tragoediaTarget;
        private int _tragoediaDiscard;
        private bool _celestiaSummoning;
        private string? _honestBattle;
        private int _bluffTurn = -1;
        private bool _bluff;
        private string? _lastDeckLog;

        public LightswornBotBrain(
            ICardEvaluator? cardEvaluator = null,
            IBoardEvaluator? boardEvaluator = null,
            IActionScorer? actionScorer = null)
            : base(cardEvaluator, boardEvaluator, actionScorer)
        {
        }

        protected override string LogTag => "[Lightsworn]";

        protected override void OnNewDuel()
        {
            base.OnNewDuel();
            _pendingEffect = 0;
            _tragoediaTarget = null;
            _tragoediaDiscard = 0;
            _celestiaSummoning = false;
            _honestBattle = null;
            _bluffTurn = -1;
            _lastDeckLog = null;
        }

        protected override int[] SynchroPriority { get; } =
        {
            StardustDragon, GoyoGuardian, BrionacDragonOfTheIceBarrier, RedDragonArchfiend, ThoughtRulerArchfiend,
            ColossalFighter, XSaberUrbellum, AllyOfJusticeCatastor, TempestMagician, MistWurm, MagicalAndroid, ArmoryArm
        };

        // Plaguespreader (level 2) is the only tuner.
        protected override int[] SynchroLevels => new[] { 4, 6, 7, 8, 9 };

        protected override int[] MonsterEffectCards { get; } = { Lyla, JudgmentDragon, Lumina, Tragoedia, CardTrooper, Honest };

        protected override int[] DeckTargetingCards { get; } = { Lyla, Lumina, Tragoedia };

        // Summons, sets and activations are all decided below; the generic fallback only repositions and changes phase.
        private static readonly int[] DeckMonsters =
            LightswornMonsters.Concat(new[] { JudgmentDragon, Tragoedia, Gorz, Honest, NecroGardna, CardTrooper, Plaguespreader }).ToArray();

        protected override int[] ExcludedFromGenericSummon => DeckMonsters;

        protected override int[] ExcludedFromGenericSet => DeckMonsters;

        protected override int[] ExcludedFromGenericActivation { get; } =
        {
            JudgmentDragon, Lumina, Lyla, Tragoedia, CardTrooper, Honest, Plaguespreader,
            MonsterReincarnation, CardDestruction, ChargeOfTheLightBrigade, FoolishBurial, PotOfAvarice, SolarRecharge
        };

        // Honest gives a LIGHT monster the ATK of the monster it battles: counted as a big pump for "can we beat it".
        protected override int BattleBoostInHand(List<DynamicCard> hand) => hand.Any(card => card.IsCode(Honest)) ? 2000 : 0;

        protected override bool CanBeBoosted(DynamicCard card) => (card.Attribute & LightAttribute) != 0;

        protected override int HandValue(DynamicCard card)
        {
            // Wulf only comes out from the deck: in hand it's a dead card. Plaguespreader and Gardna work from the GY.
            if (card.IsCode(Wulf, Plaguespreader, NecroGardna))
                return 0;
            if (card.IsCode(JudgmentDragon, Lumina))
                return 4;
            if (card.IsCode(Honest, Ryko, Celestia))
                return 3;
            return base.HandValue(card);
        }

        // Celestia's effect needs a Lightsworn as the tribute.
        protected override int MaterialCost(DynamicCard card)
        {
            int cost = base.MaterialCost(card);
            if (_celestiaSummoning && !card.IsCode(LightswornMonsters))
                cost += 10000;
            return cost;
        }

        // ---- Deck state ---------------------------------------------------------------------------------

        private int DeckCount => (CurrentField.OwnSide.Main ?? Array.Empty<int>()).Length;

        private int[] OwnGrave => CurrentField.OwnSide.Grave ?? Array.Empty<int>();

        private int GraveCopies(int cardId) => OwnGrave.Count(id => CardIdentity.Same(id, cardId));

        /// <summary>Different Lightsworn names in our GY (Judgment Dragon needs 4).</summary>
        private int GraveNames() => LightswornMonsters.Count(name => GraveCopies(name) > 0);

        private bool InHand(List<DynamicCard> hand, int cardId) => hand.Any(card => card.IsCode(cardId));

        // Our attackers already finish them: every caution below gives way to lethal.
        private bool OurLethal(Board board, int extraAttack = 0) =>
            IsBotTurn && board.IsMain1 && board.EnemyMonsters.Count == 0 &&
            board.OwnMonsters.Where(card => card.IsFaceup() && card.IsAttack()).Sum(card => card.Attack) + extraAttack >= CurrentField.EnemySide.LifePoints;

        /// <summary>Enough cards left to mill this many and stay above the floor.</summary>
        private bool CanMill(int cards, Board board) => DeckCount - cards > DeckFloor || OurLethal(board);

        // A set card that may be Bottomless or Torrential: Judgment Dragon waits for it to be cleared.
        private bool SummonTrapPossible(Board board) =>
            board.EnemySpells.Any(card => !card.IsFaceup()) && OpponentMayStillHave(board, SummonPunishers);

        private static bool HasBackrow(Board board) =>
            board.EnemySpells.Any(card => !card.IsFaceup() || IsLastingCard(card));

        // Judgment Dragon's wipe: like Black Rose, when they have 2+ cards more than we do on the field.
        private bool JudgmentWipeWorthIt(Board board)
        {
            int theirs = board.EnemyMonsters.Count + board.EnemySpells.Count;
            int ours = board.OwnMonsters.Count(card => !card.IsCode(JudgmentDragon)) + board.OwnSpells.Count;
            int lifePoints = CurrentField.OwnSide.LifePoints;
            if (lifePoints <= 1000 || board.StardustBlocking || theirs - ours < 2)
                return false;

            // Below 2000 LP only when it wins or keeps us alive.
            if (lifePoints < 2000)
            {
                int ourAttack = board.OwnMonsters.Where(card => card.IsCode(JudgmentDragon) && card.IsFaceup() && card.IsAttack() && !card.Attacked)
                    .Sum(card => card.Attack);
                bool winsNow = IsBotTurn && board.IsMain1 && ourAttack >= CurrentField.EnemySide.LifePoints;
                return winsNow || OpponentHasLethal(board);
            }
            return true;
        }

        // ---- Main Phase ---------------------------------------------------------------------------------

        protected override bool CanUseMonsterEffect(int cardId, Board board, List<DynamicCard> monsterTargets) => cardId switch
        {
            Lyla => HasBackrow(board),
            JudgmentDragon => JudgmentWipeWorthIt(board),
            Lumina => LuminaTarget(board.Graveyard, board) != 0 && board.Hand.Count > 0,
            Tragoedia => FindTragoediaSteal(board).HasValue,
            CardTrooper => IsBotTurn && board.IsMain1 && CanMill(1, board),
            // Back to the hand in Main Phase 2, after hitting (Javier: "se pega en Main 1 y en Main 2 se vuelve a la mano").
            // Below 3000 LP it stays as a 1900 DEF wall.
            Honest => IsBotTurn && !board.IsMain1 && CurrentField.OwnSide.LifePoints >= 3000,
            _ => true
        };

        protected override Task<IdleCmdResponse?> DecideMainPhaseAsync(DecisionContext<IdleCmdData> context, Board board) =>
            Task.FromResult(DecideMainPhase(context, board));

        private IdleCmdResponse? DecideMainPhase(DecisionContext<IdleCmdData> context, Board board)
        {
            var state = context.StateData;
            var hand = board.Hand;
            _celestiaSummoning = false;

            string deckLog = $"Deck {DeckCount} cards, {GraveNames()} Lightsworn names in GY";
            if (deckLog != _lastDeckLog)
                Log(deckLog);
            _lastDeckLog = deckLog;

            // Draws and mills first: they only leave cards in the GY, so they never feed Gorz's condition.
            var spell = DecideSpells(state, board);
            if (spell.HasValue)
                return spell;

            // Gorz in hand and nothing strong to play: keep the field empty so it can land (Javier).
            bool gorzBait = InHand(hand, Gorz) && board.OwnMonsters.Count == 0 && board.OwnSpells.Count == 0 &&
                board.EnemyMonsters.Count > 0 && !hand.Any(card => card.IsCode(StrongPlays));
            if (gorzBait)
            {
                Log("Gorz in hand and no strong play: nothing set or summoned, the field stays empty for it");
                return MainAction.ToEndPhase;
            }

            // Ryko set last turn: flip it first thing, breaking their backrow.
            if (IsBotTurn && board.IsMain1 && TryGetReposable(state, Ryko, out var ryko) && !ryko.IsFaceup() && CanMill(3, board))
            {
                Log("Flip summoning Ryko");
                return ryko.Reposition();
            }

            // Lyla breaks a spell/trap (she goes to defense).
            if (HasBackrow(board) && TryActivate(state, Lyla, board.StardustBlocking, out var lyla))
            {
                _pendingEffect = Lyla;
                return lyla;
            }

            bool judgmentReady = TryGetSpecialSummon(state, JudgmentDragon, out var judgment);

            // Heavy Storm to clear the way for Judgment Dragon (Edison already uses it when it's a good trade).
            if (judgmentReady && SummonTrapPossible(board) && TryActivate(state, HeavyStorm, board.StardustBlocking, out var storm))
            {
                Log("Heavy Storm: clears their backrow before Judgment Dragon");
                return storm;
            }

            // Celestia: tribute a Lightsworn, destroy up to 2 of their cards.
            if (IsBotTurn && board.EnemyMonsters.Count + board.EnemySpells.Count > 0 && CanMill(4, board) &&
                board.OwnMonsters.Any(card => card.IsCode(LightswornMonsters) && !card.IsCode(Celestia)) &&
                TryGetSummonable(state, Celestia, out var celestia))
            {
                Log("Tribute summoning Celestia to destroy their cards");
                _celestiaSummoning = true;
                return celestia.NormalSummon();
            }

            if (judgmentReady)
            {
                bool lastResort = OpponentHasLethal(board);
                if (SummonTrapPossible(board) && !lastResort)
                    LogOnChange("Judgment Dragon waits: a set card may be Bottomless / Torrential");
                else if (OppressionStopsOurSpecialSummons(board))
                    LogOnChange("Judgment Dragon waits: Royal Oppression would negate it");
                else if (!CanMill(4, board) && !lastResort)
                    LogOnChange($"Judgment Dragon waits: its End Phase mill would leave {DeckCount - 4} cards");
                else
                {
                    Log($"Special summoning Judgment Dragon ({GraveNames()} names){(lastResort ? ": only play against their lethal" : "")}");
                    return judgment.SpecialSummon();
                }
            }

            if (JudgmentWipeWorthIt(board) && TryActivate(state, JudgmentDragon, board.StardustBlocking, out var wipe))
            {
                Log("Judgment Dragon's effect: they have 2+ cards more on the field");
                return wipe;
            }

            var lumina = DecideLumina(state, board);
            if (lumina.HasValue)
                return lumina;

            var steal = FindTragoediaSteal(board);
            if (steal.HasValue && TryActivate(state, Tragoedia, false, out var tragoedia))
            {
                _tragoediaTarget = steal.Value.Target;
                _tragoediaDiscard = steal.Value.Discard;
                _pendingEffect = Tragoedia;
                Log($"Tragoedia: sending {steal.Value.Discard} to take {steal.Value.Target.ID}");
                return tragoedia;
            }

            if (CanUseMonsterEffect(CardTrooper, board, board.EnemyMonsters) && TryActivate(state, CardTrooper, false, out var trooper))
                return trooper;

            var summon = DecideNormalSummon(state, board);
            if (summon.HasValue)
                return summon;

            if (CanUseMonsterEffect(Honest, board, board.EnemyMonsters) && TryActivate(state, Honest, false, out var honestBack))
            {
                Log("Honest back to the hand in Main Phase 2");
                return honestBack;
            }

            return null;
        }

        private IdleCmdResponse? DecideSpells(IdleCmdData state, Board board)
        {
            var hand = board.Hand;
            int lifePoints = CurrentField.OwnSide.LifePoints;

            // Pot of Avarice: always, unless it takes us below 4 names. With 15 cards or fewer, or low LP, always.
            if (TryGetActivable(state, PotOfAvarice, out _))
            {
                bool forced = DeckCount <= DeckCaution || lifePoints <= 2000;
                if ((forced || AvariceKeepsNames()) && TryActivate(state, PotOfAvarice, false, out var avarice))
                {
                    Log($"Pot of Avarice{(forced ? " (deck low or LP low)" : "")}");
                    _pendingEffect = PotOfAvarice;
                    return avarice;
                }
            }

            // Solar Recharge: draw 2, mill 2. Needs a Lightsworn to discard (never Lumina unless it's the only one).
            if (CanMill(4, board) && hand.Any(card => card.IsCode(LightswornMonsters)) &&
                TryActivate(state, SolarRecharge, false, out var solar))
            {
                _pendingEffect = SolarRecharge;
                return solar;
            }

            // Charge of the Light Brigade: mill 3, search a Lightsworn.
            if (CanMill(4, board) && TryActivate(state, ChargeOfTheLightBrigade, false, out var charge))
            {
                _pendingEffect = ChargeOfTheLightBrigade;
                return charge;
            }

            // Monster Reincarnation: back for Judgment Dragon, or Lumina to keep milling when the names allow it.
            var others = hand.Where(card => !card.IsCode(MonsterReincarnation)).ToList();
            bool judgmentInGrave = GraveCopies(JudgmentDragon) > 0 && !InHand(hand, JudgmentDragon);
            bool luminaBack = GraveCopies(Lumina) > 0 && GraveNames() >= 5;
            if (others.Count > 0 && (judgmentInGrave || luminaBack) && TryActivate(state, MonsterReincarnation, false, out var reincarnation))
            {
                _pendingEffect = MonsterReincarnation;
                return reincarnation;
            }

            // Card Destruction: when the hand is mostly cards that work better in the GY (Wulf, Plaguespreader, Gardna).
            var rest = hand.Where(card => !card.IsCode(CardDestruction)).ToList();
            if (rest.Count >= 2 && rest.Count(card => HandValue(card) == 0) * 2 >= rest.Count && CanMill(rest.Count, board) &&
                !judgmentInGrave && TryActivate(state, CardDestruction, false, out var destruction))
            {
                Log("Card Destruction: most of the hand works better in the GY");
                return destruction;
            }

            if (CanMill(2, board) && TryActivate(state, FoolishBurial, false, out var foolish))
            {
                _pendingEffect = FoolishBurial;
                return foolish;
            }

            return null;
        }

        // Pot of Avarice returns 5 monsters. Without breaking the names: the extra copies and the other monsters
        // (Gardna and Plaguespreader stay), plus any names above 4.
        private bool AvariceKeepsNames()
        {
            int names = GraveNames();
            if (names < 4)
                return true;
            int monsters = OwnGrave.Count(id => CardIdentity.Same(id, JudgmentDragon) || DeckMonsters.Any(m => CardIdentity.Same(id, m)));
            int protectedCards = GraveCopies(NecroGardna) + GraveCopies(Plaguespreader);
            int spare = monsters - names - protectedCards + (names - 4);
            if (spare < 5)
                LogOnChange($"Pot of Avarice waits: it would leave fewer than 4 names ({names} names, {monsters} monsters in GY)");
            return spare >= 5;
        }

        private IdleCmdResponse? DecideLumina(IdleCmdData state, Board board)
        {
            if (!CanUseMonsterEffect(Lumina, board, board.EnemyMonsters))
                return null;
            if (!TryActivate(state, Lumina, board.StardustBlocking, out var lumina))
                return null;
            _pendingEffect = Lumina;
            return lumina;
        }

        // Lumina revives a level 4 or lower Lightsworn: Lyla against backrow, Jain, Wulf... never Ryko (it comes face-up,
        // no flip). With 4 names or fewer, a copy that's repeated in the GY, so the names stay.
        private int LuminaTarget(List<DynamicCard> grave, Board board)
        {
            var order = new List<int>();
            if (HasBackrow(board))
                order.Add(Lyla);
            if (board.EnemyMonsters.Any(card => !card.IsFaceup()))
                order.Add(Ehren);
            order.AddRange(new[] { Jain, Wulf, Garoth, Lyla, Ehren, Druid });

            bool tightNames = GraveNames() <= 4;
            foreach (bool needRepeat in tightNames ? new[] { true, false } : new[] { false })
            {
                foreach (int id in order.Distinct())
                {
                    if (!grave.Any(card => card.IsCode(id)))
                        continue;
                    if (needRepeat && GraveCopies(id) < 2)
                        continue;
                    return id;
                }
            }
            return 0;
        }

        // Tragoedia: send a monster from hand, take a face-up monster of theirs with the same Level.
        // Anything can go, Judgment Dragon too, unless we have fewer than 4 names (Javier).
        private (DynamicCard Target, int Discard)? FindTragoediaSteal(Board board)
        {
            if (!board.OwnMonsters.Any(card => card.IsCode(Tragoedia) && card.IsFaceup()))
                return null;

            foreach (var target in board.EnemyMonsters.Where(card => card.IsFaceup() && (card.Type & CardKnowledge.TokenType) == 0)
                         .OrderByDescending(ThreatScore))
            {
                var discard = board.Hand
                    .Where(card => card.HasType(CardType.Monster) && card.Level == target.Level)
                    .Where(card => !card.IsCode(JudgmentDragon) || GraveNames() >= 4)
                    .OrderBy(HandValue)
                    .FirstOrDefault();
                if (discard.ID != 0)
                    return (target, discard.ID);
            }
            return null;
        }

        private IdleCmdResponse? DecideNormalSummon(IdleCmdData state, Board board)
        {
            if (!IsBotTurn)
                return null;

            var hand = board.Hand;
            var ownMonsters = board.OwnMonsters;
            bool enemyFaceDown = board.EnemyMonsters.Any(card => !card.IsFaceup());
            int enemyBestPower = board.EnemyMonsters.Where(card => card.IsFaceup()).Select(card => card.GetRealPower()).DefaultIfEmpty(0).Max();
            bool honestInHand = InHand(hand, Honest);
            bool needBlocker = OpponentHasLethal(board) || (ownMonsters.Count == 0 && CurrentField.OwnSide.LifePoints <= 3000);

            // Ryko face-down first, with a Threatening Roar to hold the attacks (turn 1 ideal).
            if (!ownMonsters.Any(card => card.IsCode(Ryko) && !card.IsFaceup()) && TryGetMonsterSettable(state, Ryko, out var ryko))
            {
                Log("Setting Ryko");
                return ryko.SetMonster();
            }

            var order = new List<int>();
            if (HasBackrow(board))
                order.Add(Lyla);
            if (enemyFaceDown)
                order.Add(Ehren);
            order.AddRange(new[] { Jain, Garoth, CardTrooper, Lumina });
            // Druid stops our own GY targeting (Lumina, Monster Reincarnation, Pot of Avarice).
            if (!hand.Any(card => card.IsCode(Lumina, MonsterReincarnation, PotOfAvarice)))
                order.Add(Druid);
            order.AddRange(new[] { Lyla, Ehren });

            // With 15 cards or fewer, no more Lightsworn than needed: one already out is enough unless this one has a job.
            bool millerOut = ownMonsters.Any(card => card.IsFaceup() && card.IsCode(EndPhaseMillers));
            foreach (int cardId in order.Distinct())
            {
                if (!TryGetSummonable(state, cardId, out var card))
                    continue;

                bool hasJob = (cardId == Lyla && HasBackrow(board)) || (cardId == Ehren && enemyFaceDown);
                bool mills = card.IsCode(EndPhaseMillers);
                if (mills && DeckCount <= DeckFloor && !OurLethal(board, card.Attack) && !needBlocker)
                {
                    LogOnChange($"Holding {cardId}: {DeckCount} cards left, its End Phase mill is too risky");
                    continue;
                }
                if (mills && DeckCount <= DeckCaution && millerOut && !hasJob && !OurLethal(board, card.Attack) && !needBlocker)
                {
                    LogOnChange($"Holding {cardId}: {DeckCount} cards left and a Lightsworn is already milling");
                    continue;
                }

                // Bluff: a Lightsworn face-up in attack makes them wonder about Honest. Always with Honest in hand,
                // 3 times out of 10 without it.
                if (_bluffTurn != CurrentHeader.CurrentTurn)
                {
                    _bluffTurn = CurrentHeader.CurrentTurn;
                    _bluff = Rng.NextDouble() < 0.3;
                }
                bool covered = honestInHand || _bluff || card.Attack >= enemyBestPower || hasJob;
                if (!covered)
                {
                    int enemyBestAttack = board.EnemyMonsters.Where(m => m.IsFaceup() && m.IsAttack()).Select(m => m.Attack).DefaultIfEmpty(0).Max();
                    if (card.Defense > enemyBestAttack && TryGetMonsterSettable(state, cardId, out var settable))
                    {
                        Log($"Setting {cardId} (DEF {card.Defense} holds)");
                        return settable.SetMonster();
                    }
                    if (needBlocker && TryGetMonsterSettable(state, cardId, out var blocker))
                    {
                        Log($"Setting {cardId} as a blocker");
                        return blocker.SetMonster();
                    }
                    LogOnChange($"Holding {cardId}: too weak face-up and its DEF wouldn't hold");
                    continue;
                }

                Log($"Normal summon: {cardId}{(honestInHand ? " (Honest in hand)" : _bluff && card.Attack < enemyBestPower ? " (bluff)" : "")}");
                return card.NormalSummon();
            }

            // Honest: poke when nothing else can be summoned and they have no real threat (nothing above 1100 ATK);
            // it goes back to the hand in Main Phase 2. As a 1900 DEF wall only below 3000 LP.
            bool noThreat = board.EnemyMonsters.All(card => card.IsFaceup() && card.Attack <= 1100);
            if (board.IsMain1 && state.CanBattlePhase && noThreat && TryGetSummonable(state, Honest, out var honest))
            {
                Log("Honest pokes: no real threat on their side");
                return honest.NormalSummon();
            }
            if (CurrentField.OwnSide.LifePoints < 3000 && ownMonsters.Count == 0 && TryGetMonsterSettable(state, Honest, out var honestWall))
            {
                Log("Setting Honest as a wall (below 3000 LP)");
                return honestWall.SetMonster();
            }

            // Gardna / Plaguespreader are discard fodder: only as a last blocker.
            if (needBlocker)
            {
                foreach (int cardId in new[] { NecroGardna, CardTrooper, Plaguespreader })
                {
                    if (TryGetMonsterSettable(state, cardId, out var wall))
                    {
                        Log($"Setting {cardId} as a last blocker");
                        return wall.SetMonster();
                    }
                }
            }

            return null;
        }

        // ---- Chain and effect prompts -------------------------------------------------------------------

        protected override async Task<bool?> ShouldChainDeckCardAsync(DynamicCard card)
        {
            var decision = await DecideOwnEffectAsync(card);
            return decision;
        }

        public override async Task<bool> OnSelectEffectYnAsync(DecisionContext<SelectEffectYnData> context, CancellationToken ct = default)
        {
            try
            {
                await PrepareContextAsync(context, ct);
                await NormalizeAsync(new[] { context.StateData.EffectCard });
                var decision = await DecideOwnEffectAsync(context.StateData.EffectCard);
                if (decision.HasValue)
                {
                    Log($"Effect yes/no: card {context.StateData.EffectCard.ID} -> {(decision.Value ? "yes" : "no")}");
                    return decision.Value;
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Log($"[error] Lightsworn yes/no check failed, using the shared rule: {e.Message}");
            }
            return await base.OnSelectEffectYnAsync(context, ct);
        }

        private async Task<bool?> DecideOwnEffectAsync(DynamicCard card)
        {
            var location = (CardLocation)card.CurrentCoordinates.Location;

            if (card.IsCode(Gorz) && location == CardLocation.Hand)
                return await ShouldSummonGorzAsync();

            if (card.IsCode(Tragoedia) && location == CardLocation.Hand)
            {
                int handSize = (CurrentField.OwnSide.Hand ?? Array.Empty<int>()).Length;
                bool noBlockers = (await GetBotMonsterCardsAsync()).Count == 0;
                bool use = handSize >= 5 || (noBlockers && CurrentField.OwnSide.LifePoints <= 3000);
                Log($"Tragoedia from hand: {(use ? "yes" : "no")} ({handSize} cards in hand)");
                return use;
            }

            if (card.IsCode(Honest) && location == CardLocation.Hand)
                return ShouldUseHonest();

            if (card.IsCode(Ryko))
            {
                _pendingEffect = Ryko;
                return true;
            }

            if (card.IsCode(Celestia))
            {
                var board = await BuildBoardAsync();
                bool use = CanMill(4, board);
                if (use)
                    _pendingEffect = Celestia;
                else
                    Log($"Celestia's effect skipped: milling 4 would leave {DeckCount - 4} cards");
                return use;
            }

            // Threatening Roar: on their turn, once they head into battle with something that can hurt us.
            if (card.IsCode(ThreateningRoar))
            {
                var phase = (DuelPhase)CurrentHeader.CurrentPhase;
                bool battle = phase is DuelPhase.BattleStart or DuelPhase.BattleStep;
                var enemyMonsters = await GetEnemyMonsterCardsAsync();
                bool threat = enemyMonsters.Any(monster => monster.IsFaceup() && monster.IsAttack() && monster.Attack > 0);
                return !IsBotTurn && battle && threat && !Chain.AnsweredCurrentChain(CurrentHeader);
            }

            return null;
        }

        // Gorz: they usually attack from weakest to strongest, so wait for the last hit (a bigger token). If a stronger
        // attack already went through, or the ones left would kill us, summon it now to stop the rest (Javier).
        private async Task<bool> ShouldSummonGorzAsync()
        {
            var enemySide = CurrentField.EnemySide;
            int lastHit = enemySide.HasBattlingMonster ? enemySide.BattlingMonster.Attack : 0;
            var waiting = (await GetEnemyMonsterCardsAsync())
                .Where(card => card.IsFaceup() && card.IsAttack() && !card.Attacked)
                .Where(card => !enemySide.HasBattlingMonster || card.CurrentCoordinates.Sequence != enemySide.BattlingMonster.CurrentCoordinates.Sequence)
                .ToList();
            bool biggerComing = lastHit > 0 && waiting.Any(card => card.Attack > lastHit);
            bool restKills = waiting.Sum(card => card.Attack) >= CurrentField.OwnSide.LifePoints;
            bool phaseOk = (DuelPhase)CurrentHeader.CurrentPhase is DuelPhase.BattleStep or DuelPhase.Damage or DuelPhase.DamageCal or DuelPhase.Battle;
            if (phaseOk && biggerComing && !restKills)
            {
                Log($"Gorz waits: a bigger attacker ({waiting.Max(card => card.Attack)}) is still to come after {lastHit}");
                return false;
            }
            Log($"Gorz now (last hit {lastHit}, {waiting.Count} attackers left)");
            return true;
        }

        // Honest in the Damage Step: when it turns the battle (we'd lose or bounce without it).
        private bool ShouldUseHonest()
        {
            var ownSide = CurrentField.OwnSide;
            var enemySide = CurrentField.EnemySide;
            if (!ownSide.HasBattlingMonster || !enemySide.HasBattlingMonster)
                return false;

            var ours = ownSide.BattlingMonster;
            var theirs = enemySide.BattlingMonster;
            if ((ours.Attribute & LightAttribute) == 0 || theirs.Attack <= 0)
                return false;

            // One Honest per battle: the next prompt in the same damage step still shows the old ATK.
            string battle = $"{CurrentHeader.CurrentTurn}:{ours.CurrentCoordinates.Sequence}:{theirs.CurrentCoordinates.Sequence}";
            if (battle == _honestBattle)
                return false;

            int theirPower = theirs.GetRealPower();
            bool use;
            if (IsBotTurn)
                use = ours.Attack <= theirPower && ours.Attack + theirs.Attack > theirPower;
            else
                use = ours.IsAttack() && theirs.Attack >= ours.Attack;

            if (use)
            {
                _honestBattle = battle;
                Log($"Honest: {ours.ID} ({ours.Attack}) gains {theirs.Attack} against {theirs.ID}");
            }
            return use;
        }

        public override async Task<CardPosition> OnSelectPositionAsync(DecisionContext<SelectPositionData> context, CancellationToken ct = default)
        {
            var positions = context.StateData.Positions ?? Array.Empty<byte>();
            int cardId = context.StateData.CardID;
            // Gorz and Tragoedia come down in defense to stop the attack (Javier).
            if ((CardIdentity.Same(cardId, Gorz) || CardIdentity.Same(cardId, Tragoedia)) && positions.Contains((byte)CardPosition.FaceUpDefence))
            {
                Log($"{cardId} in defense");
                return CardPosition.FaceUpDefence;
            }
            return await base.OnSelectPositionAsync(context, ct);
        }

        // Card Trooper mills 3 (more chances at Wulf), 1 with 15 cards or fewer.
        public override async Task<int> OnAnnounceNumberAsync(DecisionContext<AnnounceNumberData> context, CancellationToken ct = default)
        {
            await PrepareContextAsync(context, ct);
            var options = context.StateData.SelectableOptions ?? Array.Empty<int>();
            if (options.Length == 0)
                return await base.OnAnnounceNumberAsync(context, ct);

            int wanted = DeckCount <= DeckCaution ? 1 : 3;
            int pick = options.Where(n => n <= wanted).DefaultIfEmpty(options.Min()).Max();
            Log($"Announcing {pick} (options {string.Join(",", options)}, deck {DeckCount})");
            return pick;
        }

        // ---- Selections ---------------------------------------------------------------------------------

        protected override async Task<IList<byte>?> SelectDeckCardAsync(SelectCardData state)
        {
            var selectable = state.SelectableCards ?? Array.Empty<DynamicCard>();
            if (selectable.Length == 0)
                return null;

            bool All(CardLocation location) =>
                selectable.All(card => card.CurrentCoordinates.Location == (byte)location && card.CurrentCoordinates.Controller == ChainTracker.Bot);
            var hint = (DHint)state.SelectionHint;
            var hand = await GetBotHandCardsAsync();
            var board = await BuildBoardAsync();

            // Discards and costs from our hand.
            if (All(CardLocation.Hand) && hint is DHint.DISCARD or DHint.TOGRAVE)
            {
                if (_pendingEffect == Tragoedia && _tragoediaDiscard != 0)
                {
                    var sent = PickInOrder(state, new[] { _tragoediaDiscard });
                    if (sent != null)
                        return sent;
                }
                var order = _pendingEffect == SolarRecharge ? SolarDiscardOrder(hand) : DiscardOrder(hand);
                var discard = PickInOrder(state, order);
                if (discard != null)
                {
                    Log($"Discarding {string.Join(",", discard.Select(i => selectable[i].ID))}{(_pendingEffect != 0 ? $" (for {_pendingEffect})" : "")}");
                    _pendingEffect = 0;
                    return discard;
                }
            }

            // Charge of the Light Brigade's search.
            if (All(CardLocation.Deck) && hint == DHint.ATOHAND)
                return Picked("Charge search", PickInOrder(state, ChargeSearchOrder(board)), selectable);

            // Foolish Burial.
            if (All(CardLocation.Deck) && hint == DHint.TOGRAVE)
                return Picked("Foolish Burial", PickInOrder(state, FoolishOrder(board)), selectable);

            // Pot of Avarice: 5 monsters back to the deck.
            if (All(CardLocation.Grave) && hint == DHint.TODECK)
                return Picked("Pot of Avarice returns", AvaricePicks(state), selectable);

            // Lumina's revive.
            if (All(CardLocation.Grave) && hint == DHint.SPSUMMON)
            {
                int target = LuminaTarget(selectable.ToList(), board);
                if (target != 0)
                    return Picked("Lumina revives", PickInOrder(state, new[] { target }), selectable);
            }

            // Monster Reincarnation: Judgment Dragon back, else Lumina.
            if (All(CardLocation.Grave) && hint == DHint.ATOHAND)
                return Picked("Monster Reincarnation", PickInOrder(state, new[] { JudgmentDragon, Lumina, Celestia, Jain, Lyla }), selectable);

            // Tragoedia's steal: the monster chosen when activating.
            if (hint == DHint.CONTROL && _tragoediaTarget.HasValue)
            {
                var target = _tragoediaTarget.Value;
                _tragoediaTarget = null;
                _pendingEffect = 0;
                int index = Array.FindIndex(selectable, card => card.CurrentCoordinates.Controller == ChainTracker.Opponent &&
                    card.CurrentCoordinates.Sequence == target.CurrentCoordinates.Sequence);
                if (index >= 0)
                    return new List<byte> { (byte)index };
            }

            // Ryko, Celestia and Lyla break their backrow first (Javier: clear it before Judgment Dragon).
            if (_pendingEffect is Ryko or Celestia or Lyla && hint is DHint.DESTROY or DHint.TARGET)
            {
                int wanted = _pendingEffect == Celestia ? Math.Max(state.Minimum, Math.Min(2, state.Maximum)) : Math.Max(state.Minimum, 1);
                var picks = selectable
                    .Select((card, index) => new { Card = card, Index = index })
                    .Where(x => x.Card.CurrentCoordinates.Controller == ChainTracker.Opponent)
                    .Where(x => x.Card.CurrentCoordinates.Location != (byte)CardLocation.SpellZone || !x.Card.IsFaceup() || IsLastingCard(x.Card))
                    .OrderByDescending(x => x.Card.CurrentCoordinates.Location == (byte)CardLocation.SpellZone && !x.Card.IsFaceup())
                    .ThenByDescending(x => (x.Card.Type & CardKnowledge.TokenType) == 0)
                    .ThenByDescending(x => RemovalValue(x.Card))
                    .Take(wanted)
                    .Select(x => (byte)x.Index)
                    .ToList();
                if (picks.Count >= Math.Max(state.Minimum, 1))
                {
                    Log($"{_pendingEffect} destroys {string.Join(",", picks.Select(i => selectable[i].ID))} (backrow first)");
                    _pendingEffect = 0;
                    return picks;
                }
            }

            return null;
        }

        private IList<byte>? Picked(string what, IList<byte>? picks, DynamicCard[] selectable)
        {
            if (picks != null)
            {
                Log($"{what}: {string.Join(",", picks.Select(i => selectable[i].ID))}");
                _pendingEffect = 0;
            }
            return picks;
        }

        // Takes the first cards of the list (in order, one index each), then fills up to the minimum with the lowest hand value.
        private IList<byte>? PickInOrder(SelectCardData state, IEnumerable<int> order)
        {
            var selectable = state.SelectableCards ?? Array.Empty<DynamicCard>();
            int wanted = Math.Max(state.Minimum, 1);
            var picks = new List<byte>();
            foreach (int id in order)
            {
                if (picks.Count >= wanted)
                    break;
                int index = Enumerable.Range(0, selectable.Length).FirstOrDefault(i => selectable[i].IsCode(id) && !picks.Contains((byte)i), -1);
                if (index >= 0)
                    picks.Add((byte)index);
            }
            if (picks.Count == 0)
                return null;
            foreach (int i in Enumerable.Range(0, selectable.Length).OrderBy(i => HandValue(selectable[i])))
            {
                if (picks.Count >= wanted)
                    break;
                if (!picks.Contains((byte)i))
                    picks.Add((byte)i);
            }
            return picks;
        }

        // Solar Recharge: Wulf first, then the Lightsworn repeated in hand (one whose name isn't in the GY yet adds a name),
        // then the ones missing from the GY. Never Lumina unless it's the only one.
        private int[] SolarDiscardOrder(List<DynamicCard> hand)
        {
            var inHand = LightswornMonsters.Where(id => id != Wulf && id != Lumina && InHand(hand, id)).ToList();
            int CopiesInHand(int id) => hand.Count(card => card.IsCode(id));
            return new[] { Wulf }
                .Concat(inHand.Where(id => CopiesInHand(id) >= 2 && GraveCopies(id) == 0))
                .Concat(inHand.Where(id => GraveCopies(id) == 0))
                .Concat(inHand.Where(id => CopiesInHand(id) >= 2))
                .Concat(inHand.OrderByDescending(GraveCopies))
                .Concat(new[] { Lumina })
                .ToArray();
        }

        // Other discards (Lumina, Monster Reincarnation): Wulf, Plaguespreader, Gardna, then a Lightsworn the GY is missing
        // or that's repeated, then the rest by hand value.
        private int[] DiscardOrder(List<DynamicCard> hand)
        {
            var lightsworn = LightswornMonsters.Where(id => id != Lumina && InHand(hand, id)).ToList();
            return new[] { Wulf, Plaguespreader, NecroGardna }
                .Concat(lightsworn.Where(id => GraveCopies(id) == 0 && id != Ryko))
                .Concat(lightsworn.Where(id => hand.Count(card => card.IsCode(id)) >= 2))
                .ToArray();
        }

        // Charge: Lyla against their backrow, Lumina when there's something to revive, else a name the GY is missing.
        private int[] ChargeSearchOrder(Board board)
        {
            var order = new List<int>();
            if (HasBackrow(board))
                order.Add(Lyla);
            if (OwnGrave.Any(id => new[] { Jain, Wulf, Garoth, Lyla, Ehren, Druid }.Any(t => CardIdentity.Same(id, t))))
                order.Add(Lumina);
            order.AddRange(new[] { Jain, Garoth, Ehren, Lyla, Druid, Ryko, Lumina }
                .Where(id => GraveCopies(id) == 0 && !InHand(board.Hand, id)));
            order.AddRange(new[] { Lyla, Jain, Ryko, Lumina, Garoth, Ehren, Druid, Wulf });
            return order.ToArray();
        }

        // Foolish: Gardna to protect a Lightsworn for Celestia's tribute next turn, then Wulf (it summons itself from
        // the deck), then a name the GY is missing.
        private int[] FoolishOrder(Board board)
        {
            var order = new List<int>();
            if (InHand(board.Hand, Celestia) && board.OwnMonsters.Any(card => card.IsFaceup() && card.IsCode(LightswornMonsters)))
                order.Add(NecroGardna);
            order.Add(Wulf);
            order.AddRange(LightswornMonsters.Where(id => GraveCopies(id) == 0));
            order.AddRange(new[] { NecroGardna, Plaguespreader });
            order.AddRange(LightswornMonsters);
            return order.ToArray();
        }

        // Pot of Avarice: repeated copies first, then the other monsters (Judgment Dragon can go back too), keeping
        // one copy of each name; Gardna and Plaguespreader stay unless nothing else is left.
        private IList<byte>? AvaricePicks(SelectCardData state)
        {
            var selectable = state.SelectableCards ?? Array.Empty<DynamicCard>();
            int wanted = Math.Max(state.Minimum, 5);
            var keptName = new HashSet<int>();
            var spare = new List<(int Index, int Rank)>();
            for (int i = 0; i < selectable.Length; i++)
            {
                var card = selectable[i];
                int name = Array.Find(LightswornMonsters, id => card.IsCode(id));
                if (card.IsCode(NecroGardna, Plaguespreader))
                    spare.Add((i, 4));
                else if (name != 0 && keptName.Add(name))
                    spare.Add((i, 3));
                else if (name != 0)
                    spare.Add((i, 0));
                else
                    spare.Add((i, card.IsCode(JudgmentDragon) ? 2 : 1));
            }
            var picks = spare.OrderBy(x => x.Rank).Take(Math.Min(wanted, selectable.Length)).Select(x => (byte)x.Index).ToList();
            return picks.Count >= state.Minimum ? picks : null;
        }
    }
}
