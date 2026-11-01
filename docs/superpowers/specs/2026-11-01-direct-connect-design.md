# CS2 Koop per Direct Connect – Design

Stand: 2026-11-01 · Projekt: privat (`~/Documents/IT/cs2-coop-direct`)

## Intent

Felix und ein Freund wollen Cities: Skylines II gemeinsam in **einer** Stadt spielen. Beide bauen gleichzeitig,
mal an derselben Stelle, mal an verschiedenen. Die Verbindung läuft direkt über **Tailscale** (IP:Port), ohne
Steam-Netzwerk, Steam-Lobby oder Relay. Felix' PC ist der Host.

Bisher benutzt: „Multiplayer Co-op“ (Paradox Mods 159499, v0.3.10). Probleme laut Felix und Logs vom 2026-10-31:
- Gebautes kommt beim anderen nicht an. Nötig ist dann ein manueller „Synchronisieren“-Knopf mit etwa 10 s Laden.
- Geld, Rewards und Terrain/Wasser laufen auseinander.
- Aktionen scheitern beim anderen, weil dessen Welt schon anders ist. Im Server-Log stehen 230× „Zone Tool … made
  nothing of it“, mehrfach „no NetEdge … near“, `NotEnoughMoney`, `SteepSlope`, `OverlapExisting`. Dazu kommen
  320× „Handshake timeout“.
- Ursache ist der Architekturansatz, nicht ein einzelner Bug: Jedes Spiel simuliert selbst und spielt die
  Werkzeug-Aktionen des anderen nach. CS2 simuliert nicht deterministisch (DOTS/Burst, multithreaded), deshalb
  divergiert das zwangsläufig.

## Erfolg

- Was einer baut (Straße, Zone, Gebäude, Abriss, Terrain), steht beim anderen innerhalb von etwa 1 s da. Es gibt
  keinen Sync-Knopf und keine Ladepause im Normalbetrieb.
- Geld, Meilensteine/Rewards, Policies, Zeit und Bevölkerungszahlen sind bei beiden **identisch** (Host-Wert).
- Eine Aktion des Clients gelingt oder scheitert nach den Regeln des Host-Spiels. „Bei mir geht's, bei dir nicht“
  gibt es nicht mehr.
- Eine Session über 2–3 Stunden läuft stabil, inklusive Join, Speichern und Reconnect nach einem kurzen
  Verbindungsabbruch.

## Ziele / Nicht-Ziele / Anti-Ziele

**Ziele**
- Host-autoritativer Koop für 2 Spieler (mehr ist erlaubt, aber nicht der Fokus).
- Verbindung per IP:Port über Tailscale, ohne jede Abhängigkeit von Steam (keine Steamworks-Initialisierung,
  keine Steam-ID, kein Overlay).
- Spielstand bleibt ein normales CS2-Save beim Host.

**Nicht-Ziele**
- Autos und Cims müssen nicht exakt übereinstimmen (Felix: „nicht das Schlimmste“). Häuser bzw. Gebäude und alles
  Gebaute müssen dagegen stimmen.
- Kein Internet-Matchmaking, keine Serverliste, kein NAT-Traversal (Tailscale erledigt das).
- Kein dedizierter Server ohne Spiel. Der Host ist ein laufendes CS2.
- Keine Kompatibilität zu weiteren Code-Mods in v1.

**Anti-Ziele** (das Projekt ist gescheitert, auch wenn es „technisch läuft“)
- Lockstep oder Befehls-Replay, bei dem der Client die Aktion selbst nachrechnet. Das ist exakt der Fehler des
  alten Mods.
- Ein regelmäßiger Voll-Resync als Normalfall.
- Ein eigener Mod von null. Begründung siehe unten.

## Grundsatzentscheidung: Fork von SkylineTogether

[SkylineTogether](https://github.com/ljkx/SkylineTogether) (MIT, v0.3.0 vom 2026-10-24, ca. 617 C#-Dateien,
205 Testdateien) hat bereits die Zielarchitektur:
- Nur der Host simuliert.
- Der Client schickt `Proposal`s, der Host validiert (`Host validation and commit`) und verteilt das Ergebnis als
  `Epoch`-Batches mit stabilen Net-IDs.
- Dazu kommen Sim-Werte, Zellkarten, Terrain/Wasser-Tiles, Statistiken, ein Follower-Clock mit „gated systems“
  (der Client rechnet die synchronisierten Systeme nicht selbst) und Integritäts-Digests mit gezielter Reparatur
  statt Voll-Resync.

Laut Felix' Log scheiterte der Mod am 2026-10-31 nur am Transport: Steam Networking Sockets
(`P-STEAMSOCKETS: CreatePollGroup failed`). Auch sein „Direct IP“ (`TransportKind.SteamIp`) läuft über Steams
Bibliothek und braucht ein laufendes, eingeloggtes Steam.

Der Transport ist sauber gekapselt: `ITransport` / `IConnection` / `ITransportEventSink` in
`SkylineTogether.Core/Contracts/Transport`. Ein Steam-freier `TransportKind.Udp` ist im Enum schon vorgesehen
(„LAN UDP without Steam (milestone M9)“), aber nicht implementiert. `PeerIdentity` erlaubt `SteamId = 0` mit
Adresse, und Direct-/unauthentifizierte Verbindungen verlangen schon heute zwingend das Session-Passwort
(PBKDF2 + HMAC-Challenge).

**Der Kern dieses Projekts ist also: SkylineTogether forken, einen Steam-freien Transport implementieren und alle
übrigen Steam-Abhängigkeiten abschaltbar machen.** Der Sync selbst wird zunächst nicht angefasst.

## Was gebaut wird

1. **Neuer Transport** (`TransportKind.Udp` bzw. der Name nach Wahl des Builders) hinter `ITransport`, im Game-Layer
   oder in Core, plain .NET-Sockets, ohne NuGet (Repo-Regel ADR 0001).
   - Er muss die drei Lanes abbilden: **R** (reliable, ordered, wenig Latenz), **U** (unreliable), **B** (reliable
     bulk, darf R nicht blockieren).
   - *Meine Empfehlung (Claude):* je eine TCP-Verbindung für R und B (Lane-Isolation ohne eigene
     Reliability-Schicht) und UDP-Datagramme für U. Tailscale liefert die Verschlüsselung (WireGuard), deshalb ist
     keine Transportverschlüsselung nötig. Eine eigene Reliable-UDP-Schicht ist erlaubt, wenn der Builder einen
     guten Grund hat.
   - Er muss die Semantik einhalten, die `PeerOutbox`, `SendRateController`, `BlobSender`, der Fragmenter (Limit
     480 KiB pro Nachricht) und `ConnectionStats` erwarten. Ein Netzsimulator-Test in Core existiert und ist zu
     nutzen.
   - Standardport 27015, einstellbar. Ist der Port belegt (z. B. durch den alten Mod), meldet die UI das klar,
     statt still zu scheitern.
2. **Steam-Abhängigkeiten abschaltbar machen.** Ohne funktionierendes Steam muss der Mod vollständig hosten und
   joinen können:
   - Self-Test-Gating: Ein Steam-Fehler darf Hosting und Joining nicht sperren, wenn der Direct-Transport aktiv ist.
   - Identität: keine Steam-ID. Duplikat-Erkennung, Bans und Rejoin über Adresse plus eine lokal gespeicherte
     Spieler-ID, die im `Hello` geschickt wird. Diese ID ist unauthentifiziert, aber durch das Pflicht-Passwort
     abgesichert.
   - Anzeigename aus den Mod-Einstellungen statt aus der Steam-Persona.
   - UI: Join-Feld für `IP:Port` plus Passwort als primärer Weg. Freundesliste, Einladen und Steam-ID kopieren
     werden ausgeblendet, wenn Steam fehlt.
   - Steamworks wird nicht initialisiert bzw. nicht aufgerufen, wenn der Direct-Modus aktiv ist.
3. **Unterscheidbarkeit vom Original.** Eigener Mod-Name, eigene Build-ID und eigene Protokollversion, damit der Fork
   nie mit dem Original-SkylineTogether verwechselt wird oder sich mit ihm verbindet.

## Phasen

- **Phase 1 – Durchstich:** Transport plus Steam-freies Hosten und Joinen. Felix und sein Freund joinen über
  Tailscale und bauen 30 Minuten. Danach Bewertung anhand der Erfolgskriterien und der SkylineTogether-Logs und
  Diagnose-Dateien beider PCs.
- **Phase 2 – Sync-Fehler beheben:** nur das, was Phase 1 in echten Sessions zeigt (Divergenzen aus
  `DivergenceTracker`/Diagnose). Jeder Fix bekommt einen Core-Test, wo möglich.
- Phase 2 wird nicht im Voraus geplant. Ihr Umfang hängt davon ab, wie gut der Upstream-Sync in der Praxis ist. Das
  ist das Hauptrisiko des Projekts (0 Stars, ein Entwickler, v0.3).

## Rahmenbedingungen

- **Spielversion:** 1.6.2f1 (767.21d1) bei beiden Spielern, identisch. Spiel-Updates können Reflection-Zugriffe
  brechen. Die vorhandenen Self-Tests (`P-*`) und das ApiCheck-Tool fangen das ab.
- **Installation bei Felix:** `D:\games\Cities Skylines II`. User-Daten unter
  `C:\Users\felix\AppData\LocalLow\Colossal Order\Cities Skylines II`. Mods kommen nach `…\Mods\<ModName>`.
- **Entwicklung:** Felix arbeitet meist unter Fedora. Das Spiel läuft nur unter Windows (Dual-Boot, nie per GRUB
  booten). Unter Linux ist D: als `/dev/nvme0n1p2` (NTFS) **nur lesend** einzuhängen. Die Game-DLLs zum Kompilieren
  liegen dort unter `Cities2_Data/Managed`. C: ist unter `/mnt/windows` read-only. Unter Linux wird **nichts** auf
  die NTFS-Partitionen geschrieben (Windows-Schnellstart bzw. Ruhezustand birgt Korruptionsgefahr). Deploy und
  Spieltests laufen unter Windows.
- Core-Build und Core-Tests sollen unter Linux laufen (.NET 8, laut Upstream möglich). Ob der Game-Layer (net472)
  unter Linux gegen die Game-DLLs baut, klärt der Builder. Sonst baut Windows über `tools/build.sh` in Git Bash.
- **Git:** privates Projekt, GitHub-Fork oder lokales Repo, Arbeit direkt auf `main` erlaubt. Die Änderungen werden
  möglichst in neuen Dateien isoliert, damit Upstream-Releases rebasebar bleiben.
- **Alter Mod:** „Multiplayer Co-op“ (159499) bei beiden deaktivieren bzw. aus dem Playset entfernen.
- **Nicht im Scope:** Steam-Emulator oder Spielinstallation. Der Mod fasst nichts davon an und hängt nicht davon ab.

## Geklärte Punkte aus dem Builder-Check

- **Steam-Situation (Recon 2026-11-01):** Felix' CS2 ist keine Steam-Installation. Das Spielverzeichnis enthält
  einen Steam-Emulator (`steam_emu.ini`, `steam_api64.rne` in `Cities2_Data/Plugins/x86_64`). Das bedeutet:
  `steam_api64` und `Steamworks.NET` laden, können aber Scheinerfolge oder Teilfehler liefern (siehe
  `CreatePollGroup failed`). Daraus folgt:
  - Der Direct-Modus ist eine **explizite Einstellung**, nicht auto-erkannt.
  - Im Direct-Modus ruft der Mod keinen Steam-Code auf, auch keine Self-Tests, Persona, Friends oder Invites.
  - Steam-Code liegt in eigenen Typen, die im Direct-Modus nie berührt werden. So schadet es auch nicht, wenn sie
    nicht laden.
  - DLC- und Versions-Checks in `TryAdmit` dürfen sich im Direct-Modus nicht auf Steam-Abfragen stützen. Es werden
    die Daten genutzt, die das Spiel selbst meldet (`PlatformManager`/Manifest), sonst entfällt der Check mit einer
    Warnung.
- **Reconnect (Claudes Entscheidung):** Ein Reconnect nach einem Abbruch darf neu laden (Upstream-
  `RejoinController`, frischer Snapshot). „Keine Ladepause“ gilt für den Normalbetrieb ohne Abbruch. Ein Resume
  ohne Laden ist nicht im Scope.
- **Mods/Assets (Claudes Entscheidung):** Phase 1 läuft vanilla, ohne weitere Code-Mods und ohne Paradox-Mods-Assets.
  Der Server-Log des alten Mods zeigt für die bisherige Stadt „Erin Springs“ 0 Referenz-Mods. Die Pfade für
  Asset-Transfer ohne Steam werden erst geprüft, wenn das gebraucht wird.
- **Testen (Claudes Entscheidung):** Es gibt keinen zweiten PC mit CS2 außer dem des Freundes. Deshalb muss so viel
  wie möglich unter Linux in Core getestet werden:
  - echte Socket-Integrationstests (Loopback über 127.0.0.1),
  - NetSim über den echten Transport (Verlust, Latenz, Abbruch),
  - ein In-Game-Self-Test, der `SteamSocketPair` im Direct-Modus durch ein lokales Socket-Paar ersetzt.

  Windows-Durchläufe sind teuer. Für sie gibt es ein Deploy-Skript (PowerShell, BOM, ASCII-only) und eine Checkliste,
  welche Logs beide Spieler danach einsammeln.

- **Spielpaket (Felix):** Der Freund hat dasselbe Spielpaket: Version 1.6.2f1, dieselben DLCs und Creator Packs.
  Die Upstream-Admission-Checks für Version und DLC bleiben aktiv. Wo sie im Direct-Modus keine Daten bekommen,
  entfallen sie mit einer Warnung statt den Join zu blocken.
- **Arbeitsteilung Linux/Windows (Felix):** Gebaut wird unter Linux (bessere Infrastruktur). Getestet und
  nachgefixt wird unter Windows mit einer Claude-Code-Session dort. Der Plan muss deshalb liefern:
  - einen unter Linux gebauten, deploybaren Mod-Ordner. Ist der Game-Layer-Build unter Linux nicht möglich, wird
    der Windows-Build per `tools/build.sh` (Git Bash) zum ersten Schritt der Windows-Session.
  - eine `docs/WINDOWS-TEST.md` als Einstieg für die Windows-Session: Repo-Stand holen (Git-Remote, z. B. privates
    GitHub-Repo), bauen, deployen, alten Mod deaktivieren, Firewall-Regel für den Port im Tailscale-Netz, Ablauf
    des Testabends, welche Logs ausgewertet werden.
  - Windows-Konventionen: PowerShell-Skripte mit BOM und ASCII-only, `node` statt `jq`.

## Dem Builder überlassen

- Transport-Details (TCP/UDP-Aufteilung, Framing, Keepalive, Timeouts, Reconnect) innerhalb der oben genannten
  Lane-Semantik.
- Ob der Transport in Core (testbar unter Linux) oder im Game-Layer liegt.
- Name des Forks und Form der lokalen Spieler-ID.
- Wie Steam-Code isoliert wird (Feature-Flag, Lazy-Init, eigene Klasse).
