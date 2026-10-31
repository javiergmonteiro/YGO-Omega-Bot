namespace BotTest.Game.AI.Knowledge
{
    /// <summary>
    /// Format/matchup knowledge shared by every Edison deck brain: card IDs and the tables that
    /// describe how other cards behave. Adding a known card is adding a line here, not new logic.
    /// IDs verified against the game's /card-info endpoint.
    /// </summary>
    public static class CardKnowledge
    {
        public const int TokenType = 0x4000;
        public const int TunerType = 0x1000;
        public const int DarkAttribute = 0x20;
        public const int LightAttribute = 0x10;

        // Generic Edison staples the shared rules reason about.
        public const int StardustDragon = 44508094;
        public const int MachinaFortress = 5556499;
        public const int VayuTheEmblemOfHonor = 72714392;
        public const int NecroGardna = 4906301;
        public const int DDCrow = 24508238;
        public const int DarkArmedDragon = 65192027;
        // Gains 300 ATK in the Damage Step when it attacks a monster (attack math counts it).
        public const int LightswornPaladinJain = 96235295;
        public const int BlackRoseDragon = 73580471;
        // Wins any battle for a LIGHT monster: seen in their hand, our thief doesn't attack their LIGHT.
        public const int Honest = 37742478;
        // Their Roar closes our attacks for the turn: synchros that only pay off by attacking lose their bonus.
        public const int ThreateningRoar = 36361633;
        public const int BlackwingArmorMaster = 69031175;

        // Generic Edison Extra Deck: picked by situation, not by a fixed list (see EdisonBotBrain.ScoreSynchro).
        public const int GoyoGuardian = 7391468;
        public const int BlackwingArmedWing = 76913983;
        public const int DarkEndDragon = 88643579;
        public const int SilverwindTheAscendant = 33236860;
        public const int RedDragonArchfiend = 70902743;
        public const int AllyOfJusticeCatastor = 26593852;
        public const int BrionacDragonOfTheIceBarrier = 50321816;
        public const int MagicalAndroid = 43385557;
        public const int CardTrooper = 85087012;
        // Lightsworn Druid: Lightsworn monsters can't be targeted by card effects while it's face-up.
        public const int LightswornDruid = 7183297;
        public const int LightswornWulf = 58996450;
        public static readonly int[] LightswornNames = { 94381039, 58996450, 59019082, 96235295, 22624393, 44178906, 7183297, 95503707, 21502816 };

        public const int UpstartGoblin = 70368879;
        public const int AllureOfDarkness = 1475311;
        public const int BrainControl = 87910998;
        public const int HeavyStorm = 19613556;
        public const int BookOfMoon = 14087893;
        public const int MysticalSpaceTyphoon = 5318639;

        public const int BottomlessTrapHole = 29401950;
        public const int DimensionalPrison = 70342110;
        public const int IcarusAttack = 53567095;
        public const int MirrorForce = 44095762;
        public const int StarlightRoad = 58120309;
        public const int RoyalOppression = 93016201;
        public const int SolemnJudgment = 41420027;

        // Extra threat on top of ATK for monsters whose effect matters more than their stats.
        public static readonly IReadOnlyDictionary<int, int> ThreatBonus = new Dictionary<int, int>
        {
            [StardustDragon] = 4000,
            [77585513] = 3000,  // Jinzo
            [57774843] = 3000,  // Judgment Dragon
            [DarkArmedDragon] = 3000,
            [7391468] = 2000,   // Goyo Guardian
            [88643579] = 2000,  // Dark End Dragon
            [50321816] = 2000,  // Brionac
            [9748752] = 1500,   // Caius the Shadow Monarch
            [GladiatorBeastBestiari] = 3000,   // priority one vs Gladiators
            [48156348] = 2500,  // Gladiator Beast Gyzarus
            [GladiatorBeastHeraklinos] = 2500,
            [33420078] = 1500,  // Plaguespreader Zombie
            [23205979] = 1000,  // Spirit Reaper
            [44330098] = 1000,  // Gorz
            [DestinyHeroPlasma] = 2500, // negates our monsters' effects and steals one as equip
        };

        // Book of Moon prefers these over the attacker: face-down, Plasma stops negating our monsters and loses the
        // monster it took as an equip.
        public static readonly int[] BookOfMoonPriority = { DestinyHeroPlasma };

        // Our cards whose effect destroys: Stardust Dragon negates them.
        public static readonly int[] DestructionCards =
        {
            MirrorForce, IcarusAttack, HeavyStorm, MysticalSpaceTyphoon, BottomlessTrapHole,
            SolemnJudgment, RoyalOppression, DarkArmedDragon, BlackRoseDragon
        };

        // Punishes being targeted by an opponent's monster effect (looks at our hand and discards 1).
        public static readonly int[] PunishesMonsterTargeting = { MachinaFortress };

        // Edison ruling: these GY effects can't be negated by Royal Oppression. They banish themselves as cost,
        // which is how we detect them (the API doesn't say which card started a chain link).
        public static readonly int[] UnnegatableByRoyalOppression = { VayuTheEmblemOfHonor };

        // What Royal Oppression can negate, by card name (prefix, so errata versions match too):
        // monsters that only arrive by special summon from the hand,
        public static readonly string[] SelfSpecialSummoners =
        {
            "Quickdraw Synchron", "Cyber Dragon", "Gorz the Emissary of Darkness", "Tragoedia", "Cyber Valley",
            "Chaos Sorcerer", "Black Luster Soldier", "Dark Armed Dragon", "Judgment Dragon", "Machina Fortress",
        };

        // Self special summoners that can also be tribute summoned: a tribute in the same window means a normal summon.
        public static readonly string[] AlsoTributeSummoned =
        {
            "Machina Fortress", "Cyber Dragon", "Gorz the Emissary of Darkness", "Tragoedia", "Quickdraw Synchron",
        };

        // monsters whose effect special summons (the chain they start is what it negates),
        public static readonly string[] SpecialSummonEffectMonsters =
        {
            "Lonefire Blossom", "Debris Dragon", "Dandylion", "Plaguespreader Zombie", "Gladiator Beast",
            "Blackwing - Shura", "Blackwing - Blizzard",
        };

        // Banish themselves and come back by their own effect in the owner's Standby Phase (a special summon).
        public static readonly string[] ReturnFromBanishInStandby = { "Drill Warrior" };

        // and spells/traps that special summon.
        public static readonly string[] SpecialSummonSpellTraps =
        {
            "Monster Reborn", "Call of the Haunted", "Premature Burial", "Scapegoat", "Future Fusion",
        };

        // Cards we don't mind losing to a forced discard.
        public static readonly int[] HappyToDiscard = { VayuTheEmblemOfHonor, NecroGardna };

        // Cards that hurt the most to lose from hand.
        public static readonly int[] KeyCards =
        {
            DarkArmedDragon, BrainControl, HeavyStorm, SolemnJudgment, MirrorForce, DimensionalPrison, IcarusAttack
        };

        public const int PlantRace = 0x400;
        public const int TytannialPrincessOfCamellias = 11819616;

        // Destroying these in battle triggers an effect against us (Machina Fortress destroys one of our cards).
        public static readonly int[] PunishesBattleDestruction = { MachinaFortress };

        // Negate any card/effect that targets a card on the field, by tributing a face-up Plant (can be itself).
        public static readonly int[] NegatesFieldTargeting = { TytannialPrincessOfCamellias };

        // Our generic cards that target a card on the field when activated.
        public static readonly int[] TargetsOnField =
        {
            MysticalSpaceTyphoon, BookOfMoon, BrainControl, DimensionalPrison, IcarusAttack,
            DarkArmedDragon, DarkEndDragon, BrionacDragonOfTheIceBarrier
        };

        public const int TorrentialTribute = 53582587;
        public const int TrapDustshoot = 64697231;
        public const int GorzTheEmissaryOfDarkness = 44330098;
        public const int DebrisDragon = 14943837;
        public const int DestinyHeroPlasma = 83965310;
        public const int JudgmentDragon = 57774843;
        public const int KalutMoonShadow = 85215458;

        // Gladiator Beasts: Bestiari is the key piece (backrow removal on tag-in, and it makes Gyzarus).
        public const string GladiatorBeastPrefix = "Gladiator Beast";
        public const int GladiatorBeastBestiari = 41470137;
        public const int GladiatorBeastGyzarus = 48156348;
        public const int GladiatorBeastHeraklinos = 27346636;
        public static readonly int[] GladiatorKeyMonsters = { GladiatorBeastBestiari, GladiatorBeastGyzarus, GladiatorBeastHeraklinos };

        public const int MachinaForce = 58054262;

        // Negates every trap on the field for the rest of the turn.
        public const int TrapStun = 59616123;

        // Monsters whose summon searches the deck: what keeps an engine going while their GY is still empty.
        public static readonly int[] Searchers = { 42940404 /* Machina Gearframe */, 40044918 /* Elemental HERO Stratos */ };

        // Which monster to take out of their hand (Trap Dustshoot): hand traps that punish our attacks first,
        // then the pieces that start their combos next turn. Added on top of threat and stats.
        public static readonly IReadOnlyDictionary<int, int> HandPriority = new Dictionary<int, int>
        {
            // Hand traps against our attacks
            [44330098] = 4000,  // Gorz the Emissary of Darkness
            [98777036] = 3500,  // Tragoedia
            [Honest] = 3000,    // Honest (any battle with their LIGHT goes their way; left in hand it gave them lethal)
            [85215458] = 2500,  // Blackwing - Kalut the Moon Shadow (mirror)
            // Combo starters
            [20932152] = 2500,  // Quickdraw Synchron
            [57774843] = 2500,  // Judgment Dragon
            [DarkArmedDragon] = 2500,
            [14943837] = 2000,  // Debris Dragon
            [42940404] = 2000,  // Machina Gearframe
            [48686504] = 2600,  // Lonefire Blossom (fetches a Plant from the deck, e.g. Tytannial, for free)
            [40044918] = 2700,  // Elemental HERO Stratos (searches a HERO, e.g. Destiny HERO - Plasma)
            [58054262] = 2700,  // Machina Force (level 10 discard: revives Machina Fortress for free)
            [9748752] = 1800,   // Caius the Shadow Monarch
            [73125233] = 1800,  // Raiza the Storm Monarch
            [26205777] = 1800,  // Thestalos the Firestorm Monarch
            [85087012] = 1500,  // Card Trooper
            [33420078] = 1500,  // Plaguespreader Zombie
            [58820853] = 1500,  // Blackwing - Shura the Blue Flame
            [2009101] = 1500,   // Blackwing - Gale the Whirlwind
            [49003716] = 1000,  // Blackwing - Bora the Spear
            [DestinyHeroPlasma] = 1000, // +2500 when they have the 3 monsters to tribute for it (see HandPickScore)
        };

        // Backrow that punishes committing too much (attacking with everything / summoning more).
        // Checked against the banlist and what they've already used (CardCounter).
        public static readonly int[] PunishesOverextension = { MirrorForce, TorrentialTribute };

        // Destroy a non-DARK monster they battle (start of the Damage Step), whatever its ATK.
        public static readonly int[] DestroysNonDarkInBattle = { AllyOfJusticeCatastor };

        // Effects that, used on a monster we stole, end up helping its owner (Stardust tributes itself and its
        // End Phase revival is for whoever activated it from *their* GY... the owner's): let it be destroyed instead.
        public static readonly int[] DontUseWhenStolen = { StardustDragon };

        // Tuners that can't be synchro material while their effects are active (Vayu: only when negated).
        public static readonly int[] CannotBeSynchroMaterial = { VayuTheEmblemOfHonor };

        // Take the monster they destroy in battle: attack with them first, into the best monster to steal.
        public static readonly int[] StealsOnBattle = { GoyoGuardian };

        // Traps that answer an attack declaration (banish, destroy, reflect or stop the attacker).
        public static readonly int[] AttackResponseTraps =
        {
            DimensionalPrison, MirrorForce, 56120475 /* Sakuretsu Armor */, 62279055 /* Magic Cylinder */,
            14315573 /* Negate Attack */, 12607053 /* Waboku */
        };

        // Monsters whose effect fires as they're summoned (field wipe, backrow removal, discard...): answering after the
        // summon (Bottomless, Prison) is too late, Solemn Judgment on the summon itself is the answer.
        public static readonly int[] SolemnPriorityTargets =
        {
            BlackRoseDragon, 9748752 /* Caius */, 73125233 /* Raiza */, 26205777 /* Thestalos */,
            GladiatorBeastBestiari, GladiatorBeastGyzarus
        };

        // Can't be destroyed by battle and take no battle damage: they should always attack.
        public static readonly int[] FearlessAttackers = { BlackwingArmorMaster };
    }
}
