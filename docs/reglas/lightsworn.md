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

- **Wulf choca con empate** (también contra tokens): en el campo es solo un cuerpo de 2100.

Código: `BattleBoostInHand`, `CanBeBoosted`, `TradesOnTie`

### Material para tributo

**Cuando invoca a Celestia, tributa un Lightsworn** (su efecto solo se activa así). Fuera de eso, el más barato como siempre.
- Un Druid boca arriba se tributa último: mientras vive, tus Lightsworn no pueden ser objetivo (Icarus).

Código: `MaterialCost`



## Plan de juego

Orden en que el bot decide en la Main Phase (la primera regla que se cumple, juega y vuelve a preguntar). Antes corren las aperturas generales (Trap Dustshoot, Heavy Storm cuando conviene, etc.). Después corren los efectos de monstruo y los sets de trampas. El resto del fallback genérico no invoca, no setea ni activa ninguna carta del mazo.

1. **Flipear Ryko**, lo primero del turno (Main 1), antes que cualquier otra cosa, para limpiar la backrow. No si el rival no tiene cartas (rompería una tuya; tampoco lo da vuelta la lógica genérica), ni contra Tytannial (niega el objetivo) ni con Plasma.
2. **Magias de robo y mill** (ver cada carta): Pot of Avarice, Solar Recharge, Charge of the Light Brigade, Monster Reincarnation, Card Destruction, Foolish Burial.
3. **Gorz como cebo.** Si tenés Gorz en mano, el campo vacío, el rival tiene monstruos y no tenés una jugada fuerte (Ryko, Lumina, Jain), el bot no setea ni invoca nada y pasa al End Phase.
4. **Efecto de Lyla** si el rival tiene backrow y nada lo niega (ni Stardust ni Plasma). Cuándo y cómo:
   - **2 cartas seteadas o más, sin monstruo seteado del rival:** efecto con prioridad apenas se invoca. Lo obliga a usar sus trampas de invocación. No ataca primero.
   - **2 cartas seteadas o más y un monstruo seteado del rival:** sin prioridad. Deja pasar su ventana de invocación (Bottomless) y después activa el efecto en Main 1.
   - **Una sola carta seteada:** si el rival no tiene monstruos, o solo tiene uno boca abajo, Lyla ataca primero para sacarle un Prison o Mirror Force. Si sobrevive, usa el efecto en Main 2.
   - **Lyla que quedó viva del turno anterior:** usa el efecto primero, antes de invocar otro monstruo (un Torrential se llevó a Lyla y Jain juntas). Lo de atacar primero es solo para una Lyla invocada ese turno.
5. **Heavy Storm** si JD está listo y una carta seteada del rival puede ser Bottomless o Torrential.
6. **Celestia** tributando un Lightsworn, si el rival tiene una carta que valga romper (no un Fortress que cueste un descarte, ni un monstruo protegido por Gearframe). Nunca contra una Tytannial boca arriba: la niega aunque esté sola, tributándose a sí misma. Con Stardust activo solo baja como cuerpo si te deja con más monstruos que el rival, o en Main 1 con Honest en mano para atacar a Stardust en ataque, y nunca usa el efecto (Stardust lo niega y la destruye).
7. **Efecto de JD**, antes de invocar otro JD (si no, el wipe lo mandaría al cementerio).
8. **Judgment Dragon** (ver su ficha).
9. **Efecto de Lumina.**
10. **Robo con Tragoedia.**
11. **Efecto de Card Trooper.**
12. **Invocación Synchro** (regla genérica con la lista del mazo).
13. **Plaguespreader desde el cementerio** para un synchro (ver su ficha).
14. **Invocación normal** (ver "Prioridad de invocación normal").
15. **Honest vuelve a la mano** en la Main 2.

### Cartas en el deck

- Las magias de mill opcionales (Solar Recharge, Charge) solo si el deck queda por encima de 15, y no si ya tenés JD en mano con 4 nombres: en un turno bajaron el deck de 23 a 7 y JD nunca pudo bajar.

**El bot cuenta las cartas que quedan en el deck (si llegás a 0, perdés).**
- **Nunca un deck-out por la End Phase:** no baja un Lightsworn si el mill de la End Phase (los que ya tenés boca arriba, más él, más lo que reviva Lumina) se come el deck, salvo lethal. Lumina reviviendo a Ehren al lado de dos JD molió 14 con 8 cartas.
- **15 o menos:** no baja otro Lightsworn que muela en la End Phase si ya hay uno moliendo, salvo que tenga un trabajo (Lyla con backrow rival, Ehren con monstruos boca abajo), haya lethal o haga falta un bloqueador. Card Trooper muele 1. Pot of Avarice se usa sí o sí.
- **6 o menos (piso):** ningún mill opcional que lo deje en 6 o menos (Solar Recharge, Charge, Card Trooper, Celestia, flipear Ryko, JD), salvo lethal.

Código: `CanMill`, `DeckCaution`, `DeckFloor`

### Los 4 nombres

**JD necesita 4 Lightsworn con nombres distintos en el cementerio.** Al descartar, revivir o devolver al deck, el bot cuida no bajar de 4: usa primero los repetidos y descarta lo que todavía no está en el cementerio.
- Nombres que cuentan: Celestia, Wulf, Garoth, Jain, Lyla, Ehren, Druid, Lumina, Ryko. (JD y Honest no son Lightsworn.)

Código: `GraveNames`

### Prioridad de invocación normal

- Si Lyla puede romper una backrow, va antes que setear Ryko.
- Yendo segundo: Card Trooper primero para limpiar (si el rival tiene monstruos y el deck tiene más de 15). Contra un campo vacío en tu primer turno, Druid ataca y muele en vez de Ryko.
- Ryko no se setea contra un Dark End Dragon boca arriba, ni en Main 1 si tenés atacantes y el rival tiene cartas seteadas (puede ser Icarus): primero se ataca y Ryko va en Main 2. Lumina con algo para revivir y un descarte barato cuenta como "con trabajo" y se invoca en Main 1 aunque sea más débil.

**Contra 2 cartas seteadas o más:** si Lyla no puede entrar, va Card Trooper como cebo barato y Lumina espera (Bottomless sobre lo que revive, Icarus).

**Primero Ryko boca abajo** (si no hay otro Ryko seteado). Después:
1. Lyla, si el rival tiene backrow, o en el turno 1 yendo primero (el rival queda obligado a setear una backrow que ella rompe, y su mill puede tirar a Wulf)
2. Ehren, si el rival tiene monstruos boca abajo
3. Jain
4. Garoth
5. Card Trooper
6. Lumina
7. Druid, salvo que tengas en mano Lumina, Monster Reincarnation o Pot of Avarice: en ese caso va último, solo si no hay otro monstruo para jugar (mejor que dejar el campo vacío)
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
- su efecto limpia el campo del rival (el rival tiene más cartas que vos, sin contar Ryko ni Card Trooper; 2000 LP o más);
- mid-game: nunca en tu primer turno, y solo si el rival no tiene ninguna carta seteada (una Dimensional Prison lo desterró atacando).
- **Main 1 si gana o si limpia:** da lethal, o el efecto limpia el campo y después JD ataca. Si no, espera a la Main 2, cuando la batalla ya mostró la backrow.
- **Con dos JD:** el primero baja en Main 1 y limpia (si el rival tiene 2 cartas o más que vos y tenés más de 1000 LP), el segundo baja después y atacan los dos. Sin setear nada antes.
- Sin motivo, espera. Con Plasma boca arriba no se invoca.

**Stardust:** su efecto negaría el wipe, así que JD baja en Main 1 para atacar a Stardust (sin efecto); si pasa, el wipe va en Main 2.

**Probabilidad de Bottomless:** si invocás un monstruo de 1500+ y el rival lo deja pasar con sus cartas seteadas, esas cartas cuentan como "probablemente no Bottomless" y no frenan a JD ni al synchro. Si el rival cambia sus cartas seteadas, se olvida.

**Y aun así espera si:**
- una carta seteada del rival pueda ser Bottomless o Torrential (las que todavía le puedan quedar). Excepción: el rival tiene lethal en campo (es la única jugada para no perder);
- Royal Oppression la negaría;
- su mill de la End Phase (4) lo dejaría en el piso de 6.

**Su efecto (1000 LP, destruye todas las demás cartas), parecido a Black Rose:**
- Se usa si el rival tiene más cartas que vos en el campo (sin contar a JD, ni a Ryko ni a Card Trooper, que no se pierden: Trooper encima roba). Primero el efecto, después desplegar.
- No con Stardust activo (lo negaría).
- Con 2000 LP o más, como regla general. Por debajo de 2000, solo si JD da lethal ese turno, si el rival tiene lethal, o si un segundo JD baja después para atacar con los dos.
- **Gorz en Main 2:** si después de atacar el rival tiene Gorz o su token y ninguno de tus monstruos le gana, el efecto se usa (otra vez) en la Main 2 para limpiar todo, cueste lo que cueste (más de 1000 LP). El token de 3000 DEF frenó a dos JD durante dos turnos. Contra un token que JD o Wulf pueden sacar, no (barrió 4 propios por un token de 2100).

Código: `JudgmentReason`, `JudgmentWipeWorthIt`, `SecondJudgmentReady`, `SummonTrapPossible`

### Contra Tytannial

**Con una Tytannial boca arriba (niega todo lo que apunta tributando una Planta, ella misma incluida), el bot sigue este plan.** Si tiene lethal pasando por ella, lo ignora.
- **Ryko:** se setea primero y se flipea aunque esté Tytannial. Sola, apunta a ella y Tytannial se tributa a sí misma. **Si tiene otras Plantas para tributar, Ryko solo muele** (no destruye nada). Charge busca Ryko primero si no hay uno en mano.
- **Con Plantas para tributar, nada que apunte** (tampoco Lyla). Lo que sirve es la batalla: **Honest se guarda para ella**, nunca se descarta (Lumina tampoco lo descarta) ni se usa en otra batalla, salvo que esa batalla te mate. Catastor sube de prioridad.
- **Judgment Dragon:** esperar los 4 nombres (los monstruos boca arriba muelen) y bajarlo, porque su wipe no apunta. El wipe se usa si ninguno de tus monstruos le gana a Tytannial en batalla.
- **Con Plaguespreader:** un nivel 3 (Card Trooper, Lumina) hace Catastor (si Tytannial no es DARK); un nivel 4 hace Goyo, que choca con empate contra ella.
- **Si no hay nada de eso:** un solo monstruo en el campo, sin extenderse (Lumina tampoco revive más cuerpos). Un LIGHT en ataque hace de farol de Honest y muele, o uno boca abajo hace de farol de Ryko.
- Celestia nunca contra Tytannial boca arriba. Brionac contra ella solo en emergencia o con lethal (niega el rebote).

Código: `TytannialPlan`, `DecideNormalSummon`, `ChargeSearchOrder`, `PlaguespreaderPlan`, `JudgmentReason`, `JudgmentWipeWorthIt`, `TradesOnTie`

### Gorz

**Se invoca cuando no controlás cartas.**
- Siempre en defensa, y su token también.
- Espera al último ataque (el token sale más grande): si todavía falta atacar un monstruo con más ATK que el golpe recibido, no lo baja. También al inicio de la batalla (antes salía ahí sin esperar).
- Si el golpe ya fue el más fuerte, o lo que falta atacar te mata, lo baja enseguida.

Código: `ShouldSummonGorzAsync`, `OnSelectPositionAsync`

### Tragoedia

**Desde la mano, al recibir daño de batalla:** si tenés 5 cartas o más en la mano, o no tenés monstruos y estás en 3000 o menos. Siempre en defensa.

**Su efecto (robo permanente):** manda a la mano un monstruo del mismo nivel que el mejor monstruo boca arriba del rival que se pueda robar (no tokens).
- Manda el de menor valor. JD solo si ya tenés 4 nombres o más.
- ⚠ Tiene dos efectos (robar y cambiar de nivel). Si el motor ofrece primero el de nivel, puede usar ese primero.

Código: `FindTragoediaSteal`

### Pot of Avarice

**Se usa si después quedan 4 nombres en el cementerio y hay algo que valga la pena recuperar** (2 o más de: JD, Honest, Gorz, Tragoedia, Celestia, Lumina, Card Trooper). Sí o sí con 15 cartas o menos en el deck, con 2000 LP o menos, con los 2 JD en el cementerio, si vas muy atrás (3500 LP o más de diferencia y 3 cartas o más de diferencia en el campo), o si es tu única carta en la mano con el campo vacío.
- Devuelve en este orden: las copias repetidas, los demás monstruos, JD, una copia de cada nombre y, por último, Necro Gardna y Plaguespreader.

Código: `AvariceKeepsNames`, `AvaricePicks`

### Solar Recharge

**Se usa si hay un Lightsworn para descartar** y el mill no deja el deck en el piso.
- Qué descarta: Wulf, después un Lightsworn repetido en la mano que todavía no esté en el cementerio, después uno que falte en el cementerio, después uno repetido. Lumina solo si es lo único.

Código: `SolarDiscardOrder`

### Charge of the Light Brigade

**Se usa siempre que el mill no deje el deck en el piso.**
- Qué busca: Lumina si hay algo para revivir (puede traer a Lyla también), después Lyla si el rival tiene backrow, después un nombre que no esté en el cementerio ni en la mano.

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
3. Wulf, si tenés 2 Celestia en mano, el rival tiene 2 cartas o más y no hay un Lightsworn en tu campo: Wulf se invoca solo y es el tributo de Celestia, que rompe 2.
4. Plaguespreader para un Goyo (nivel 6) el turno siguiente, si Goyo tiene un buen robo, tenés un Lightsworn nivel 4, una carta barata para su costo, y el rival no tiene lethal en campo (si no llegás al turno siguiente, no hay plan).
- Si no queda otra opción que mandar algo, Plaguespreader va último.
5. El nombre que falta, si y solo si tenés JD en la mano, es el 4º nombre y JD baja ese turno (sin Bottomless / Torrential posibles, sin Oppression, deck suficiente).
- Si no hay plan, no se usa.

Código: `FoolishPlan`, `FoolishOrder`, `JudgmentCanLand`

### Plaguespreader

**Synchro con Plaguespreader (nivel 2) y uno de tus monstruos boca arriba**, desde Foolish o si ya está en el cementerio:
- Solo si el rival no tiene una carta seteada que pueda ser Bottomless o Torrential, y sin Royal Oppression.
- El synchro tiene que rendir ya: la regla genérica de synchros le da su bonus (Goyo roba, Catastor rompe un no-DARK, Magical Android cura con poca vida...) o da lethal.
- Brionac como jugada de valor solo si tenés 2 cartas baratas en mano (una va arriba del deck y la otra paga su efecto); si no, solo si da lethal.
- **Con el deck en 15 o menos y un Lightsworn que muele en la End Phase en campo (Jain):** sincro con él para sacarlo del campo, aunque haya riesgo de Bottomless y con cualquier carta arriba del deck (menos JD). Se quedó quieto con Plaguespreader en el cementerio y perdió por deck-out.
- Para revivirlo pone una carta de la mano arriba del deck, y tiene que ser barata: Wulf, Necro Gardna, Plaguespreader o un Lightsworn cuyo nombre ya está en el cementerio (no Lumina). Si no hay, no se hace (economía de cartas).

Código: `PlaguespreaderPlan`, `SelectDeckCardAsync`

### Lumina

**Revive solo si hay un objetivo que no sea Ryko** (Ryko vuelve boca arriba y no activa su efecto flip). Si hay objetivo, descarta lo que haga falta, menos Judgment Dragon (algo que usarías enseguida). Con Plasma no se usa.
- Qué revive: Lyla si el rival tiene backrow, Ehren si hay monstruos boca abajo, después Jain, Wulf, Garoth, Lyla, Ehren, Druid.
- Con 4 nombres o menos, revive primero uno repetido en el cementerio.
- Qué descarta: Wulf, Plaguespreader (solo si tiene plan: synchro ese turno o Goyo el siguiente), Necro Gardna, después un Lightsworn que falte en el cementerio o que esté repetido en la mano, después lo que menos valga en la mano.

Código: `LuminaTarget`, `DiscardOrder`

### Ryko, Celestia y Lyla

**Rompen primero la backrow boca abajo del rival** (también Ryko cuando lo dan vuelta atacándolo), después las cartas continuas y después los monstruos por amenaza (los tokens al final).
- Celestia destruye 2 si puede. Puede tributar otra Celestia (con 2 en mano). Su efecto se saltea si moler 4 lo deja en el piso.
- **Ryko contra Stardust:** no destruye nada (Stardust lo negaría) y solo muele. Depende de que el juego deje elegir cero objetivos; si obliga a elegir, destruye como siempre.
- **Como Icarus (regla del Edison):** no apuntan a un Fortress salvo que valga el descarte, ni a un monstruo con Gearframe equipado (solo moriría el Gearframe: se apunta al Gearframe).
- **Si los ataques que le quedan al rival te matan y no tenés Gorz en mano** (sin contar el que está en batalla, descontando tus otros monstruos como bloqueadores): rompe al atacante más fuerte en vez de la backrow, aunque sea un Fortress. Ryko rompió un Raigeki Break y dos Fortress pegaron 5000.

Código: `SelectDeckCardAsync`, `AttackerThatKillsUsAsync`, `AvoidedMonsterEffectTargetsAsync`

### Necro Gardna

**Solo en la declaración de ataque del rival, para salvar un monstruo clave** que moriría en esa batalla. Nunca en el damage step (por ejemplo, cuando Ryko se da vuelta por el ataque): ahí ya no lo salva.
- Lumina o Judgment Dragon;
- un Lightsworn (Ryko incluido), si tenés Celestia en mano para tributarlo al turno siguiente;
- un LIGHT en ataque que Honest también salvaría: va Gardna y Honest queda para una amenaza mayor.
- Nunca Card Trooper.
- También ante un ataque directo que te mataría.
- Nunca en la Draw Phase ni por un monstruo cualquiera (protegió un Ryko de 200 y un Card Trooper).

Código: `ShouldUseGardnaAsync`

### Ryko y Card Trooper son sacrificables

**Están para el trade 1 a 1** (el robo de Card Trooper es parte de la gracia):
- No se gasta nada para salvarlos: ni Necro Gardna (salvo Ryko con el plan de Celestia), ni Honest, ni Mirror Force.
- Card Trooper no va de material de synchro salvo que dé lethal o que el rival tenga lethal; es el último material elegido.

Código: `Sacrificable`, `MaterialCost`, `PlaguespreaderPlan`

### Card Trooper

**Muele 3 mientras sirva (queda algún Wulf en el deck o faltan nombres para JD); si no, 1.** Se activa en la Main 1 de tu turno (o en la Main 2 si no se usó, por ejemplo un Trooper robado en batalla), solo con más de 15 cartas en el deck (o con lethal). Con 15 o menos se setea boca abajo y no muele (molió con 5, 3 y 1 carta y perdió por deck-out).

Código: `OnAnnounceNumberAsync`, `TrooperMills`

### Mirror Force

**La regla general de Edison, salvo que el ataque vaya a un sacrificable** (Ryko, Card Trooper) **o a un Lightsworn cuya muerte da el 4º nombre:** ahí se deja morir.
- Excepción: el rival tiene 2 atacantes o más que tus monstruos (ventaja numérica); ahí se activa igual.

Código: `DecideOwnEffectAsync`

### Honest

**En el damage step:**
- Atacando: si tu monstruo LIGHT no gana o rebota y con Honest gana, o si con Honest el daño da lethal.
- Defendiendo: si atacan a tu LIGHT en ataque con un monstruo de igual o más ATK.
- No contra una Machina Fortress con Gearframe equipado (se destruye el Gearframe y no se gana nada).
- No contra Catastor boca arriba: su efecto destruye a tu LIGHT al inicio del damage step igual.
- Nunca para salvar un sacrificable (Ryko, Card Trooper), salvo que dé lethal o que Ryko sea el tributo de Celestia (en mano) para el turno siguiente.
- Una vez por batalla.

**Como muro (1900 DEF):** con el campo vacío, si su defensa aguanta al atacante más fuerte del rival, o con menos de 3000 LP.

**Farol:** un LIGHT boca arriba en ataque no pasa a defensa si su DEF tampoco aguantaría al atacante más fuerte del rival y tenés 3000 LP o más. Pasar a defensa le avisa al rival que no tenés Honest, y solo salva la diferencia de LP.

**Corregido (2026-10-24):** Honest no salía nunca. Un bug hacía que el bot viera su propio monstruo como el del rival en las ventanas de batalla.

Código: `ShouldUseHonest`, `DecideNormalSummon`, `KeepsAttackAsBluff`

### Threatening Roar

**En el turno rival, al entrar en batalla (o al declarar un ataque) si tienen un monstruo en ataque.** Una por turno (encadenó dos seguidas). Se setea una sola copia por vez.
- **Si la van a destruir, se encadena igual:** siempre contra Heavy Storm; contra MST o Icarus (pueden apuntar a otra) solo si el rival tiene un monstruo en ataque.
- **Cualquier jugada del rival en su Main 1** (por ejemplo el efecto de Hyunlei, que la rompió sin usar) con un monstruo suyo en ataque: se encadena, frena los ataques de ese turno igual.

Código: `DecideOwnEffectAsync`

### Druid

**Con Lumina, Monster Reincarnation o Pot of Avarice en mano va último**, solo si no hay otro monstruo para jugar, porque nadie puede elegir Lightsworn como objetivo, tampoco el bot. Mejor eso que dejar el campo vacío.

Código: `DecideNormalSummon`



## Selecciones del mazo

- **Descartes:** según la carta que los pide (Solar Recharge, Lumina, Tragoedia). Para el resto, el menor valor en la mano.
- **Synchro:** Stardust, Goyo, Brionac, Red Dragon Archfiend, Thought Ruler, Colossal Fighter, X-Saber Urbellum, Catastor, Tempest Magician, Mist Wurm, Magical Android, Armory Arm (la regla genérica elige según el campo). Plaguespreader es el único tuner.

Código: `SynchroPriority`, `DiscardOrder`
