# Lightsworn

Cómo juega el bot con Lightsworn (variante pura), encima de las reglas del Edison Brain. El plan lo escribió Javier en `lightsworn deck.md`. Las cartas genéricas (Mirror Force, Trap Dustshoot, Heavy Storm...) siguen las reglas del Edison Brain salvo lo que diga "Cambios sobre el Edison Brain".

La idea: moler rápido para tener 4 Lightsworn con nombres distintos en el cementerio y bajar Judgment Dragon (JD), limpiando antes la backrow del rival. Si hay lethal, todas las precauciones se ignoran.


## Cambios sobre el Edison Brain

### Valor de cada carta en la mano (lo que "duele" perder)

**Lightsworn valora distinto algunas cartas al descartar.**
- Wulf, Plaguespreader y Necro Gardna valen 0: Wulf solo sale desde el deck (en mano es una carta muerta) y los otros dos funcionan desde el cementerio.
- JD y Lumina valen 4. Honest, Ryko y Celestia valen 3.

Código: `HandValue`

### Batalla

**Honest en la mano cuenta como un pump de +2000 para un monstruo LIGHT** (en realidad suma el ATK del monstruo rival).
- Un LIGHT que no le gana a un monstruo pero con Honest sí, ataca igual.
- Un LIGHT que con Honest le gana al más fuerte del rival se queda en ataque.
- ⚠ Contra un monstruo en defensa con poco ATK, Honest suma poco: el bot puede atacar de más.

Código: `BattleBoostInHand`, `CanBeBoosted`

### Material para tributo

**Cuando invoca a Celestia, tributa un Lightsworn** (su efecto solo se activa así). Fuera de eso, el más barato como siempre.

Código: `MaterialCost`



## Plan de juego

Orden en que el bot decide en la Main Phase (la primera regla que se cumple, juega y vuelve a preguntar). Antes corren las aperturas generales (Trap Dustshoot, Heavy Storm cuando conviene, etc.). Después corren los efectos de monstruo y los sets de trampas. El resto del fallback genérico no invoca, no setea ni activa ninguna carta del mazo.

1. **Flipear Ryko**, lo primero del turno (Main 1), antes que cualquier otra cosa, para limpiar la backrow. No contra Tytannial (niega el objetivo) ni con Plasma.
2. **Magias de robo y mill** (ver cada carta): Pot of Avarice, Solar Recharge, Charge of the Light Brigade, Monster Reincarnation, Card Destruction, Foolish Burial.
3. **Gorz como cebo.** Si tenés Gorz en mano, el campo vacío, el rival tiene monstruos y no tenés una jugada fuerte (Ryko, Lumina, Jain), el bot no setea ni invoca nada y pasa al End Phase.
4. **Efecto de Lyla** si el rival tiene backrow y nada lo niega (ni Stardust ni Plasma).
5. **Heavy Storm** si JD está listo y una carta seteada del rival puede ser Bottomless o Torrential.
6. **Celestia** tributando un Lightsworn, si el rival tiene cartas.
7. **Judgment Dragon** (ver su ficha).
8. **Efecto de JD.**
9. **Efecto de Lumina.**
10. **Robo con Tragoedia.**
11. **Efecto de Card Trooper.**
12. **Invocación Synchro** (regla genérica con la lista del mazo).
13. **Plaguespreader desde el cementerio** para un synchro (ver su ficha).
14. **Invocación normal** (ver "Prioridad de invocación normal").
15. **Honest vuelve a la mano** en la Main 2.

### Cartas en el deck

**El bot cuenta las cartas que quedan en el deck (si llegás a 0, perdés).**
- **15 o menos:** no baja otro Lightsworn que muela en la End Phase si ya hay uno moliendo, salvo que tenga un trabajo (Lyla con backrow rival, Ehren con monstruos boca abajo), haya lethal o haga falta un bloqueador. Card Trooper muele 1. Pot of Avarice se usa sí o sí.
- **6 o menos (piso):** ningún mill opcional que lo deje en 6 o menos (Solar Recharge, Charge, Card Trooper, Celestia, flipear Ryko, JD), salvo lethal.

Código: `CanMill`, `DeckCaution`, `DeckFloor`

### Los 4 nombres

**JD necesita 4 Lightsworn con nombres distintos en el cementerio.** Al descartar, revivir o devolver al deck, el bot cuida no bajar de 4: usa primero los repetidos y descarta lo que todavía no está en el cementerio.
- Nombres que cuentan: Celestia, Wulf, Garoth, Jain, Lyla, Ehren, Druid, Lumina, Ryko. (JD y Honest no son Lightsworn.)

Código: `GraveNames`

### Prioridad de invocación normal

**Primero Ryko boca abajo** (si no hay otro Ryko seteado). Después:
1. Lyla, si el rival tiene backrow
2. Ehren, si el rival tiene monstruos boca abajo
3. Jain
4. Garoth
5. Card Trooper
6. Lumina
7. Druid, salvo que tengas en mano Lumina, Monster Reincarnation o Pot of Avarice
8. Lyla y Ehren sin trabajo

**Boca arriba o boca abajo:**
- Con Honest en mano, siempre boca arriba en ataque.
- Sin Honest, 3 de cada 10 turnos igual boca arriba (bluff).
- Si no, un monstruo más débil que el mejor del rival se setea si su DEF aguanta; si no, se guarda (o se setea como bloqueador si el rival tiene lethal o estás en 3000 o menos sin monstruos).

**Honest:** si no hubo otra invocación, el rival no tiene nada de más de 1100 de ATK y hay Battle Phase, se invoca para pegar y vuelve a la mano en la Main 2. Por debajo de 3000 LP y sin monstruos, se setea como muro (1900 de DEF).

**Necro Gardna, Card Trooper y Plaguespreader** se setean solo como último bloqueador.

Código: `DecideNormalSummon`



## Ficha por carta

### Judgment Dragon

**Se invoca con los 4 nombres y un motivo:**
- da lethal ese turno;
- su efecto limpia el campo del rival (2 cartas o más que vos, 2000 LP o más);
- mid-game: nunca en tu primer turno, y solo si el rival no tiene ninguna carta seteada (una Dimensional Prison lo desterró atacando).
- Sin motivo, espera. Con Plasma boca arriba no se invoca.

**Y aun así espera si:**
- una carta seteada del rival pueda ser Bottomless o Torrential (las que todavía le puedan quedar). Excepción: el rival tiene lethal en campo (es la única jugada para no perder);
- Royal Oppression la negaría;
- su mill de la End Phase (4) lo dejaría en el piso de 6.

**Su efecto (1000 LP, destruye todas las demás cartas), parecido a Black Rose:**
- Se usa si el rival tiene 2 cartas o más que vos en el campo (sin contar a JD).
- No con Stardust activo (lo negaría).
- Con 2000 LP o más, como regla general. Por debajo de 2000, solo si JD da lethal ese turno o el rival tiene lethal.

Código: `JudgmentReason`, `JudgmentWipeWorthIt`, `SummonTrapPossible`

### Gorz

**Se invoca cuando no controlás cartas.**
- Siempre en defensa.
- Espera al último ataque (el token sale más grande): si todavía falta atacar un monstruo con más ATK que el golpe recibido, no lo baja.
- Si el golpe ya fue el más fuerte, o lo que falta atacar te mata, lo baja enseguida.

Código: `ShouldSummonGorzAsync`, `OnSelectPositionAsync`

### Tragoedia

**Desde la mano, al recibir daño de batalla:** si tenés 5 cartas o más en la mano, o no tenés monstruos y estás en 3000 o menos. Siempre en defensa.

**Su efecto (robo permanente):** manda a la mano un monstruo del mismo nivel que el mejor monstruo boca arriba del rival que se pueda robar (no tokens).
- Manda el de menor valor. JD solo si ya tenés 4 nombres o más.
- ⚠ Tiene dos efectos (robar y cambiar de nivel). Si el motor ofrece primero el de nivel, puede usar ese primero.

Código: `FindTragoediaSteal`

### Pot of Avarice

**Se usa si después quedan 4 nombres en el cementerio.** Con 15 cartas o menos en el deck, o con 2000 LP o menos, se usa sí o sí.
- Devuelve en este orden: las copias repetidas, los demás monstruos, JD, una copia de cada nombre y, por último, Necro Gardna y Plaguespreader.

Código: `AvariceKeepsNames`, `AvaricePicks`

### Solar Recharge

**Se usa si hay un Lightsworn para descartar** y el mill no deja el deck en el piso.
- Qué descarta: Wulf, después un Lightsworn repetido en la mano que todavía no esté en el cementerio, después uno que falte en el cementerio, después uno repetido. Lumina solo si es lo único.

Código: `SolarDiscardOrder`

### Charge of the Light Brigade

**Se usa siempre que el mill no deje el deck en el piso.**
- Qué busca: Lyla si el rival tiene backrow, después Lumina si hay algo para revivir, después un nombre que no esté en el cementerio ni en la mano.

Código: `ChargeSearchOrder`

### Monster Reincarnation

**Se guarda para recuperar JD del cementerio** (si no tenés otro en mano). También trae a Lumina, pero solo con 5 nombres o más (así no baja de 4).

Código: `DecideSpells`

### Card Destruction

No está en la lista actual (va Monster Reincarnation), pero la regla queda por si vuelve:

**Se usa cuando al menos la mitad del resto de la mano son cartas que rinden más en el cementerio** (Wulf, Plaguespreader, Necro Gardna), con 2 cartas o más para descartar.

Código: `DecideSpells`

### Foolish Burial

**Solo se usa con un plan para ese turno:**
1. Necro Gardna, si tenés Celestia en mano y un Lightsworn en el campo.
2. Plaguespreader, si se cumple su plan de synchro (ver su ficha).
3. El nombre que falta, si y solo si tenés JD en la mano, es el 4º nombre y JD baja ese turno (sin Bottomless / Torrential posibles, sin Oppression, deck suficiente).
- Si no hay plan, no se usa.

Código: `FoolishPlan`, `FoolishOrder`, `JudgmentCanLand`

### Plaguespreader

**Synchro con Plaguespreader (nivel 2) y uno de tus monstruos boca arriba**, desde Foolish o si ya está en el cementerio:
- Solo si el rival no tiene una carta seteada que pueda ser Bottomless o Torrential, y sin Royal Oppression.
- El synchro tiene que rendir ya: la regla genérica de synchros le da su bonus (Goyo roba, Catastor rompe un no-DARK, Magical Android cura con poca vida...) o da lethal.
- Brionac solo si da lethal: cuesta otro descarte más.
- Para revivirlo pone una carta de la mano arriba del deck, y tiene que ser barata: Wulf, Necro Gardna, Plaguespreader o un Lightsworn cuyo nombre ya está en el cementerio (no Lumina). Si no hay, no se hace (economía de cartas).

Código: `PlaguespreaderPlan`, `SelectDeckCardAsync`

### Lumina

**Revive solo si hay un objetivo que no sea Ryko** (Ryko vuelve boca arriba y no activa su efecto flip), **y si hay un descarte barato en la mano** (Wulf, Plaguespreader, Necro Gardna o un Lightsworn ya en el cementerio). Nunca descarta un Pot of Avarice u otra magia útil. Con Plasma no se usa.
- Qué revive: Lyla si el rival tiene backrow, Ehren si hay monstruos boca abajo, después Jain, Wulf, Garoth, Lyla, Ehren, Druid.
- Con 4 nombres o menos, revive primero uno repetido en el cementerio.
- Qué descarta: Wulf, Plaguespreader, Necro Gardna, después un Lightsworn que falte en el cementerio o que esté repetido en la mano.

Código: `LuminaTarget`, `DiscardOrder`

### Ryko, Celestia y Lyla

**Rompen primero la backrow boca abajo del rival**, después las cartas continuas y después los monstruos por amenaza (los tokens al final).
- Celestia destruye 2 si puede. Su efecto se saltea si moler 4 lo deja en el piso.

Código: `SelectDeckCardAsync`

### Necro Gardna

**Solo en la declaración de ataque del rival, para salvar un monstruo clave** que moriría en esa batalla:
- Lumina o Judgment Dragon;
- un Lightsworn que no sea Ryko, si tenés Celestia en mano para tributarlo al turno siguiente.
- También ante un ataque directo que te mataría.
- Nunca en la Draw Phase ni por un monstruo cualquiera (protegió un Ryko de 200 y un Card Trooper).

Código: `ShouldUseGardnaAsync`

### Card Trooper

**Muele 3 (más chances de Wulf), 1 con 15 cartas o menos.** Se activa en la Main 1 de tu turno.

Código: `OnAnnounceNumberAsync`

### Honest

**En el damage step:**
- Atacando: si tu monstruo LIGHT no gana o rebota y con Honest gana.
- Defendiendo: si atacan a tu LIGHT en ataque con un monstruo de igual o más ATK.
- Una vez por batalla.

Código: `ShouldUseHonest`

### Threatening Roar

**En el turno rival, al entrar en batalla (o al declarar un ataque) si tienen un monstruo en ataque.** Una por cadena.

Código: `DecideOwnEffectAsync`

### Druid

**No se invoca si tenés en mano Lumina, Monster Reincarnation o Pot of Avarice**, porque nadie puede elegir Lightsworn como objetivo, tampoco el bot.

Código: `DecideNormalSummon`



## Selecciones del mazo

- **Descartes:** según la carta que los pide (Solar Recharge, Lumina, Tragoedia). Para el resto, el menor valor en la mano.
- **Synchro:** Stardust, Goyo, Brionac, Red Dragon Archfiend, Thought Ruler, Colossal Fighter, X-Saber Urbellum, Catastor, Tempest Magician, Mist Wurm, Magical Android, Armory Arm (la regla genérica elige según el campo). Plaguespreader es el único tuner.

Código: `SynchroPriority`, `DiscardOrder`
