using OmegaBotWrapper;
using OmegaBotWrapper.Enum;
using OmegaBotWrapper.Struct;

namespace BotTest.Game.Field
{
    public partial class GameField
    {
        private enum FieldPerspective
        {
            Bot,
            Player
        }

        private async Task<FieldInfo> RefreshFieldAsync(FieldPerspective perspective)
        {
            // Only the bot's view is kept as OwnSide/EnemySide: caching the player's view swapped both sides for every
            // rule reading CurrentField after an enemy list (2026-10-24: Honest never fired, it saw our monster as theirs).
            switch (perspective)
            {
                case FieldPerspective.Bot:
                    _fieldInfo = await _botWrapper.GetBotFieldInfo();
                    return _fieldInfo;
                case FieldPerspective.Player:
                    return await DuelBotWrapper.GetPlayerFieldInfo();
                default:
                    throw new ArgumentOutOfRangeException(nameof(perspective));
            }
        }

        private Task<DynamicCard> GetFieldCardAsync(
            FieldPerspective perspective,
            int controller,
            CardLocation location,
            int sequence)
        {
            return perspective switch
            {
                FieldPerspective.Bot => _botWrapper.GetBotFieldCardInfo(controller, (int)location, sequence),
                FieldPerspective.Player => DuelBotWrapper.GetPlayerFieldCardInfo(controller, (int)location, sequence),
                _ => throw new ArgumentOutOfRangeException(nameof(perspective))
            };
        }

        private static CardInfo GetSide(FieldInfo field, bool isOwnSide)
        {
            return isOwnSide ? field.OwnSide : field.EnemySide;
        }

        private static bool IsLinearLocation(CardLocation location)
        {
            return location is CardLocation.Hand
                or CardLocation.Grave
                or CardLocation.Removed
                or CardLocation.Deck
                or CardLocation.Extra;
        }

        private static int[] GetLinearZoneCards(CardInfo side, CardLocation location)
        {
            return location switch
            {
                CardLocation.Hand => side.Hand,
                CardLocation.Grave => side.Grave,
                CardLocation.Removed => side.Banish,
                CardLocation.Deck => side.Main,
                CardLocation.Extra => side.Extra,
                _ => Array.Empty<int>()
            };
        }

        private static CardArray[] GetStructuredZones(CardInfo side, CardLocation location)
        {
            return location switch
            {
                CardLocation.MonsterZone => side.MonsterZones,
                CardLocation.SpellZone => side.SpellZones,
                CardLocation.PendulumZone => side.SpellZones,
                CardLocation.FieldZone => side.SpellZones,
                _ => Array.Empty<CardArray>()
            };
        }

        private static bool IsMissingCardException(Exception ex)
        {
            return ex.Message.Contains("404") || ex.Message.Contains("No card found");
        }

        private async Task<List<DynamicCard>> GetCardsInLocationAsync(
            FieldPerspective perspective,
            CardLocation location,
            int controller = 0)
        {
            var field = await RefreshFieldAsync(perspective);
            var side = controller == 0 ? field.OwnSide : field.EnemySide;
            var cards = new List<DynamicCard>();

            if (IsLinearLocation(location))
            {
                var cardIds = GetLinearZoneCards(side, location);

                for (int sequence = 0; sequence < cardIds.Length; sequence++)
                {
                    if (cardIds[sequence] <= 0)
                    {
                        continue;
                    }

                    try
                    {
                        var card = await GetFieldCardAsync(perspective, controller, location, sequence);
                        if (card.ID > 0)
                        {
                            cards.Add(card);
                        }
                    }
                    catch (Exception ex) when (IsMissingCardException(ex))
                    {
                        continue;
                    }
                }

                return cards;
            }

            var zones = GetStructuredZones(side, location);
            for (int zoneIndex = 0; zoneIndex < zones.Length; zoneIndex++)
            {
                if (!zones[zoneIndex].Cards.Any(cardId => cardId > 0))
                {
                    continue;
                }

                try
                {
                    var card = await GetFieldCardAsync(perspective, controller, location, zoneIndex);
                    if (card.ID > 0)
                    {
                        cards.Add(card);
                    }
                }
                catch (Exception ex) when (IsMissingCardException(ex))
                {
                    continue;
                }
            }

            if (zones.Length == 0)
            {
                throw new ArgumentException($"Unsupported location: {location}");
            }

            return cards;
        }

        private async Task<List<DynamicCard>> GetCardsInZoneAsync(
            FieldPerspective perspective,
            CardLocation location,
            bool isOwnSide = true)
        {
            var controller = isOwnSide ? 0 : 1;
            return await GetCardsInLocationAsync(perspective, location, controller);
        }

        private static int GetZoneCardCount(CardInfo side, CardLocation location)
        {
            return location switch
            {
                CardLocation.MonsterZone => side.MonsterZones.Sum(zone => zone.Cards.Length),
                CardLocation.SpellZone => side.SpellZones.Sum(zone => zone.Cards.Length),
                CardLocation.Hand => side.Hand.Length,
                CardLocation.Grave => side.Grave.Length,
                CardLocation.Removed => side.Banish.Length,
                CardLocation.Deck => side.Main.Length,
                CardLocation.Extra => side.Extra.Length,
                _ => 0
            };
        }

        private async Task<int> GetLifePointsAsync(FieldPerspective perspective, bool isOwnSide = true)
        {
            var field = await RefreshFieldAsync(perspective);
            return GetSide(field, isOwnSide).LifePoints;
        }

        private async Task<bool> IsUnderAttackAsync(FieldPerspective perspective, bool isOwnSide = true)
        {
            var field = await RefreshFieldAsync(perspective);
            return GetSide(field, isOwnSide).UnderAttack;
        }

        private async Task<DynamicCard> GetBattlingMonsterAsync(FieldPerspective perspective, bool isOwnSide = true)
        {
            var field = await RefreshFieldAsync(perspective);
            var side = GetSide(field, isOwnSide);
            return side.HasBattlingMonster ? side.BattlingMonster : default;
        }
    }
}
