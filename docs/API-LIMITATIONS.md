# Limitaciones de la API de YGO Omega y trampas del framework

Leé esto antes de tocar reglas del bot. Varios errores se repitieron porque una regla daba por hecho algo que la API no cumple.

Cada entrada tiene:
- **Qué pasa:** el comportamiento real, verificado con logs.
- **Consecuencia:** cómo hay que escribir las reglas.

Última actualización: 2026-10-14. Hay un reporte para los desarrolladores de Omega (issues #0–#5) en https://claude.ai/code/artifact/be440741-8b11-4508-82f2-05e05f1677ae.

---

## 1. Bugs y limitaciones de la API (no se arreglan desde el bot)

### #0. El ataque siempre lo hace `AttackableCards[0]`
- **Qué pasa:** `SelectBattleCmd` ignora el índice de atacante que se responde. El juego ataca siempre con el primero de la lista. Confirmado en 18 de 18 casos, en 10 sesiones. La hipótesis de "orden de llegada" se probó con logs y se descartó.
- **Consecuencia:** el bot solo decide sobre `attackers[0]`, con `AttackWith(card) => card.DeclareAttack()`. En `OnBattleCmdAsync`, `attackers` queda truncado a ese primero y `allAttackers` guarda la lista completa.
  - **Si el primero no ataca (M2/End), no ataca nadie.** No escribas reglas del tipo "este monstruo se queda quieto" si hay otros esperando detrás (`allAttackers.Skip(1).Any(c => !c.Attacked)`). Eso hace perder toda la Battle Phase. Ya pasó en el turno 9 del 2026-10-14.
  - No se puede elegir el orden de ataque: ni "primero el más débil" ni "Shura primero". Lo único que se elige es el objetivo del primero, o no atacar.
  - Antes de leer `attackers[0]`, chequeá `attackers.Length > 0`. Puede llegar un BattleCmd con `Attackable=0` (por ejemplo, con Book of Moon en la mano), y eso ya crasheó el bot.

### #0b. Activaciones desde BattleCmd: sin verificar (precaución)
- **Qué pasa:** no sabemos si el índice de `ActivableCards` en un BattleCmd se respeta o tiene el mismo bug que el ataque.
- **Consecuencia:** el bot solo activa desde BattleCmd (por ejemplo, Icarus en su battle step) si la carta es `ActivableCards[0]`. Si se confirma que el índice anda, se puede relajar.

### #0c. No se controla el orden de ataque con la zona
- **Qué pasa:** la lista `AttackableCards` viene ordenada por zona, de izquierda a derecha, y ataca la primera (#0). En `SelectPlace` el framework siempre responde `seq 0`, pero el juego coloca igual donde quiere: el primer monstruo va al centro (el `Filter` siguiente sale `0x1B`).
- **Consecuencia:** elegir zona no sirve, hoy, para que Shura u otro monstruo ataque primero. Si algún día se confirma la codificación correcta de la zona, ahí está la solución al bug #0.

### #1. El replay de batalla nunca llega al bot custom
- **Qué pasa:** cuando cambia el campo durante un ataque (Scapegoat, etc.), el juego no le pregunta al bot si quiere repetir el ataque. Se confirmó con la traza completa de estados: no llega ningún YesNo ni ningún estado sin manejar. El siguiente BattleCmd ya muestra a ese atacante como no disponible.
- **Consecuencia:** ese ataque se pierde y el bot no lo puede evitar.
  - Al principio afirmé esto sin pruebas y el usuario lo cuestionó con razón. Cualquier hipótesis de este tipo se verifica con la traza `[Trace]` de `DuelBotWrapper` antes de afirmarla.

### #2. No se puede elegir ir primero o segundo
- **Qué pasa:** al bot custom no le llega ningún prompt para elegir turno, ni siquiera al empezar el game 2. La IA interna sí puede elegir.
- **Consecuencia:** no hay regla posible. Está pendiente de los desarrolladores.

### #3. Las cartas reveladas de la mano llegan ocultas (ID -3)
- **Qué pasa:** en la selección de Trap Dustshoot, las cartas de la mano rival llegan con ID -3 o 0.
- **Consecuencia:** se leen por posición con `RevealHandCardsAsync`: primero la perspectiva del bot y después la del jugador. Lo que se ve se guarda en `_revealedEnemyHand`.
  - **Esa información envejece.** Usá `StillInRevealedHand(id)`, no `_revealedEnemyHand.Contains(id)`. La carta deja de contar en cuanto aparece una copia en público (GY, desterrada o campo). Ya pasó: una Prison seteada y destruida varios turnos antes seguía frenando ataques.

### #4. Las versiones con errata tienen otro ID, con `Alias = 0`
- **Qué pasa:** por ejemplo, Quickdraw Synchron es 20932152 y su versión del formato es 20932172. Son cartas con errata, no artes alternativos, y el `Alias` no apunta a la original.
- **Consecuencia:** nunca compares IDs a mano. Usá `card.IsCode(...)`, `CardIdentity.Same(a, b)` o `CardIdentity.Canonical(id)`, que comparan por nombre vía `/card-info`. `NormalizeAsync()` corre en cada decisión. Los conteos contra la banlist también van por identidad (`CardCounter.OpponentCopiesLeft`).

### #5. `/room-info` no trae el nombre de la banlist
- **Qué pasa:** solo trae `LFListHash`.
- **Consecuencia:** el mapeo hash → lista está en `BotTest/Banlists/rooms.json` (`4064611988` = edison, que también es el default). Si cambia el formato, agregá el hash ahí.

---

## 2. Convenciones de la API (verificadas, fáciles de confundir)

| Dato | Significado |
|---|---|
| `CurrentCoordinates.Controller` en datos de selección | **0 = bot, 1 = rival** (relativo al bot) |
| Header `LastChainPlayer` / `LastSummonPlayer` | 0 = bot, 1 = rival, -1 = desconocido |
| Header `CurrentPlayer` | 0 = turno del bot |
| Respuesta idle/battle | `ushort action, ushort index` |
| BotState codes | 1 BattleCmd, 2 SelectCard, 4 SelectChain, 7 EffectYn, 8 IdleCmd, 10 SelectPlace, 11 Position, 12 Sum, 19 WaitingForDeck, 20 YesNo |

- **Cartas boca abajo del rival:** en los datos de selección llegan con ID 0 o -3. Las cartas enemigas se identifican por **controller + sequence**, nunca por ID; eso incluye `_forcedAttackTarget` en `ATTACKTARGET`. Ya pasó: Icarus apuntó al propio Solemn del bot por comparar por ID.
- **Hints de selección:** las reglas de objetivos tienen que cubrir todos los que aparecen: `FACEUP`, `CONTROL`, `TARGET`, `DESTROY`, `REMOVE`, `TOGRAVE`, `RTOHAND`, `POSCHANGE`, `TODECK`, `DISCARD`, `RELEASE`, `TRIBUTE`, `SMATERIAL`, `ATTACKTARGET`, `SPSUMMON`, `ATOHAND`. Ya pasó: Gale y Brain Control apuntaron a tokens de 0 ATK porque no se manejaba `FACEUP`/`CONTROL`.
- **Origen de la cadena:** Royal Oppression no puede saber solo con el header si la acción es propia. Para eso están `ChainTracker.Source(header)` y `OwnActionPendingNow`, sellados con turno y fase para que no queden flags viejos.

---

## 3. Trampas del framework (`DeckBotBrain` / `GameField`)

- **Perspectiva invertida:** `GetPlayerCardsInZone*` lee el lado del **jugador humano** (el rival), no el del bot. El lado propio sale de `CurrentField.GetCardsInLocation(...)`. Para la zona de magias y trampas propia está `GetOwnSpellCardsAsync`.
- **Listas enemigas con IDs reales:** las listas del enemigo vienen desde la perspectiva del jugador, donde sus cartas boca abajo muestran el ID real. Las reglas no deben espiar esos IDs (ver el punto de controller + sequence de arriba).
- **`SelectSum` del framework:** ignora el tuner obligatorio y los combos de una sola carta. Por eso se usa `FindCheapestSum` propio.
- **Ataque base:** saltea a los monstruos rivales negados (`IsDisabled`), por ejemplo con Plasma. Por eso se usa `DecideGenericAttackAsync` propio.
- **Excepciones:** antes, cualquier excepción en una decisión terminaba el loop del heartbeat y el bot moría. Ahora las decisiones de `EdisonBotBrain` pasan por `On*Async → Decide*CoreAsync`, con try/catch que vuelve a la respuesta base. Si agregás un override nuevo, que siga ese patrón.

---

### Fin de match: 404 en el heartbeat (normal)
- **Qué pasa:** al terminar el match, el bot desaparece de `/bot-list` y `/get-bot-state-header/{id}` devuelve 404. El log muestra `[ERROR] Heartbeat failed ... 404` seguido de `Duel finished.`.
- **Consecuencia:** no es un bug del bot, no hay que arreglarlo. Solo preocupa si el 404 aparece en medio de un duelo.

---

## 4. Cómo verificar antes de afirmar algo sobre la API

1. Mirá `logs/<fecha>_<mazo>/decisiones.log`. Las líneas `[Trace] Answered X with [hex]` y `Empty ... raw: {json}` muestran el estado crudo y la respuesta exacta.
2. Cruzalo con `duelo-gN.txt`, que es el log del juego: qué carta atacó, qué se reveló, qué se seteó.
3. Si el log no lo prueba, decilo y agregá trazas. No lo des por confirmado.
