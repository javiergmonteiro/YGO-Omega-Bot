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

        // Roughly what each End Phase miller sends from the deck.
        private static int MillAmount(int cardId) =>
            CardIdentity.Same(cardId, JudgmentDragon) || CardIdentity.Same(cardId, Celestia) ? 4 :
            CardIdentity.Same(cardId, Jain) || CardIdentity.Same(cardId, Druid) ? 2 :
            EndPhaseMillers.Any(id => CardIdentity.Same(cardId, id)) ? 3 : 0;

        private static int EndPhaseMill(Board board) =>
            board.OwnMonsters.Where(card => card.IsFaceup() && !card.IsDisabled()).Sum(card => MillAmount(card.ID));

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
        private readonly HashSet<int> _passedSummon = new();
        private HashSet<int> _setZonesSeen = new();
        private HashSet<string> _ownMonstersSeen = new();
        private int _trackTurn = -1;
        private int _roarTurn = -1;
        private Func<DynamicCard, bool> _avoidedTarget = _ => false;
        private int _lylaSeenTurn = -1;
        private HashSet<int> _lylaFromLastTurn = new();

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
            _passedSummon.Clear();
            _setZonesSeen = new();
            _ownMonstersSeen = new();
            _trackTurn = -1;
            _roarTurn = -1;
            _lylaSeenTurn = -1;
            _lylaFromLastTurn = new();
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

        // Ryko is flipped only by its own rule (they have cards to hit): into an empty field it destroys ours (Javier).
        protected override int[] ExcludedFromGenericFlip { get; } = { Ryko };

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
            if (card.IsCode(CardTrooper))
                cost += 5000;
            // A face-up Druid keeps our Lightsworn untargetable (Icarus, Javier: "ojo con Druid e Icarus"): tribute it last.
            if (card.IsCode(Druid) && card.IsFaceup())
                cost += 2000;
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

        // Optional mill spells (Solar Recharge, Charge) only while the deck stays above the caution line (15), so
        // Judgment Dragon still has room for its End Phase mill: in one turn they took the deck from 23 to 7 and JD
        // never came down. With Judgment Dragon in hand and 4 names already, no more milling is needed.
        private bool MillKeepsCaution(int cards, Board board)
        {
            if (OurLethal(board))
                return true;
            if (InHand(board.Hand, JudgmentDragon) && GraveNames() >= 4)
                return false;
            return DeckCount - cards > DeckCaution;
        }

        // A set card that may be Bottomless or Torrential: Judgment Dragon waits for it to be cleared.
        // Set cards that already let one of our summons through don't count (see TrackUnansweredSummons).
        private bool SummonTrapPossible(Board board) =>
            board.EnemySpells.Any(card => !card.IsFaceup() && !_passedSummon.Contains(card.CurrentCoordinates.Sequence)) &&
            OpponentMayStillHave(board, SummonPunishers);

        // Javier: "baje y no uso bottomless asi que baja la probabilidad de que lo tenga". When one of our monsters with
        // 1500+ ATK appears and their set cards stay the same, those set cards let it through: probably not Bottomless.
        // Any change in their set cards forgets it (a zone may have been reused).
        private void TrackUnansweredSummons(Board board)
        {
            var setZones = board.EnemySpells.Where(card => !card.IsFaceup()).Select(card => (int)card.CurrentCoordinates.Sequence).ToHashSet();
            var ownMonsters = board.OwnMonsters.Where(card => card.IsFaceup() && card.Attack >= 1500)
                .Select(card => $"{card.CurrentCoordinates.Sequence}:{card.ID}").ToHashSet();

            if (!setZones.SetEquals(_setZonesSeen))
                _passedSummon.Clear();
            else if (IsBotTurn && _trackTurn == CurrentHeader.CurrentTurn && setZones.Count > 0 && !ownMonsters.IsSubsetOf(_ownMonstersSeen) &&
                !setZones.IsSubsetOf(_passedSummon))
            {
                _passedSummon.UnionWith(setZones);
                Log($"Their {setZones.Count} set card(s) let our summon through: probably not Bottomless");
            }

            _setZonesSeen = setZones;
            _ownMonstersSeen = ownMonsters;
            _trackTurn = CurrentHeader.CurrentTurn;
        }

        // Lylas already on the field when our turn started (zones), read at our first Main Phase decision.
        private void TrackLylaFromLastTurn(Board board)
        {
            if (!IsBotTurn || _lylaSeenTurn == CurrentHeader.CurrentTurn)
                return;
            _lylaSeenTurn = CurrentHeader.CurrentTurn;
            _lylaFromLastTurn = board.OwnMonsters.Where(card => card.IsCode(Lyla) && card.IsFaceup())
                .Select(card => (int)card.CurrentCoordinates.Sequence).ToHashSet();
        }

        private static bool HasBackrow(Board board) =>
            board.EnemySpells.Any(card => !card.IsFaceup() || IsLastingCard(card));

        // Ryko and Card Trooper are there to trade 1-for-1 (Card Trooper's draw is part of the deal): no Gardna, Honest
        // or Mirror Force to save them, and Trooper is no synchro material unless it wins or saves the game (Javier).
        private static readonly int[] Sacrificable = { Ryko, CardTrooper };

        // A face-up Destiny HERO - Plasma negates our monsters' effects: Lyla, Ryko, Lumina, Judgment Dragon do nothing.
        private static bool PlasmaNegates(Board board) =>
            board.EnemyMonsters.Any(card => card.IsCode(CardKnowledge.DestinyHeroPlasma) && card.IsFaceup() && !card.IsDisabled());

        // Lyla's effect only when it can land: backrow to hit, no Stardust to negate it, no Plasma (Javier).
        // Nor into a Tytannial with Plants to tribute: she'd negate it for free (Javier: nothing that targets).
        private bool LylaCanBreak(Board board) => HasBackrow(board) && !board.StardustBlocking && !PlasmaNegates(board) && !board.TargetingBlocked;

        // Javier: with one lone set card, a face-up attack Lyla attacks first (directly into an empty field, or into
        // their only monster when it's face-down) to draw out a Prison / Mirror Force; if she survives, her effect in
        // Main 2. With 2+ set cards she never attacks first: her effect goes in Main 1.
        // Only a Lyla summoned this turn: one that survived from last turn uses her effect first, before we summon
        // anything else (a Torrential took her and Jain together, 2026-10-25).
        private bool LylaAttacksFirst(Board board)
        {
            if (!IsBotTurn || !board.IsMain1 ||
                !board.OwnMonsters.Any(card => card.IsCode(Lyla) && card.IsFaceup() && card.IsAttack() && !card.Attacked &&
                    !_lylaFromLastTurn.Contains(card.CurrentCoordinates.Sequence)))
                return false;
            if (board.EnemySpells.Count(card => !card.IsFaceup()) != 1)
                return false;
            return board.EnemyMonsters.Count == 0 || (board.EnemyMonsters.Count == 1 && !board.EnemyMonsters[0].IsFaceup());
        }

        // Javier: with a set monster of theirs and 2+ set cards, Lyla's effect waits until their summon window passed
        // (no priority): a Bottomless has to show itself first. With no set monster, the effect goes with priority and
        // forces their summon traps.
        private static bool LylaWaitsForSummonWindow(Board board) =>
            board.EnemyMonsters.Any(card => !card.IsFaceup()) && board.EnemySpells.Count(card => !card.IsFaceup()) >= 2;

        private bool LylaEffectNow(Board board) => LylaCanBreak(board) && !LylaAttacksFirst(board);

        // Cards we can spend on a cost without losing anything: Wulf (dead in hand), Gardna, Plaguespreader, or a
        // Lightsworn whose name is already in the GY (not Lumina). Pot of Avarice, Charge, Judgment Dragon... are not.
        private bool Expendable(DynamicCard card) =>
            card.IsCode(Wulf, NecroGardna, Plaguespreader) ||
            (card.IsCode(LightswornMonsters) && !card.IsCode(Lumina) && GraveCopies(card.ID) > 0);

        // Why Judgment Dragon comes down (Javier): it gives lethal, or its wipe clears their field. Otherwise only mid-game
        // (never on our first turn) and with no set card of theirs (Dimensional Prison banished it into a backrow).
        // Outside lethal it waits for Main 2.
        private string? JudgmentReason(Board board)
        {
            if (PlasmaNegates(board))
                return null;
            if (OurLethal(board, 3000))
                return "it gives lethal";
            // Javier: against Tytannial, wait for the 4 names (face-up millers help) and JD's wipe, which she can't negate.
            if (IsTytannialActive(board.EnemyMonsters) && CurrentField.OwnSide.LifePoints > 1000)
                return "its wipe answers Tytannial (no targeting)";
            int theirs = board.EnemyMonsters.Count + board.EnemySpells.Count;
            int ours = board.OwnMonsters.Count + board.OwnSpells.Count;
            bool wipe = WipeBehind(board) >= 1 && CurrentField.OwnSide.LifePoints >= 2000 && !board.StardustBlocking;

            // Stardust negates the wipe: JD comes down in Main 1 to attack it; if it passes, the wipe in Main 2 (Javier).
            bool stardustToHit = board.StardustBlocking &&
                board.EnemyMonsters.Any(card => card.IsFaceup() && card.IsCode(StardustDragon) && card.GetRealPower() < 3000);
            if (board.IsMain1 && stardustToHit && !SummonTrapPossible(board))
                return "attacks Stardust (its wipe waits for Main 2)";

            // Two Judgment Dragons (Javier): the first comes down in Main 1 and wipes, the second follows and both attack.
            bool judgmentUp = board.OwnMonsters.Any(card => card.IsCode(JudgmentDragon) && card.IsFaceup());
            if (board.IsMain1 && !judgmentUp && SecondJudgmentReady(board) && theirs - ours >= 2 &&
                CurrentField.OwnSide.LifePoints > 1000 && !board.StardustBlocking)
                return "the first of two: it wipes, the second follows and both attack";
            if (board.IsMain1 && judgmentUp && board.EnemyMonsters.Count == 0)
                return "joins the other Judgment Dragon's attack on an empty field";

            // Main 1 when it wipes (Javier: "lo bajó en M2 no M1": the wipe first, then JD attacks on a clear field);
            // otherwise Main 2, after the battle showed their backrow.
            if (board.IsMain1)
                return wipe ? (3000 >= CurrentField.EnemySide.LifePoints ? "its wipe and a direct hit win" : "its wipe clears their field, then it attacks") : null;
            if (wipe)
                return "its wipe clears their field (Main 2)";
            if (CurrentHeader.CurrentTurn > 2 && !board.EnemySpells.Any(card => !card.IsFaceup()) && board.EnemyMonsters.Count > 0)
                return "mid-game, no backrow (Main 2)";
            return null;
        }

        // Cards they have more than we do, for Judgment Dragon's wipe. Ryko and Card Trooper don't count as ours: losing
        // them costs nothing (Trooper even draws).
        private static int WipeBehind(Board board) =>
            board.EnemyMonsters.Count + board.EnemySpells.Count -
            board.OwnMonsters.Count(card => !card.IsCode(JudgmentDragon) && !card.IsCode(Sacrificable)) - board.OwnSpells.Count;

        // Judgment Dragon's wipe: when they have more cards than we do on the field (Javier: "hay que usarlo mas para
        // eliminar amenazas, principalmente la backrow", first the wipe, then the deploy).
        private bool JudgmentWipeWorthIt(Board board)
        {
            int lifePoints = CurrentField.OwnSide.LifePoints;
            if (lifePoints <= 1000 || board.StardustBlocking || PlasmaNegates(board))
                return false;

            // Javier: Gorz answered our attack: in Main 2 the wipe goes (again) and clears Gorz and its token, whatever
            // it costs us (the 3000 DEF token walled two Judgment Dragons for two turns, 2026-10-27). Only when nothing
            // of ours beats them: against a 2100 token it wiped 4 of ours while JD and Wulf could handle it.
            int ourBest = board.OwnMonsters.Where(card => card.IsFaceup()).Select(card => card.Attack).DefaultIfEmpty(0).Max();
            // Javier: the wipe doesn't target, so it's the clean answer to a Tytannial we can't beat in battle.
            if (IsBotTurn && board.EnemyMonsters.Any(card => card.IsCode(CardKnowledge.TytannialPrincessOfCamellias) && card.IsFaceup() &&
                    !card.IsDisabled() && card.GetRealPower() >= ourBest))
                return true;
            if (IsBotTurn && !board.IsMain1 &&
                board.EnemyMonsters.Any(card => card.IsCode(Gorz, GorzToken) && card.GetRealPower() >= ourBest))
                return true;
            if (WipeBehind(board) < 1)
                return false;

            // Below 2000 LP only when it wins or keeps us alive.
            if (lifePoints < 2000)
            {
                int ourAttack = board.OwnMonsters.Where(card => card.IsCode(JudgmentDragon) && card.IsFaceup() && card.IsAttack() && !card.Attacked)
                    .Sum(card => card.Attack);
                bool winsNow = IsBotTurn && board.IsMain1 && ourAttack >= CurrentField.EnemySide.LifePoints;
                return winsNow || OpponentHasLethal(board) || SecondJudgmentReady(board);
            }
            return true;
        }

        // A second Judgment Dragon in hand that can follow the first one this Main 1 (4+ names in the GY).
        private bool SecondJudgmentReady(Board board) =>
            IsBotTurn && board.IsMain1 && GraveNames() >= 4 && InHand(board.Hand, JudgmentDragon) &&
            board.Hand.Count(card => card.IsCode(JudgmentDragon)) +
            board.OwnMonsters.Count(card => card.IsCode(JudgmentDragon) && card.IsFaceup()) >= 2;

        // Card Trooper's mill: not at 15 cards or fewer (it milled at 5, 3 and 1 cards and we lost by deck-out), unless
        // our attackers already finish them. Main 2 too: a Trooper stolen in battle (Goyo) only becomes ours there.
        private bool TrooperMills(Board board)
        {
            if (!IsBotTurn)
                return false;
            bool use = (DeckCount > DeckCaution && CanMill(1, board)) || OurLethal(board);
            if (!use)
                LogOnChange($"Card Trooper's mill skipped: {DeckCount} cards left");
            return use;
        }

        // Wulf on the field is just a 2100 body: it trades on equal ATK (Javier: Wulf into the Gorz token, not JD's wipe).
        protected override int[] OneSetCopyAtATime => new[] { ThreateningRoar };

        protected override bool TradesOnTie(DynamicCard attacker, DynamicCard target) =>
            attacker.IsCode(Wulf) || (attacker.IsCode(GoyoGuardian) && target.IsCode(CardKnowledge.TytannialPrincessOfCamellias));

        // Javier's plan against a face-up Tytannial (she negates every targeting effect by tributing a Plant, herself
        // included): Ryko (set it, Charge for it) breaks her one Plant at a time; with Plaguespreader, Catastor (a level 3)
        // or a Goyo trade; otherwise one monster only (a LIGHT in attack bluffs Honest and mills, or a face-down one
        // bluffs Ryko). Lethal through her ignores all of this.
        private bool TytannialPlan(Board board) =>
            IsBotTurn && IsTytannialActive(board.EnemyMonsters) && !OurLethal(board);

        // A stolen monster (Goyo's Fortress) plus one more of ours tribute Celestia, which breaks 2 of their cards.
        protected override bool CanTributeStolenMonster(List<DynamicCard> hand, List<DynamicCard> ownMonsters) =>
            InHand(hand, Celestia) && ownMonsters.Count >= 1;

        // ---- Main Phase ---------------------------------------------------------------------------------

        protected override bool CanUseMonsterEffect(int cardId, Board board, List<DynamicCard> monsterTargets) => cardId switch
        {
            Lyla => LylaEffectNow(board),
            JudgmentDragon => JudgmentWipeWorthIt(board),
            // Lumina with something to revive discards whatever it takes, except what we'd use right away (Judgment
            // Dragon, Javier).
            Lumina => LuminaTarget(board.Graveyard, board) != 0 && !PlasmaNegates(board) &&
                board.Hand.Any(card => !card.IsCode(Lumina, JudgmentDragon) && !(board.TargetingBlocked && card.IsCode(Honest))),
            Tragoedia => FindTragoediaSteal(board).HasValue,
            CardTrooper => TrooperMills(board),
            // Back to the hand in Main Phase 2, after hitting (Javier: "se pega en Main 1 y en Main 2 se vuelve a la mano").
            // Below 3000 LP it stays as a 1900 DEF wall.
            Honest => IsBotTurn && !board.IsMain1 && CurrentField.OwnSide.LifePoints >= 3000,
            _ => true
        };

        protected override async Task<IdleCmdResponse?> DecideMainPhaseAsync(DecisionContext<IdleCmdData> context, Board board)
        {
            _avoidedTarget = await AvoidedMonsterEffectTargetsAsync(destroying: true);
            return DecideMainPhase(context, board);
        }

        private IdleCmdResponse? DecideMainPhase(DecisionContext<IdleCmdData> context, Board board)
        {
            var state = context.StateData;
            var hand = board.Hand;
            _celestiaSummoning = false;

            TrackUnansweredSummons(board);
            TrackLylaFromLastTurn(board);

            string deckLog = $"Deck {DeckCount} cards, {GraveNames()} Lightsworn names in GY";
            if (deckLog != _lastDeckLog)
                Log(deckLog);
            _lastDeckLog = deckLog;

            // Ryko set last turn: flip it first thing, breaking their backrow, before anything else (Javier).
            // Not into Plasma, and not when they have no card (it would hit ours). Into Tytannial yes: she has to spend
            // a Plant (herself when alone) to negate it, one by one (Javier).
            if (IsBotTurn && board.IsMain1 && TryGetReposable(state, Ryko, out var ryko) && !ryko.IsFaceup() && CanMill(3, board) &&
                board.EnemyMonsters.Count + board.EnemySpells.Count > 0 && !PlasmaNegates(board))
            {
                Log("Flip summoning Ryko");
                return ryko.Reposition();
            }

            // Draws and mills next: they only leave cards in the GY, so they never feed Gorz's condition.
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

            // Lyla breaks a spell/trap (she goes to defense).
            if (LylaEffectNow(board) && TryActivate(state, Lyla, false, out var lyla))
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

            // Celestia: tribute a Lightsworn, destroy up to 2 of their cards. Not into any face-up Tytannial: she negates
            // it even alone, tributing herself (2026-10-24 with a Fluff Token, 2026-10-27 alone). Needs a target worth
            // hitting: not a Fortress that costs us a discard, not a monster a Gearframe protects (Javier).
            // Javier: into an active Stardust (it negates the effect and destroys her), only as a body that keeps us ahead
            // in monsters, without using her effect.
            bool celestiaEffect = !board.StardustBlocking && board.EnemyMonsters.Concat(board.EnemySpells).Any(card => !_avoidedTarget(card)) &&
                CanMill(4, board) && !board.TargetingBlocked && !IsTytannialActive(board.EnemyMonsters);
            // Or to attack Stardust with Honest in hand (Celestia is LIGHT: 2300 + Stardust's 2500).
            bool honestIntoStardust = board.IsMain1 && InHand(board.Hand, Honest) &&
                board.EnemyMonsters.Any(card => card.IsCode(StardustDragon) && card.IsFaceup() && card.IsAttack());
            bool celestiaBody = board.StardustBlocking && (board.OwnMonsters.Count > board.EnemyMonsters.Count || honestIntoStardust);
            if (IsBotTurn && (celestiaEffect || celestiaBody) &&
                board.OwnMonsters.Any(card => card.IsCode(LightswornMonsters)) &&
                TryGetSummonable(state, Celestia, out var celestia))
            {
                Log(celestiaEffect ? "Tribute summoning Celestia to destroy their cards" : "Tribute summoning Celestia as a body (Stardust up: no effect)");
                _celestiaSummoning = true;
                return celestia.NormalSummon();
            }

            // The wipe before another Judgment Dragon comes down: it would send the second one to the GY too.
            if (JudgmentWipeWorthIt(board) && TryActivate(state, JudgmentDragon, board.StardustBlocking, out var wipe))
            {
                Log(board.EnemyMonsters.Any(card => card.IsCode(Gorz, GorzToken)) && !board.IsMain1
                    ? "Judgment Dragon's effect: Gorz / its token walls us, wipe in Main 2"
                    : "Judgment Dragon's effect: they have more cards on the field");
                return wipe;
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
                else if (JudgmentReason(board) == null && !lastResort)
                    LogOnChange("Judgment Dragon waits: no lethal, nothing to wipe, and not a mid-game turn into an empty backrow");
                else
                {
                    Log($"Special summoning Judgment Dragon ({GraveNames()} names): {(lastResort ? "only play against their lethal" : JudgmentReason(board))}");
                    return judgment.SpecialSummon();
                }
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

            if (TrySynchroSummon(state, board, out var synchro))
                return synchro;

            // Plaguespreader back from the GY (a card from hand goes on top of the deck) for a synchro that pays off now.
            if (GraveCopies(Plaguespreader) > 0 && hand.Count > 0 && PlaguespreaderPlan(board) &&
                TryActivate(state, Plaguespreader, false, out var plague))
            {
                Log("Plaguespreader from the GY for a synchro");
                return plague;
            }

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
                bool farBehind = FarBehind(board);
                // Javier: "tenia pot of avarice y no lo uso siendo su unica carta en la mano?": our last card with an empty
                // field goes no matter the names.
                bool lastCard = hand.Count == 1 && board.OwnMonsters.Count + board.OwnSpells.Count == 0;
                bool forced = DeckCount <= DeckCaution || lifePoints <= 2000 || GraveCopies(JudgmentDragon) >= 2 || farBehind || lastCard;
                if ((forced || (AvariceKeepsNames() && AvariceWorthIt())) && TryActivate(state, PotOfAvarice, false, out var avarice))
                {
                    Log($"Pot of Avarice{(farBehind ? " (far behind: 3500+ LP and 3+ cards)" : lastCard ? " (our only card, empty field)" : forced ? " (deck low, LP low, or both Judgment Dragons in the GY)" : "")}");
                    _pendingEffect = PotOfAvarice;
                    return avarice;
                }
            }

            // Solar Recharge: draw 2, mill 2. Needs a Lightsworn to discard (never Lumina unless it's the only one).
            if (CanMill(4, board) && MillKeepsCaution(2, board) && hand.Any(card => card.IsCode(LightswornMonsters)) &&
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

            // Foolish only with a clear plan for this turn (Javier).
            string? foolishPlan = FoolishPlan(board);
            if (foolishPlan != null && TryActivate(state, FoolishBurial, false, out var foolish))
            {
                Log($"Foolish Burial: {foolishPlan}");
                _pendingEffect = FoolishBurial;
                return foolish;
            }

            return null;
        }

        // Pot of Avarice returns 5 monsters. Without breaking the names: the extra copies and the other monsters
        // (Gardna and Plaguespreader stay), plus any names above 4.
        // Javier: Pot of Avarice only when the GY holds monsters worth having back (Judgment Dragon, Honest, Gorz...);
        // "no uso pot porque no me interesa recuperar nada".
        private static readonly int[] WorthRecovering = { JudgmentDragon, Honest, Gorz, Tragoedia, Celestia, Lumina, CardTrooper };

        private bool AvariceWorthIt()
        {
            int worth = WorthRecovering.Sum(GraveCopies);
            if (worth < 2)
                LogOnChange($"Pot of Avarice waits: nothing worth recovering ({worth} key monster(s) in the GY)");
            return worth >= 2;
        }

        // Far behind (Javier): 3500+ LP under them and 3+ cards fewer on the field. Pot of Avarice no matter what.
        private bool FarBehind(Board board) =>
            CurrentField.EnemySide.LifePoints - CurrentField.OwnSide.LifePoints >= 3500 &&
            (board.EnemyMonsters.Count + board.EnemySpells.Count) - (board.OwnMonsters.Count + board.OwnSpells.Count) >= 3;

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
            // Against Tytannial, only to bring a level 3/4 for a Plaguespreader synchro (Catastor / Goyo).
            if (TytannialPlan(board) && board.OwnMonsters.Count > 1)
            {
                LogOnChange("Lumina waits: Tytannial up, no more bodies on the field");
                return null;
            }
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

            if (TytannialPlan(board))
            {
                if (!ownMonsters.Any(card => card.IsCode(Ryko) && !card.IsFaceup()) && TryGetMonsterSettable(state, Ryko, out var rykoTyt))
                {
                    Log("Setting Ryko: against Tytannial it makes her spend a Plant");
                    return rykoTyt.SetMonster();
                }
                if (ownMonsters.Count > 0)
                {
                    LogOnChange("Tytannial up and no Ryko: one monster on the field is enough, no more summons");
                    return null;
                }
            }

            // Lyla first when she can break their backrow now (Javier: clear it as soon as possible).
            if (LylaCanBreak(board) && TryGetSummonable(state, Lyla, out var lylaNow))
            {
                Log("Normal summon: Lyla, clears their backrow before anything else");
                return lylaNow.NormalSummon();
            }

            // Going second (Javier): Card Trooper first to clear their monsters; into an empty field on our first turn,
            // Druid attacks and mills instead of a slow Ryko.
            bool wentFirst = (IsBotTurn ? CurrentHeader.CurrentTurn : CurrentHeader.CurrentTurn + 1) % 2 == 1;
            if (!wentFirst && board.EnemyMonsters.Count > 0 && DeckCount > DeckCaution && TryGetSummonable(state, CardTrooper, out var trooperFirst))
            {
                Log("Normal summon: Card Trooper (going second, it clears)");
                return trooperFirst.NormalSummon();
            }
            // Against 2+ set cards the cheap bait goes: Card Trooper, while Lumina waits (Bottomless on what it revives,
            // Icarus, Javier).
            if (board.EnemySpells.Count(card => !card.IsFaceup()) >= 2 && DeckCount > DeckCaution &&
                TryGetSummonable(state, CardTrooper, out var trooperBait))
            {
                Log("Normal summon: Card Trooper (cheap bait into 2+ set cards, Lumina waits)");
                return trooperBait.NormalSummon();
            }
            bool druidOk = !hand.Any(card => card.IsCode(Lumina, MonsterReincarnation, PotOfAvarice));
            if (!wentFirst && CurrentHeader.CurrentTurn == 2 && board.EnemyMonsters.Count == 0 && board.EnemySpells.Count == 0 && druidOk &&
                TryGetSummonable(state, Druid, out var druidFirst))
            {
                Log("Normal summon: Druid (going second into an empty field: attack and mill)");
                return druidFirst.NormalSummon();
            }

            // Ryko is no use into a face-up Dark End Dragon: it sends Ryko away before the flip (Javier).
            bool darkEndUp = board.EnemyMonsters.Any(card => card.IsFaceup() && card.IsCode(CardKnowledge.DarkEndDragon) && !card.IsDisabled());
            // Our attackers first: a set card could be Icarus and break the set Ryko plus an attacker (Javier).
            bool attackFirst = board.IsMain1 && board.EnemySpells.Any(card => !card.IsFaceup()) &&
                ownMonsters.Any(card => card.IsFaceup() && card.IsAttack() && !card.Attacked);

            // Ryko face-down first, with a Threatening Roar to hold the attacks (turn 1 ideal).
            if (!darkEndUp && !attackFirst && !ownMonsters.Any(card => card.IsCode(Ryko) && !card.IsFaceup()) &&
                TryGetMonsterSettable(state, Ryko, out var ryko))
            {
                Log("Setting Ryko");
                return ryko.SetMonster();
            }

            var order = new List<int>();
            // Going first without Ryko, Lyla on turn 1: next turn they must set a backrow she breaks (or none), and her
            // mill may hit Wulf (Javier).
            if (LylaCanBreak(board) || (wentFirst && CurrentHeader.CurrentTurn == 1))
                order.Add(Lyla);
            if (enemyFaceDown)
                order.Add(Ehren);
            order.AddRange(new[] { Jain, Garoth, CardTrooper, Lumina });
            // Druid stops our own GY targeting (Lumina, Monster Reincarnation, Pot of Avarice).
            // Then it only goes last, when nothing else can: an empty board is worse (Javier: "no seteo nada ni para defenderse").
            bool druidLast = hand.Any(card => card.IsCode(Lumina, MonsterReincarnation, PotOfAvarice));
            if (!druidLast)
                order.Add(Druid);
            order.AddRange(new[] { Lyla, Ehren });
            if (druidLast)
                order.Add(Druid);

            // With 15 cards or fewer, no more Lightsworn than needed: one already out is enough unless this one has a job.
            bool millerOut = ownMonsters.Any(card => card.IsFaceup() && card.IsCode(EndPhaseMillers));
            foreach (int cardId in order.Distinct())
            {
                if (!TryGetSummonable(state, cardId, out var card))
                    continue;

                bool hasJob = (cardId == Lyla && LylaCanBreak(board)) || (cardId == Ehren && enemyFaceDown) ||
                    (cardId == Lumina && CanUseMonsterEffect(Lumina, board, board.EnemyMonsters));
                bool mills = card.IsCode(EndPhaseMillers);
                if (cardId == CardTrooper && DeckCount <= DeckCaution && !OurLethal(board))
                {
                    if (TryGetMonsterSettable(state, CardTrooper, out var trooperSet))
                    {
                        Log($"Setting Card Trooper: {DeckCount} cards left, no more milling");
                        return trooperSet.SetMonster();
                    }
                    continue;
                }
                // Javier: Lumina reviving Ehren next to two Judgment Dragons milled 14 with 8 cards left.
                int endMill = EndPhaseMill(board) + MillAmount(cardId) + (cardId == Lumina && hasJob ? 3 : 0);
                if (mills && endMill >= DeckCount && !OurLethal(board, card.Attack))
                {
                    LogOnChange($"Holding {cardId}: the End Phase would mill {endMill} with {DeckCount} cards left");
                    continue;
                }
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
            int theirBestAttack = board.EnemyMonsters.Where(m => m.IsFaceup() && m.IsAttack()).Select(m => m.Attack).DefaultIfEmpty(0).Max();
            if (ownMonsters.Count == 0 && (CurrentField.OwnSide.LifePoints < 3000 || (board.EnemyMonsters.Count > 0 && theirBestAttack < 1900)) &&
                TryGetMonsterSettable(state, Honest, out var honestWall))
            {
                Log("Setting Honest as a wall (its 1900 DEF holds, or below 3000 LP)");
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
                return ShouldUseHonest(await GetEnemySpellCardsAsync(), await GetEnemyMonsterCardsAsync());

            if (card.IsCode(Ryko))
            {
                _pendingEffect = Ryko;
                return true;
            }

            if (card.IsCode(Lyla) && location == CardLocation.MonsterZone)
            {
                var lylaBoard = await BuildBoardAsync();
                if (IsBotTurn && CurrentHeader.LastChainPlayer == -1 && LylaWaitsForSummonWindow(lylaBoard))
                {
                    Log("Lyla's effect waits: no priority, their summon window first (set monster and 2+ set cards)");
                    return false;
                }
                bool use = LylaEffectNow(lylaBoard);
                if (use)
                    _pendingEffect = Lyla;
                else
                    Log("Lyla's effect skipped: no backrow, Stardust / Plasma would negate it, or she attacks first");
                return use;
            }

            if (card.IsCode(NecroGardna))
                return await ShouldUseGardnaAsync();

            if (card.IsCode(Celestia))
            {
                var board = await BuildBoardAsync();
                bool use = CanMill(4, board) && !board.StardustBlocking;
                if (use)
                    _pendingEffect = Celestia;
                else
                    Log(board.StardustBlocking ? "Celestia's effect skipped: Stardust would negate it" : $"Celestia's effect skipped: milling 4 would leave {DeckCount - 4} cards");
                return use;
            }

            // Threatening Roar: on their turn, once they head into battle with something that can hurt us.
            if (card.IsCode(MirrorForce))
            {
                var ownSide = CurrentField.OwnSide;
                if (!ownSide.HasBattlingMonster)
                    return null;
                var target = ownSide.BattlingMonster;
                bool fourthName = target.IsCode(LightswornMonsters) && GraveNames() == 3 && GraveCopies(target.ID) == 0;
                if (!target.IsCode(Sacrificable) && !fourthName)
                    return null;
                int theirAttackers = (await GetEnemyMonsterCardsAsync()).Count(monster => monster.IsFaceup() && monster.IsAttack());
                int ours = (await GetBotMonsterCardsAsync()).Count;
                bool outnumbered = theirAttackers - ours >= 2;
                Log($"Mirror Force on an attack into {target.ID}: {(outnumbered ? "yes, they have 2+ attackers more" : fourthName ? "no, its death brings the 4th name" : "no, it's a sacrificable 1-for-1")}");
                return outnumbered;
            }

            if (card.IsCode(ThreateningRoar))
            {
                // One per turn: a second Roar stops nothing more (it chained two in a row, 2026-10-27).
                if (IsBotTurn || _roarTurn == CurrentHeader.CurrentTurn)
                    return false;
                var phase = (DuelPhase)CurrentHeader.CurrentPhase;
                bool battle = phase is DuelPhase.BattleStart or DuelPhase.BattleStep;
                var enemyMonsters = await GetEnemyMonsterCardsAsync();
                bool threat = enemyMonsters.Any(monster => monster.IsFaceup() && monster.IsAttack() && monster.Attack > 0);
                // Javier: if it's about to be destroyed, chain it anyway (it died to a Heavy Storm unused). Heavy Storm
                // always takes it; an MST / Icarus may be aimed elsewhere, so only with an attacker to stop.
                var enemySpells = await GetEnemySpellCardsAsync();
                bool theirChain = CurrentHeader.LastChainPlayer == ChainTracker.Opponent;
                bool storm = theirChain && enemySpells.Any(spell => spell.IsFaceup() && spell.IsCode(CardKnowledge.HeavyStorm));
                bool removal = theirChain && enemySpells.Any(spell => spell.IsFaceup() &&
                    spell.IsCode(CardKnowledge.MysticalSpaceTyphoon, CardKnowledge.IcarusAttack));
                // Javier: a monster effect (Hyunlei) destroyed it unused too. Any of their plays in their Main 1 with an
                // attacker up: it stops this turn's attacks all the same.
                bool mainPlay = theirChain && threat && phase == DuelPhase.Main1;
                bool use = storm || (removal && threat) || mainPlay || (battle && threat && !Chain.AnsweredCurrentChain(CurrentHeader));
                if (use)
                {
                    _roarTurn = CurrentHeader.CurrentTurn;
                    Log($"Threatening Roar{(storm || removal || mainPlay ? " before it gets destroyed" : "")}");
                }
                return use;
            }

            return null;
        }

        // Necro Gardna only on an attack declaration, to save a key monster (Javier): Lumina, Judgment Dragon, or a
        // Lightsworn (Ryko included) when Celestia in hand wants it as tribute next turn. Also a direct attack that kills.
        private async Task<bool> ShouldUseGardnaAsync()
        {
            var enemySide = CurrentField.EnemySide;
            var ownSide = CurrentField.OwnSide;
            if (IsBotTurn || !enemySide.HasBattlingMonster || Chain.AnsweredCurrentChain(CurrentHeader))
            {
                LogOnChange("Necro Gardna waits for an attack declaration");
                return false;
            }
            // In the damage step (Ryko flipped by the attack) it's too late: the battle still destroys it and Gardna is
            // gone for nothing (2026-10-27).
            if ((DuelPhase)CurrentHeader.CurrentPhase is DuelPhase.Damage or DuelPhase.DamageCal)
            {
                Log("Necro Gardna skipped: the battle is already in the damage step");
                return false;
            }

            int attack = enemySide.BattlingMonster.Attack;
            if (!ownSide.HasBattlingMonster)
            {
                bool lethal = attack >= ownSide.LifePoints;
                Log($"Necro Gardna on a direct attack of {attack}: {(lethal ? "yes, it would kill us" : "no, not lethal")}");
                return lethal;
            }

            var target = ownSide.BattlingMonster;
            bool dies = attack >= target.GetRealPower();
            bool celestiaTribute = target.IsCode(LightswornMonsters) &&
                (CurrentField.OwnSide.Hand ?? Array.Empty<int>()).Any(id => CardIdentity.Same(id, Celestia));
            bool key = target.IsCode(Lumina, JudgmentDragon) || celestiaTribute;
            // Javier: when Honest would save it too, Gardna goes and Honest stays for a bigger threat.
            bool honestWouldGo = (CurrentField.OwnSide.Hand ?? Array.Empty<int>()).Any(id => CardIdentity.Same(id, Honest)) &&
                (target.Attribute & LightAttribute) != 0 && target.IsAttack() && !target.IsCode(Sacrificable);
            bool use = dies && (key || honestWouldGo);
            Log($"Necro Gardna on an attack into {target.ID}: {(use ? key ? "yes, key monster" : "yes, Honest stays in hand" : dies ? "no, not a key monster" : "no, it survives")}");
            return use;
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
            bool phaseOk = (DuelPhase)CurrentHeader.CurrentPhase is DuelPhase.BattleStart or DuelPhase.BattleStep or DuelPhase.Damage or DuelPhase.DamageCal or DuelPhase.Battle;
            if (phaseOk && biggerComing && !restKills)
            {
                Log($"Gorz waits: a bigger attacker ({waiting.Max(card => card.Attack)}) is still to come after {lastHit}");
                return false;
            }
            Log($"Gorz now (last hit {lastHit}, {waiting.Count} attackers left)");
            return true;
        }

        // Javier: going to defense tells them we hold no Honest. When its DEF wouldn't hold anyway (only the LP
        // difference is saved), a LIGHT attacker stays in attack while we're at 3000+ LP.
        protected override bool KeepsAttackAsBluff(DynamicCard card, int strongestEnemyAttack) =>
            (card.Attribute & LightAttribute) != 0 && card.Defense < strongestEnemyAttack && CurrentField.OwnSide.LifePoints >= 3000;

        // Honest in the Damage Step: when it turns the battle (we'd lose or bounce without it).
        private bool ShouldUseHonest(List<DynamicCard> enemySpells, List<DynamicCard> enemyMonsters)
        {
            var ownSide = CurrentField.OwnSide;
            var enemySide = CurrentField.EnemySide;
            if (!ownSide.HasBattlingMonster || !enemySide.HasBattlingMonster)
                return false;

            var ours = ownSide.BattlingMonster;
            var theirs = enemySide.BattlingMonster;
            if ((ours.Attribute & LightAttribute) == 0 || theirs.Attack <= 0)
                return false;

            // Catastor destroys our LIGHT monster by its effect at the start of the Damage Step: Honest saves nothing (Javier).
            if (theirs.IsFaceup() && !theirs.IsDisabled() && theirs.IsCode(CardKnowledge.DestroysNonDarkInBattle))
            {
                Log("Honest skipped: Catastor destroys ours by its effect anyway");
                return false;
            }

            // A Machina Fortress with Gearframe equipped: the union is destroyed instead, nothing gained (Javier).
            bool unionShield = theirs.IsCode(CardKnowledge.MachinaFortress) &&
                enemySpells.Any(card => card.IsFaceup() && card.IsCode(MachinaGearframe));
            if (unionShield)
            {
                Log("Honest skipped: Gearframe protects their Machina Fortress");
                return false;
            }

            // One Honest per battle: the next prompt in the same damage step still shows the old ATK.
            string battle = $"{CurrentHeader.CurrentTurn}:{ours.CurrentCoordinates.Sequence}:{theirs.CurrentCoordinates.Sequence}";
            if (battle == _honestBattle)
                return false;

            int theirPower = theirs.GetRealPower();
            // Javier: with a Tytannial that has Plants to tribute, Honest is kept for her (it targets nothing), unless
            // this battle kills us.
            bool savedForTytannial = IsTargetingBlocked(enemyMonsters) && !theirs.IsCode(CardKnowledge.TytannialPrincessOfCamellias) &&
                !(!IsBotTurn && theirs.Attack - ours.GetRealPower() >= ownSide.LifePoints);
            if (savedForTytannial)
            {
                Log("Honest kept for Tytannial");
                return false;
            }
            bool use;
            if (IsBotTurn)
            {
                bool turnsIt = ours.Attack <= theirPower && ours.Attack + theirs.Attack > theirPower;
                // With Honest the damage is our own ATK (we gain exactly theirs): lethal it wasn't before.
                int lifePoints = CurrentField.EnemySide.LifePoints;
                bool lethal = theirs.IsAttack() && ours.Attack >= lifePoints && ours.Attack - theirs.Attack < lifePoints;
                use = (turnsIt && !ours.IsCode(Sacrificable)) || lethal;
            }
            else
            {
                bool celestiaTribute = ours.IsCode(Ryko) && (ownSide.Hand ?? Array.Empty<int>()).Any(id => CardIdentity.Same(id, Celestia));
                use = ours.IsAttack() && theirs.Attack >= ours.Attack && (!ours.IsCode(Sacrificable) || celestiaTribute);
            }

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
            // Gorz, its token and Tragoedia come down in defense to stop the attack (Javier).
            if ((CardIdentity.Same(cardId, Gorz) || CardIdentity.Same(cardId, GorzToken) || CardIdentity.Same(cardId, Tragoedia)) &&
                positions.Contains((byte)CardPosition.FaceUpDefence))
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

            // Mill 3 while it still helps: a Wulf left in the deck, or names missing for Judgment Dragon (Javier).
            int wulfSeen = GraveCopies(Wulf) + (CurrentField.OwnSide.Hand ?? Array.Empty<int>()).Count(id => CardIdentity.Same(id, Wulf)) +
                (await GetBotMonsterCardsAsync()).Count(card => card.IsCode(Wulf));
            bool helps = wulfSeen < 3 || GraveNames() < 4;
            int wanted = DeckCount <= DeckCaution || !helps ? 1 : 3;
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
                var order = _pendingEffect == SolarRecharge ? SolarDiscardOrder(hand) : DiscardOrder(hand, board);
                var discard = PickInOrder(state, order);
                if (discard != null)
                {
                    Log($"Discarding {string.Join(",", discard.Select(i => selectable[i].ID))}{(_pendingEffect != 0 ? $" (for {_pendingEffect})" : "")}");
                    _pendingEffect = 0;
                    return discard;
                }
            }

            // Plaguespreader's cost: a card from hand on top of the deck. Wulf first: the next mill sends it to the GY
            // and it summons itself.
            if (All(CardLocation.Hand) && hint == DHint.TODECK)
                return Picked("On top of the deck", PickInOrder(state, new[] { Wulf, NecroGardna, Plaguespreader }
                    .Concat(LightswornMonsters.Where(id => id != Lumina && GraveCopies(id) > 0)).ToArray()), selectable);

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
            if (_pendingEffect == 0 && !IsBotTurn && hint == DHint.DESTROY)
                _pendingEffect = Ryko;
            // Javier: against a Stardust that would negate it, or a Tytannial with Plants to tribute, Ryko skips the
            // destruction and only mills.
            var rykoBoard = _pendingEffect == Ryko && state.Minimum == 0 ? await BuildBoardAsync() : null;
            if (rykoBoard != null && (rykoBoard.StardustBlocking || rykoBoard.TargetingBlocked))
            {
                Log($"Ryko destroys nothing: {(rykoBoard.StardustBlocking ? "Stardust" : "Tytannial with Plants to tribute")} would negate it, it only mills");
                _pendingEffect = 0;
                return new List<byte>();
            }
            if (_pendingEffect is Ryko or Celestia or Lyla && hint is DHint.DESTROY or DHint.TARGET)
            {
                int wanted = _pendingEffect == Celestia ? Math.Max(state.Minimum, Math.Min(2, state.Maximum)) : Math.Max(state.Minimum, 1);
                var killer = await AttackerThatKillsUsAsync();
                bool IsKiller(DynamicCard card) => killer.HasValue && card.CurrentCoordinates.Location == (byte)CardLocation.MonsterZone &&
                    card.CurrentCoordinates.Sequence == killer.Value.CurrentCoordinates.Sequence;
                var avoided = await AvoidedMonsterEffectTargetsAsync(hint == DHint.DESTROY);
                var picks = selectable
                    .Select((card, index) => new { Card = card, Index = index })
                    .Where(x => x.Card.CurrentCoordinates.Controller == ChainTracker.Opponent)
                    // Like Icarus: no Fortress that costs a discard, no Gearframe-protected monster. The attacker
                    // that would kill us is still fair game.
                    .Where(x => !avoided(x.Card) || IsKiller(x.Card))
                    .Where(x => x.Card.CurrentCoordinates.Location != (byte)CardLocation.SpellZone || !x.Card.IsFaceup() || IsLastingCard(x.Card))
                    .OrderByDescending(x => IsKiller(x.Card))
                    // Into a face-up Tytannial: she negates any target anyway, so aim at her (if she lets it through, she dies).
                    .ThenByDescending(x => x.Card.IsCode(CardKnowledge.TytannialPrincessOfCamellias) && x.Card.IsFaceup())
                    .ThenByDescending(x => x.Card.CurrentCoordinates.Location == (byte)CardLocation.SpellZone && !x.Card.IsFaceup())
                    .ThenByDescending(x => (x.Card.Type & CardKnowledge.TokenType) == 0)
                    .ThenByDescending(x => RemovalValue(x.Card))
                    .Take(wanted)
                    .Select(x => (byte)x.Index)
                    .ToList();
                if (picks.Count >= Math.Max(state.Minimum, 1))
                {
                    Log($"{_pendingEffect} destroys {string.Join(",", picks.Select(i => selectable[i].ID))} " +
                        (killer.HasValue ? $"(the attacks left kill us: {killer.Value.ID} first)" : "(backrow first)"));
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

        // On their turn: the attackers still to come (not the one in battle now) kill us through our other monsters.
        // Then the strongest of them is the target, not the backrow (Javier: Ryko broke a Raigeki Break and two
        // Fortresses hit for 5000, 2026-10-27).
        private async Task<DynamicCard?> AttackerThatKillsUsAsync()
        {
            if (IsBotTurn)
                return null;
            var enemySide = CurrentField.EnemySide;
            var ownSide = CurrentField.OwnSide;
            // Javier: with Gorz in hand it stops the attacks left; hitting a Fortress would only cost us a card from
            // hand, maybe Gorz itself (2026-10-27).
            if ((ownSide.Hand ?? Array.Empty<int>()).Any(id => CardIdentity.Same(id, Gorz)))
                return null;
            var attackers = (await GetEnemyMonsterCardsAsync())
                .Where(card => card.IsFaceup() && card.IsAttack() && !card.Attacked &&
                    !(enemySide.HasBattlingMonster && card.CurrentCoordinates.Sequence == enemySide.BattlingMonster.CurrentCoordinates.Sequence))
                .OrderByDescending(card => card.Attack)
                .ToList();
            int blockers = (await GetBotMonsterCardsAsync())
                .Count(card => !(ownSide.HasBattlingMonster && card.CurrentCoordinates.Sequence == ownSide.BattlingMonster.CurrentCoordinates.Sequence));
            if (attackers.Count == 0 || attackers.Skip(blockers).Sum(card => card.Attack) < ownSide.LifePoints)
                return null;
            return attackers[0];
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

        // Other discards (Lumina, Monster Reincarnation): Wulf, Plaguespreader (only with a plan for it), Gardna, then a
        // Lightsworn the GY is missing or that's repeated, then the rest by hand value.
        private int[] DiscardOrder(List<DynamicCard> hand, Board board)
        {
            var lightsworn = LightswornMonsters.Where(id => id != Lumina && InHand(hand, id)).ToList();
            var cheap = PlaguespreaderWorthDiscarding(board) ? new[] { Wulf, Plaguespreader, NecroGardna } : new[] { Wulf, NecroGardna };
            return cheap
                .Concat(lightsworn.Where(id => GraveCopies(id) == 0 && id != Ryko))
                .Concat(lightsworn.Where(id => hand.Count(card => card.IsCode(id)) >= 2))
                // Honest is the answer to a Tytannial with Plants (battle, no targeting): never discarded then (Javier).
                .Concat(hand.Where(card => !card.IsCode(JudgmentDragon, Lumina) && !(board.TargetingBlocked && card.IsCode(Honest)))
                    .OrderBy(HandValue).Select(card => card.ID))
                .ToArray();
        }

        // Charge: Lumina when there's something to revive (it can bring Lyla back too, Javier), Lyla against their backrow,
        // else a name the GY is missing.
        private int[] ChargeSearchOrder(Board board)
        {
            var order = new List<int>();
            if (TytannialPlan(board) && !InHand(board.Hand, Ryko))
                order.Add(Ryko);
            if (OwnGrave.Any(id => new[] { Jain, Wulf, Garoth, Lyla, Ehren, Druid }.Any(t => CardIdentity.Same(id, t))))
                order.Add(Lumina);
            if (HasBackrow(board))
                order.Add(Lyla);
            order.AddRange(new[] { Jain, Garoth, Ehren, Lyla, Druid, Ryko, Lumina }
                .Where(id => GraveCopies(id) == 0 && !InHand(board.Hand, id)));
            order.AddRange(new[] { Lyla, Jain, Ryko, Lumina, Garoth, Ehren, Druid, Wulf });
            return order.ToArray();
        }

        private bool GardnaPlan(Board board) =>
            InHand(board.Hand, Celestia) && board.OwnMonsters.Any(card => card.IsFaceup() && card.IsCode(LightswornMonsters));

        private bool PlaguespreaderFoolishPlan(Board board) =>
            GraveCopies(Plaguespreader) == 0 && !InHand(board.Hand, Plaguespreader) &&
            board.Hand.Count(card => !card.IsCode(FoolishBurial)) > 0 && PlaguespreaderPlan(board);

        // The missing name only when it's the 4th and Judgment Dragon comes down this same turn.
        private bool MissingNamePlan(Board board) =>
            InHand(board.Hand, JudgmentDragon) && GraveNames() == 3 && JudgmentCanLand(board);

        // Javier: holding 2 Celestia, Foolish sends Wulf (it summons itself) and Wulf is the tribute for Celestia, which
        // breaks 2 of their cards. Only without a Lightsworn already on the field to tribute.
        private bool WulfForCelestiaPlan(Board board) =>
            IsBotTurn && board.IsMain1 && board.Hand.Count(card => card.IsCode(Celestia)) >= 2 &&
            board.EnemyMonsters.Count + board.EnemySpells.Count >= 2 && CanMill(5, board) &&
            !board.OwnMonsters.Any(card => card.IsCode(LightswornMonsters)) &&
            GraveCopies(Wulf) + board.Hand.Count(card => card.IsCode(Wulf)) < 3;

        // Javier: Plaguespreader to the GY now, for a Goyo (level 6 = Plaguespreader + a level 4) next turn that steals
        // their best monster. Needs a level 4 of ours and a cheap card for its top-of-deck cost, and that we live to
        // next turn (at 800 LP against 3 attackers it was a brainless play, 2026-10-27).
        private bool GoyoNextTurnSetup(Board board) =>
            IsBotTurn && !OpponentHasLethal(board) && ScoreSynchro(GoyoGuardian, board) > 30 &&
            board.OwnMonsters.Concat(board.Hand).Any(card => card.IsCode(LightswornMonsters) && card.Level == 4) &&
            board.Hand.Any(card => !card.IsCode(FoolishBurial, Plaguespreader) && Expendable(card));

        private bool PlaguespreaderGoyoNextTurn(Board board) =>
            GraveCopies(Plaguespreader) == 0 && !InHand(board.Hand, Plaguespreader) && GoyoNextTurnSetup(board);

        // Javier: Plaguespreader only goes to the GY (Foolish, a discard) with a plan to use it: a synchro this turn or
        // a Goyo next turn.
        private bool PlaguespreaderWorthDiscarding(Board board) =>
            GraveCopies(Plaguespreader) == 0 && (PlaguespreaderPlan(board) || GoyoNextTurnSetup(board));

        private string? FoolishPlan(Board board)
        {
            if (!CanMill(1, board))
                return null;
            if (GardnaPlan(board))
                return "Necro Gardna, protects a Lightsworn for Celestia's tribute";
            if (PlaguespreaderFoolishPlan(board))
                return "Plaguespreader for a synchro this turn";
            if (WulfForCelestiaPlan(board))
                return "Wulf summons itself and is tributed for Celestia";
            if (PlaguespreaderGoyoNextTurn(board))
                return "Plaguespreader for a Goyo next turn";
            if (MissingNamePlan(board))
                return "the 4th name, Judgment Dragon comes down this turn";
            return null;
        }

        // Foolish: the plan that made us activate it, then the fallbacks.
        private int[] FoolishOrder(Board board)
        {
            var order = new List<int>();
            if (GardnaPlan(board))
                order.Add(NecroGardna);
            if (PlaguespreaderFoolishPlan(board))
                order.Add(Plaguespreader);
            if (WulfForCelestiaPlan(board))
                order.Add(Wulf);
            if (PlaguespreaderGoyoNextTurn(board))
                order.Add(Plaguespreader);
            order.AddRange(LightswornMonsters.Where(id => GraveCopies(id) == 0 && id != Wulf));
            order.AddRange(new[] { Wulf, NecroGardna });
            order.AddRange(LightswornMonsters);
            order.Add(Plaguespreader);
            return order.ToArray();
        }

        // Judgment Dragon would come down now (same checks as the summon itself).
        private bool JudgmentCanLand(Board board)
        {
            bool lastResort = OpponentHasLethal(board);
            return IsBotTurn && (!SummonTrapPossible(board) || lastResort) && !OppressionStopsOurSpecialSummons(board) &&
                (CanMill(4, board) || lastResort) && (JudgmentReason(board) != null || lastResort);
        }

        // Synchros in our Extra Deck: level and ATK (Plaguespreader is a level 2 tuner).
        private static readonly Dictionary<int, (int Level, int Attack)> Synchros = new()
        {
            [StardustDragon] = (8, 2500), [GoyoGuardian] = (6, 2800), [RedDragonArchfiend] = (8, 3000),
            [ThoughtRulerArchfiend] = (8, 2700), [ColossalFighter] = (8, 2800), [XSaberUrbellum] = (7, 2200),
            [BrionacDragonOfTheIceBarrier] = (6, 2300), [MagicalAndroid] = (5, 2400), [TempestMagician] = (6, 2200),
            [AllyOfJusticeCatastor] = (5, 2200), [MistWurm] = (9, 2500), [ArmoryArm] = (4, 1800),
        };

        // Plaguespreader + one of our face-up monsters into a synchro that pays off now: the situational synchro rule
        // likes it (Goyo steals, Catastor breaks a non-DARK, Brionac bounces, Android heals at low LP...) or it gives
        // lethal. Only when no set card of theirs may be Bottomless / Torrential (Javier).
        private bool PlaguespreaderPlan(Board board)
        {
            // Javier: with the deck running low, a Lightsworn that mills every End Phase (Jain) goes into any synchro to
            // get it off the field, Bottomless or not, and any card can go on top of the deck (the bot sat idle and
            // decked out with Plaguespreader in the GY and Jain milling, 2026-10-27).
            bool millerOut = DeckCount <= DeckCaution && board.OwnMonsters.Any(card => card.IsFaceup() && card.IsCode(EndPhaseMillers) &&
                Synchros.Any(x => x.Value.Level == card.Level + 2));
            if (!IsBotTurn || (SummonTrapPossible(board) && !millerOut) || OppressionStopsOurSpecialSummons(board))
                return false;
            if (millerOut && board.Hand.Any(card => !card.IsCode(FoolishBurial, Plaguespreader, JudgmentDragon)))
            {
                LogOnChange($"Plaguespreader: synchro to take an End Phase miller off the field ({DeckCount} cards left)");
                return true;
            }
            // Its cost puts a card from hand on top of the deck: only a cheap one.
            if (!board.Hand.Any(card => !card.IsCode(FoolishBurial, Plaguespreader) && Expendable(card)))
                return false;

            // Against Tytannial (Javier): a level 3 (Card Trooper, Lumina) makes Catastor, a level 4 a Goyo that trades.
            var tytannial = board.EnemyMonsters.FirstOrDefault(card => card.IsCode(CardKnowledge.TytannialPrincessOfCamellias) && card.IsFaceup());
            bool tytannialPlan = TytannialPlan(board) && board.IsMain1;
            bool catastorWorks = tytannial.ID != 0 && (tytannial.Attribute & CardKnowledge.DarkAttribute) == 0;
            if (tytannialPlan && board.OwnMonsters.Any(card => card.IsFaceup() && !card.HasType((CardType)CardKnowledge.TunerType) &&
                    ((card.Level == 3 && catastorWorks) || card.Level == 4)))
            {
                LogOnChange("Plaguespreader: synchro against Tytannial (Catastor with a level 3, Goyo trade with a level 4)");
                return true;
            }

            int ourAttack = board.OwnMonsters.Where(card => card.IsFaceup() && card.IsAttack() && !card.Attacked).Sum(card => card.Attack);
            foreach (var material in board.OwnMonsters.Where(card => card.IsFaceup() && !card.HasType((CardType)CardKnowledge.TunerType)))
            {
                foreach (var (synchro, stats) in Synchros.Where(x => x.Value.Level == material.Level + 2).Select(x => (x.Key, x.Value)))
                {
                    int materialAttack = material.IsAttack() && !material.Attacked ? material.Attack : 0;
                    bool lethal = board.IsMain1 && board.EnemyMonsters.Count == 0 &&
                        ourAttack - materialAttack + stats.Attack >= CurrentField.EnemySide.LifePoints;
                    if (material.IsCode(CardTrooper) && !lethal && !OpponentHasLethal(board))
                        continue;
                    // Brionac costs another discard: a value play only with two cheap cards (one for the top of the
                    // deck, one for its bounce), else only for lethal (below).
                    bool brionacPaid = board.Hand.Count(card => !card.IsCode(FoolishBurial) && Expendable(card)) >= 2;
                    if (ScoreSynchro(synchro, board) > 30 && (synchro != BrionacDragonOfTheIceBarrier || brionacPaid))
                        return true;
                    if (lethal)
                        return true;
                }
            }
            return false;
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
