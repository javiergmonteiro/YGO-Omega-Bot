# Blackwings

Cómo juega el bot con Blackwings, encima de las reglas del Edison Brain. Las cartas genéricas (Solemn, Icarus, Book of Moon...) siguen las reglas del Edison Brain salvo lo que diga la sección "Cambios sobre el Edison Brain".


## Cambios sobre el Edison Brain

### Valor de cada carta en la mano (lo que "duele" perder)

**Blackwings valora distinto algunas cartas al desterrar (Allure) o descartar.**
- Kalut y Gale valen 3 (en vez de 2): son las cartas que más se extrañan.
- Dark Armed Dragon vale 4 si hay 2 o más DARK en el cementerio; si no, 2 (está muerto por varios turnos), así Allure lo destierra antes que un Blackwing que sí pega.
- Desempate: el orden de destierro del mazo (ver "Selecciones del mazo").

Código: `HandValue`, `BanishLastOrder`

### Brain Control

**Además de los planes generales, el robado vale como "uso del mazo":**
- Material de synchro con Gale (Gale en la mano, un Blackwing en el campo, y el nivel del robado + 3 da un synchro).
- Material de synchro con Blizzard (Normal Summon disponible y nivel del robado + 2 da un synchro).
- Tributo para Sirocco (Normal Summon disponible y Sirocco en la mano). Si el robado tiene 2000 de ATK o más, ataca primero y se tributa en la Main 2.
- ⚠ Para Sirocco acepta cualquier robado, aunque Sirocco podría invocarse sin tributo (propuesta pendiente: solo si el robado tiene más de 2000 de ATK).

Código: `CanUseStolenMonster`, `DecideMainPhaseAsync`

### Allure of Darkness

**Igual que en el Edison Brain; lo que cambia es qué destierra** (ver "Valor de cada carta en la mano" arriba): primero el valor más bajo, después el orden de destierro del mazo.

Código: `HandValue`, `BanishLastOrder`

### Batalla

**Kalut en la mano cambia la batalla:**
- Un Blackwing que no le gana a un monstruo pero con +1400 sí, ataca igual (Kalut entra en el damage step).
- Un monstruo que con +1400 le gana al más fuerte del rival se queda en ataque.
- Shura ataca primero a lo que destruye, contando el +1400.

Código: `BattleBoostInHand`, `CanBeBoosted`, `ShouldBoostWithKalut`



## Plan de juego

Orden en que el bot decide en la Main Phase (la primera regla que se cumple, juega y vuelve a preguntar; el bot repite el análisis después de cada jugada). Antes de todo esto corren las jugadas de apertura de las reglas generales (Upstart, Allure, Brain Control, etc.). Después de este plan corren los efectos de monstruo (Gale, DAD, Armor Master, etc.) y los sets de trampas.

1. **Invocación especial desde la mano: Dark Armed Dragon, Bora, Gale**, en ese orden.
   - Si la carta se puede invocar de especial desde la mano, la invoca.
   - Excepción general: no la baja si ya tenés un monstruo, el rival tiene 2+ cartas seteadas y todavía puede tener Mirror Force/Torrential. Los tuners (Gale) y los monstruos "importantes" (Extra Deck o 2300+ de ATK base) bajan igual.
2. **Invocación Synchro** (regla genérica con la lista de prioridad del mazo).
   - Se saltea si Sirocco ya usó su efecto este turno (el synchro no podría atacar).
   - Si los materiales pueden atacar primero y ganan todos, espera a la Main 2.
3. **Sirocco tributando un monstruo robado.**
   - Si tenés un monstruo robado en campo y Sirocco se puede invocar, lo invoca tributando el robado.
   - Pero si el robado ataca primero (Main 1, podés ir a Battle, está en ataque, no atacó, 2000+ de ATK), no invoca nada y espera a la Main 2. Además, en ese caso ninguna invocación normal se hace antes de la batalla (se reserva la invocación normal para Sirocco).
4. **Black Whirlwind antes de la invocación normal.**
   - Solo si después de bajarla va a haber una invocación normal que la dispare (ver "Invocaciones que cuentan").
   - Baja una copia por cada búsqueda que falta (Gale / Kalut que no tenés en mano), hasta 2.
5. **Invocación normal que arma un synchro con Gale este turno.**
6. **Blizzard con objetivo en el cementerio.**
7. **Prioridad de invocación normal**: Shura, Sirocco (solo con campo vacío), Bora, Gale, con la lógica de "monstruo débil / me lo guardo".
8. **Blizzard como cuerpo** (si no había objetivo y el campo está vacío).
9. **Vayu seteado.**
10. **Kalut como única jugada** (campo vacío y nada más que bajar).

Si nada aplica, devuelve "nada": pasa a efectos de monstruo, sets y Battle.

**Invocaciones que cuentan para el punto 4 (Black Whirlwind sube solo si va a haber una de estas):**
- Shura o Bora invocables.
- Sirocco invocable y 0 monstruos propios.
- Blizzard invocable y con objetivo en el cementerio.
- O bien "Kalut es la única jugada" (ver punto 10).
- ⚠ Gale no cuenta como invocación que justifique el Whirlwind (aunque sí tiene la regla de Gale-synchro, punto 5, que cuenta el Whirlwind ya boca arriba). Si la única carta invocable es Gale, el Whirlwind no sube antes.

**Regla del punto 5: invocación normal para synchro con Gale** (bajar al Blackwing que, con Gale, da un nivel de synchro disponible).
- Se prueba en este orden: Sirocco (solo sin monstruos propios), Shura, Bora.
- Requisito: nivel del monstruo + 3 (Gale) debe ser un nivel de synchro que tenga el bot (5, 6, 7 u 8). Sirocco nivel 5 da 8; Shura/Bora nivel 4 dan 7.
- Gale tiene que estar en mano; o, si hay un Black Whirlwind boca arriba, Gale todavía en el mazo (no está en cementerio, campo ni desterrada). En ese segundo caso el monstruo debe tener más de 1300 de ATK para disparar la búsqueda.
- ⚠ El orden pone Sirocco antes que Shura, aunque la prioridad normal pone Shura antes. Así, con Gale, el bot va por el nivel 8.

**Regla del punto 6: Blizzard con revive.**
- Invoca a Blizzard de normal si hay en el cementerio algún Shura, Bora, Kalut, Gale o Vayu.
- No cuentan Sirocco ni Dark Armed Dragon como objetivos.

**Regla del punto 7: monstruo débil o guardado** (se aplica a cada candidato de la prioridad en orden).
- Sirocco solo si no tenés monstruos propios.
- Contra Gladiator Beast: si ya tenés un monstruo boca arriba, se guarda Shura y Sirocco en mano (salvo que el rival tenga letal en mesa). Espera a Bestiari.
- Si el ATK del monstruo es menor que el mejor poder boca arriba del rival, y no está "cubierto", y no es tuner: no se baja boca arriba. "Cubierto" = hay un Black Whirlwind boca arriba, o tenés Kalut en mano, o tenés 2+ trampas/magias seteadas propias.
  - Lo setea si su DEF es mayor que el mejor ataque (en posición de ataque) del rival y se puede setear.
  - Si no, y el rival tiene letal en mesa, lo setea igual como bloqueador.
  - Si no, se queda en mano y pasa al siguiente.
- Si no se cumple la condición de "débil", lo baja boca arriba.

**Regla del punto 8: Blizzard como cuerpo.**
- Solo si: no tenés monstruos, Blizzard no tiene objetivo en el cementerio, ningún monstruo boca arriba del rival tiene poder 1300 o más, Blizzard está invocable, y Kalut no se puede invocar ni setear (porque Kalut es mejor cuerpo: los tuners escasean y Blizzard puede revivirlo después).
- Se evalúa recién después de la prioridad normal, o sea que si algo de la lista de normal summon se baja primero, esto no ocurre.

**Regla del punto 9: Vayu.**
- Si Vayu se puede setear, lo setea (cualquier turno, aunque haya otras jugadas posibles, siempre que lo anterior no haya jugado nada).
- Ojo: va antes que "Kalut única jugada", por lo que con Vayu seteable Kalut nunca baja por esa vía.

**Regla del punto 10: Kalut única jugada.**
- Condición: 0 monstruos propios, no hay invocación normal que valga (ni Blizzard-cuerpo), Vayu no es seteable, y Kalut es invocable.
- Con Black Whirlwind boca arriba: baja a Kalut boca arriba (busca Gale; Gale + Kalut = synchro nivel 6).
- Si el rival no tiene monstruos: boca arriba.
- Si el rival tiene monstruos y se puede setear: lo setea como bloqueador.
- Código: `DecideMainPhaseAsync`, `FindGaleSynchroSummon`.

---

## Ficha por carta

### Shura the Blue Flame
**Cuándo se juega**
- Es la primera de la prioridad de invocación normal (Shura, Sirocco, Bora, Gale).
- Se invoca con Black Whirlwind si hay uno boca arriba.
- Va primera en el chequeo de "synchro con Gale" después de Sirocco (nivel 4 + 3 = 7).
- Ataca primero contra un monstruo que pueda destruir y que no sea token: el efecto (invocar del mazo) necesita que algo vaya al cementerio.
**Cuándo no**
- Se guarda en mano si el ATK es menor al del mejor monstruo rival (y no hay cobertura, ver punto 7), salvo que se setee con DEF suficiente.
- Se guarda en mano contra Gladiator Beast si ya tenés un monstruo boca arriba.
**A qué apunta / qué elige**
- Es el objetivo preferido del pump de Sirocco.
- Flotante: cuando Shura destruye algo, el bot elige Gale del mazo, o Vayu si Gale no está.
- Es el primero que Blizzard revive después de Gale (ver Selecciones).
- Kalut lo protege con prioridad (ver Kalut).
- Código: `NormalSummonPriority`, `OnBattleDestroyAttackers`, `SelectDeckCardAsync`.

### Bora the Spear
**Cuándo se juega**
- Invocación especial desde la mano, si hay un Blackwing en campo: después de DAD y antes de Gale.
- También entra en la prioridad de invocación normal (tercera) y en el chequeo de synchro con Gale (nivel 4 + 3 = 7).
**Cuándo no**
- No baja como especial si estás "sobre-extendiendo" (ver punto 1).
- Si se invoca de normal y es más débil que lo del rival, aplica la regla de monstruo débil (punto 7).
**A qué apunta / qué elige**
- Prioridad de Blizzard: tercera.
- Búsqueda de Black Whirlwind: tercera.
- Código: `HandSpecialPriority`, `NormalSummonPriority`.

### Gale the Whirlwind
**Cuándo se juega**
- Invocación especial desde la mano (si hay un Blackwing en campo), después de Bora.
- Es tuner nivel 3 (1300 ATK): se usa como base de los synchros 7 y 8 (con Shura/Bora, con Sirocco).
- Su efecto (cortar ATK y DEF a la mitad) se usa en la Main Phase si hay un monstruo del rival boca arriba con ATK o DEF mayor que 0 ("vale la pena": un monstruo 0/0 no vale).
**Cuándo no**
- No hay condición de "mi monstruo gana por eso"; cualquier monstruo rival boca arriba con stats vale.
- Se evalúa antes de DAD, Sirocco, etc. en la lista de efectos de monstruo (tercero, después de Armor Master y Dark End).
**A qué apunta / qué elige**
- El objetivo lo elige la regla genérica Edison (monstruo prioritario del rival).
- Gale es la primera búsqueda de Black Whirlwind.
- Es el primer revive de Blizzard.
- Es el flotante de Shura (primera opción).
- Valor en mano: 3.
- Código: `CanUseMonsterEffect`, `IsWorthHalving`, `HandSpecialPriority`.

### Blizzard the Far North
**Cuándo se juega**
- Invocación normal si hay algo para revivir en el cementerio (Shura, Bora, Kalut, Gale, Vayu).
- Sin objetivo: como cuerpo, solo si campo vacío y el rival no tiene nada de 1300+ boca arriba, y Kalut no es invocable ni seteable (punto 8).
- Con un monstruo robado: se usa como invocación normal para armar synchro (nivel 2 + nivel del robado debe ser nivel de synchro).
**Cuándo no**
- Nunca entra en la invocación genérica (la baja solo este código).
- Sin objetivo y con algo del rival que lo supere, se queda en mano.
**A qué apunta / qué elige**
- Revive en este orden: Gale, Shura, Bora, Kalut, Vayu.
- Si no hay ninguno de esos, el código no elige (cae en el sistema genérico).
- ⚠ El revive de Blizzard nunca elige Sirocco ni Dark Armed Dragon, aunque estén en el cementerio (no está claro en el código qué pasa si son lo único elegible).
- Blizzard es lo primero que se destierra del cementerio (ver Selecciones).
- Código: `BlizzardTargets`, `BlizzardRevivePriority`, `AlwaysExcludedFromGenericSummon`.

### Kalut the Moon Shadow
**Cuándo se juega**
- Normalmente se queda en mano; es una carta de la mano (valor 3) que da +1400 de ATK durante el cálculo de daño.
- Efecto de la mano (ver abajo).
- Como cuerpo si es la "única jugada" (punto 10).
- Es la segunda búsqueda de Black Whirlwind.
**Cuándo no**
- Nunca entra en la invocación genérica.
- Solo una vez por batalla (el código lo marca).
- No hay efecto Kalut si no hay monstruo propio en batalla.
**Efecto de Kalut: usarlo (+1400) cuando…**
- Turno del bot, ataque directo (el rival no tiene monstruo en la batalla): el LP del rival es mayor que tu ATK, y menor o igual a tu ATK + 1400 (letal).
- Turno del bot, atacando a un monstruo: tu ATK no gana solo, y con +1400 sí (compara contra el "poder real" del rival).
- Turno del rival, te atacan:
  - Nunca si tu monstruo está en defensa.
  - Siempre si la batalla te mata (ataque - tu ATK >= tus LP) y con +1400 sobrevivís, no importa cuán chico sea el monstruo.
  - Si no: solo si tu monstruo es "importante" (Shura, un monstruo del Extra Deck o 1700+ de ATK), el rival lo destruiría (ATK rival >= el tuyo) y con +1400 le pasás por encima.
  - Con un solo Kalut en mano, no lo gasta si hay otro atacante rival más amenazante, todavía sin atacar, al que tu otro monstruo ganaría con el pump.
**Otros usos del pump (lógica genérica)**
- La mano con Kalut le suma 1400 a los cálculos de "¿puedo ganar?" para decidir ataques y posiciones.
**A qué apunta / qué elige**
- Solo Blackwings reciben el pump en los cálculos de la mano.
- Código: `ShouldChainDeckCardAsync`, `ShouldUseKalut`, `ShouldBoostWithKalut`, `BattleBoostInHand`, `CanBeBoosted`.

### Sirocco the Dawn
**Cuándo se juega**
- Invocación normal solo sin monstruos propios (sin tributos).
- Con un monstruo robado: se invoca tributándolo (ver punto 3), siempre que haya invocación normal disponible.
- Línea de synchro con Gale: nivel 5 + 3 = 8.
- Efecto (la mitad es suya, la mitad la del pump): solo se activa en tu Main 1, solo si no hay un synchro disponible, y solo si vale la pena (ver abajo). No hay synchro después de usarlo ese turno.
**Cuándo no**
- Con monstruos propios no se invoca de normal (tributo no se usa para nada que no sea el robado).
- Contra Gladiator Beast: se guarda si ya tenés un monstruo boca arriba.
- No usa el efecto si hay un synchro disponible (el synchro tiene prioridad).
**Cuándo vale la pena el efecto** (con 2 o más Blackwings boca arriba en ataque que no atacaron)
- **Para romper algo:** un monstruo rival con poder mayor o igual a tu mejor ATK, pero menor que la suma de todos tus Blackwings. Pumpea a Shura si está.
- **Para el piercing de Bora:** todos los monstruos rivales están en defensa y son al menos tantos como tus atacantes (o sea, el resto no iba a hacer daño), y Bora pumpeado perfora más que Bora solo. Pumpea a Bora. Ejemplo: Bora 1700 + Sirocco 2000 contra dos tokens de 0 DEF: 3700 de daño en vez de 1700.
- En ese caso sube el efecto y se marca el turno (ya no hay synchro).
**A qué apunta / qué elige**
- El que se eligió al decidir (Bora o Shura); si no está, Shura; si no, el de mayor ATK.
- Como el efecto impide atacar a los demás, el pumpeado queda solo en la lista de ataque, así que el bug de "ataca solo el primero" no lo afecta.
- Es lo último de la lista de efectos de monstruo (después de Gale y DAD).
- Valor de mano: 2 (genérico).
- Código: `CanUseMonsterEffect`, `IsSiroccoBoostWorthIt`, `ShouldUseSiroccoNowAsync`, `CanSynchroNow`.

### Vayu the Emblem of Honor
**Cuándo se juega**
- Se setea siempre que se pueda (punto 9). Nunca se invoca boca arriba por este código.
- Su efecto en el cementerio (Synchro con efectos negados) se usa por la lógica genérica (no documentado acá).
**Cuándo no**
- No entra en la invocación genérica.
- No se setea antes que las jugadas anteriores del plan (se ejecuta después de Blizzard-cuerpo).
**A qué apunta / qué elige**
- Segundo flotante de Shura (después de Gale): nivel 1 + Shura 4 = nivel 5.
- Se desterrá último del cementerio (el más protegido).
- Su efecto apunta a una carta propia (para la regla de Machina Fortress, no se cuenta Fortress como objetivo).
- Código: `AlwaysExcludedFromGenericSummon`, `EffectsTargetingOwnCards`, `BanishLastOrder`.

### Dark Armed Dragon
**Cuándo se juega**
- Efecto en el campo: se activa si hay al menos 1 objetivo (monstruo del rival no-Fortress, o carta mágica/trampa del rival).
- Es la primera invocación especial desde la mano (el juego la ofrece cuando hay exactamente 3 DARK en el cementerio).
**Cuándo no**
- No se invoca de especial si Stardust Dragon está bloqueando y la única carta del rival (monstruos + mágicas/trampas) es esa.
- No sobre-extender (punto 1).
**A qué apunta / qué elige**
- El costo (desterrar de tu cementerio) sigue el orden de desterrado (ver Selecciones).
- Valor en mano: 4 si hay 2+ DARKs en el cementerio, 2 si no (así Allure lo destierra antes que un Blackwing que sí pega).
- El umbral de 2 es a propósito: con 2 DARK está a uno de jugarse; con menos es una carta muerta por varios turnos.
- Código: `HandSpecialPriority`, `HandValue`, `CanUseMonsterEffect`.

### Black Whirlwind
**Cuándo se juega**
- Antes de la invocación normal (punto 4), cuando va a haber una invocación normal que la dispare.
- Baja una copia por cada búsqueda que falta (Gale / Kalut en la mano), hasta 2 en total.
- Cuando se activa su efecto en una cadena, siempre se activa (busca Blackwing por invocación).
**Cuándo no**
- No sube si no hay invocación que la dispare.
- Con Stardust bloqueando no se activa.
**A qué apunta / qué elige**
- Búsqueda: Gale primero, luego Kalut, Bora, Shura. Se saltea lo que ya tenés en mano; si ya tenés todo, repite la lista.
- Código: `SearchPriority`, `ShouldChainDeckCardAsync`, `SelectDeckCardAsync`.

### Extra Deck
**Lista de prioridad (primero = mejor desempate)**
1. Blackwing Armor Master
2. Silverwind the Ascendant
3. Dark End Dragon
4. Red Dragon Archfiend
5. Stardust Dragon
6. Thought Ruler Archfiend
7. Colossal Fighter
8. Blackwing Armed Wing
9. Goyo Guardian
10. Ally of Justice Catastor
11. Magical Android
12. Brionac, Dragon of the Ice Barrier
- Black Rose Dragon no está en la lista pero la regla genérica la puede elegir.
- La lista es solo desempate: la situación manda (puntaje genérico Edison). Esto afecta también la elección de synchro cuando un efecto (Vayu) te lo pone.

**Cuándo se prefiere uno (generico Edison, solo para entender la lista)**
- Goyo: hay algo para robar y podés atacar.
- Armed Wing: el rival tiene monstruos en defensa; Armor Master: el rival tiene algo de 2800+.
- Silverwind: 2 monstruos rivales con DEF menor a 2800.
- Black Rose: solo si estás muy atrás.

**Efectos de monstruo del Extra Deck usados en la Main (en este orden)**
- Blackwing Armor Master: si el rival tiene al menos un monstruo objetivo.
- Dark End Dragon: si el rival tiene al menos un monstruo objetivo.
- Brionac: solo si tenés en mano una carta de valor 2 o menos para pagar, y el rival tiene un monstruo con amenaza 2500+, una mágica/trampa boca arriba, o backrow seteado con monstruos chicos (menos de 1900).
- Armed Wing, Silverwind y los demás: no hay regla propia (el código no dice nada del Extra Deck más allá de la prioridad).
- ⚠ Silverwind no está en la lista de efectos de monstruo, aunque esté segundo en la prioridad de synchro (puede ser que se active solo al invocarse; no está claro en el código).

**Material de synchro (Gale/Shura/etc.)**
- Con monstruos robados: el robado sirve como material si el bot tiene Gale en mano y un Blackwing en campo y el nivel suma un synchro disponible (nivel + 3); o Blizzard en mano e invocación normal (nivel + 2); o Sirocco en mano e invocación normal (como tributo).
- Código: `SynchroPriority`, `MonsterEffectCards`, `CanUseStolenMonster`, `CanSynchroNow`.

---

## Selecciones del mazo

**Búsqueda de Black Whirlwind (desde el mazo)**
- Orden: Gale, Kalut, Bora, Shura.
- Primero se prueba lo que no tenés en mano (en ese orden); si ya tenés todo, se usa el orden completo.
- Vayu, Blizzard y Sirocco nunca se buscan.

**Revive de Blizzard (desde tu cementerio)**
- Gale, Shura, Bora, Kalut, Vayu (Gale primero para volver a cortar a la mitad y tener tuner).
- Si no hay ninguno, el código no elige.

**Flotante de Shura (invoca del mazo con efectos negados)**
- Gale; si no está, Vayu. Gale + Shura = synchro 7; Vayu + Shura = synchro 5.
- También se aplica si el hint no llega como invocación especial pero estás en Battle y todas las opciones son del mazo (solo puede ser Shura).

**Objetivo del pump de Sirocco**
- Shura si es elegible; si no, el monstruo con más ATK.
- Solo se aplica si Sirocco acaba de marcar el efecto como pendiente y todas las opciones son tuyas.

**Orden de desterrado del cementerio (costo de DAD, y más)**
- El primero de la lista es el primero que se destierra; las cartas que no están en la lista van antes.
- Orden: Blizzard, Kalut, Bora, Shura, Dark Armed Dragon, Gale, Sirocco, Armor Master, Armed Wing, Vayu.
- Se protege a Vayu y a su cadena de synchros (Sirocco, Armed Wing, Armor Master, Silverwind).
- ⚠ Kalut se destierra segundo, aunque Blizzard puede revivirlo y es la segunda búsqueda de Whirlwind. Revisar si es intencional.
- ⚠ Silverwind no está en el orden: entra como "carta desconocida" y se destierra antes que todas (aunque la nota dice que se quiere conservar).

**Desterrado de la mano (Allure of Darkness)**
- Se destierra la carta de menor valor de mano. Desempate: el orden de desterrado de arriba.
- Valores: Kalut y Gale 3; Dark Armed Dragon 4 si hay 2+ DARKs en el cementerio, si no 2; las demás usan el valor genérico (Upstart 1, cartas clave 4, el resto 2).

**Valor de mano (para descartes y Brionac)**
- Brionac solo se paga con cartas de valor 2 o menos, y descarta primero las de menor valor.
- Código: `HandValue`, `BanishLastOrder`, `SelectDeckCardAsync`.

**Otras notas de la API que afectan al mazo**
- El bot ataca siempre con `AttackableCards[0]`: no se puede decidir "Shura primero" ni "pegarle primero con el pumpeado de Sirocco". Esto limita la efectividad de Shura, Sirocco y Kalut.
- Las cartas con errata tienen otro ID; las reglas comparan por nombre/identidad, no por ID.
