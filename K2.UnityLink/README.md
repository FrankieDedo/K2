# K2 Unity Link

Mod BepInEx generica per giochi Unity (backend **Mono**, non IL2CPP): espone lo stato del gioco
in JSON su `http://127.0.0.1:34873/state`, cosi' K2 lo puo' leggere come un qualsiasi "collegamento
al gioco" (`CustomSource.Link`, Game Studio > Collegamenti al gioco).

Non serve conoscere il gioco in anticipo: la mod cammina **tutte le scene caricate** (non solo
quella attiva) piu' gli oggetti persistenti (`DontDestroyOnLoad`, dove molti giochi tengono
personaggio/HUD/manager) e legge i campi di ogni script (`MonoBehaviour`) trovato su ogni
`GameObject` — vita, munizioni, fame, posizione, qualsiasi cosa il gioco tenga in un campo o in una
proprieta' leggibile, **inclusi i campi privati** (vedi sotto: e' li' che di solito stanno
vita/munizioni/fame in un gioco scritto bene). Componenti nativi del motore (Transform a parte,
Collider, Renderer, Rigidbody...) non hanno stato di gioco riflettibile e sono esclusi in automatico.

## Requisiti

- Il gioco deve usare il backend Mono di Unity (la maggioranza dei titoli indie/medi). I giochi
  IL2CPP non sono supportati da questa mod.
- [BepInEx 5](https://github.com/BepInEx/BepInEx/releases) installato nel gioco (x86 o x64, in base
  al gioco — la guida di BepInEx spiega come scegliere).

## Installazione

1. Installa BepInEx 5 nella cartella del gioco (scompatta lo zip li' dentro, avvia il gioco una
   volta per lasciargli generare `BepInEx/plugins`).
2. Copia `K2.UnityLink.dll` dentro `<cartella gioco>/BepInEx/plugins/`.
3. Avvia il gioco. Il log di BepInEx (`BepInEx/LogOutput.log`) conferma con una riga
   `serving snapshot on http://127.0.0.1:34873/state`.
4. In K2, Game Studio > apri/crea un profilo > una lettura > Collegamento al gioco > Nuovo
   collegamento (mod) > preset "K2 Unity Link" (o incolla a mano l'indirizzo sopra) > Test, o
   il bottone a forma di lente accanto al campo Valore per sfogliare tutto ad albero — utile
   perche' con i campi privati inclusi i valori possono essere centinaia.

## Performance: cammina tutto solo quando serve

La mod tiene DUE istantanee, a velocita' diverse:
- una **completa** (tutta la scena), ricostruita **solo quando qualcuno la chiede** e solo se
  l'ultima e' piu' vecchia di `FullRefreshMs` (4s di default, cioe' un'ETA' MASSIMA, non un timer) —
  quella che vedi nel browser ad albero e nel bottone Test, per scoprire un valore che non hai
  ancora salvato da nessuna parte. Mentre giochi e basta non viene ricostruita mai: girava a timer
  nelle prime versioni e costava un micro-freeze ogni pochi secondi, per tenere fresco qualcosa che
  serve solo mentre un umano guarda un elenco;
- una **ristretta**, ricostruita ogni `RefreshMs` (0.5s di default) — SOLO ai valori che K2 le dice
  di leggere, una volta che una lettura e' salvata in un profilo. K2 comunica quali con una query
  string (`?k2want=...`) che aggiunge da solo alle richieste in background: niente da configurare,
  non serve nemmeno sapere che esiste. Il vantaggio e' che, una volta finita la configurazione, il
  cammino costoso di TUTTA la scena si ferma quasi del tutto — resta solo quello lento (ogni 4s) per
  poter sempre riaprire il browser e cercare qualcos'altro.

## Configurazione

Al primo avvio del gioco la mod scrive `BepInEx/config/com.k2tent.unitylink.cfg`: porta, quanto
spesso ricostruire lo snapshot ristretto/quello completo, quanti GameObject/campi al massimo, se
includere gli oggetti disattivati. Utile anche quando la porta di default (34873) confligge con
un'altra mod.

Due flag decidono cosa entra nello snapshot:
- `IncludePrivateFields` (ON di default) — campi privati, dove di solito stanno vita/munizioni/fame.
- `ShowPublicFields` (OFF di default, marcato "avanzato/debug") — campi pubblici, quasi sempre
  riferimenti dell'Inspector (altri GameObject, prefab, colori) piu' che numeri di gioco: con i
  privati gia' inclusi sono per lo piu' rumore, utile solo se stai cercando qualcosa esposto
  proprio cosi'. Sempre modificabile a mano nel `.cfg`; se hai anche la mod
  `BepInEx.ConfigurationManager` installata (F1 in gioco), compare solo attivando "Show advanced
  settings". Vale solo per i campi dello script: dentro gli oggetti del gioco letti in
  profondita' (`Stats.Health` in 7 Days to Die) i campi pubblici si leggono sempre. Su un
  assembly "publicizzato" (7 Days to Die) i campi marcati `[PublicizedFrom]` contano come privati.

## Limiti noti

- Solo Mono, non IL2CPP (vedi sopra).
- Legge solo campi/proprieta' di tipo semplice (numeri, stringhe, enum, Vector2/3/4, Quaternion,
  Color) — oggetti annidati complessi non vengono seguiti, per evitare cicli di riferimenti e dump
  incontrollati. Le proprieta' PRIVATE non vengono lette (solo i campi privati): sono quasi sempre
  storage interno senza nulla che valga la pena mostrare su un tasto.
- I nomi mostrati sono quelli dello sviluppatore del gioco (nome dello script, nome del campo): la
  mod non sa cosa significhino, si limita a esporli. Usa il browser ad albero nello studio K2 per
  orientarti tra i valori mentre il gioco gira.
- Lo snapshot ristretto (vedi sopra) trova il GameObject per nome/percorso: se lo rinomini o lo
  sposti dopo aver salvato la lettura, o se e' disattivato in quel momento, quel valore torna "—"
  finche' non lo ri-salvi da capo — stessa regola di sempre per un path che smette di risolversi.
