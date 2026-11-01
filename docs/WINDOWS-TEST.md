# Windows-Test: Skyline Together Direct

Einstieg für die Claude-Code-Session unter Windows und für Felix am Testabend. Entwickelt wurde unter Linux, dort läuft
alles außer dem Spiel selbst. Hier wird zum ersten Mal gegen ein echtes CS2 getestet, und Bugs werden hier gefixt.

**Stand:** Fork von SkylineTogether, Direct-Modus ohne Steam: TCP, Standardport 27015, Passwort Pflicht,
Einstellung `DirectOnly` (Standard an), Mod-Ordner `SkylineTogetherDirect`, Anzeigename „Skyline Together Direct“,
Protokoll 1009, Version 0.3.0, Spiel 1.6.2f1. Spec: `docs/superpowers/specs/2026-11-01-direct-connect-design.md`,
Plan: `docs/superpowers/plans/2026-11-01-direct-connect.md`, Architektur: `docs/ARCHITECTURE.md`.

**Pfade:** Spiel `D:\games\Cities Skylines II`, Nutzerdaten
`C:\Users\felix\AppData\LocalLow\Colossal Order\Cities Skylines II` (darin `Mods\`, `Logs\`). Beide Spieler haben
dasselbe Spielpaket und dieselben DLCs.

## 1. Repo holen

```bash
git clone https://github.com/Felix-Hempel/cs2-coop-direct.git C:\Users\felix\Documents\IT\cs2-coop-direct
# oder, falls schon vorhanden:
git -C C:\Users\felix\Documents\IT\cs2-coop-direct pull origin main
```

## 2. Voraussetzungen

- .NET SDK 8.0.425 oder neuer 8.0.x (`global.json`): `winget install Microsoft.DotNet.SDK.8`
- Git Bash (`winget install Git.Git`). `deploy-windows.ps1` nimmt gezielt dessen `bash.exe`, nicht das WSL-`bash`.
- node (optional, für die UI-Checks im Build; ohne node werden sie übersprungen)
- Kein NuGet nötig, es wird nichts restored.

## 3. Bauen und deployen

```powershell
powershell -ExecutionPolicy Bypass -File tools\deploy-windows.ps1
# andere Spielinstallation: -GameDir "E:\Games\Cities Skylines II"; ohne Firewall-Schritt: -SkipFirewall
```

Das Skript macht nacheinander:

1. `bash tools/build.sh --game-dir <GameDir> --deploy`: Build, API-Check gegen die Spiel-DLLs, Core-Tests, Deploy.
   Jede Warnung ist ein Buildfehler, ein roter Test bricht ab.
2. Prüft, dass `Mods\SkylineTogetherDirect\SkylineTogether.dll` im Nutzerordner liegt.
3. Legt die Firewall-Regel `SkylineTogetherDirect` an (TCP 27015, nur aus `100.64.0.0/10`, also Tailscale) und ersetzt
   eine vorhandene gleichen Namens. Ohne Adminrechte kommt ein UAC-Prompt nur für diesen Schritt. Nur der **Host**
   braucht die Regel.
4. Warnt, wenn der alte Mod (Paradox Mods ID 159499, `.cache\Mods\pdx_mods\159499_*`) noch abonniert ist.
5. Gibt die **Build-ID** aus (erste 12 Hex-Zeichen des SHA-256 der DLL). Beide Spieler müssen dieselbe haben, sie steht
   auch unten im Mod-Panel.

Pakete: `dist/SkylineTogetherDirect-0.3.0.zip` mit dem Ordner `SkylineTogetherDirect` baut `tools/build.sh --package`
(unter Windows) bzw. `tools/linux-test.sh --package` (unter Linux im Container, D: vorher read-only mounten).

## 4. Der Freund

Gleicher Mod, gleiche Build-ID, keine Firewall-Regel nötig (er wählt sich nur aus).

- Am einfachsten: der Freund klont ebenfalls und führt `deploy-windows.ps1 -SkipFirewall` aus (braucht dann auch .NET SDK
  und Git Bash).
- Oder per Zip: Felix zippt seinen fertig deployten Ordner
  (`Compress-Archive "$env:USERPROFILE\AppData\LocalLow\Colossal Order\Cities Skylines II\Mods\SkylineTogetherDirect" SkylineTogetherDirect.zip`),
  der Freund entpackt ihn nach `...\Cities Skylines II\Mods\` (Ergebnis: `Mods\SkylineTogetherDirect\SkylineTogether.dll`).
  Die Build-ID danach beim Freund prüfen, sie muss identisch sein. Eine unter Linux gebaute Zip (`dist/`) hat eine andere
  Build-ID als Felix' lokal gebaute DLL und taugt nur, wenn **beide** sie nutzen.

## 5. Alten Mod deaktivieren (beide)

Den alten Skyline Together (Paradox Mods 159499) im Spiel unter Paradox Mods deaktivieren bzw. das Abo entfernen und das
Spiel neu starten. Sonst laufen zwei Mods mit denselben Typen, und der alte Mod-Server kann Port 27015 belegen.

## 6. Testabend

**Erster Start (beide):** Danach `Logs\SkylineTogether.log` ansehen:

- `P-DIRECT` ist OK, es gibt kein Steam-Probe-FAIL (die Steam-Probes stehen auf „off: direct mode“).
- Lädt der Mod gar nicht: im Log und in `Player.log` nach `TypeLoadException` oder `MissingMethodException` mit
  `Steamworks` suchen. Das hieße, die JIT-Isolierung des Steam-Codes hat unter Mono nicht gehalten. **Das ist das
  größte Risiko**, hier zuerst schauen.
- Der Selbsttest P-DIRECT bindet nur Loopback. Eine Windows-Firewall-Abfrage für `Cities2.exe` beim Spielstart ist
  deshalb **nicht** zu erwarten. Kommt sie trotzdem: **nicht auf Abbrechen klicken**, das legt eine Block-Regel an. Die
  Regel aus dem Deploy-Skript lässt den Freund herein.

**Ablauf:**

1. Host (Felix): Stadt laden, Mod-Panel öffnen (Button oben links), Passwort setzen, „Host this city“. Die Tailscale-IP
   findet man mit `tailscale ip -4`.
2. Freund: Hauptmenü, Multiplayer, „Join a friend“, `<Tailscale-IP von Felix>:27015` und das Passwort. Er lädt die Stadt
   (~44 MB), der Host pausiert währenddessen.
3. Die 30-Minuten-Checkliste (Erfolgskriterien der Spec, Abschnitt „Erfolg“):
   - beide bauen gleichzeitig, auch an derselben Stelle
   - Zonen malen, Abriss, Terrain und Wasser
   - Geld, Meilensteine, Bevölkerung, Zeit sind bei beiden identisch (Host-Wert)
   - Meilenstein erreichen, Speichern
   - Tailscale kurz trennen und wieder verbinden: Reconnect ohne doppelten Spieler
   - Ziel: Session über 2 bis 3 Stunden stabil

**Bekannte Eigenheit:** Der DLC-Abgleich wird im Direct-Modus übersprungen (Logwarnung `DLC list unavailable on …; DLC check
skipped`), wenn eine Seite eine leere DLC-Liste meldet. „Kein DLC“ und „DLC-Liste nicht verfügbar“ lassen sich so nicht
unterscheiden. Deshalb sollten beide Spieler dieselben DLCs haben (haben sie).

## 7. Logs einsammeln (jeder Spieler)

Im Panel „Write diagnostics“ klicken, dann aus
`C:\Users\felix\AppData\LocalLow\Colossal Order\Cities Skylines II\Logs\`:

- `SkylineTogether.log`
- `SkylineTogether-diagnostics-*.txt`

Adressen sind darin maskiert. Keine `.dmp`-Dateien weitergeben (können Passwörter enthalten).

## 8. Auswerten

```bash
grep -inE "refused proposal|divergence|repair|resync|timeout|ProtocolViolation" "$LOG"
```

Dazu `DLC check skipped` (siehe oben) und beim ersten Start `P-DIRECT`. Auf den Host-Log zuerst schauen (er entscheidet),
dann den des Clients. Zu jedem Treffer die Uhrzeit mit dem Erlebten abgleichen.

## 9. Bugfix-Regel

Jeder Fix bekommt, wo möglich, einen Core-Test (`tests/SkylineTogether.Core.Tests`, `[Test]`). Vor dem Commit
`bash tools/build.sh --game-dir "D:\games\Cities Skylines II"` (Build + API-Check + Tests grün), Conventional Commit,
Push nach `origin/main`. Die Linux-Session zieht nach. Die CI-Untergrenze `--expect-at-least` in
`.github/workflows/ci.yml` mit der Testanzahl anheben.
