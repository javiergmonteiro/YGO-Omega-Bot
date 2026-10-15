using BotTest.Game.AI.Brains;
using OmegaBotWrapper.Heuristics;

namespace BotTest.Game.AI.Bots.MasterDuel.EdisonGeneric
{
    /// <summary>
    /// A deck brain with no deck rules at all: only the shared Edison rules from <see cref="EdisonBotBrain"/>.
    /// Used to check that any deck picks them up (Stardust, Royal Oppression, Machina Fortress, threat targeting).
    /// </summary>
    public sealed class EdisonGenericBotBrain : EdisonBotBrain
    {
        public EdisonGenericBotBrain(
            ICardEvaluator? cardEvaluator = null,
            IBoardEvaluator? boardEvaluator = null,
            IActionScorer? actionScorer = null)
            : base(cardEvaluator, boardEvaluator, actionScorer)
        {
        }
    }
}
