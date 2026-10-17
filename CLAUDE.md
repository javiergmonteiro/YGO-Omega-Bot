# YGO-Omegabot

Bot for YGO Omega (Edison format, Blackwings deck) built on duelbotwrapper. The user (Javier) plays against it and leaves notes; rules get tuned from that feedback. Talk to him in Spanish (Argentina).

## Before changing rules
- Read `docs/API-LIMITATIONS.md`. It lists confirmed YGO Omega API bugs, bot-relative conventions, and framework pitfalls that past rules got wrong. Examples: attacks always use `AttackableCards[0]`, there is no battle replay, revealed hands arrive hidden, errata versions have different IDs, and some perspective helpers are inverted. Add any newly confirmed limitation there.
- Where the rules live:
  - Generic rules for any deck: `BotTest/Game/AI/Brains/EdisonBotBrain.cs`.
  - Deck strategy: `BotTest/Game/AI/Bots/MasterDuel/Blackwings/BlackwingsBotBrain.cs`.
  - Card lists and IDs: `BotTest/Game/AI/Knowledge/CardKnowledge.cs`.
- Javier prefers few, targeted rules (gated by matchup or situation) over broad changes that can break other plays.
- Compare cards with `IsCode` or `CardIdentity`, never with raw IDs, because errata versions have different IDs.

## Rules document (docs/REGLAS.md)
- `docs/REGLAS.md` describes every bot decision in plain Spanish for Javier. Read it first to know the intent of a rule before reading code.
- Every commit that changes a rule updates the matching section of `REGLAS.md` in the same commit (keep the style: bold decision, bullet conditions, `Código:` line).
- Javier leaves `> PROPUESTA:` and `> DUDA:` lines in it. At the start of a session, grep for them; implement or answer each one, then delete it.

## Reviewing a test session
Two steps, so the expensive model only reads summaries:
1. **Summarize (Sonnet).** Find the session folders not reviewed yet: no `resumen.md` inside, and newer than the last folder named in the git log. Launch one `log-reviewer` agent per folder (`.claude/agents/log-reviewer.md`, runs on Sonnet), all in parallel. Each writes `logs/<folder>/resumen.md`: notes, evidence, the rule that fired, verdict.
2. **Fix (main session).** Read only the `resumen.md` files. Group the notes by theme, decide the fixes, apply them, build, and commit naming the folders reviewed. Go back to the raw logs only to settle a specific doubt the summary leaves open.

Don't ask Javier to paste logs. If you do read raw logs, filter them, because the raw lines are expensive:
- Location: `logs/<date>_<deck>/`. Use the latest folder.
- Read in this order:
  1. `notas.txt`: his notes, with the turn they refer to.
  2. `decisiones.log`: keep only `[decision]`, `[nota]` and `[error]` lines. Skip `[motor]` Trace and raw JSON unless you're debugging an API question.
  3. `duelo-gN.txt`: the game's own duel log. Read it only around the noted turns.
- `[ERROR] Heartbeat failed ... 404` right before `Duel finished.` is normal at the end of a match.

## Build and run
- Build `BotTest/BotTest.csproj`, not the `.sln`, because the `.sln` references a missing test project.
- If the panel is open, `BotTest.exe` is locked:
  - To check that it compiles, build with `-o <scratch dir>`.
  - To build the real binary, ask him to close the panel first.
- He launches the bot with `Iniciar Bot.cmd`, which opens the control panel. The panel auto-connects when an AI_CUSTOM bot waits for a deck.
- `Empaquetar.cmd` builds the shareable `dist/BotYGOOmega.zip` with a self-contained publish.

## Git
- Commit locally after each round of fixes.
- Push only to the `github` remote (https://github.com/javiergmonteiro/YGO-Omega-Bot.git), and only when he says so.
- Never push to `origin`, the upstream GitLab repo.
