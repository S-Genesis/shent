# SHENT: ein gemeinschaftliches Peer-to-Peer-System für elektronisches Bargeld

**Sprachen:** [Español](GUIDE.es.md) · [English](GUIDE.en.md) · [Português](GUIDE.pt.md) · [Français](GUIDE.fr.md) · [Deutsch](GUIDE.de.md) · [Italiano](GUIDE.it.md) · [Русский](GUIDE.ru.md) · [中文](GUIDE.zh.md) · [日本語](GUIDE.ja.md) · [Türkçe](GUIDE.tr.md)

---

## Zusammenfassung

Eine reine Peer-to-Peer-Version von elektronischem Bargeld würde es ermöglichen, Zahlungen von einer Person
an eine andere zu senden, ohne über eine Institution zu gehen. Digitale Signaturen lösen einen Teil des
Problems, aber die wichtigsten Vorteile gehen verloren, wenn weiterhin ein vertrauenswürdiger Dritter nötig
ist, um zu verhindern, dass dieselbe Coin zweimal ausgegeben wird. SHENT löst das Problem der Doppelausgabe
mit einem Peer-to-Peer-Netzwerk. Das Netzwerk zeichnet die Transaktionen in einer Blockchain auf, und jeder
Block ist durch einen Arbeitsnachweis versiegelt. Der so entstehende Datensatz kann nicht geändert werden,
ohne diese Arbeit erneut zu leisten. Die längste Kette, gemessen an der akkumulierten Arbeit und nicht an der
Anzahl der Blöcke, dient als Beweis für die Abfolge der Ereignisse und als Beweis dafür, dass sie aus der
größten Rechenleistung hervorgegangen ist. Solange der größte Teil dieser Leistung in den Händen von Knoten
liegt, die nicht zusammenarbeiten, um das Netzwerk anzugreifen, erzeugen diese Knoten die längste Kette und
hängen jeden Angreifer ab. Das Netzwerk selbst benötigt nur eine minimale Struktur. Nachrichten werden nach
bestem Bemühen verbreitet, und Knoten können das Netzwerk nach Belieben verlassen und wieder beitreten, wobei
sie die Kette mit der meisten Arbeit als Beweis dafür akzeptieren, was während ihrer Abwesenheit geschehen ist.

SHENT ist eine Gemeinschaftswährung. Der Genesis-Block sagt es in seinem eigenen Text:
`SHENT 13/10/2026 - moneda comunitaria sin valor monetario` („SHENT 13.10.2026 – Gemeinschaftswährung ohne
Geldwert“). Sie ist an keinem Markt notiert, die Anwendung kauft und verkauft nichts, und niemand verspricht,
dass sie jemals etwas wert sein wird.

---

## 1. Einleitung

Das elektronische Geld, das wir täglich verwenden, hängt fast immer von Vermittlern ab, die die Zahlungen
abwickeln. Das System funktioniert, hat aber die Schwächen, die einem vertrauensbasierten Modell eigen sind:
Transaktionen können rückgängig gemacht werden, die Vermittlungskosten begrenzen kleine Zahlungen, und jede
Partei muss mehr Informationen preisgeben als nötig.

Was benötigt wird, ist ein Zahlungssystem, das auf kryptografischem Beweis statt auf Vertrauen beruht und es
zwei beliebigen Personen erlaubt, direkt miteinander zu handeln. Transaktionen, deren Umkehrung rechnerisch
unmöglich ist, schützen den Empfänger. Das Netzwerk, das die Reihenfolge der Transaktionen aufzeichnet und sie
mit Arbeit versiegelt, schützt vor Doppelausgaben.

SHENT ist eine Umsetzung dieser Idee: ein vollständiger Knoten mit Wallet, Mining und P2P-Netzwerk, der auf
einem gewöhnlichen Personal Computer läuft.

## 2. Konten und Transaktionen

SHENT verwendet ein Kontenmodell. Jedes Konto wird durch eine **Adresse** identifiziert, die aus einem
öffentlichen Ed25519-Schlüssel gewonnen wird: die ersten 20 Bytes seines SHA-256, kodiert in Bech32m mit dem
Präfix des Netzwerks (`shent1…` im Hauptnetz, `tshent1…` im Testnetz, `rshent1…` im lokalen Netz). Die
Adresse gibt den öffentlichen Schlüssel erst preis, wenn das Konto seine erste Transaktion sendet.

Jedes Konto hat einen **Kontostand** und eine **Nonce**, einen Zähler der gesendeten Transaktionen. Eine
Überweisung enthält:

- den öffentlichen Schlüssel des Absenders,
- die Zieladresse,
- den Betrag und die Gebühr,
- die nächste Nonce des Kontos,
- eine optionale Ablaufhöhe,
- ein optionales Memo von bis zu 80 Bytes,
- und eine Ed25519-Signatur über alles Vorstehende und über die Kennung des Netzwerks.

Die Nonce erfüllt die Aufgabe, die in anderen Entwürfen der Verweis auf frühere Ausgaben übernimmt: Jede
Transaktion eines Kontos kann nur einmal und nur der Reihe nach verwendet werden. Zwei Transaktionen mit
derselben Nonce sind ein Versuch der Doppelausgabe, und das Netzwerk akzeptiert höchstens eine davon. Da die
Signatur die Kennung des Netzwerks einschließt, ist eine im Testnetz gültige Transaktion im Hauptnetz nicht
verwendbar.

Beträge werden als Ganzzahlen gespeichert: 1 SHENT entspricht 100.000.000 Einheiten. Für Geld werden keine
Gleitkommazahlen verwendet.

## 3. Blöcke und Kette

Jeder Block enthält einen Header und eine Liste von Transaktionen. Der Header umfasst den Hash des vorherigen
Blocks, die Merkle-Wurzel der Transaktionen, den Zeitstempel, das Schwierigkeitsziel und die Nonce des
Arbeitsnachweises. Da jeder Block den Hash des vorherigen enthält, bilden die Blöcke eine Kette. Um einen
Block zu ändern, müsste die Arbeit dieses Blocks und aller nachfolgenden Blöcke erneut geleistet werden.

Ein Block ist nur gültig, wenn folgende Bedingungen erfüllt sind:

- Der Zeitstempel ist größer als der Median der 11 vorherigen Blöcke und liegt nicht mehr als 5 Minuten in
  der Zukunft.
- Der Block ist höchstens 1.000.000 Bytes groß.
- Die erste Transaktion ist die Belohnung des Miners (Coinbase) und zahlt genau den zustehenden Betrag aus.
- Alle übrigen Transaktionen haben gültige Signaturen, Nonces in der richtigen Reihenfolge und ausreichendes
  Guthaben.

## 4. Arbeitsnachweis

Um einen Block zu versiegeln, muss eine Nonce gefunden werden, bei der der Hash des Headers unter dem
Schwierigkeitsziel liegt. SHENT verwendet **Argon2id** als Hashfunktion für den Arbeitsnachweis, mit 2 MiB
Speicher pro Versuch. Es ist eine „memory-hard“-Funktion: Jeder Versuch benötigt neben Rechenleistung auch
Speicher, was die Leistung einer gewöhnlichen CPU näher an die spezialisierter Hardware heranbringt.
Spezialisierte Hardware wird dadurch nicht unmöglich: Ihr Vorteil wird nur verringert.

Für das Finden dieser Nonce gibt es keine Abkürzung: Jeder Versuch ist unabhängig, und die
Erfolgswahrscheinlichkeit ist bei jedem gleich. Die Überprüfung dagegen kostet nur einen einzigen Hash.

Die Schwierigkeit wird bei jedem Block anhand der letzten 60 Blöcke neu berechnet, mit dem Ziel, dass **im
Durchschnitt alle 10 Minuten ein Block** entsteht, ob es nun wenige oder viele Miner gibt. Kommt
Rechenleistung hinzu, steigt die Schwierigkeit; wird sie abgezogen, sinkt sie.

## 5. Netzwerk

Das Netzwerk durchläuft folgende Schritte:

1. Neue Transaktionen werden an alle Knoten verbreitet.
2. Jeder Knoten sammelt neue Transaktionen in einem Block.
3. Jeder minende Knoten sucht den Arbeitsnachweis für seinen Block.
4. Findet ein Knoten ihn, verbreitet er den Block an alle anderen.
5. Die anderen Knoten akzeptieren den Block nur, wenn alle seine Transaktionen gültig sind und nicht bereits
   ausgegeben wurden.
6. Die Knoten bringen ihre Zustimmung zum Ausdruck, indem sie am nächsten Block arbeiten und dabei den Hash des
   akzeptierten Blocks als vorherigen Block verwenden.

Ein Knoten vertraut keinem anderen. Jeder Block und jede Transaktion wird lokal anhand der Konsensregeln
überprüft, auch wenn sie von einem bekannten Knoten stammen. Eine Netzwerknachricht kann für sich allein
keine Coins gutschreiben: Nur ein gültiger Block in der Kette mit der meisten Arbeit verändert die
Kontostände.

Um einander zu finden, sind die Knoten nicht auf einen zentralen Server angewiesen. Sie nutzen gleichzeitig
drei Wege:

- die öffentliche DHT von BitTorrent, in der jeder Knoten seinen Port unter einem Schlüssel veröffentlicht,
  der aus dem Netzwerk und seinem Genesis-Block abgeleitet ist;
- die Erkennung im lokalen Netzwerk;
- den Austausch von Adressen zwischen Peers.

Jede gefundene Adresse durchläuft einen Handshake, bei dem beide Seiten eine zufällige Challenge mit ihrer
Ed25519-Identität signieren. Damit das Netzwerk funktioniert, genügt es, dass mindestens ein Knoten
eingehende Verbindungen annimmt (TCP-Port 37373 im Hauptnetz geöffnet). Knoten ohne offene Ports können
Verbindungen über ein Relay empfangen.

## 6. Anreiz

Per Konvention erzeugt die erste Transaktion jedes Blocks neue Coins, die demjenigen gehören, der den Block
gemint hat. Das ist der Anreiz, das Netzwerk zu tragen, und die einzige Art, wie Coins in Umlauf kommen,
ohne eine Autorität, die sie ausgibt.

- Die Belohnung beträgt **50 SHENT pro Block**.
- Sie halbiert sich alle **1.000.000 Blöcke** (etwa 19 Jahre bei Blöcken im 10-Minuten-Takt).
- Die Summe übersteigt nie **100.000.000 SHENT**.
- Zusätzlich erhält, wer mint, die Gebühren der Transaktionen, die er aufnimmt.

Die Belohnung erhält, wer den Block findet; sie wird nicht aufgeteilt. Bei vielen Minern findet jeder Blöcke
mit einer Häufigkeit, die seinem Anteil an der gesamten Rechenleistung entspricht. Im Durchschnitt ist das
Ergebnis dasselbe wie bei einer Aufteilung, aber in Form ganzer, zufälliger Gewinne.

Der Anreiz trägt auch dazu bei, dass die Knoten ehrlich bleiben. Wer mehr Rechenleistung aufbrächte als alle
anderen zusammen, müsste wählen, ob er sie nutzt, um die Geschichte umzuschreiben, oder um nach den Regeln
neue Coins zu verdienen. Es lohnt sich für ihn mehr, sich an die Regeln zu halten, als das System zu
untergraben, von dem sein eigener Wohlstand abhängt.

## 7. Konkurrierende Ketten

Es kann vorkommen, dass zwei Knoten fast gleichzeitig einen Block finden oder dass eine Gruppe von Knoten
vom Netz getrennt war und für sich allein gemint hat. Dann existieren zwei Versionen der Kette. Die Knoten
arbeiten an der zuerst empfangenen weiter, bewahren die andere aber auf, falls diese sie überholt. Der
Gleichstand wird aufgelöst, wenn der nächste Block erscheint: Der Zweig mit **der meisten akkumulierten
Arbeit** wird auf allen Knoten zum gültigen.

Ketten werden nicht zusammengeführt. Die Blöcke des unterlegenen Zweigs werden verworfen und mit ihnen ihre
Belohnungen. Die darin enthaltenen Transaktionen werden wieder ausstehend, sofern sie weiterhin gültig sind.
Ein nach den Regeln geminter Block behält seine Belohnung, solange er in der Kette mit der meisten Arbeit
bleibt. Deshalb empfiehlt es sich, einige Bestätigungen abzuwarten, bevor eine große Zahlung als endgültig
gilt, und mit verbundenem Netzwerk zu minen: Ein isolierter Knoten kann gültige Blöcke minen, die später
gegen die Kette der übrigen Knoten verlieren.

Es gibt zwei Schutzregeln:

- **Maximale Reorganisationstiefe:** Ein Knoten ersetzt nicht mehr als 288 bereits akzeptierte Blöcke (etwa
  zwei Tage).
- **Checkpoints:** Das veröffentlichte Programm enthält die Hashes einiger Blöcke, die bereits tief in der
  Geschichte begraben sind.

Beide begrenzen den Schaden eines langen Angriffs, um den Preis eines gewissen Vertrauens in die
heruntergeladene Software. Sie sind eine bewusste und bekannte Designentscheidung.

## 8. Privatsphäre

Das traditionelle Bankmodell erreicht Privatsphäre, indem es den Zugang zu Informationen beschränkt. In
SHENT sind, wie in jedem öffentlichen Register, alle Transaktionen sichtbar: Adressen, Beträge und
Zeitpunkte. Die Privatsphäre wird auf andere Weise gewahrt: Die Adressen tragen keine Namen.

Enthaltene Maßnahmen:

- **Neue Adressen zum Empfangen.** Die Wallet erzeugt jedes Mal eine neue Adresse, wenn „Empfangen“ angefordert
  wird. Alle stammen aus demselben Backup, stehen aber von außen betrachtet in keiner Beziehung zueinander …
  bis sie zusammen in einer Zahlung verwendet werden.
- **Dandelion++.** Eigene Transaktionen laufen zuerst über einen zufälligen Pfad von Knoten und werden erst
  danach verbreitet, damit nicht offensichtlich ist, von welcher IP sie stammen.
- **Tor.** Der Knoten kann sich ausschließlich über einen SOCKS5-Proxy verbinden. In diesem Modus nimmt er
  keine Verbindungen an und nutzt weder die DHT noch das lokale Netzwerk noch UPnP.

Zur Verschlüsselung, damit keine Missverständnisse entstehen:

- **Die privaten Schlüssel sind sehr wohl verschlüsselt**, nur auf dem Computer der jeweiligen Person.
- **Die Blockchain ist nicht verschlüsselt**, und zwar absichtlich. Jeder Knoten muss jede Transaktion lesen
  und prüfen können. Blöcke, Überweisungen und Kontostände werden nahezu in Echtzeit im Klartext zwischen den
  Knoten geteilt. Sie können nicht gefälscht werden, weil sie signiert und mit Arbeit versiegelt sind, nicht
  weil sie verborgen wären.
- **Auch das P2P-Protokoll ist nicht verschlüsselt**, ebenso wenig wie das von Bitcoin. Wer die Verbindung
  beobachtet, kann sehen, dass auf dem Computer SHENT läuft und welche öffentlichen Daten übertragen werden.
  Zum Verbergen der IP gibt es den Tor-Modus.

## 9. Wallet und Backup

Die Schlüssel werden auf dem eigenen Computer mit dem Zufallsgenerator des Betriebssystems erzeugt. Sie
werden in einer `.shent`-Datei gespeichert, die mit einem Master-Passwort verschlüsselt ist (Argon2id +
ChaCha20-Poly1305). Der private Schlüssel verlässt diesen Computer nie und wird nie über das Netzwerk
gesendet.

- Ohne die Datei und ohne das Passwort können die Coins nicht wiederhergestellt werden. Es gibt keinen
  Wiederherstellungsmechanismus: Niemand kann das Passwort zurücksetzen.
- Eine Kopie der `.shent`-Datei muss außerhalb des Computers aufbewahrt werden (auf einem USB-Stick, in einer
  Cloud) und das Passwort an einem sicheren Ort.
- Die Datei, das Passwort und der Schlüssel dürfen mit niemandem geteilt werden. Kein Entwickler und kein
  Knoten wird jemals danach fragen.

## 10. Sicherheit und Grenzen

Ein Angreifer mit mehr Rechenleistung als der Rest des Netzwerks könnte:

- die jüngsten Blöcke umschreiben,
- seine eigenen Transaktionen rückgängig machen (Doppelausgabe),
- verhindern, dass fremde Transaktionen bestätigt werden.

Er könnte jedoch nicht:

- Coins aus dem Nichts erschaffen,
- Coins von einem Konto ausgeben, dessen Schlüssel er nicht besitzt,
- die Regeln ändern.

Ehrliche Knoten lehnen jeden ungültigen Block ab, gleich woher er kommt.

Die Sicherheit hängt davon ab, wie viel ehrliche Rechenleistung vorhanden ist. In einem jungen Netzwerk mit
wenigen Minern kann ein einziger leistungsstarker Computer die Mehrheit haben. Die Seite „Statistiken“ der
Anwendung zeigt, wie viele Adressen in den letzten Tagen gemint haben, und warnt, wenn eine einzelne mehr als
die Hälfte der Blöcke auf sich vereint.

Diese Software wird so bereitgestellt, wie sie ist, ohne Garantien. Kein System ist unverwundbar. Die
Regeln, der Code und die Tests liegen offen, damit jeder sie prüfen kann.

## 11. Praktische Nutzung

### Installieren (Windows 10/11, 64 Bit)

1. `SHENT-<version>-win-x64.zip` von der *Releases*-Seite des Repositorys herunterladen.
2. Den Hash mit `Get-FileHash .\SHENT-<version>-win-x64.zip -Algorithm SHA256` prüfen und mit der
   veröffentlichten `.sha256`-Datei vergleichen.
3. Das ZIP vollständig entpacken und `SHENT.exe` öffnen. Es muss nichts weiter installiert werden.
4. Falls die Windows-Firewall nachfragt, den Zugriff erlauben, damit sich andere Knoten verbinden können.

### Erste Schritte

- **Wallet → Wallet erstellen.** Ein Master-Passwort wählen und das `.shent`-Backup sichern.
- **Mining.** Threads und Intensität wählen und den Reaktor starten. Wenn keine Peers verbunden sind, weist
  die Anwendung darauf hin: Was ohne Netzwerk gemint wird, kann verworfen werden.
- **Senden.** Die Zieladresse, den Betrag und die Gebühr einfügen. Vor dem Signieren den
  Bestätigungsbildschirm prüfen.
- **Empfangen.** Die angezeigte Adresse weitergeben. Sie ist öffentlich und erlaubt es nicht, etwas
  auszugeben.
- **Sprache.** Die Auswahl mit dem Globus schaltet die Oberfläche zwischen 10 Sprachen um. Beträge werden mit
  dem Dezimaltrennzeichen der gewählten Sprache geschrieben. Ist ein Betrag mehrdeutig, lehnt die Anwendung
  ihn ab, statt zu raten.

### Das Netzwerk stärken

Im Router den Port **TCP 37373** zum Computer hin öffnen oder UPnP aktivieren. Jeder Knoten, der
Verbindungen annimmt, erleichtert es anderen, sich anzuschließen. Für einen Server ohne Bildschirm gibt es den
Konsolenknoten: `nodo-consola\shent-node.exe help`.

### Aus dem Quellcode kompilieren

Erfordert das .NET 10 SDK.

```
dotnet build Shent.sln
dotnet test tests/Shent.Core.Tests
dotnet run --project src/Shent.Desktop
```

Das Release für Windows wird mit `scripts/publish-windows.ps1` erzeugt. Die technische Dokumentation des
Netzwerks befindet sich in [`docs/P2P_INTERNET.md`](../P2P_INTERNET.md).

## 12. Fazit

Wir haben ein System für elektronische Transaktionen vorgeschlagen, das nicht auf Vertrauen beruht. Digitale
Signaturen geben die Kontrolle über die Coins demjenigen, der den Schlüssel besitzt. Um Doppelausgaben zu
verhindern, zeichnet das Netzwerk die öffentliche Geschichte der Transaktionen in einer Kette aus
Arbeitsnachweisen auf, die für einen Angreifer unmöglich zu verändern wird, solange die ehrlichen Knoten den
größten Teil der Rechenleistung besitzen. Das Netzwerk ist robust durch seine Einfachheit: Die Knoten
arbeiten gleichzeitig mit sehr wenig Koordination, müssen sich nicht identifizieren und können nach Belieben
gehen und zurückkehren, wobei sie die Kette mit der meisten Arbeit als Beweis für das Geschehene akzeptieren.
Sie stimmen mit ihrer Rechenleistung ab: Sie akzeptieren gültige Blöcke, indem sie auf ihnen weiterarbeiten,
und lehnen ungültige ab, indem sie sich weigern, dies zu tun. Alle notwendigen Regeln und Anreize werden mit
diesem einzigen Konsensmechanismus durchgesetzt.

SHENT existiert, damit eine Gemeinschaft es nutzt, untersucht und verbessert.
