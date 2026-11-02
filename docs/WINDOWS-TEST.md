# Windows-Test: Skyline Together Direct

Einstieg für die Claude-Code-Session unter Windows und für Felix am Testabend. Entwickelt wurde unter Linux, dort läuft
alles außer dem Spiel selbst. Hier wird zum ersten Mal gegen ein echtes CS2 getestet, und Bugs werden hier gefixt.

**Stand:** Fork von SkylineTogether, Direkt-Modus ohne Steam: TCP, Standardport 27015, Passwort Pflicht,
Einstellung `DirectOnly` (Standard an), Mod-Ordner `SkylineTogetherDirect`, Anzeigename „Skyline Together Direct“,
Protokoll 1009, Version 0.3.0, Spiel 1.6.2f1. Spec: `docs/superpowers/specs/2026-11-01-direct-connect-design.md`,
Plan: `docs/superpowers/plans/2026-11-01-direct-connect.md`, Architektur: `docs/ARCHITECTURE.md`.

**Pfade:** Spiel `D:\games\Cities Skylines II`, Nutzerdaten
`C:\Users\felix\AppData\LocalLow\Colossal Order\Cities Skylines II` (darin `Mods\`, `Logs\`). Beide Spieler haben
dasselbe Spielpaket und dieselben DLCs.

## 1. Repo holen

```powershell
git clone https://github.com/Felix-Hempel/cs2-coop-direct.git C:\Users\felix\Documents\IT\cs2-coop-direct
# oder, falls schon vorhanden:
git -C C:\Users\felix\Documents\IT\cs2-coop-direct pull origin main
```

## 2. Voraussetzungen

- .NET SDK 8.0.425 oder neuer 8.0.x (`global.json`): `winget install Microsoft.DotNet.SDK.8`
- Git Bash (`winget install Git.Git`). `deploy-windows.ps1` nimmt gezielt dessen `bash.exe`, nicht das WSL-`bash`.
- node (optional, für die UI-Checks im Build; ohne node werden sie übersprungen)
- Kein NuGet nötig, es wird nichts restored.
- **Tailscale** auf beiden PCs installiert und angemeldet. Der Freund ist in Felix' Tailnet, oder Felix' Node ist mit ihm
  geteilt. Auf dem Host die IP mit `tailscale ip -4` holen.
- Vor dem Abend prüfen: `tailscale ping <Tailscale-IP von Felix>` vom Freund. Die Antwort sollte `via direct` bzw.
  `via <ip>:<port>` zeigen. Steht dort `via DERP(...)`, läuft der Verkehr über ein Tailscale-Relay, und der
  Stadt-Download (~44 MB) wird langsam. Dann auf beiden Seiten eine Direktverbindung ermöglichen: UDP 41641 ausgehend
  (und am Router möglichst eingehend bzw. per UPnP) freigeben, eine Firewall, die UDP blockiert, für `tailscaled`
  öffnen, und mit `tailscale netcheck` prüfen (`UDP: true`, ideal ohne „hard NAT“). Danach erneut `tailscale ping`.
  Sobald der Host hostet: `Test-NetConnection <Tailscale-IP von Felix> -Port 27015` vom Freund
  (`TcpTestSucceeded : True`).
- **Spiel schließen** vor jedem Deploy: die geladene DLL blockiert das Kopieren.

## 3. Bauen und deployen

```powershell
powershell -ExecutionPolicy Bypass -File tools\deploy-windows.ps1
# andere Spielinstallation: -GameDir "E:\Games\Cities Skylines II"; ohne Firewall-Schritt: -SkipFirewall;
# Git Bash an ungewöhnlichem Ort: -BashPath "D:\Git\bin\bash.exe"
```

Das Skript macht nacheinander:

1. `tools/build.sh --game-dir <GameDir> --deploy` in Git Bash: Build, API-Check gegen die Spiel-DLLs, Core-Tests, Deploy.
   Jede Warnung ist ein Buildfehler, ein roter Test bricht ab.
2. Prüft, dass `Mods\SkylineTogetherDirect\SkylineTogether.dll` im Nutzerordner liegt.
3. Legt die Firewall-Regel `SkylineTogetherDirect` an (TCP 27015, nur aus `100.64.0.0/10`, also Tailscale) und ersetzt
   eine vorhandene gleichen Namens. Ohne Adminrechte kommt ein UAC-Prompt nur für diesen Schritt. Nur der **Host**
   braucht die Regel.
4. Warnt einzeln vor jedem anderen Co-op-Mod: „Multiplayer Co-op“ (Paradox Mods 159499,
   `.cache\Mods\pdx_mods\159499_*`), das Original „Skyline Together“ (Paradox Mods 163578,
   `.cache\Mods\pdx_mods\163578_*`) und ein lokaler Ordner `Mods\SkylineTogether`. Danach meldet es aktive
   Firewall-Block-Regeln für `Cities2.exe` mit Name und Befehl zum Entfernen. Gelöscht wird nichts.
5. Gibt die **Build-ID** aus (erste 12 Hex-Zeichen des SHA-256 der DLL). Beide Spieler müssen dieselbe haben, sie steht
   auch unten im Mod-Panel.

Pakete: `dist/SkylineTogetherDirect-0.3.0.zip` mit dem Ordner `SkylineTogetherDirect` baut `tools/build.sh --package`
(unter Windows) bzw. `tools/linux-test.sh --package` (unter Linux im Container, D: vorher read-only mounten).

## 4. Der Freund

Gleicher Mod, gleiche Build-ID, keine Firewall-Regel nötig (er wählt sich nur aus).

- **Standard: der Zip von Felix' eigenem Build.** Felix zippt nach `deploy-windows.ps1` seinen fertig deployten Ordner
  (`Compress-Archive "$env:USERPROFILE\AppData\LocalLow\Colossal Order\Cities Skylines II\Mods\SkylineTogetherDirect" SkylineTogetherDirect.zip`),
  der Freund entpackt ihn nach `...\Cities Skylines II\Mods\` (Ergebnis: `Mods\SkylineTogetherDirect\SkylineTogether.dll`).
  So ist die Build-ID garantiert identisch, und der Freund braucht weder .NET SDK noch Git Bash. Die Build-ID danach
  beim Freund trotzdem kurz prüfen (unten im Mod-Panel). Eine unter Linux gebaute Zip (`dist/`) hat eine andere
  Build-ID als Felix' lokal gebaute DLL und taugt nur, wenn **beide** sie nutzen.
- **Ausnahme: selbst bauen** (klonen, `deploy-windows.ps1 -SkipFirewall`, braucht .NET SDK und Git Bash). Ein eigener
  Build hat in der Regel eine **andere Build-ID** als Felix' DLL. Dann muss einer von beiden die DLL des anderen
  übernehmen, sonst lehnt der Host den Beitritt ab. Die Prüfung aus Abschnitt 3 Schritt 4 (andere Co-op-Mods,
  Block-Regeln) macht beim Zip-Weg niemand automatisch: Abschnitt 5 dann von Hand durchgehen.

## 5. Andere Co-op-Mods deaktivieren (beide)

Alle drei müssen weg bzw. deaktiviert sein, danach das Spiel neu starten:

- „Multiplayer Co-op“ (Paradox Mods 159499, der bisher genutzte Mod): im Spiel unter Paradox Mods deaktivieren bzw. aus
  dem Playset entfernen. Er kann außerdem Port 27015 belegen.
- Das Original „Skyline Together“ (Paradox Mods 163578): Abo entfernen bzw. deaktivieren.
- Ein lokal installiertes Original im Ordner `...\Cities Skylines II\Mods\SkylineTogether`: Spiel schließen, Ordner
  löschen oder aus `Mods\` verschieben.

Der Fork hat denselben Assembly-Namen (`SkylineTogether.dll`) und dieselbe Settings-Datei wie das Original. Laufen beide,
lädt das Spiel womöglich das falsche, und beide schreiben dieselben Einstellungen.

## 6. Testabend

**Erster Start (beide):** Danach `Logs\SkylineTogether.log` ansehen:

- `P-DIRECT` ist OK, es gibt kein Steam-Probe-FAIL (die Steam-Probes stehen auf „off: direct mode“).
- Steht „Steam relay network“ oder P-STEAMSOCKETS als ausgeführte Probe im Log, läuft das Original statt des Forks
  (Abschnitt 5). Im Direkt-Modus stehen dort stattdessen `direct mode: Steam is not used …` und `P-DIRECT`.
- Lädt der Mod gar nicht: im Log und in `Player.log` nach `TypeLoadException` oder `MissingMethodException` mit
  `Steamworks` suchen. Das hieße, die JIT-Isolierung des Steam-Codes hat unter Mono nicht gehalten. **Das ist das
  größte Risiko**, hier zuerst schauen.
- Der Selbsttest P-DIRECT bindet nur Loopback. Eine Windows-Firewall-Abfrage für `Cities2.exe` beim Spielstart ist
  deshalb **nicht** zu erwarten. Kommt sie trotzdem: **nicht auf Abbrechen klicken**, das legt eine Block-Regel an. Die
  Regel aus dem Deploy-Skript lässt den Freund herein.

**Ablauf:**

1. Host (Felix): Stadt laden, Mod-Panel öffnen (Button oben links), Passwort setzen, „Host this city“. Die Tailscale-IP
   findet man mit `tailscale ip -4`.
   - Beim **ersten** Klick auf „Host this city“ kann der Windows-Firewall-Dialog für `Cities2.exe` erscheinen (jetzt
     wird auf allen Schnittstellen gelauscht). **„Zulassen“** wählen, **privat und öffentlich** anhaken, **nicht
     abbrechen** (Abbrechen legt eine Block-Regel an, die das Deploy-Skript dann meldet).
   - Danach prüft der Host selbst in PowerShell: `Test-NetConnection (tailscale ip -4) -Port 27015` muss
     `TcpTestSucceeded : True` zeigen. Wenn nicht: im Log nach `hosting failed` bzw. `listening on IPv4 only` schauen
     und die Firewall-Regel prüfen.
2. Freund: Hauptmenü, Multiplayer, „Join a friend“, `<Tailscale-IP von Felix>:27015` und das Passwort. Er lädt die Stadt
   (~44 MB), der Host pausiert währenddessen.
3. Die 30-Minuten-Checkliste (Erfolgskriterien der Spec, Abschnitt „Erfolg“):
   - beide bauen gleichzeitig, auch an derselben Stelle
   - Zonen malen, Abriss, Terrain und Wasser
   - Geld, Meilensteine, Bevölkerung, Zeit sind bei beiden identisch (Host-Wert)
   - Meilenstein erreichen, Speichern
   - Tailscale kurz trennen und wieder verbinden: Reconnect ohne doppelten Spieler
   - Ziel: Session über 2 bis 3 Stunden stabil

**Bekannte Eigenheit:** Der DLC-Abgleich wird im Direkt-Modus übersprungen (Logwarnung `DLC list unavailable on …; DLC check
skipped`), wenn eine Seite eine leere DLC-Liste meldet. „Kein DLC“ und „DLC-Liste nicht verfügbar“ lassen sich so nicht
unterscheiden. Deshalb sollten beide Spieler dieselben DLCs haben (haben sie).

## 7. Logs einsammeln (jeder Spieler)

Im Panel „Write diagnostics“ klicken, dann aus
`C:\Users\felix\AppData\LocalLow\Colossal Order\Cities Skylines II\Logs\`:

- `SkylineTogether.log`
- `SkylineTogether-1.log` bis `-3.log` (die Logs der letzten drei Spielstarts; nach einem Hänger und Neustart steht der
  Hänger in `-1.log`)
- `SkylineTogether-diagnostics-*.txt`

Adressen sind darin maskiert. Keine `.dmp`-Dateien weitergeben (können Passwörter enthalten).

## 8. Auswerten

```powershell
Select-String -Path "$env:USERPROFILE\AppData\LocalLow\Colossal Order\Cities Skylines II\Logs\SkylineTogether.log" -Pattern 'refused proposal','divergence','repair','resync','timeout','ProtocolError','stalled','responding again','corrupt'
```

Bei einem Hänger schreibt die Mod nach 10 s ohne Frame `main thread stalled: …` mit dem Abschnitt, in dem es stand
(`inside the mod's section '…'` oder `outside the mod's code`). Das Spiel dann nicht sofort abschießen, sondern 30 s
warten, damit die zweite Meldung auch im Log landet.

Dazu `DLC check skipped` (siehe oben) und beim ersten Start `P-DIRECT`. Auf den Host-Log zuerst schauen (er entscheidet),
dann den des Clients. Zu jedem Treffer die Uhrzeit mit dem Erlebten abgleichen.

## 9. Bugfix-Regel

Jeder Fix bekommt, wo möglich, einen Core-Test (`tests/SkylineTogether.Core.Tests`, `[Test]`). Vor dem Commit
`& "C:\Program Files\Git\bin\bash.exe" tools/build.sh --game-dir "D:\games\Cities Skylines II"` in PowerShell, oder `tools/build.sh ...` in Git Bash (nie ein nacktes `bash`: das ist unter Windows oft das WSL-`bash.exe`) (Build + API-Check + Tests grün), Conventional Commit,
Push nach `origin/main`. Die Linux-Session zieht nach. Die CI-Untergrenze `--expect-at-least` in
`.github/workflows/ci.yml` mit der Testanzahl anheben.
