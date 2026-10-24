# Edison Brain

Reglas del bot que valen para cualquier mazo de Edison, en lenguaje de jugador. Es un reflejo del código: si algo de acá no coincide con lo que ves en una partida, hay un bug (del documento o del bot). Lo propio de cada mazo está en su página.

Las marcas ⚠ son rarezas que aparecieron al escribir esto: decisiones dudosas o huecos del código. Cada sección termina con `Código: ...`, los métodos donde vive la regla.


## Antes de leer: lo que la API nos obliga a hacer

Algunas reglas parecen raras porque rodean bugs del juego (detalle en `docs/API-LIMITATIONS.md`):
- **Ataca solo el primer monstruo de la lista** (el de más a la izquierda). El bot no elige quién ataca, solo a quién ataca el primero, o no atacar. Si el primero no ataca, no ataca nadie.
- **No hay replay de ataque:** si el campo cambia en medio de un ataque, ese ataque se pierde.
- **No puede elegir ir primero o segundo.**
- **La mano que revela Dustshoot llega oculta;** el bot la reconstruye y la "olvida" carta por carta cuando esa carta aparece en público.
- **Las cartas con errata tienen otro ID;** el bot compara por nombre.

---

# Reglas generales de Edison

Valen para cualquier mazo. Lo específico de Blackwings está más abajo, en su propia parte.

## Main Phase: en qué orden piensa

En cada decisión de la Main Phase, el bot recorre esta lista y hace la primera jugada que corresponda:

1. **Jugadas de apertura** (cartas de robo, robo de monstruos, remoción): ver la ficha de cada carta.
   - Dustshoot (solo en nuestra Main 1), Upstart Goblin, Allure (si hay un monstruo DARK para desterrar), Brain Control, Book of Moon sobre Stardust, Heavy Storm, MST.
2. **Si ya hay lethal en el campo, no compromete más.**
   - Cuándo: el rival no tiene monstruos, nuestros monstruos en ataque ya suman su vida, tiene cartas seteadas, y todavía puede tener Mirror Force o Torrential.
   - Entonces: no invoca ni activa nada más, solo setea y va a la batalla.
3. **Jugadas del mazo** (invocaciones, synchro…): ver Blackwings.
4. **Efectos de monstruos** del mazo, en el orden del mazo.
   - Si el efecto va a apuntar a Machina Fortress, antes setea todo lo que pueda de la mano, para que el descarte de Fortress le cueste lo menos posible.
5. **Seteos** (ver abajo).
6. **Cambios de posición** y pasar a la batalla.

`Código: DecideIdleAsync, DecideGenericOpeningAsync`

## No sobreextenderse

**No agrega otro monstruo si solo alimenta un Mirror Force o un Torrential.**
- Cuándo: es nuestro turno, ya tenemos un monstruo, el rival tiene 2 o más cartas seteadas y todavía puede tener Mirror Force o Torrential.
- Excepciones: tuners, monstruos importantes (synchros o 2300 de ATK o más), o si con ese monstruo hay lethal directo.
- ⚠ Hoy solo se aplica a las invocaciones especiales desde la mano, no a la Normal Summon.

`Código: ShouldAvoidOverextending`

## Synchro

**Hace synchro en su turno**, o en cualquier momento si controla un monstruo robado (que sirve de material).

**No hace synchro con Royal Oppression boca arriba** (de cualquiera de los dos): el rival paga 800, lo niega y perdemos los dos materiales.
- Excepción, como carnada: si al rival le quedan 3000 de vida o menos, o en un 25% de los turnos (se sortea una vez por turno).
- Lo mismo vale para invocaciones especiales desde la mano y efectos que invocan (ver Royal Oppression).

**Espera a la Main 2 para que los materiales ataquen primero.**
- Cuándo: es nuestra Main 1 con batalla disponible, el rival no tiene cartas seteadas, y nuestro atacante más débil le gana a cada monstruo suyo.
- **Si el rival no tiene monstruos:** los materiales pegan directo primero, siempre que juntos peguen al menos lo mismo que el synchro más fuerte disponible. El synchro sale en Main 2.
- **Contra Cyber Valley boca arriba, nunca espera:** atacarla termina la Battle Phase, así que el synchro va primero.

**Elige el synchro según el campo.** El orden del mazo solo desempata:
- **Goyo:** si hay algo que le gane en batalla y valga robar (no tokens).
- **Armed Wing:** si hay monstruos en defensa que le gane con piercing.
- **Armor Master:** contra un monstruo de 2800 o más que no podemos pasar.
- **Silverwind:** con 2 o más monstruos boca arriba de DEF menor a 2800.
- **Dark End:** contra una amenaza de 2000 o más.
- **Red Dragon Archfiend:** con 2 o más monstruos rivales en defensa.
- **Catastor:** contra un no-DARK de 2200 o más.
- **Black Rose:** solo si vamos claramente atrás (el rival tiene 2 cartas más que nosotros en el campo) y no hay Stardust que la niegue. Si no, nunca.
- **Magical Android:** con 4000 de vida o menos.
- **Brionac:** contra algo difícil de sacar, pagando con una carta que no necesitamos. También cuando lo único que estorba es el backrow seteado del rival (sus monstruos tienen menos de 1900), antes que un Goyo que se come la trampa.
- **Stardust:** contra 2 o más cartas seteadas.

**Materiales: entrega primero lo que menos sirve.**
- Orden: un monstruo robado, después tokens, después el monstruo en defensa (en nuestra Main 1 no iba a atacar), después el de menor ATK.
- Para la suma de niveles, busca la combinación más barata que dé el nivel exacto, respetando el tuner obligatorio.

`Código: TrySynchroSummon, ShouldAttackBeforeSynchro, ScoreSynchro, MaterialCost, FindCheapestSum`

## Seteo de magias y trampas

**En nuestra Main 1 no setea si va a haber batalla:** espera a la Main 2. Así no da información y las cartas no quedan expuestas a efectos de batalla (por ejemplo, Fortress al ser destruida).

**Siempre deja una zona libre** para jugar magias el turno siguiente (cuentan también las continuas boca arriba).
- Excepción: con 2000 de vida o menos, setea todo.

**Topes por Heavy Storm** (todos se levantan con 2000 de vida o menos):
- **Si vio Heavy Storm en la mano del rival:** como mucho 1 carta seteada, hasta que lo use.
- **Si el Heavy Storm lo tiene el bot en la mano:** como mucho 1, para que usarlo el turno siguiente le cueste poco.
- **Si no:** setea hasta la mayor de dos cantidades, 2 o las cartas que tenga seteadas el rival. Sin tope si tiene Solemn o Starlight Road seteado, porque protegen el campo.

**Orden en que setea las trampas:** Solemn, Starlight Road, Prison, Mirror Force, Bottomless, Royal Oppression, Dustshoot, Icarus.
- Book of Moon y MST solo se setean fuera de la Main 1 con batalla.
- ⚠ No evita setear dos copias de la misma carta (lo dejamos para más adelante).

`Código: DecideSets`

## Posiciones

**Al cambiar de posición antes de la batalla** (si no, decide el framework):
- Los monstruos que no reciben daño ni se destruyen en batalla se quedan en ataque, salvo que el rival solo tenga Machina Fortress.
- Un monstruo en defensa no pasa a ataque si algo del rival le gana y él no le gana a ningún monstruo real (los tokens no cuentan).
- Los que destruyen no-DARK en batalla se quedan en ataque si hay no-DARK enfrente.
- Uno boca abajo débil (menos de 1500) no se da vuelta solo para pegarle a uno boca abajo, salvo que tenga Kalut en la mano.
- Con Kalut en la mano, un monstruo que con +1400 le gana al más fuerte del rival se queda en ataque.
- En Main 1 con batalla por delante, si el ATK de nuestros monstruos en ataque suma los LP del rival o más, ninguno pasa a defensa (puede haber lethal, por ejemplo Bora perforando un token).

**Al invocar:** los indestructibles en batalla van en defensa si el rival solo tiene Fortress, o si ese turno no hay batalla (turno 1 o Main 2). El resto lo decide el framework.

`Código: DecideIdleAsync (reposicionamiento), DecideSelectPositionCoreAsync`

## Batalla

Recordá: ataca solo el primero de la lista. En cada paso, el bot decide sobre ese primero:

1. **Icarus en nuestro battle step:** si los monstruos del rival frenan a nuestro primer atacante, tenemos 2 o más monstruos, Stardust no lo niega e Icarus vale la pena (ver la ficha de Icarus).
2. **Si vio Mirror Force en su mano** y el rival tiene cartas seteadas: un solo ataque por batalla, y el resto se queda.
3. **Si vio en su mano una trampa de batalla** (Prison, Sakuretsu…) con cartas seteadas: un monstruo importante no ataca, salvo que gane el duelo.
   - Solo si no hay otros atacantes esperando detrás: si los hay, ataca igual, porque quedarse frena a todos.
4. **Los que tienen efecto al destruir en batalla (Shura) atacan primero**, a un monstruo que no sea token y que le ganen (contando Kalut si está en la mano).
5. **Book of Moon al declarar el ataque:** atacar a un bloqueador que no le gana boca arriba pero sí boca abajo, y darlo vuelta en la ventana de declaración.
6. **Goyo** ataca primero a lo mejor que pueda robar (nunca Fortress).
7. **Los que destruyen no-DARK** van a la mayor amenaza no-DARK.
9. **Los que no se destruyen ni reciben daño** atacan a lo mejor del rival (nunca Fortress).
10. **Elección general**, en este orden:
    - **Un monstruo robado choca aunque empate** con un monstruo del rival en ataque, porque igual se devuelve en la End Phase. Solo si no tiene un ataque mejor que gane.
    - **El mejor ataque que gana.** Entre objetivos que gana igual, va al más peligroso. Un monstruo importante no ataca a Machina Fortress, salvo que sea robado.
    - **Si no gana solo pero sí con Kalut en la mano** (solo Blackwings): ataca igual al más peligroso que le gane con +1400 (nunca Fortress).
    - **Si no gana pero uno de atrás sí:** rebota contra un monstruo en defensa con DEF hasta 500 mayor a su ATK (y solo si le quedan más de 1000 de vida después), para que el siguiente pase a ser el primero.
    - **Si hay uno boca abajo en defensa:** le pega con el más fuerte.
    - **Si no,** termina la batalla.
    - Si el rival no tiene monstruos, ataca directo con el más fuerte.

`Código: DecideBattleCmdCoreAsync, DecideGenericAttackAsync`

## Cadenas: guardas generales

Antes de mirar la ficha de cada carta, el bot aplica estos filtros en orden:

1. **Si viene un ataque letal, cualquier respuesta vale**, incluso destrucción contra Stardust (si Stardust la niega, se tributa y el ataque se va igual).
2. **Con Stardust activo, no usa destrucción.**
   - "Destrucción" incluye Mirror Force, Icarus, Heavy Storm, MST, Bottomless, Solemn y Royal Oppression (además de DAD y Black Rose). Prison, Book of Moon, Brain Control, Starlight Road y Dustshoot se usan igual.
   - Se levanta si tenemos Royal Oppression en el campo (con más de 800 de vida) o D.D. Crow en la mano.
   - ⚠ Solemn figura como destrucción aunque negar no destruye: con Stardust activo no niega invocaciones ni efectos.
   - Excepciones: el ataque letal, Solemn negando un Heavy Storm (negar no destruye), y **Mirror Force contra el ataque de la propia Stardust** (para negarla, Stardust se tributa y el ataque se cae).
   - Cuando la guarda por Stardust o por Tytannial, ahora lo deja en el log.
3. **Trap Stun resuelto este turno:** no activa más trampas.
4. **Tytannial con una Planta para tributar:** no usa cartas que apuntan.
   - Las que además cuestan otra carta (tributo, descarte) tampoco con cualquier Tytannial activa.
5. **Heavy Storm del rival en la cadena:**
   - Royal Oppression boca abajo no se encadena (no lo frena, y le quita la cadena a Solemn).
   - Para Solemn, ver su ficha.
6. **En nuestro turno no encadena magias ni trampas,** para no pisar nuestras jugadas. Excepción: Icarus en respuesta a una remoción del rival.
7. **Nunca dos copias de la misma carta en una cadena.**
8. **Una sola respuesta por cadena.** (Y como máximo 3 activaciones de la misma carta por turno.)
   - Excepciones: Icarus si un monstruo nuestro muere igual en batalla, un ataque letal (pasa una segunda respuesta por si la primera falla), y efectos de monstruos.
9. **Contra Gladiator Beasts,** Bottomless espera a Bestiari, Gyzarus o Heraklinos.

**Los efectos de monstruos** se usan siempre que se ofrecen, salvo que el mazo diga otra cosa (Kalut, Sirocco: ver Blackwings).
- **Los efectos que el bot usa en la Main Phase (Dark Armed Dragon, Dark End, Brionac...) piden la misma condición cuando el juego los ofrece en la ventana justo después de nuestra invocación.** Sin esto, Dark Armed Dragon se activó con el campo del rival vacío y se destruyó a sí mismo.

`Código: ShouldChain, DecideSelectChainCoreAsync`

## Selecciones

- **A qué apunta** con remoción: primero el objetivo elegido al decidir la cadena (por ejemplo, el atacante para Book of Moon, o el par tuner + no-tuner para Icarus).
  - **Los tokens van últimos** (Dark Armed Dragon se llevó dos Fluff Tokens y dejó el backrow seteado).
  - Después, en una sola carta, la mayor amenaza en monstruos.
  - En remoción múltiple (Icarus), las cartas más valiosas, incluido el campo de magias. Prefiere el campo de magias si ninguno de sus monstruos le gana al nuestro y tiene 2 o más seteadas.
  - Nunca una magia normal o trampa normal que ya está resolviendo.
  - Si el monstruo está protegido por una unión (Gearframe), apunta a la unión.
  - Evita Machina Fortress con efectos de monstruo si su descarte no vale la pena.
- **Tributos y costos:** primero el monstruo que muere igual (Icarus en respuesta), después el más barato (robado, token, defensa, menor ATK). Un monstruo que ya atacó en nuestro turno cuenta como más barato.
- **Desterrar de la mano (Allure):** la carta que menos se extraña, según el valor de mano del mazo.
- **Desterrar del cementerio (costo de DAD):** el orden del mazo.
- **Synchro elegido por un efecto (Vayu):** el mismo puntaje que para invocar.
- **Sí/No de efectos:** siempre sí.

**Cuánto vale cada carta** para estas decisiones:
- **Amenaza de un monstruo:** su ATK base (o la mitad de su DEF si es mayor), +300 si es del Extra, más un bonus por carta peligrosa. Uno boca abajo vale 1000 y un 0/0 resta.
- **Cartas en el campo de magias:** una seteada vale 1500; una boca arriba, 1200 más su bonus.

`Código: PickEnemyTargetsAsync, DecideSelectCardCoreAsync, ThreatScore, RemovalValue, HandValue`

## Memoria del duelo

- **Cartas vistas en la mano rival (Dustshoot):** las recuerda hasta que esa carta aparece en público (cementerio, desterrada o campo).
- **Trap Stun:** lo da por resuelto si lo ve boca arriba o si aparece una copia nueva en su cementerio o desterrada. Vale solo ese turno.
- **Synchros invocados con Vayu:** los trata como negados mientras sigan en el campo.
- **Copias que le quedan al rival** (Mirror Force, Heavy Storm…): banlist del formato menos las copias vistas.
- **Al empezar cada partida del match borra todo lo anterior,** porque los turnos vuelven a 1 y lo viejo coincidía con turnos nuevos.

`Código: OnNewDuel, StillInRevealedHand, UpdateTrapStun, OpponentMayStillHave`

---

# Carta por carta (Edison)

## Nuestras cartas (staples Edison)

Cómo leer esto: cada regla es una línea en negrita con la decisión y debajo las condiciones. "Su turno" = turno del rival. "Bloqueo de Stardust" y "ventana" se explican en las secciones que las usan. Los números (LP, ATK, %) son los del código. El bot activa las cartas de nuestro lado solo cuando el juego se las ofrece: si una trampa no aparece en la lista de cadena, ninguna regla puede usarla.

### Valor de cada carta en la mano (lo que "duele" perder)

**Si el bot tiene que tirar/desterrar de la mano, elige la de menor valor.**
- Valor 0: Vayu, Necro Gardna.
- Valor 1: Upstart Goblin.
- Valor 4 (cartas clave): Dark Armed Dragon, Brain Control, Heavy Storm, Solemn Judgment, Mirror Force, Dimensional Prison, Icarus Attack.
- Valor 2: todo lo demás (incluye Book of Moon, MST, Starlight Road, Bottomless, Oppression, D.D. Crow, Allure).
- Se usa para: costo de Allure of Darkness, descartes (Brionac), y para decidir si vale la pena apuntar a Machina Fortress.
- Empates en Allure: desempata el orden del mazo (no está en esta sección).

Código: `HandValue`

### Solemn Judgment

**Se encadena a la invocación de un monstruo con efecto "al invocar" (siempre, sin mirar LP).**
- Lista: Black Rose Dragon, Caius, Raiza, Thestalos, Gladiator Beast Bestiari, Gladiator Beast Gyzarus.
- Solo si el monstruo es nuevo en su campo (apareció desde nuestra última decisión).
- Va primero que cualquier respuesta barata, porque estos efectos se disparan al invocarse y Bottomless/Prison llegan tarde.
- ⚠ Esta prioridad sobre Bottomless no está forzada en el código: si el juego ofrece Bottomless antes que Solemn en la lista, el bot encadena la primera que dé "sí". (no está claro en el código cuál aparece primero)

**Se encadena en otros casos solo con 4000 LP o más y un monstruo "digno".**
- Digno = Stardust Dragon o cualquier monstruo con 2400+ ATK.
- Si es una invocación, cuenta solo el monstruo que se está invocando (un monstruo grande que ya estaba no alcanza).
- Si es una cadena del rival (efecto, magia), cuenta cualquier monstruo de su campo. ⚠ Con un monstruo 2400+ en su campo, Solemn puede gastar la mitad de los LP en negar cualquier cosa, aunque el efecto sea menor.
- No se encadena si tenemos boca abajo un Bottomless, Dimensional Prison o Mirror Force y es una invocación: se reserva para la respuesta más barata (el LP cuesta).
- Solo en el turno del rival (salvo lo de Heavy Storm abajo).

**Solemn sobre el Heavy Storm del rival: sí cuando salva más de lo que pierde.**
- Se activa solo si perdemos 2 o más cartas de magias/trampas (sin contar la propia Solemn), perdemos más de las que pierde el rival, y tenemos 2000 LP o más.
- Cuenta lo que pierde el rival: todas sus magias/trampas excepto la Storm misma.
- Vale incluso con Stardust activo (negar un spell no destruye nada).
- Si no se cumple, el bot no encadena Solemn ahí (devuelve "no").
- Mientras la Storm resuelve, Royal Oppression boca abajo no se encadena (le sacaría la cadena a Solemn).

**Orden para setear:** primera de la lista de sets (ver "Seteo de magias y trampas" en las reglas generales). Además protege el backrow: si hay una Solemn seteada, el tope de sets contra Heavy Storm se levanta.

Código: `SolemnOnSummonThreat`, `IsWorthSolemn`, `HasCheaperMonsterAnswer`, `ShouldChain` (bloque de theirStormResolving)

### Icarus Attack

**Cómo se elige objetivo y tributo (vale para todas las formas de activarla).**
- Tributo: el monstruo de menor "costo" para nosotros. Orden: un monstruo robado (-1000), un token (-500), un defensor en Main 1 (ATK menos 1500), y en nuestro turno los que ya atacaron (-3000 extra). Si no, el de menor ATK.
- Objetivos: hasta 2 cartas del rival con mayor valor. Valores: monstruo = su amenaza (el mayor entre ATK base y DEF/2, más bonus por carta conocida; +300 si es del Extra; -1000 si es 0/0; 1000 si está boca abajo); backrow boca abajo = 1500; backrow boca arriba duradera (continua, equipo, campo) = 1200 más su bonus; magias normales/trampas normales boca arriba no cuentan (ya se resolvieron).
- No toma monstruos protegidos por una Union (destruiría la Union, no el monstruo): apunta a la Union.
- Con un Lightsworn Druid boca arriba del rival, sus Lightsworn no se cuentan como objetivos, porque no se los puede apuntar. Ya pasó: Icarus terminó destruyendo a Bora y Sirocco propios.
- ⚠ Hay una variable que prefiere backrow cuando el rival no tiene un monstruo que supere a nuestro mejor y hay 2+ trampas seteadas, pero cuando la selección pide 2 objetivos el orden por zona se anula. Es posible que esa preferencia no tenga efecto. (no está claro en el código)

**Respondiendo a la remoción del rival: tributa el monstruo que se iba a perder igual.**
- Se encadena cuando el último en la cadena es el rival y no hay ya una Icarus de nuestra en la cadena.
- Monstruos "condenados" según lo que resuelve: Torrential Tribute = todos los nuestros; Mirror Force = nuestros en ataque; Bottomless = el que acabamos de invocar si tiene 1500+ ATK; Dimensional Prison o Sakuretsu Armor = el que está en batalla.
- Se activa si hay condenado y (hay 2 objetivos o la suma de lo que se lleva es 1500 o más). Tributa ese condenado.
- Funciona en cualquier turno.

**Icarus contra el Heavy Storm del rival: la mitad de las veces la usa antes de que se la lleve.**
- Si la Storm está resolviéndose, hay 2+ objetivos válidos y sale el 50% (azar). Es "arriesgado" y por eso no siempre.
- ⚠ En este caso no hay un monstruo condenado: el tributo sale del más barato y podemos perder un monstruo que no se iba. (no está claro en el código si eso es lo buscado)

**Icarus en nuestro paso de batalla: despeja bloqueadores para seguir atacando.**
- Solo si es la primera carta activable de la lista de batalla (la API puede ignorar el índice).
- El primer atacante aún no atacó y ningún monstruo rival puede ser vencido por él (todos lo frenan).
- Tenemos 2 o más monstruos (uno es el tributo), no hay bloqueo de Stardust ni de Tytannial.
- Y además pasa el chequeo general de rentabilidad (abajo).

**Cuando no hay respuesta: se activa si es rentable, en el turno del rival.**
- Necesita 2 o más cartas del rival en juego (monstruos + magias/trampas).
- Caso 1: un monstruo nuestro en batalla va a morir (su ATK mayor o igual al poder real nuestro) y el atacante sigue en el campo: tributa ese monstruo.
- Caso 2 (Black Rose): ver abajo.
- Caso 3: ganancia mayor o igual al costo. Ganancia = suma de sus 2 mejores objetivos. Costo = 1500 + mitad de la amenaza de nuestro tributo más barato (0 si es robado o token).
- En la práctica casi siempre que haya 2 objetivos decentes. ⚠ El comentario del código dice "no es un reflejo", pero el filtro es bajo. La ventana exacta en que el juego la ofrece decide cuándo ocurre. (no está claro en el código)

**Icarus contra el par tuner + no-tuner de nivel 7 (Black Rose).**
- Reciente. Solo en el turno del rival.
- Condición: ven dos monstruos boca arriba del rival, uno tuner y otro no, con niveles que suman 7.
- Y nos costaría 2 o más cartas el barrido (nuestros monstruos + nuestras magias/trampas menos la Icarus).
- Y todavía puede quedar una Black Rose: según la banlist, menos las copias vistas en su GY, desterradas o boca arriba.
- Entonces se activa y esos dos monstruos son los objetivos prioritarios (antes que cualquier otro).

**No se activa si:** Tytannial activo (siempre, porque Icarus cuesta tributo), Stardust activo sin Oppression/Crow, Trap Stun, ya hay otra Icarus en la cadena.

**Setear:** última de las trampas de la lista.

Código: `ShouldUseIcarus`, `FindBlackRosePair`, `DoomedByTheirRemovalAsync`, `ShouldChain` (bloque de Icarus), `DecideBattleCmdCoreAsync` (Icarus en el paso de batalla), `PickEnemyTargetsAsync`, `MaterialCost`

### Dimensional Prison

**Se activa contra el atacante en su turno solo si vale la pena.**
- Si el atacante es Stardust Dragon: siempre.
- Si hay un ataque letal (ATK mayor o igual a nuestros LP): siempre.
- Si hay un Stardust activo en su campo (otro que no ataca): solo con lethal. La Prison se guarda para Stardust.
- Si no hay Stardust: ataca con 1900 ATK o más, o lethal.
- Si el rival tiene otro monstruo en ataque más fuerte que todavía no atacó, la Prison lo espera (salvo que este golpe sea letal).
- Sin atacante declarado, nunca.
- No la bloquea el Stardust (no es de destrucción), pero sí Tytannial (apunta).

**Cómo cambia el bot cuando sabe que el rival tiene una trampa de ataque en la mano (se vio con Dustshoot).**
- Lista: Dimensional Prison, Mirror Force, Sakuretsu Armor, Magic Cylinder, Negate Attack, Waboku.
- Si además tiene backrow seteado, nuestros monstruos importantes (Extra Deck o 2300+ de ATK base) no atacan, salvo que el ataque gane el duelo (directo con ATK mayor o igual a sus LP).
- Si hay otros atacantes esperando detrás, el primero ataca igual (la API solo ataca con el primero de la lista: retenerlo retendría a todos).
- La información "vista en la mano" caduca: si aparece una copia pública (GY, desterrada, campo) se descarta.

**Setear:** tercera de la lista.

Código: `ShouldUsePrison`, `StrongerAttackerWaiting`, `DecideBattleCmdCoreAsync`, `StillInRevealedHand`

### Mirror Force

**Se activa contra un ataque si:** hay atacante y se cumple una de estas tres: el rival tiene 2 o más monstruos en posición de ataque, o el atacante tiene 1800 ATK o más, o el ataque es letal.
- Bloqueada con Stardust activo salvo lethal/Oppression/Crow. Un Mirror Force es de destrucción.
- **Excepción: cuando el que ataca es la propia Stardust, se activa.** Si Stardust la niega, se tributa y su ataque se cae.
- No se encadena un segundo si ya respondimos la cadena.
- **No contra un Card Trooper solo:** si el único monstruo del rival en ataque es Card Trooper (y no es letal), no se activa. Al destruirse le roba una carta, así que es un +1 para él.

**Si vimos una Mirror Force en su mano:** solo atacamos con un monstruo por batalla si ellos tienen backrow seteado y todavía queda una copia posible (banlist). Una vez que ya atacó uno, pasa a Main 2 o End.

**Setear:** cuarta de la lista.

Código: `ShouldChain` (rama de MirrorForce), `DecideBattleCmdCoreAsync`

### Bottomless Trap Hole

**Se encadena a cualquier invocación que el juego le ofrezca, en el turno del rival.**
- No hay chequeo de ATK propio (el juego solo la ofrece para 1500+).
- No hay chequeo de LP ni de "ya tenemos una respuesta barata".
- Vs Gladiator Beasts: se guarda hasta que haya una pieza clave boca arriba (Bestiari, Gyzarus, Heraklinos), porque el resto se retira solo.
- Bloqueada por Stardust activo (es de destrucción).
- ⚠ Con un Solemn disponible que debería salir primero (monstruo con efecto al invocar) no hay nada que frene a Bottomless: depende del orden en la lista. (no está claro en el código)

**Setear:** quinta de la lista.

Código: `ShouldChain` (bloque de Gladiator Beasts y tabla final)

### Royal Oppression

**Boca abajo: se da vuelta solo cuando el rival está por invocar de modo especial.** Una vez boca arriba, el rival también puede pagar 800 para negar *nuestras* invocaciones, así que darla vuelta sin negar nada es regalársela.
- Solo en el turno del rival y en una cadena o invocación del rival.
- La API no dice qué carta empezó la cadena, así que el bot lo deduce de su campo:
  - **Ventana de invocación** (sin cadena del rival) con un monstruo nuevo que solo entra por invocación especial: del Extra Deck, o de los que se invocan solos desde la mano (Quickdraw, Cyber Dragon, Gorz, Tragoedia, Cyber Valley, Chaos Sorcerer, BLS, DAD, Judgment Dragon, Machina Fortress).
  - La API no dice si fue normal o especial. Si en esa misma ventana un monstruo del rival dejó el campo, fue un tributo: Machina Fortress, Cyber Dragon, Gorz, Tragoedia y Quickdraw se toman como invocación por tributo y no se da vuelta.
  - **Cadena del rival** mientras controla un monstruo cuyo efecto invoca (Lonefire, Debris Dragon, Dandylion, Plaguespreader, Gladiator Beasts, Shura, Blizzard), o con una magia/trampa boca arriba que invoca (Monster Reborn, Call of the Haunted, Premature Burial, Scapegoat, Future Fusion).
  - **Cadena del rival en su Standby Phase con Drill Warrior desterrado:** es su vuelta al campo, así que se niega ahí. Antes la dejaba pasar y se daba vuelta en la ventana de invocación, cuando ya no había nada que negar.
- Si no, se queda boca abajo. Cada vez que se la ofrece, el log muestra la ventana (LastChainPlayer, LastSummonPlayer, monstruos nuevos) y la decisión, para revisar casos raros.
- ⚠ Es una deducción: si el rival tiene un Lonefire en el campo y activa otra cosa, igual se da vuelta.
- Con el Heavy Storm del rival resolviéndose, no se encadena (le quita la cadena a Solemn).

**Boca arriba: niega lo del rival, no lo propio, y no lo que no puede negar.**
- Hace falta tener más de 800 LP.
- Cada negación sale 800 LP.
- Si la fuente de la cadena es el rival: niega. Si es el bot: no. Si la API no dice quién fue: niega salvo que el bot haya iniciado una acción en su última decisión.
- No niega el efecto de Vayu desde el GY (se detecta porque se destierra a sí mismo como costo); ahí pasa.

**Sinergia con el Stardust:** tener Oppression seteada o boca arriba (con más de 800 LP) levanta el bloqueo de destrucción vs Stardust, porque penaliza su vuelta al campo.

**Con una Oppression boca arriba (nuestra o suya), cualquiera puede pagar 800 para negar una invocación especial, también el rival con la nuestra.** Por eso, mientras el rival pueda pagar:
- **Sincro:** solo como carnada: siempre si el rival tiene 3000 de vida o menos, o el 25% de los turnos (se sortea una vez por turno). Si no, no se invoca (se perderían los dos materiales).
- **Invocaciones especiales desde la mano (DAD, Bora, Gale…):** no se hacen si el rival tiene más de 3000 de vida.
- **Efectos que invocan (revive de Blizzard, floater de Shura):** se responde "no" al efecto si el rival tiene más de 3000 de vida, porque Oppression lo niega y destruye al monstruo.
- Pasó el 2026-10-17: el rival pagó dos veces con la Oppression del bot y destruyó a DAD y a Blizzard.

**Setear:** sexta de la lista.

Código: `ShouldUseRoyalOppression`, `SpecialSummonUnderway`, `TrySynchroSummon`

### Trap Dustshoot

**En nuestro turno se activa en Main Phase 1, antes de invocar o atacar.**
- El motor solo la ofrece con 4+ cartas en la mano del rival.

**En su turno se encadena apenas es legal** (primera oferta: Draw/Standby, cuando tienen más cartas), una vez por cadena.
- No la frena el Stardust.

**Qué monstruo se lleva (de la mano revelada del rival), por puntaje:**
- Primero las manos trampa que castigan los ataques: Gorz (4000, y +2000 más si tenemos monstruos en ataque), Tragoedia (3500), Kalut espejo (2500).
- Después los que arrancan combos: Stratos (2700), Machina Force (2700), Lonefire Blossom (2600), Quickdraw Synchron, Judgment Dragon, Dark Armed Dragon (2500), Debris Dragon, Machina Gearframe (2000), Caius, Raiza, Thestalos (1800), Card Trooper, Plaguespreader Zombie, Shura, Gale (1500), Bora (1000), Plasma (1000, más 2500 si ya tienen 3 monstruos para tributar).
- Más el bonus de amenaza, más ATK/10, más 300 si es de nivel 4 o menos (invocable ya).
- Ajustes: Debris Dragon pierde todo su valor si su GY está vacío (carta muerta). Con el GY vacío, los que buscan del mazo (Gearframe, Stratos) suben 1500. Machina Force baja 1500 si no hay Machina Fortress en su GY. Lightsworn Wulf nunca, salvo que sea el único: en la mano es una carta muerta y de vuelta en el mazo se vuelve viva.

**Lo que vemos de la mano se recuerda** para otras reglas (Gorz, Mirror Force, trampas de ataque, Heavy Storm), pero caduca cuando aparece una copia pública.

**Setear:** séptima de la lista.

Código: `HandPickScore`, `RevealHandCardsAsync`, `DecideGenericOpeningAsync`, `StillInRevealedHand`

### Starlight Road

**Se encadena siempre que el juego la ofrece.**
- El juego solo la ofrece ante destrucción masiva de nuestras cartas (Heavy Storm, Dark Hole...).
- En cualquier turno, sin chequeos de LP ni de Stardust.
- Solo la frena Trap Stun.
- ⚠ No hay chequeo de "ya respondimos" ni de copia duplicada para esta carta: sale antes de esos filtros. (en la práctica el juego no la ofrece mal)

**Setear:** segunda de la lista; además es una de las dos cartas que levantan el tope de sets contra Heavy Storm.

Código: `ShouldChain` (bloque de Starlight Road), `DecideSets`

### Book of Moon

**En nuestro turno, en la declaración de ataque: gira boca abajo un bloqueador al que no le ganamos boca arriba.**
- El atacante ataca a un monstruo del rival boca arriba, no token, con poder real mayor o igual al ATK del atacante, pero DEF menor al ATK del atacante.
- El bloqueador es de amenaza 1500 o más, o el atacante es de los que tienen efecto al destruir por batalla (Shura).
- Los atacantes de efecto de batalla van primero; después, por ATK descendente.
- Si el primero no es de ese tipo y hay uno de ese tipo esperando, y el primero ya gana solo contra algo, ataca eso y la Luna se guarda para el que viene.
- No se hace con Tytannial bloqueando.
- En nuestro turno no se usa la Luna de otra manera (solo si se planeó en esa declaración).

**En Main Phase 1 contra un Stardust activo:** si tenemos Heavy Storm en la mano o una Icarus seteada, se juega Luna sobre el Stardust para que no bloquee la destrucción ese turno.
- ⚠ Solo cuenta Storm en mano e Icarus seteada. Una Mirror Force o Solemn seteada no activa esta regla en nuestro turno (sí en el suyo, abajo).

**En su turno, en Draw/Standby:** si hay un Stardust activo y tenemos destrucción seteada (Mirror Force, Bottomless, Solemn, Icarus...), Luna sobre el Stardust para que nuestras trampas funcionen ese turno.

**En su turno, en declaración de ataque: sobre el atacante, solo si vale una carta.**
- Prioridad: si tienen Plasma boca arriba y sin negar, la Luna va al Plasma en vez de al atacante (boca abajo deja de negar nuestros monstruos y suelta el que robó), salvo que el ataque sea letal.
- Ataque directo: solo si es letal o de 2000 ATK o más.
- Ataque contra un monstruo nuestro: solo si el nuestro muere (ATK mayor o igual al ATK nuestro, o mayor que la DEF si está en defensa), está boca arriba y es importante (Extra Deck o ATK base 2300+) o tiene 1700 o más de ATK.
- No se usa para salvar un monstruo boca abajo ni por un golpe chico.

**Setear:** la juega seteada cuando no hay Main 1 con batalla pendiente (después de las trampas).

Código: `DecideBattleCmdCoreAsync` (Book of Moon at declaration), `UseBookOfMoonOnAttacker`, `DecideGenericOpeningAsync`, `ShouldChain`

### Mystical Space Typhoon

**Desde la mano en nuestro turno:**
- Siempre contra una carta boca arriba duradera (continua, equipo, campo).
- Contra una carta boca abajo si vamos a comprometer algo ese turno (hay invocación posible en Main 1).
- Si no, solo el 30% de las veces (azar) mientras tengan backrow seteado: para no ser predecible. El resto de las veces se setea para después.
- No con Stardust activo ni con Tytannial bloqueando (apunta).

**Seteada, en su turno:** se encadena en su End Phase si tienen una carta boca abajo (limpia antes de nuestro turno, tarde para que setee otra), o cuando el rival empieza una cadena y tiene una carta duradera boca arriba.

**Qué elige:** la de mayor valor de remoción.
- ⚠ Boca abajo = 1500, boca arriba duradera = 1200 más bonus. Así que, si hay ambas, elige la boca abajo aunque la regla de la mano dijera "siempre boca arriba". (no está claro en el código si es intencional)

Código: `DecideGenericOpeningAsync`, `ShouldChainMst`, `PickEnemyTargetsAsync`

### Heavy Storm

**Se juega cuando se lleva más de lo que se llevaría de nosotros.**
- Sus magias/trampas (todas, boca arriba y abajo) más que las nuestras: sí.
- Igual cantidad (y al menos 1): solo si estamos adelante en cartas totales (mano + campo, restando la Storm) o abre lethal.
- Total propio = mano + monstruos + magias/trampas. Total del rival = su mano + monstruos + magias/trampas.
- Nunca con Stardust activo (salvo reglas comunes).
- Solo desde Main (no se encadena).
- ⚠ Cuenta todas las cartas del rival aunque sean magias normales ya resueltas, no solo backrow seteado.

**Mientras tengamos Heavy Storm en la mano, no se setea más de 1 carta** (para perder poco al usarla), salvo que estemos desesperados (2000 LP o menos).

Código: `DecideGenericOpeningAsync` (stormEdge), `DecideSets`

### Brain Control

**Roba al monstruo más amenazante, pero solo con un plan para ese turno.**
- El objetivo es el mayor por amenaza (Stardust primero), con amenaza 1500 o más.
- Debemos tener más de 1600 LP (cuesta 800).
- Planes válidos (al menos uno):
  - En nuestro Main 1, con batalla posible, atacar con él a otro monstruo rival boca arriba que no sea token y cuyo poder real sea menor o igual al ATK del robado.
  - Quedarse sin monstruos rivales y que robado + nuestros atacantes en ataque hagan lethal.
  - Sincro con un tuner nuestro boca arriba (nivel tuner + nivel robado dentro de los niveles del Extra del mazo, 5 a 8 por defecto). Vayu no cuenta como tuner de material si sus efectos están activos.
  - Un uso propio del mazo (por ejemplo tributarlo, ver sección de mazo).
- No se roba "para guardar" ni en Main 2.
- Tytannial con otro Plant lo bloquea (apunta).
- Tytannial acorralada (es su único Plant) es plan suficiente en nuestro Main 1: o se autosacrifica para negarlo, o nos quedamos con el monstruo.
- Tras robar: el monstruo robado es el material más barato (-1000), ataca con empates a su favor (vuelve al End Phase), y si es Stardust no usa su efecto (se destruye en vez de volver).
- Se prefiere el mismo monstruo al elegir objetivo (misma tabla de amenaza).

Código: `DecideGenericOpeningAsync`, `HasPlanForStolenMonster`, `DecideGenericAttackAsync` (robados)

### Kalut (contra Blackwings)

**No se ataca a un Blackwing boca arriba en ataque si Kalut (+1400) le daría la batalla**: el rival tiene cartas en la mano y le quedan copias de Kalut. Mejor sacarlos 1 a 1 con efectos.

Código: `KalutTurnsIt`

### Book of Moon contra el wipe de Judgment Dragon

**Si el rival activa el efecto de su Judgment Dragon en su Main 1, Book of Moon lo da vuelta en respuesta**: Book se destruiría igual, y boca abajo JD no puede atacar ese turno.

Código: `ShouldChainAsync` (Book of Moon)

### Judgment Dragon propio o robado

**Si controlás un Judgment Dragon sin regla del mazo (por ejemplo robado con Brain Control), usa su efecto** (1000 LP, destruye todas las demás cartas):
- si después JD pega directo y gana (Main 1, todavía no atacó, su ATK alcanza la vida del rival), o
- si el rival tiene 2 cartas o más que vos en el campo.
- No con 1000 LP o menos ni con Stardust activo.

Código: `DecideJudgmentDragon`

### Allure of Darkness

**Se juega si tenemos en la mano otro monstruo DARK.**
- Chequea monstruos DARK en la mano que no sean la propia Allure.
- Costo (desterrar): la carta de menor valor de mano (Vayu/Necro Gardna 0, Upstart 1, el resto 2, cartas clave 4). Desempata el orden del mazo. Una carta clave (como DAD) solo se destierra si es la única opción.
- ⚠ El código solo exige "un DARK en la mano", no que robar 2 sea buena idea con la mano llena. (no está claro en el código)

Código: `DecideGenericOpeningAsync`, `DecideSelectCardCoreAsync` (REMOVE desde la mano)

### Upstart Goblin

**Se juega siempre que el juego lo ofrece en Main, salvo que le rompa un lethal.** Valor de mano 1, así que se descarta primero.
- **En nuestra Main 1, si nuestros monstruos en ataque suman lo que le queda de vida al rival, espera a la Main 2** (le da 1000 LP).

Código: `DecideGenericOpeningAsync`

### D.D. Crow

**Se guarda para el Stardust en el GY del rival.**
- Se encadena cuando el juego la ofrece y hay un Stardust Dragon (cualquier copia) en el GY rival; si no, no se usa.
- Tenerla en la mano levanta el bloqueo de destrucción vs Stardust.
- ⚠ No chequea cuándo se activa respecto de la vuelta del Stardust: usa la ventana que el juego le dé. (no está claro en el código)

Código: `ShouldChain` (bloque de DDCrow), `IsStardustBlocking`

El orden de seteo, los topes contra Heavy Storm y qué synchro elige están en las reglas generales (Seteo y Synchro).

## Cartas del rival que el bot reconoce

### Stardust Dragon

**Qué hace el bot distinto:**
- Amenaza +4000 (la mayor de la tabla); Brain Control lo apunta primero; Solemn lo considera "digno" aunque no tenga 2400 ATK.
- Bloqueo de destrucción: ver "Cadenas: guardas generales" (cartas de destrucción en pausa mientras esté activo y no tengamos Oppression/Crow).
- Prison se guarda para él; si Stardust no ataca pero está en juego, Prison solo con lethal.
- Book of Moon para apagarlo (Main 1 con Storm/Icarus, o su Draw/Standby con destrucción seteada).
- D.D. Crow se guarda para cuando esté en su GY.
- Black Rose no se invoca con un Stardust activo.
- Si lo robamos con Brain Control: su efecto no se usa (se destruiría y volvería con el dueño original), y ataca con empate de ATK a un monstruo equivalente porque vuelve igual.
- Una sincro nuestra de Stardust suma 30 si hay 2+ cartas seteadas (protección contra backrow).
- Si está negado (por ejemplo Plasma), no cuenta como activo.

Código: `IsStardustActive`, `IsStardustBlocking`, `ShouldUsePrison`

### Machina Fortress

**Dos castigos distintos, dos reglas:**
- Si lo apuntamos con un efecto de monstruo, ve nuestra mano y descarta la mejor. Si lo destruimos en batalla, destruye una carta nuestra.

**Efectos de monstruo (los de nuestro mazo) contra Fortress:**
- Se lo excluye como objetivo salvo que la necesidad iguale o supere el costo.
- Costo = valor de la mejor carta de la mano (0, 1, 2 o 4 según la tabla de valor).
- Necesidad = 3 si ninguno de nuestros monstruos "prescindibles" le gana en batalla (contando bonus de la mano), más 2 si es peligroso (no tenemos monstruos o nuestros LP son menores o iguales al doble de su ATK).
- Monstruos importantes (Extra Deck o 2300+ ATK base) no cuentan como "le gana en batalla" salvo que sean robados: no los arriesgamos.
- Mano vacía: siempre se puede apuntarlo.
- Si se va a apuntar igual, antes se vacía la mano en el backrow (magias incluidas) para que el descarte cueste lo menos posible.
- Si la última activación fue un efecto de monstruo, la selección no apunta a Fortress salvo que valga la pena.

**Ataques contra Fortress:**
- Un monstruo importante no lo ataca (se pierde por su efecto), salvo que sea robado.
- Armor Master no ataca si solo hay Fortress en su campo, y se invoca/queda en defensa; también se queda en defensa si no habrá batalla este turno (turno 1 o Main 2) y el rival tiene una Fortress boca arriba, para que no se la estrellen. Si no, queda en ataque (en defensa Ehren la devolvió al deck).
- Goyo no roba una Fortress ni la ataca primero.
- Los hechizos/trampas que apuntan (Prison, MST, Icarus) no lo penalizan: solo se evitan los efectos de monstruo.

**Machina Gearframe (Union):** destruir el monstruo con la Union equipada destruye la Union; por eso Icarus y el ataque apuntan a la Union, no al monstruo.

Código: `IsFortressTargetWorthItAsync`, `IsFortressTrap`, `WillTargetFortress`, `DecideGenericAttackAsync`

### Tytannial, Princess of Camellias

**Niega todo lo que apunte a una carta del campo, tributando un Plant boca arriba (puede ser ella misma).**
- Si tiene otro Plant (Fluff Token, etc.): ninguna carta que apunte se juega (Mystical Space Typhoon, Book of Moon, Brain Control, Dimensional Prison, Icarus Attack, y los efectos de apuntado del mazo, incluidos Dark Armed Dragon, Dark End Dragon y Brionac).
- **Si solo puede tributarse ella (acorralada): se juega cualquier carta que apunte, aunque cueste una carta extra (Icarus, Dark Armed Dragon, Brionac).** Quemar un recurso para que se autosacrifique conviene.
- No cuenta si está negada o boca abajo.
- El Plant se detecta por raza, no por nombre.

Código: `IsTargetingBlocked`, `IsTytannialActive`, `TytannialCornered`, `TryActivate`

### Gladiator Beasts

**Detección:** cualquier carta cuyo nombre empiece con "Gladiator Beast" vista en su GY, desterrada, en campo o revelada.
- Amenaza extra: Bestiari +3000, Gyzarus +2500, Heraklinos +2500.
- Bottomless Trap Hole espera una pieza clave (Bestiari, Gyzarus, Heraklinos) en su campo, porque el resto se retira solo.
- Solemn tiene prioridad en la invocación de Bestiari y Gyzarus.
- El mazo Blackwings usa además un indicador "vs Gladiator Beasts" en dos de sus jugadas (no cubierto acá).

Código: `OpponentPlaysArchetype`, `ShouldChain` (Bottomless)

### Destiny HERO - Plasma

**Niega los efectos de nuestros monstruos y roba uno como equipo.**
- Amenaza +2500.
- Book of Moon la prefiere al atacante en su turno (boca abajo deja de negar y suelta el robado), salvo ataque letal.
- Cuando Plasma niega nuestros monstruos, el ataque genérico igual los cuenta como monstruos (la lógica base los ignoraba y veía un campo vacío).
- Dustshoot: 1000, más 2500 si ya tienen 3 monstruos para tributar.

Código: `BookOfMoonPriority`, `UseBookOfMoonOnAttacker`, `DecideGenericAttackAsync`

### Gorz, the Emissary of Darkness

**Ya no se frena el ataque por Gorz** (se sacó: el 2026-10-24 canceló 3 ataques directos por 5100 de daño, porque quedarse con el primero frena a todos).
- Dustshoot: prioridad máxima (4000, más 2000 con nuestros atacantes en juego).
- Icarus en nuestro paso de batalla se usa para despejar a Gorz y su token cuando frenan al próximo atacante.

Código: `HandPickScore`

### Mirror Force y Torrential Tribute (castigan sobreextenderse)

**Qué hace el bot:**
- "Todavía puede tener" = según la banlist, aún quedan copias descontando las vistas en GY, desterradas o boca arriba. Si ya se usó la única copia, la regla se apaga.
- Con lethal ya en la mesa (su campo sin monstruos y nuestros atacantes en ataque suman sus LP) y backrow seteado: no se juega nada más en Main (se retiene el resto de la mano).
- Con 1+ monstruo propio y 2+ cartas seteadas del rival: no se suma otro cuerpo, salvo tuners, monstruos importantes o si el monstruo da lethal sobre campo vacío.
- Torrential y Mirror también marcan "condenados" para Icarus (ver arriba).

Código: `ShouldAvoidOverextending`, `OpponentMayStillHave`, `HasLethalOnBoard`

### Trap Stun

**Sabe que ese turno ninguna trampa funciona.**
- Se detecta cuando está boca arriba en su campo o cuando aparece una copia nueva en su GY o desterrada desde la última mirada.
- Hasta el final de ese turno: no se activan ni se encadenan trampas nuestras.
- Se limpia al empezar otra partida del match.
- ⚠ Setear sí se sigue haciendo con Trap Stun activo; solo se frenan las activaciones.

Código: `UpdateTrapStun`, `TrapsStunned`

### Heavy Storm del rival

**Qué cambia:**
- Solemn Judgment sobre la Storm si salva más de lo que perdemos (ver Solemn).
- Icarus: la mitad de las veces la usa en respuesta (ver Icarus).
- Si se vio una Storm en su mano (Dustshoot) y no salió: máximo 1 carta seteada nuestra.
- Si podría tener una: tope de 2 o la cantidad de sus cartas seteadas, salvo Solemn/Starlight Road seteadas.
- Royal Oppression boca abajo no se encadena.

Código: `ShouldChain` (theirStormResolving), `DecideSets`

### Black Rose Dragon (rival)

**Qué cambia:**
- Se juega Solemn sobre su invocación (efecto de barrer el campo).
- Si el rival tiene un tuner y un no-tuner que suman nivel 7 y podría tener Black Rose, Icarus rompe el par (ver Icarus).
- Se mira la banlist para saber si todavía puede quedar una.

Código: `FindBlackRosePair`, `SolemnOnSummonThreat`

### Caius, Raiza, Thestalos (Monarcas)

**Qué cambia:** Solemn sobre su invocación (efecto al invocar). Caius además +1500 de amenaza. En Dustshoot valen 1800.

### Otros con amenaza extra en el puntaje (a quién se ataca / roba / destruye primero)

**El puntaje base de amenaza es el mayor entre el ATK base y DEF/2** (se usa ATK base: un pump temporal como los de Card Trooper no cuenta). +300 si es del Extra Deck. Un 0/0 boca arriba resta 1000. Boca abajo = 1000.
- Bonos: Stardust +4000, Jinzo +3000, Judgment Dragon +3000, Dark Armed Dragon +3000, Bestiari +3000, Gyzarus +2500, Heraklinos +2500, Plasma +2500, Goyo +2000, Dark End Dragon +2000, Brionac +2000, Caius +1500, Plaguespreader Zombie +1500, Spirit Reaper +1000, Gorz +1000.
- Sirve para: a quién va Brain Control, qué ataca el bot cuando todos los objetivos puntúan igual, a quién roba Goyo, qué va primero para Icarus y qué se lleva Dustshoot.
- Con empate de objetivos de ataque, elige la mayor amenaza, no el de más a la izquierda.
- Un monstruo negado (por ejemplo por Plasma) pierde su bonus de amenaza.

Código: `ThreatScore`, `GetPriorityEnemyMonster`, `RemovalValue`

### Cartas que el bot solo conoce por Dustshoot (prioridad de mano)

- Quickdraw Synchron, Lonefire Blossom (trae un Plant como Tytannial gratis), Elemental HERO Stratos (busca un HERO, como Plasma), Machina Gearframe, Machina Force (con nivel 10 descartado revive Machina Fortress gratis), Debris Dragon, Card Trooper, Plaguespreader Zombie, Tragoedia y los Blackwings espejo (Shura, Gale, Bora, Kalut). Ver tabla de puntajes en Trap Dustshoot.
- Dandylion: no hay regla propia. Solo aparece como parte de un posible par tuner + no-tuner de nivel 7 para la regla de Icarus vs Black Rose.

### Trampas de ataque en la mano del rival (conocidas por Dustshoot)

- Dimensional Prison, Mirror Force, Sakuretsu Armor, Magic Cylinder, Negate Attack, Waboku: ver Dimensional Prison (nuestros monstruos importantes no se tiran contra el backrow seteado, salvo lethal u otros atacantes esperando).

### Royal Oppression del rival (boca arriba)

- Como ya se explicó en Royal Oppression: con una Oppression boca arriba de cualquiera de los dos, el bot frena sus invocaciones especiales mientras el rival pueda pagar.
- El efecto de Vayu desde el GY no se puede negar con Oppression: el bot no la gasta ahí (la detecta porque Vayu se destierra solo).

Código: `TrySynchroSummon`, `Chain.OpponentJustUsedUnnegatable`
