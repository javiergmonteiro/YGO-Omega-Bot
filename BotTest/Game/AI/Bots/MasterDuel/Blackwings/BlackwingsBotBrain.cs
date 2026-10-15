using BotTest.Game.AI.Brains;
using BotTest.Game.AI.Knowledge;
using OmegaBotWrapper;
using OmegaBotWrapper.Enum;
using OmegaBotWrapper.Heuristics;
using OmegaBotWrapper.States;
using OmegaBotWrapper.Struct;
using static BotTest.Game.AI.Bots.MasterDuel.Blackwings.BlackwingsCardId;

namespace BotTest.Game.AI.Bots.MasterDuel.Blackwings
{
    /// <summary>
    /// Blackwing strategy for Edison. Everything about opposing cards (Stardust, Machina Fortress,
    /// Royal Oppression, targeting, traps) comes from <see cref="EdisonBotBrain"/>; this class only
    /// says how to play Blackwing cards.
    /// </summary>
    public sealed class BlackwingsBotBrain : EdisonBotBrain
    {
        private static readonly int[] HandSpecialPriority = { DarkArmedDragon, BoraTheSpear, GaleTheWhirlwind };

        // Blizzard is handled separately (only with a GY target), Kalut stays in hand, Vayu gets set.
        private static readonly int[] NormalSummonPriority =
        {
            ShuraTheBlueFlame, BoraTheSpear, SiroccoTheDawn, GaleTheWhirlwind
        };

        private static readonly int[] BlizzardTargets =
        {
            ShuraTheBlueFlame, BoraTheSpear, KalutTheMoonShadow, GaleTheWhirlwind, VayuTheEmblemOfHonor
        };

        // Blizzard's revive keeps effects active: Gale first for another halving + tuner.
        private static readonly int[] BlizzardRevivePriority =
        {
            GaleTheWhirlwind, ShuraTheBlueFlame, BoraTheSpear, KalutTheMoonShadow, VayuTheEmblemOfHonor
        };

        private static readonly int[] SearchPriority = { GaleTheWhirlwind, KalutTheMoonShadow, BoraTheSpear, ShuraTheBlueFlame };

        private static readonly int[] BlackwingMonsters =
        {
            ShuraTheBlueFlame, BoraTheSpear, KalutTheMoonShadow, GaleTheWhirlwind, BlizzardTheFarNorth,
            VayuTheEmblemOfHonor, SiroccoTheDawn, BlackwingArmorMaster, BlackwingArmedWing, SilverwindTheAscendant
        };

        // Turn Sirocco's effect was used (no synchro after it), and whether a synchro was available at the last idle.
        private int _siroccoUsedTurn = -1;
        private bool _siroccoTargetPending;
        private bool _synchroAvailable;

        // The battle Kalut was last used in (one Kalut per battle).
        private string? _kalutBattle;

        public BlackwingsBotBrain(
            ICardEvaluator? cardEvaluator = null,
            IBoardEvaluator? boardEvaluator = null,
            IActionScorer? actionScorer = null)
            : base(cardEvaluator, boardEvaluator, actionScorer)
        {
        }

        protected override string LogTag => "[Blackwings]";

        protected override int[] SynchroPriority { get; } =
        {
            BlackwingArmorMaster, SilverwindTheAscendant, DarkEndDragon, RedDragonArchfiend,
            StardustDragon, ThoughtRulerArchfiend, ColossalFighter,
            BlackwingArmedWing, GoyoGuardian, AllyOfJusticeCatastor, MagicalAndroid, BrionacDragonOfTheIceBarrier
        };

        // Keep Vayu and its synchro partners (Sirocco -> Armed Wing, Armed Wing -> Armor Master, Armor Master -> Silverwind).
        protected override int[] BanishLastOrder { get; } =
        {
            BlizzardTheFarNorth, KalutTheMoonShadow, BoraTheSpear, ShuraTheBlueFlame, DarkArmedDragon,
            GaleTheWhirlwind, SiroccoTheDawn, BlackwingArmorMaster, BlackwingArmedWing, VayuTheEmblemOfHonor
        };

        protected override int[] MonsterEffectCards { get; } =
        {
            BlackwingArmorMaster, DarkEndDragon, GaleTheWhirlwind, DarkArmedDragon, SiroccoTheDawn, VayuTheEmblemOfHonor,
            BrionacDragonOfTheIceBarrier
        };

        protected override int[] EffectsTargetingOwnCards { get; } = { SiroccoTheDawn, VayuTheEmblemOfHonor };

        // Shura special summons from the deck when it destroys a monster that goes to the GY.
        protected override int[] OnBattleDestroyAttackers { get; } = { ShuraTheBlueFlame };

        // Gale and Sirocco target a card on the field (Tytannial can negate them).
        protected override int[] DeckTargetingCards { get; } = { GaleTheWhirlwind, SiroccoTheDawn };

        protected override int[] ExcludedFromGenericSummon { get; } =
        {
            BlizzardTheFarNorth, KalutTheMoonShadow, VayuTheEmblemOfHonor, SiroccoTheDawn
        };

        // Sirocco's effect leaves only its target able to attack: it goes last, after any synchro,
        // and once used this turn we don't synchro (the synchro couldn't attack).
        protected override bool CanSynchroNow(Board board) => _siroccoUsedTurn != CurrentHeader.CurrentTurn;

        protected override int BattleBoostInHand(List<DynamicCard> hand) =>
            hand.Any(card => card.IsCode(KalutTheMoonShadow)) ? 1400 : 0;

        // A stolen monster becomes synchro material with Gale (special from hand, needs a Blackwing on field)
        // or Blizzard (normal summon), or the tribute for Sirocco.
        protected override bool CanUseStolenMonster(DynamicCard target, IdleCmdData state, Board board)
        {
            bool normalSummonAvailable = (state.SummonableCards ?? Array.Empty<DynamicCard>()).Length > 0;
            bool blackwingOnField = board.OwnMonsters.Any(card => card.IsFaceup() && card.IsCode(BlackwingMonsters));

            if (board.Hand.Any(card => card.IsCode(GaleTheWhirlwind)) && blackwingOnField && SynchroLevels.Contains(target.Level + 3))
                return true;
            if (normalSummonAvailable && board.Hand.Any(card => card.IsCode(BlizzardTheFarNorth)) && SynchroLevels.Contains(target.Level + 2))
                return true;
            if (normalSummonAvailable && board.Hand.Any(card => card.IsCode(SiroccoTheDawn)))
                return true;

            return false;
        }

        protected override int HandValue(DynamicCard card) =>
            card.IsCode(KalutTheMoonShadow, GaleTheWhirlwind) ? 3 : base.HandValue(card);

        protected override bool CanUseMonsterEffect(int cardId, Board board, List<DynamicCard> monsterTargets) => cardId switch
        {
            GaleTheWhirlwind => monsterTargets.Any(card => IsWorthHalving(card)),
            // Bounce something hard to answer, paying with a card we don't need.
            BrionacDragonOfTheIceBarrier => board.Hand.Any(card => HandValue(card) <= 2) &&
                (monsterTargets.Any(card => ThreatScore(card) >= 2500) || board.EnemySpells.Any(card => card.IsFaceup())),
            DarkArmedDragon => monsterTargets.Count + board.EnemySpells.Count > 0,
            DarkEndDragon or BlackwingArmorMaster => monsterTargets.Count > 0,
            SiroccoTheDawn => board.IsMain1 && !_synchroAvailable && IsSiroccoBoostWorthIt(board.OwnMonsters, board.EnemyMonsters) && MarkSiroccoUsed(),
            _ => true
        };

        protected override async Task<IdleCmdResponse?> DecideMainPhaseAsync(DecisionContext<IdleCmdData> context, Board board)
        {
            var state = context.StateData;
            var hand = board.Hand;
            var ownMonsters = board.OwnMonsters;
            _synchroAvailable = (state.SpecialSummonableCards ?? Array.Empty<DynamicCard>()).Any(card => card.IsCode(SynchroPriority));

            // Special summons from hand.
            foreach (int cardId in HandSpecialPriority)
            {
                if (cardId == DarkArmedDragon && board.StardustBlocking && board.EnemyMonsters.Count + board.EnemySpells.Count == 1)
                    continue;

                if (TryGetSpecialSummon(state, cardId, out var card) && card.CurrentCoordinates.Location == (byte)CardLocation.Hand)
                {
                    Log($"Special summon from hand: {cardId}");
                    return card.SpecialSummon();
                }
            }

            if (TrySynchroSummon(state, board, out var synchro))
                return synchro;

            // Tribute summon Sirocco using a stolen monster.
            if (board.ControlsStolenMonster && TryGetSummonable(state, SiroccoTheDawn, out var siroccoTribute))
            {
                Log("Tributing stolen monster for Sirocco");
                return siroccoTribute.NormalSummon();
            }

            // Black Whirlwind goes up before the Normal Summon so the summon triggers its search.
            // One copy per useful search we're missing (Gale/Kalut not in hand), up to two.
            int whirlwindsUp = board.OwnSpells.Count(card => card.IsCode(BlackWhirlwind) && card.IsFaceup());
            int missingSearches = SearchPriority.Take(2).Count(id => !hand.Any(card => card.IsCode(id)));
            var summonable = state.SummonableCards ?? Array.Empty<DynamicCard>();
            bool hasNormalSummon = summonable.Any(card => card.IsCode(ShuraTheBlueFlame, BoraTheSpear, SiroccoTheDawn, BlizzardTheFarNorth));

            // Kalut normally stays in hand, but with an empty board and no other monster to play
            // it's better as a body (and with Black Whirlwind, a 1400 summon still searches Gale).
            bool kalutIsOnlyPlay = ownMonsters.Count == 0 && !hasNormalSummon &&
                !TryGetMonsterSettable(state, VayuTheEmblemOfHonor, out _) &&
                TryGetSummonable(state, KalutTheMoonShadow, out _);

            if ((hasNormalSummon || kalutIsOnlyPlay) && whirlwindsUp < Math.Min(2, missingSearches) &&
                TryActivate(state, BlackWhirlwind, board.StardustBlocking, out var whirlwind))
                return whirlwind;

            // Normal Summon the Blackwing that becomes a synchro with Gale this turn (before Blizzard's revive):
            // Sirocco (5) + Gale (3) = 8, Shura/Bora (4) + Gale = 7 (Black Rose / Armor Master).
            var galeLine = FindGaleSynchroSummon(state, board, whirlwindsUp > 0);
            if (galeLine.HasValue)
            {
                Log($"Normal summon for a Gale synchro: {galeLine.Value.ID}");
                return galeLine.Value.NormalSummon();
            }

            // Normal summon.
            int enemyBestPower = board.EnemyMonsters.Where(card => card.IsFaceup()).Select(card => card.GetRealPower()).DefaultIfEmpty(0).Max();
            bool blizzardHasTarget = board.Graveyard.Any(card => card.IsCode(BlizzardTargets));
            if (blizzardHasTarget && TryGetSummonable(state, BlizzardTheFarNorth, out var blizzard))
                return blizzard.NormalSummon();

            foreach (int cardId in NormalSummonPriority)
            {
                // Sirocco is only worth it when summoned without tributes.
                if (cardId == SiroccoTheDawn && ownMonsters.Count > 0)
                    continue;

                if (!TryGetSummonable(state, cardId, out var card))
                    continue;

                // Weaker than what they have and not a tuner we need: set it, unless the face-up summon is covered.
                // A face-up Normal Summon triggers Black Whirlwind (Kalut search), and Kalut / set traps protect it.
                bool summonIsCovered =
                    whirlwindsUp > 0 ||
                    hand.Any(h => h.IsCode(KalutTheMoonShadow)) ||
                    board.OwnSpells.Count(s => !s.IsFaceup()) >= 2;
                if (card.Attack < enemyBestPower && !summonIsCovered && !card.HasType((CardType)CardKnowledge.TunerType) &&
                    TryGetMonsterSettable(state, cardId, out var settable))
                {
                    Log($"Setting weak monster: {cardId}");
                    return settable.SetMonster();
                }

                Log($"Normal summon: {cardId}");
                return card.NormalSummon();
            }

            if (TryGetMonsterSettable(state, VayuTheEmblemOfHonor, out var vayu))
            {
                Log("Setting Vayu");
                return vayu.SetMonster();
            }

            if (kalutIsOnlyPlay)
            {
                // With Black Whirlwind up, a face-up Kalut searches Gale (then Gale + Kalut = level 6 synchro).
                bool whirlwindActive = (await GetOwnSpellCardsAsync()).Any(card => card.IsCode(BlackWhirlwind) && card.IsFaceup());
                if (whirlwindActive && TryGetSummonable(state, KalutTheMoonShadow, out var kalut))
                {
                    Log("Only play: Kalut face-up for Black Whirlwind");
                    return kalut.NormalSummon();
                }

                // Otherwise Kalut is worth more in hand than as a blocker: take the hit unless it's actually dangerous.
                if (IsInDanger(board) && TryGetMonsterSettable(state, KalutTheMoonShadow, out var kalutSet))
                {
                    Log("Only play and in danger: setting Kalut");
                    return kalutSet.SetMonster();
                }
            }

            return null;
        }

        protected override async Task<bool?> ShouldChainDeckCardAsync(DynamicCard card)
        {
            if (card.IsCode(KalutTheMoonShadow))
                return ShouldUseKalut();

            if (card.IsCode(SiroccoTheDawn))
                return await ShouldUseSiroccoNowAsync();

            // Black Whirlwind's search triggers on our own summons: always take it.
            if (card.IsCode(BlackWhirlwind))
                return true;

            return null;
        }

        protected override async Task<IList<byte>?> SelectDeckCardAsync(SelectCardData state)
        {
            var selectable = state.SelectableCards ?? Array.Empty<DynamicCard>();
            if (selectable.Length == 0)
                return null;

            bool allFromGrave = selectable.All(card => card.CurrentCoordinates.Location == (byte)CardLocation.Grave);

            // Sirocco's target (our own monster): Shura, whose battle-destroy effect gets the most out of the pump.
            if (_siroccoTargetPending && selectable.All(card => card.CurrentCoordinates.Controller == ChainTracker.Bot))
            {
                _siroccoTargetPending = false;
                var pick = PreferCardSelection(state, ShuraTheBlueFlame);
                if (pick != null)
                {
                    Log("Sirocco's pump on Shura");
                    return pick;
                }
                var strongest = selectable.Select((card, index) => new { card, index }).OrderByDescending(x => x.card.Attack).First();
                return new List<byte> { (byte)strongest.index };
            }
            bool allFromDeck = selectable.All(card => card.CurrentCoordinates.Location == (byte)CardLocation.Deck);

            // Blizzard's revive from our GY.
            if (state.SelectionHint == (int)DHint.SPSUMMON && allFromGrave)
            {
                foreach (int cardId in BlizzardRevivePriority)
                {
                    var revive = PreferCardSelection(state, cardId);
                    if (revive != null)
                        return revive;
                }
            }

            // Shura's floater (special summon from deck, effects negated): a tuner to attack again and synchro in Main 2.
            // Gale (level 3, 1300 ATK) makes a level 7 with Shura; Vayu (level 1) a level 5.
            if (state.SelectionHint == (int)DHint.SPSUMMON && allFromDeck)
            {
                var floater = PreferCardSelection(state, GaleTheWhirlwind) ?? PreferCardSelection(state, VayuTheEmblemOfHonor);
                if (floater != null)
                    return floater;
            }

            // Searches from the deck (Black Whirlwind): Gale first, then Kalut, skipping what's already in hand.
            if (allFromDeck)
            {
                var hand = await GetBotHandCardsAsync();
                foreach (int cardId in SearchPriority.Where(id => !hand.Any(card => card.IsCode(id))).Concat(SearchPriority))
                {
                    var search = PreferCardSelection(state, cardId);
                    if (search != null)
                        return search;
                }
            }

            return null;
        }

        // Gale halves ATK *and* DEF (the DEF cut matters too, e.g. so Bora can break or pierce a defender).
        // Tokens count like anything else; only a 0 ATK / 0 DEF body is a waste.
        private static bool IsWorthHalving(DynamicCard card) =>
            card.IsFaceup() && (card.Attack > 0 || card.Defense > 0);

        // Gale is the tuner: from hand (special summon once a Blackwing is on the field), or searched by Black Whirlwind,
        // which needs a summon with more than Gale's 1300 ATK. Gale is limited, so if it's not in hand, GY, field
        // or banished, it's still in the deck.
        private DynamicCard? FindGaleSynchroSummon(IdleCmdData state, Board board, bool whirlwindUp)
        {
            bool galeInHand = board.Hand.Any(card => card.IsCode(GaleTheWhirlwind));
            bool galeGone = board.Graveyard.Any(card => card.IsCode(GaleTheWhirlwind)) ||
                board.OwnMonsters.Any(card => card.IsCode(GaleTheWhirlwind)) ||
                (CurrentField.OwnSide.Banish ?? Array.Empty<int>()).Contains(GaleTheWhirlwind);
            bool galeSearchable = whirlwindUp && !galeInHand && !galeGone;
            if (!galeInHand && !galeSearchable)
                return null;

            foreach (int cardId in new[] { SiroccoTheDawn, ShuraTheBlueFlame, BoraTheSpear })
            {
                // Sirocco only without tributes.
                if (cardId == SiroccoTheDawn && board.OwnMonsters.Count > 0)
                    continue;
                if (!TryGetSummonable(state, cardId, out var card))
                    continue;
                if (!galeInHand && card.Attack <= 1300)
                    continue;
                if (!SynchroLevels.Contains(card.Level + 3))
                    continue;
                return card;
            }

            return null;
        }

        // Sirocco: one Blackwing gains the ATK of all our other Blackwings, but only it can attack this turn.
        // Pointless alone; worth it only when the pumped monster breaks something none of ours beats on its own.
        private static bool IsSiroccoBoostWorthIt(List<DynamicCard> ownMonsters, List<DynamicCard> enemyMonsters)
        {
            var attackers = ownMonsters.Where(card => card.IsCode(BlackwingMonsters) && card.IsFaceup() && card.IsAttack()).ToList();
            if (attackers.Count < 2)
                return false;

            int bestSingle = attackers.Max(card => card.Attack);
            int pumped = attackers.Sum(card => card.Attack);
            return enemyMonsters.Any(enemy => enemy.GetRealPower() >= bestSingle && enemy.GetRealPower() < pumped);
        }

        private async Task<bool> ShouldUseSiroccoNowAsync() =>
            IsBotTurn && (DuelPhase)CurrentHeader.CurrentPhase == DuelPhase.Main1 && !_synchroAvailable &&
            IsSiroccoBoostWorthIt(await GetBotMonsterCardsAsync(), await GetEnemyMonsterCardsAsync()) && MarkSiroccoUsed();

        private bool MarkSiroccoUsed()
        {
            _siroccoUsedTurn = CurrentHeader.CurrentTurn;
            _siroccoTargetPending = true;
            return true;
        }

        // Low LP, or what they have face-up could finish us: then a blocker matters more than cards in hand.
        private bool IsInDanger(Board board)
        {
            int lifePoints = CurrentField.OwnSide.LifePoints;
            int enemyDamage = board.EnemyMonsters.Where(card => card.IsFaceup() && card.IsAttack()).Sum(card => card.Attack);
            return lifePoints <= 3000 || enemyDamage >= lifePoints;
        }

        // Kalut (+1400 in the damage step) only when it flips the battle:
        //  - they're about to destroy one of our important monsters (Shura above all), or
        //  - our attack wouldn't destroy their monster without it, or
        //  - it makes a direct attack lethal.
        private bool ShouldUseKalut()
        {
            var ownSide = CurrentField.OwnSide;
            var enemySide = CurrentField.EnemySide;
            if (!ownSide.HasBattlingMonster)
                return false;

            // One Kalut per battle: the next prompt in the same damage step still shows the un-pumped ATK.
            string battle = $"{CurrentHeader.CurrentTurn}:{ownSide.BattlingMonster.ID}:{ownSide.BattlingMonster.CurrentCoordinates.Sequence}:" +
                $"{(enemySide.HasBattlingMonster ? enemySide.BattlingMonster.ID : 0)}";
            if (battle == _kalutBattle)
                return false;

            bool use = ShouldBoostWithKalut(ownSide, enemySide);
            if (use)
                _kalutBattle = battle;
            return use;
        }

        private bool ShouldBoostWithKalut(CardInfo ownSide, CardInfo enemySide)
        {
            var ours = ownSide.BattlingMonster;
            const int boost = 1400;

            if (!enemySide.HasBattlingMonster)
                return IsBotTurn && enemySide.LifePoints > ours.Attack && enemySide.LifePoints <= ours.Attack + boost;

            var theirs = enemySide.BattlingMonster;
            int theirPower = theirs.GetRealPower();

            if (IsBotTurn)
            {
                // We attack: only if we don't already win, and the boost makes us win.
                bool alreadyWins = ours.Attack > theirPower;
                return !alreadyWins && ours.Attack + boost > theirPower;
            }

            // They attack: save our monster if it's worth saving and the boost turns the battle.
            if (ours.IsDefense())
                return false;
            bool important = ours.IsCode(ShuraTheBlueFlame) || ours.IsExtraCard() || ours.Attack >= 1700;
            bool wouldDie = theirs.Attack >= ours.Attack;
            return important && wouldDie && ours.Attack + boost > theirs.Attack;
        }
    }
}
