#pragma warning disable CS8618
namespace BotTest.Decks
{
    /// <summary>
    /// Represents a deck option with an ID and name.
    /// </summary>
    public class DeckOption
    {
        public object Id { get; set; }
        public string Name { get; set; }
    }

    /// <summary>
    /// Enum to identify available Master Duel decks.
    /// </summary>
    public enum MasterDuelDeck
    {
        BlueEyes,
        DarkMagician,
        SwordSoul,
        HandTraps,
        Blackwings
    }

    /// <summary>
    /// Enum to identify available Speed Duel decks.
    /// </summary>
    public enum SpeedDuelDeck
    {
        SuperDuck,
        MachineAngelAscension,
        Amazoness
    }

    /// <summary>
    /// Enum to identify available Speed Duel decks.
    /// </summary>
    public enum RushDuelDeck
    {
        SevensRoad,
        CustomRushDeck
    }

    /// <summary>
    /// Manages deck names and hashes, providing access to deck data and options.
    /// </summary>
    public static class DeckManager
    {
        private static readonly Dictionary<MasterDuelDeck, (string DeckName, string DeckHash)> _decks = new()
        {
            {
                MasterDuelDeck.BlueEyes,
                (
                    DeckName: "Blue-Eyes",
                    DeckHash: "M2cwO76VaXuDL/PiPRNZl2tYM77c7c00/7c1S1KpEyuIXrwynHUpEC8DYpAcCN/svcRYwWvLzJgoAFFbs4MJhpsfHGSBYcM7gozue2oZYHgH1J6EDF7GrWV8jGsrtRiuSR9kTO+zZemaxcCclLWZGaQHhEH2g/CzOgWGs07hrKYXJjDDcLBZARMMM8U0wfHk3mCmvCJdhuMLrzJnRSsygtwOAA=="
                )
            },
            {
                MasterDuelDeck.DarkMagician,
                (
                    DeckName: "Dark Magician",
                    DeckHash: "M2Pg6L/AlCiRw/AwzIVp1wVGlhU/LzEKqaiygMQnWrEyXNjkyAhiz3Y/zrxhEyPDui9nmDKmngfTETNzmUD6uC48A8vnWUszsMj2MIPYvzYKsTjHpbLc3fSYESQP0vN6QiMrSD2I/eaHHgPIXpDc4ycVDLFnXzDA1IPsAcmB7Nixby5T0EtB1msT/jGtEnRkCv0SwfCQ9T7rTqA4iG95KoHlMZAPYlfNrWE6b17H8gjIB+kFAA=="
                )
            },
            {
                MasterDuelDeck.SwordSoul,
                (
                    DeckName: "SwordSoul",
                    DeckHash: "M2ewnRXLrM0Tx+y54yHzEn8DVhje1JXKCMIr+iaxwjBIHQgvNjJkhOGcDG9mGF6vfI8BhivP3GQFYXteKSYYBtkFwk48M1hB9oFwTnMnIwwrecxkgGEmrUdgfLcmhSlMl481ueUQGHcsV2KszJrAcOSNDwPDUgUWEI5m+seyz2wy0/b9SSxbjD8wgrDphQlg/wAA"
                )
            },
            {
                MasterDuelDeck.HandTraps,
                (
                    DeckName: "HandTraps",
                    DeckHash: "s2Fer3yPAYaVPGbC8XWp+Yww/LdzDisMbzI6A8dyLgFMft9LGWE4v+w9AwxfPFTB+s5tAwsMGy2qZQDhtY+4mGD4gpwRKwh7f0pnguGXZ06wwrDFxmQWGNbklmKG4aDvXXC8wX0THD+MmAvHpuzNTDAcMfkok7B7KgOIvlR8jRWGQX4GAA=="
                )
            },
            {
                MasterDuelDeck.Blackwings,
                (
                    DeckName: "Blackwings",
                    DeckHash: "M2eMPvCIeT1zAwsMf+1oZIbhI3teM8HwIw8RVhjmXSHHcDI/hhGGZ3SEsoDw+yYxhrAsW9YSd23G/H2GLDB89ds1hvfqgQxeLwtZQThu8QFGEL4XasgCwuVxhswwLHRzAVNnQDfrb5YKpn7j28xBn3awXH9syVKj/pvRqTOT8TuLBcvv6R6s+5SXM7GHyrJs35/E8uZEAYP93KksIDzj5n+mq0zTmGxOTWUEmQMA"
                )
            }
        };

        private static readonly Dictionary<SpeedDuelDeck, (string DeckName, string DeckHash)> _SDdecks = new()
        {
            {
                SpeedDuelDeck.SuperDuck,
                (
                    DeckName: "Super Duck",
                    DeckHash: "k2UrsbZkv3n/IqNv3AGG901iDPwskxlBGCTm93YHKwyfiuEB453hP5l7NziypLn/ZgSpB6lb8F+Vpf//EgYQNtHfxgwyC4S7p4sz3NnQzQLCc67HMhRaazKZFMaDzQDpB9lzePJNMAbpA9kDAA=="
                )
            },
            {
                SpeedDuelDeck.MachineAngelAscension,
                (
                    DeckName: "Machine Angel Ascension",
                    DeckHash: "k2IrsLZkLxBNZQRhKe18ZhDeuL+MFYT7mmexwLBSgjkDDM8P8QPjL7XRrDD8Xj2Q4a9XEhMIP+Sax9g9XZzhzoZulqMJcxhMCuNZD0++ySh6ZgMTDMtqJzODMMh+AA=="
                )
            }
            ,
            {
                SpeedDuelDeck.Amazoness,
                (
                    DeckName: "Amazoness",
                    DeckHash: "k2F7rx7IsGz2F0YQblGtY1iT9hmOG35wssKwb0sWMwzv2/CDBYbbeG6wgjCb1EcGEP5SG8162tqSvXu6OMOdDd0sc67HMjx/+4PJpDCe1dvmJDPIDhD2+17KCMIbpH4z+8YdANsFAA=="
                )
            }
        };

        private static readonly Dictionary<RushDuelDeck, (string DeckName, string DeckHash)> _RDdecks = new()
        {
            {
                RushDuelDeck.SevensRoad,
                (
                    DeckName: "Sevens Road",
                    DeckHash: "s2KY+0STHYZ1V2jAcaaLJhwbAfkgvJVbC46fbNeA44uPEFj3uBYcb9ytwa65AoEtz0Ow8TJNOL4ENAuGw45D8FluBDYF6oPhjUD+eiS8CQm/0EPgpVYI7H0cgkHqAQ=="
                )
            },
            {
                RushDuelDeck.CustomRushDeck,
                (
                    DeckName: "Custom Rush Deck",
                    DeckHash: "M2S4wKnJHvxOC46fXUBgPwdtOO6cogXHG2IROPAVAq+LReAiYQS+slQbjhW9tOBYe5Y2HGshYXUkbO6BwL+3a8BxTKEWHIP8AAA="
                )
            }
        };

        /// <summary>
        /// Gets the deck name for the specified Master Duel deck ID.
        /// </summary>
        /// <param name="deckId">The deck ID.</param>
        /// <returns>The deck name.</returns>
        /// <exception cref="KeyNotFoundException">Thrown if the deck ID is not found.</exception>
        public static string GetDeckName(MasterDuelDeck deckId)
        {
            return _decks[deckId].DeckName;
        }

        /// <summary>
        /// Gets the deck hash for the specified Master Duel deck ID.
        /// </summary>
        /// <param name="deckId">The deck ID.</param>
        /// <returns>The deck hash.</returns>
        /// <exception cref="KeyNotFoundException">Thrown if the deck ID is not found.</exception>
        public static string GetDeckHash(MasterDuelDeck deckId)
        {
            return _decks[deckId].DeckHash;
        }

        /// <summary>
        /// Gets a list of available Master Duel deck options for selection.
        /// </summary>
        /// <returns>A list of DeckOption objects containing DeckId and DeckName.</returns>
        public static IEnumerable<DeckOption> GetMasterDuelDeckOptions()
        {
            return _decks.Select(d => new DeckOption { Id = d.Key, Name = d.Value.DeckName });
        }

        /// <summary>
        /// Gets the deck name for the specified Speed Duel deck ID.
        /// </summary>
        /// <param name="deckId">The deck ID.</param>
        /// <returns>The deck name.</returns>
        /// <exception cref="KeyNotFoundException">Thrown if the deck ID is not found.</exception>
        public static string GetDeckName(SpeedDuelDeck deckId)
        {
            return _SDdecks[deckId].DeckName;
        }

        /// <summary>
        /// Gets the deck hash for the specified Speed Duel deck ID.
        /// </summary>
        /// <param name="deckId">The deck ID.</param>
        /// <returns>The deck hash.</returns>
        /// <exception cref="KeyNotFoundException">Thrown if the deck ID is not found.</exception>
        public static string GetDeckHash(SpeedDuelDeck deckId)
        {
            return _SDdecks[deckId].DeckHash;
        }

        /// <summary>
        /// Gets a list of available Speed Duel deck options for selection.
        /// </summary>
        /// <returns>A list of DeckOption objects containing DeckId and DeckName.</returns>
        public static IEnumerable<DeckOption> GetSpeedDuelDeckOptions()
        {
            return _SDdecks.Select(d => new DeckOption { Id = d.Key, Name = d.Value.DeckName });
        }

        /// <summary>
        /// Gets the deck name for the specified Rush Duel deck ID.
        /// </summary>
        /// <param name="deckId">The deck ID.</param>
        /// <returns>The deck name.</returns>
        /// <exception cref="KeyNotFoundException">Thrown if the deck ID is not found.</exception>
        public static string GetDeckName(RushDuelDeck deckId)
        {
            return _RDdecks[deckId].DeckName;
        }

        /// <summary>
        /// Gets the deck hash for the specified Rush Duel deck ID.
        /// </summary>
        /// <param name="deckId">The deck ID.</param>
        /// <returns>The deck hash.</returns>
        /// <exception cref="KeyNotFoundException">Thrown if the deck ID is not found.</exception>
        public static string GetDeckHash(RushDuelDeck deckId)
        {
            return _RDdecks[deckId].DeckHash;
        }

        /// <summary>
        /// Gets a list of available Rush Duel deck options for selection.
        /// </summary>
        /// <returns>A list of DeckOption objects containing DeckId and DeckName.</returns>
        public static IEnumerable<DeckOption> GetRushDuelDeckOptions()
        {
            return _RDdecks.Select(d => new DeckOption { Id = d.Key, Name = d.Value.DeckName });
        }
    }
}
