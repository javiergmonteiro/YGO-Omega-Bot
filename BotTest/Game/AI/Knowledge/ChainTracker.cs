using OmegaBotWrapper.States;

namespace BotTest.Game.AI.Knowledge
{
    /// <summary>
    /// Tracks who started the chain the bot is answering. The API doesn't say who owns each chain link, so it
    /// combines the header fields (bot-relative: 1 = opponent, 0 = bot, -1 = unknown) with the bot's own
    /// latest decision and with cards that banish themselves as cost.
    /// </summary>
    public sealed class ChainTracker
    {
        public const int Opponent = 1;
        public const int Bot = 0;

        private readonly Dictionary<int, int> _seenEnemyBanished = new();
        private int _markedTurn = -1;
        private int _markedPhase = -1;

        /// <summary>True when the bot's latest decision activated/summoned something.</summary>
        public bool OwnActionPending { get; private set; }

        /// <summary>Whether the bot's latest activation was a monster effect (matters for Machina Fortress).</summary>
        public bool LastActivationWasMonsterEffect { get; private set; }

        public void MarkOwnAction(BotStateHeader header)
        {
            OwnActionPending = true;
            Stamp(header);
        }

        public void MarkOwnActivation(bool isMonsterEffect, BotStateHeader header, int cardId = 0)
        {
            if (header.CurrentTurn != _markedTurn || header.CurrentPhase != _markedPhase || !OwnActionPending)
                _chainedCards.Clear();
            OwnActionPending = true;
            LastActivationWasMonsterEffect = isMonsterEffect;
            Stamp(header);
            if (cardId > 0)
                _chainedCards.Add(cardId);
        }

        private readonly List<int> _chainedCards = new();

        /// <summary>We already put this card (or another copy) on the chain being answered.</summary>
        public bool AlreadyChained(int cardId, BotStateHeader header) =>
            AnsweredCurrentChain(header) && _chainedCards.Any(id => CardIdentity.Same(id, cardId));

        /// <summary>
        /// We already put something on the chain being answered right now. The flag alone isn't enough: it's only
        /// cleared when we pass, so it must also be the same turn and phase, and there must be a chain in progress
        /// (an attack declaration is a fresh window, not an answered chain).
        /// </summary>
        public bool AnsweredCurrentChain(BotStateHeader header) =>
            OwnActionPending && header.CurrentTurn == _markedTurn && header.CurrentPhase == _markedPhase &&
            header.LastChainPlayer is Opponent or Bot;

        /// <summary>Our latest decision started what's being answered now (same turn and phase).</summary>
        public bool OwnActionPendingNow(BotStateHeader header) =>
            OwnActionPending && header.CurrentTurn == _markedTurn && header.CurrentPhase == _markedPhase;

        private void Stamp(BotStateHeader header)
        {
            _markedTurn = header.CurrentTurn;
            _markedPhase = header.CurrentPhase;
        }

        /// <summary>We passed: whatever gets chained next comes from the opponent.</summary>
        public void MarkPassed()
        {
            OwnActionPending = false;
            _chainedCards.Clear();
        }

        public void SetOwnActionPending(bool pending, BotStateHeader header)
        {
            OwnActionPending = pending;
            Stamp(header);
        }

        /// <summary>Remember what the opponent had banished at this decision.</summary>
        public void SnapshotEnemyBanish(int[]? enemyBanish)
        {
            foreach (int id in CardKnowledge.UnnegatableByRoyalOppression)
                _seenEnemyBanished[id] = Count(enemyBanish, id);
        }

        /// <summary>
        /// A card that banishes itself as cost (Vayu) showed up in the opponent's banish pile since our last decision.
        /// </summary>
        public bool OpponentJustUsedUnnegatable(int[]? enemyBanish, out int cardId)
        {
            foreach (int id in CardKnowledge.UnnegatableByRoyalOppression)
            {
                _seenEnemyBanished.TryGetValue(id, out int seen);
                if (Count(enemyBanish, id) > seen)
                {
                    cardId = id;
                    return true;
                }
            }

            cardId = 0;
            return false;
        }

        /// <summary>
        /// Source of the summon/effect being answered: Opponent, Bot, or null when the API gives no source.
        /// Chain links come first (an effect that summons isn't a summon), then the summon itself.
        /// </summary>
        public static int? Source(BotStateHeader header)
        {
            if (header.LastChainPlayer is Opponent or Bot)
                return header.LastChainPlayer;
            if (header.LastSummonPlayer is Opponent or Bot)
                return header.LastSummonPlayer;
            return null;
        }

        private static int Count(int[]? ids, int id) => (ids ?? Array.Empty<int>()).Count(x => CardIdentity.Same(x, id));
    }
}
