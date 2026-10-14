# SHENT: un sistema di contante elettronico comunitario peer-to-peer

**Lingue:** [Español](GUIDE.es.md) · [English](GUIDE.en.md) · [Português](GUIDE.pt.md) · [Français](GUIDE.fr.md) · [Deutsch](GUIDE.de.md) · [Italiano](GUIDE.it.md) · [Русский](GUIDE.ru.md) · [中文](GUIDE.zh.md) · [日本語](GUIDE.ja.md) · [Türkçe](GUIDE.tr.md)

---

## Sommario

Una versione puramente peer-to-peer del contante elettronico permetterebbe di inviare pagamenti da una
persona a un'altra senza passare attraverso alcuna istituzione. Le firme digitali risolvono una parte del
problema, ma i vantaggi principali vanno perduti se serve ancora una terza parte fidata per impedire che una
stessa moneta venga spesa due volte. SHENT risolve la doppia spesa con una rete peer-to-peer. La rete registra
le transazioni in una blockchain, e ogni blocco è sigillato da una prova di lavoro. Il registro risultante non
può essere modificato senza rifare quel lavoro. La catena più lunga, misurata in lavoro accumulato e non in
numero di blocchi, serve come prova della sequenza degli eventi e come prova di essere stata prodotta dalla
maggiore potenza di calcolo. Finché la maggior parte di quella potenza è nelle mani di nodi che non cooperano
per attaccare la rete, questi nodi generano la catena più lunga e superano qualsiasi attaccante. La rete in sé
richiede una struttura minima. I messaggi vengono diffusi secondo il criterio del miglior sforzo, e i nodi
possono andarsene e tornare quando vogliono, accettando la catena con più lavoro come prova di ciò che è
accaduto durante la loro assenza.

SHENT è una moneta comunitaria. Il blocco genesi lo dice nel proprio testo:
`SHENT 13/10/2026 - moneda comunitaria sin valor monetario` («SHENT 13/10/2026 - moneta comunitaria senza
valore monetario»). Non è quotata su alcun mercato, l'applicazione non compra né vende nulla e nessuno promette
che varrà mai qualcosa.

---

## 1. Introduzione

Il denaro elettronico che usiamo ogni giorno dipende quasi sempre da intermediari che elaborano i pagamenti.
Il sistema funziona, ma ha le debolezze proprie di un modello basato sulla fiducia: le transazioni possono
essere annullate, i costi di intermediazione limitano i pagamenti di piccola entità e ciascuna parte deve
fornire più informazioni del necessario.

Ciò che serve è un sistema di pagamento basato su una prova crittografica anziché sulla fiducia, che permetta
a due persone qualsiasi di operare direttamente tra loro. Le transazioni computazionalmente impossibili da
annullare proteggono chi riceve. La rete, che registra l'ordine delle transazioni e lo sigilla con il lavoro,
protegge dalla doppia spesa.

SHENT è un'implementazione di questa idea: un nodo completo con portafoglio, mining e rete P2P, che funziona
su un comune personal computer.

## 2. Conti e transazioni

SHENT usa un modello a conti. Ogni conto è identificato da un **indirizzo**, che si ottiene da una chiave
pubblica Ed25519: i primi 20 byte del suo SHA-256, codificati in Bech32m con il prefisso della rete
(`shent1…` nella rete principale, `tshent1…` in quella di test, `rshent1…` in quella locale). L'indirizzo non
rivela la chiave pubblica finché il conto non invia la sua prima transazione.

Ogni conto ha un **saldo** e un **nonce**, un contatore delle transazioni inviate. Un trasferimento contiene:

- la chiave pubblica di chi invia,
- l'indirizzo di destinazione,
- l'importo e la commissione,
- il nonce successivo del conto,
- un'altezza di scadenza facoltativa,
- un memo facoltativo fino a 80 byte,
- e una firma Ed25519 su tutto quanto sopra e sull'identificatore della rete.

Il nonce svolge la funzione che in altri progetti svolge il riferimento a output precedenti: ogni transazione
di un conto può essere usata una sola volta e in ordine. Due transazioni con lo stesso nonce sono un tentativo
di doppia spesa, e la rete ne accetta al massimo una. Poiché la firma include l'identificatore della rete, una
transazione valida nella rete di test non è utilizzabile in quella principale.

Gli importi sono memorizzati come numeri interi: 1 SHENT corrisponde a 100.000.000 unità. Per il denaro non si
usano numeri in virgola mobile.

## 3. Blocchi e catena

Ogni blocco contiene un'intestazione e un elenco di transazioni. L'intestazione include l'hash del blocco
precedente, la radice Merkle delle transazioni, la marca temporale, l'obiettivo di difficoltà e il nonce della
prova di lavoro. Poiché ogni blocco include l'hash del precedente, i blocchi formano una catena. Per modificare
un blocco bisognerebbe rifare il lavoro di quel blocco e di tutti quelli venuti dopo.

Un blocco è valido solo se sono soddisfatte queste condizioni:

- La marca temporale è maggiore della mediana degli 11 blocchi precedenti e non è più di 5 minuti nel
  futuro.
- Il blocco pesa al massimo 1.000.000 di byte.
- La prima transazione è la ricompensa del miner (coinbase) e paga esattamente quanto spetta.
- Tutte le altre transazioni hanno firme valide, nonce in ordine e saldo sufficiente.

## 4. Prova di lavoro

Per sigillare un blocco bisogna trovare un nonce tale che l'hash dell'intestazione risulti inferiore
all'obiettivo di difficoltà. SHENT usa **Argon2id** come funzione di hash per la prova di lavoro, con 2 MiB di
memoria per tentativo. È una funzione "memory-hard": ogni tentativo richiede memoria oltre che calcolo, il che
avvicina le prestazioni di una CPU personale a quelle di apparecchiature specializzate. Non le rende
impossibili: ne riduce soltanto il vantaggio.

Per trovare quel nonce non esistono scorciatoie: ogni tentativo è indipendente e la probabilità di successo è
la stessa per ciascuno. Verificarlo, invece, costa un solo hash.

La difficoltà viene ricalcolata a ogni blocco osservando gli ultimi 60, con l'obiettivo di produrre **un blocco
ogni 10 minuti in media**, che i miner siano pochi o molti. Se si aggiunge potenza, la difficoltà sale; se la si
ritira, scende.

## 5. Rete

I passi che segue la rete sono:

1. Le nuove transazioni vengono diffuse a tutti i nodi.
2. Ogni nodo raccoglie le nuove transazioni in un blocco.
3. Ogni nodo che fa mining cerca la prova di lavoro del proprio blocco.
4. Quando un nodo la trova, diffonde il blocco a tutti gli altri.
5. Gli altri nodi accettano il blocco solo se tutte le sue transazioni sono valide e non sono già state
   spese.
6. I nodi esprimono la loro accettazione lavorando al blocco successivo, usando l'hash di quello accettato come
   blocco precedente.

Un nodo non si fida di nessun altro. Ogni blocco e ogni transazione vengono verificati localmente con le regole
di consenso, anche se arrivano da un nodo conosciuto. Un messaggio della rete non può accreditare monete da
solo: soltanto un blocco valido all'interno della catena con più lavoro modifica i saldi.

Per trovarsi, i nodi non dipendono da un server centrale. Usano tre vie contemporaneamente:

- la DHT pubblica di BitTorrent, dove ogni nodo pubblica la propria porta sotto una chiave derivata dalla rete
  e dal suo blocco genesi;
- la scoperta nella rete locale;
- lo scambio di indirizzi tra peer.

Ogni indirizzo trovato passa attraverso un handshake in cui entrambe le parti firmano una sfida casuale con la
propria identità Ed25519. Perché la rete funzioni basta che almeno un nodo accetti connessioni in entrata
(porta TCP 37373 aperta nella rete principale). I nodi senza porte aperte possono ricevere connessioni tramite
un relay.

## 6. Incentivo

Per convenzione, la prima transazione di ogni blocco crea nuove monete che appartengono a chi lo ha minato. È
l'incentivo a sostenere la rete e l'unico modo in cui le monete entrano in circolazione, senza un'autorità che
le emetta.

- La ricompensa è di **50 SHENT per blocco**.
- Si dimezza ogni **1.000.000 di blocchi** (circa 19 anni con blocchi da 10 minuti).
- La somma non supera mai **100.000.000 SHENT**.
- Inoltre, chi fa mining incassa le commissioni delle transazioni che include.

La ricompensa va a chi trova il blocco e non viene ripartita. Con molti miner, ciascuno trova blocchi con una
frequenza proporzionale alla propria quota della potenza di calcolo totale. In media il risultato è lo stesso
di una ripartizione, ma sotto forma di premi interi e casuali.

L'incentivo aiuta anche i nodi a restare onesti. Chi riunisse più potenza di calcolo di tutti gli altri messi
insieme dovrebbe scegliere se usarla per riscrivere la storia o per guadagnare nuove monete seguendo le regole.
Gli conviene di più giocare secondo le regole che compromettere il sistema da cui dipende la sua stessa
ricchezza.

## 7. Catene in competizione

Può succedere che due nodi trovino un blocco quasi nello stesso momento, o che un gruppo di nodi sia rimasto
disconnesso e abbia minato per conto proprio. In quel caso esistono due versioni della catena. I nodi
continuano a lavorare sulla prima che hanno ricevuto, ma conservano l'altra nel caso in cui arrivi a superarla.
Il pareggio si rompe quando compare il blocco successivo: il ramo con **più lavoro accumulato** diventa quello
valido su tutti i nodi.

Le catene non si combinano. I blocchi del ramo perdente vengono scartati e, con essi, le loro ricompense. Le
transazioni che contenevano tornano in sospeso se sono ancora valide. Un blocco minato secondo le regole
conserva la sua ricompensa finché rimane nella catena con più lavoro. Per questo conviene attendere alcune
conferme prima di considerare definitivo un pagamento importante, e conviene fare mining con la rete connessa:
un nodo isolato può minare blocchi validi che poi perdono contro la catena del resto della rete.

Ci sono due regole di protezione:

- **Profondità massima di riorganizzazione:** un nodo non sostituisce più di 288 blocchi già accettati (circa
  due giorni).
- **Punti di controllo:** il programma pubblicato include gli hash di alcuni blocchi già sepolti nella
  storia.

Entrambe limitano il danno di un attacco lungo, in cambio di un po' di fiducia nel software che si scarica.
Sono una scelta progettuale deliberata e nota.

## 8. Privacy

Il modello bancario tradizionale ottiene la privacy limitando l'accesso alle informazioni. In SHENT, come in
qualsiasi registro pubblico, tutte le transazioni sono visibili: indirizzi, importi e momenti. La privacy si
preserva in un altro modo: gli indirizzi non hanno nome.

Misure incluse:

- **Indirizzi nuovi per ricevere.** Il portafoglio genera un nuovo indirizzo ogni volta che si richiede
  "Ricevi". Derivano tutti dallo stesso backup, ma visti dall'esterno non hanno alcuna relazione tra loro…
  finché non vengono usati insieme in un pagamento.
- **Dandelion++.** Le proprie transazioni passano prima per un percorso casuale di nodi e solo dopo vengono
  diffuse, perché non sia evidente da quale IP sono partite.
- **Tor.** Il nodo può connettersi esclusivamente tramite un proxy SOCKS5. In questa modalità non accetta
  connessioni e non usa la DHT, la rete locale né UPnP.

Sulla cifratura, per evitare malintesi:

- **Le chiavi private, sì, sono cifrate**, solo sul computer di ciascuna persona.
- **La blockchain non è cifrata**, di proposito. Ogni nodo deve poter leggere e verificare ogni transazione.
  Blocchi, trasferimenti e saldi vengono condivisi tra i nodi quasi in tempo reale, in chiaro. Non possono
  essere falsificati perché sono firmati e sigillati con il lavoro, non perché siano nascosti.
- **Nemmeno il protocollo P2P è cifrato**, proprio come quello di Bitcoin. Chi osserva la connessione può
  vedere che il computer esegue SHENT e quali dati pubblici transitano. Per nascondere l'IP c'è la modalità
  Tor.

## 9. Portafoglio e backup

Le chiavi vengono generate sul computer stesso con il generatore casuale del sistema operativo. Vengono salvate
in un file `.shent`, cifrato con una password principale (Argon2id + ChaCha20-Poly1305). La chiave privata non
lascia mai quel computer e non viene mai inviata attraverso la rete.

- Senza il file e senza la password, le monete non possono essere recuperate. Non esiste un meccanismo di
  recupero: nessuno può reimpostare la password.
- Bisogna conservare una copia del file `.shent` fuori dal computer (una chiavetta USB, un cloud) e la
  password in un luogo sicuro.
- Il file, la password e la chiave non devono essere condivisi con nessuno. Nessuno sviluppatore né alcun nodo
  li chiederà mai.

## 10. Sicurezza e limiti

Un attaccante con più potenza di calcolo del resto della rete potrebbe:

- riscrivere i blocchi recenti,
- annullare le proprie transazioni (doppia spesa),
- impedire che vengano confermate transazioni altrui.

Non potrebbe invece:

- creare monete dal nulla,
- spendere monete di un conto di cui non possiede la chiave,
- cambiare le regole.

I nodi onesti rifiutano qualsiasi blocco non valido, da qualunque parte provenga.

La sicurezza dipende da quanta potenza onesta c'è. In una rete giovane, con pochi miner, un solo computer
potente può avere la maggioranza. La pagina Statistiche dell'applicazione mostra quanti indirizzi hanno minato
negli ultimi giorni e avvisa quando uno solo concentra più della metà dei blocchi.

Questo software viene fornito così com'è, senza garanzie. Nessun sistema è invulnerabile. Le regole, il codice
e i test sono sotto gli occhi di tutti, perché chiunque possa esaminarli.

## 11. Uso pratico

### Installare (Windows 10/11, 64 bit)

1. Scaricare `SHENT-<version>-win-x64.zip` dalla pagina *Releases* del repository.
2. Verificare l'hash con `Get-FileHash .\SHENT-<version>-win-x64.zip -Algorithm SHA256` e confrontarlo
   con il file `.sha256` pubblicato.
3. Estrarre lo zip completo e aprire `SHENT.exe`. Non serve installare nient'altro.
4. Se il firewall di Windows lo chiede, consentire l'accesso perché altri nodi possano connettersi.

### Primi passi

- **Portafoglio → Crea portafoglio.** Scegliere una password principale e conservare il backup `.shent`.
- **Mining.** Scegliere thread e intensità e accendere il reattore. Se non ci sono peer connessi,
  l'applicazione lo segnala: ciò che viene minato senza rete può essere scartato.
- **Invia.** Incollare l'indirizzo di destinazione, l'importo e la commissione. Controllare la schermata di
  conferma prima di firmare.
- **Ricevi.** Condividere l'indirizzo mostrato. È pubblico e non permette di spendere nulla.
- **Lingua.** Il selettore con il globo cambia l'interfaccia tra 10 lingue. Gli importi si scrivono con il
  separatore decimale della lingua scelta. Se un importo è ambiguo, l'applicazione lo rifiuta invece di
  tirare a indovinare.

### Rendere più forte la rete

Aprire sul router la porta **TCP 37373** verso il computer, oppure attivare UPnP. Ogni nodo che accetta
connessioni rende più facile ad altri unirsi. Per un server senza schermo c'è il nodo da console:
`nodo-consola\shent-node.exe help`.

### Compilare dal codice sorgente

Richiede l'SDK di .NET 10.

```
dotnet build Shent.sln
dotnet test tests/Shent.Core.Tests
dotnet run --project src/Shent.Desktop
```

La release per Windows si genera con `scripts/publish-windows.ps1`. La documentazione tecnica della rete si
trova in [`docs/P2P_INTERNET.md`](../P2P_INTERNET.md).

## 12. Conclusione

Abbiamo proposto un sistema di transazioni elettroniche che non dipende dalla fiducia. Le firme digitali danno
il controllo delle monete a chi possiede la chiave. Per impedire la doppia spesa, la rete registra la storia
pubblica delle transazioni in una catena di prove di lavoro, che diventa impossibile da alterare per un
attaccante finché i nodi onesti detengono la maggior parte della potenza di calcolo. La rete è robusta grazie
alla sua semplicità: i nodi lavorano contemporaneamente con pochissimo coordinamento, non hanno bisogno di
identificarsi e possono andarsene e tornare quando vogliono, accettando la catena con più lavoro come prova di
quanto accaduto. Votano con la loro potenza di calcolo: accettano i blocchi validi lavorando su di essi e
rifiutano quelli non validi rifiutandosi di farlo. Tutte le regole e gli incentivi necessari vengono imposti
con quest'unico meccanismo di consenso.

SHENT esiste perché una comunità lo usi, lo studi e lo migliori.
