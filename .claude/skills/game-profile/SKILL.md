---
name: game-profile
description: Aggiunge o modifica un game profile di K2 (profilo DisplayPad per-gioco che si attiva quando il gioco parte). Usala quando si parla di "game profile", "profilo gioco", di aggiungere un gioco alla scheda Game profiles, di tile/background per un gioco, di azioni specifiche di un gioco nel browser azioni, o di leggere lo stato live di un gioco per accendere i tasti.
---

# Game profile K2

Un game profile è un profilo DisplayPad riservato che K2 crea da solo, mantiene
allineato al catalogo e attiva quando il processo del gioco parte. L'utente non
lo costruisce: lo accende, lo configura e al massimo ritocca qualche tasto.

Prima di toccare qualcosa leggi
`K2.App/MainWindow.GameProfiles.cs` — il commento di classe descrive il gate
di visibilità e l'assegnazione del device, che non vanno reinventati.

## Le quattro regole

Valgono per **ogni** profilo, presente e futuro. Se un gioco non può
rispettarne una, dillo esplicitamente invece di aggirarla in silenzio.

1. **I tile indossano la grafica del gioco.** Ogni tasto *mappato* del profilo
   viene generato sul background del gioco, non con l'icona d'azione generica di
   K2 — anche i tasti che l'utente ha aggiunto a mano, perché la pagina deve
   leggersi come un unico pannello. Uno slot **vuoto** non viene mai stilizzato:
   un tasto spento è onesto, una cornice accesa attorno al nulla no. Il
   comportamento è il flag `StyleAllTiles` dello spec, non una lista di
   eccezioni.
2. **Le azioni del gioco stanno nel browser accanto a "Input".** Un game profile
   non mostra la griglia generica dei tipi d'azione: mostra le *famiglie di
   comandi* del gioco (livello 2) e i comandi (livello 3). Che sotto siano
   `keys` o un tipo live è un dettaglio implementativo che l'utente non vede.
3. **Si cerca una connessione al gioco per i valori di ritorno.** Prima di
   inventare, cerca sempre una fonte ufficiale e in sola lettura che il gioco
   già espone (file di stato, log, API locale) per: (a) accendere/spegnere i
   tile sullo stato reale, (b) leggere i **bind reali** del giocatore invece di
   premere i default. Mai hooking, mai lettura di memoria.
4. **L'icona si prende dall'eseguibile del gioco.** Mai una PNG shippata: la si
   estrae dall'exe trovato su quella macchina.

## I file, e cosa mettere dove

| Cosa | Dove |
|---|---|
| Mappatura tasti, pagine, nome, processo, AppID Steam | `GameProfileCatalog` in `K2.App/MainWindow.GameProfiles.cs` |
| Grafica, accento, `StyleAllTiles`, famiglie comandi | `GameProfileSpecs.All` in `K2.Core/GameProfileSpec.cs` |
| Famiglie e comandi | `ActionTypeHelper` in `K2.Core/ActionTypeHelper.cs` |
| Background dei tile | `K2.App/Assets/GameProfiles/<ArtFolder>/` |
| Lettori di stato / bind del gioco | `K2.App/Services/` (modello: `EliteStatusReader`, `EliteBindsReader`) |
| Pittura live dei tile | `DpLiveTileService` in `K2.App/Services/` |
| Ricerca exe + icona | `GameExeResolver` in `K2.App/Services/` — **non toccare**, funziona per tutti |

Catalogo e spec sono legati **solo** dalla stringa `Id`: devono coincidere
esattamente. Un gioco senza spec è legittimo — è il caso "solo scorciatoie da
tastiera", che eredita il default.

## Aggiungere un gioco: checklist

1. **`Definition`** in `GameProfileCatalog.All`: `Id`, nome, `ExeName` (nome
   processo, senza estensione né percorso — *verificato*, non indovinato),
   `SteamAppId` o `null`, le pagine.
   - Uno slot che il profilo lascia scoperto è `Empty()`, non un'azione
     inventata. Un tasto che non fa nulla è onesto; uno che manda un bind
     indovinato no.
   - Le caption del catalogo sono **loc key** (`CaptionIsLocKey: true`). Quelle
     che l'utente riscrive diventano testo letterale con il flag a `false` e non
     passano più da `Loc`.
2. **Grafica** (regola 1): sei PNG in `Assets/GameProfiles/<ArtFolder>/`,
   nominate `{ArtPrefix}_{colore}_{on|off}.png`. Il colore di riposo è
   `orange` (vedi `GameProfileTheme.RestingArtColor`); le altre varianti sono le
   eccezioni per gli stati. Il glob `Content` in `K2.App.csproj` le raccoglie
   già: nessuna modifica al csproj. Un PNG mancante degrada a "nessuno sfondo",
   non lancia.
3. **`GameProfileSpec`** in `GameProfileSpecs.All`: `ArtFolder`, `ArtPrefix`,
   `Accent` (il colore HUD del gioco), `StyleAllTiles`, famiglie comandi.
4. **Comandi** (regola 2): un array
   `(LocKey, Glyph, GameCommand[])` sul modello di
   `ActionTypeHelper.EliteCommandFamilies`. Ordina le famiglie come vanno a
   schermo e raggruppa un comando dove il giocatore lo cercherebbe, non dove è
   comodo al codice.
5. **Connessione al gioco** (regola 3), se esiste:
   - *Stato live* → nuovo lettore in `Services/` + nuovo action type live.
     Aggiungere un action type live tocca più punti di quanti sembri: fai
     `grep -rn dp_edstatus . --include=*.cs --include=*.xaml` e replica ogni
     occorrenza per il tuo tipo (`ActionTypeHelper`, `ButtonActionDialog`
     Simple/TypePicker/xaml, `DpDefaultIconRenderer`, `DpLiveTileService`,
     `LiveTileRenderer`, `Models/DisplayPadKey`, `DpKeyConfigDialog`).
   - *Bind reali* → lettore sul modello di `EliteBindsReader`, agganciato in
     `SyncedBind` (`MainWindow.GameProfiles.cs`). `SyncedBind` restituisce
     `null` — cioè non cambia nulla — ogni volta che staremmo tirando a
     indovinare: gioco che non conosciamo, controlli mai personalizzati,
     comando su HOTAS, o valore che l'utente ha già fissato a mano.
   - Se il gioco non espone niente, il profilo resta di sole scorciatoie. È un
     esito accettabile, da dichiarare.
6. **Stringhe**: ogni chiave nuova in `K2.Core/Strings.xml` **e**
   `Strings.it.xml`, nella stessa sessione. Le altre lingue non hanno le chiavi
   `game_profile_*` / `gp_*` e vanno lasciate stare (fallback su EN).
7. **Build**: `dotnet build K2.sln`. Se K2.App è in esecuzione **elevato** il
   copy in `bin` fallisce e la build resta stantia in silenzio: compila su un
   `-p:OutputPath=` alternativo, oppure chiedi all'utente di chiuderlo.
8. **Test su hardware**: sempre a carico dell'utente. Da far verificare:
   il profilo si attiva davvero all'avvio del processo, i tile prendono la
   grafica giusta, gli stati live seguono il gioco.

## Quello che è già risolto per tutti — non rifarlo

- **Icona ed exe (regola 4).** `GameExeResolver` cerca in ordine: percorso già
  risolto e ricordato → processo in esecuzione → metadati di Steam. L'utente può
  scavalcare tutto indicando un `.exe` arbitrario dalla config del profilo
  (chiave `gameprofile.{id}.exeuser`, distinta dalla cache `.exepath`
  dell'auto-detect proprio perché l'una non deve poter sovrascrivere l'altra).
  Il path fissato governa **sia** l'icona **sia** il processo su cui si arma il
  watcher, e non ha fallback: un pin rotto resta visibile invece di mostrare
  l'icona di un altro programma.
- **Attivazione.** `ProfileLaunchWatcher` fa già polling processo, gate
  foreground, ritorno automatico e ripristino all'uscita.
- **Slot sul device.** `EnsureGameSlot` crea e risincronizza uno slot riservato,
  riconosciuto dal prefisso `"Game: "` nel nome, e lo tiene fuori dalla lista
  dei profili normali.
- **Persistenza.** Tutto sta in `DisplayPadStore` sotto `gameprofile.{id}.*`.
  Un tile non toccato non occupa nulla e continua a seguire il catalogo: le
  chiavi assenti valgono "come shippato", e i flag assenti valgono **acceso**
  (solo uno `"0"` esplicito spegne).
