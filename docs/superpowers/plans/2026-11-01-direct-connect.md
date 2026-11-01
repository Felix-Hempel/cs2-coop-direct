# CS2 Koop Direct-Connect Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Den Fork von SkylineTogether (v0.3.0+, Commit `fa9a443`) so umbauen, dass zwei Spieler ohne Steam per
`IP:Port` über Tailscale host-autoritativ zusammen bauen.

**Architecture:** Ein neuer `DirectTransport` (TCP, plain `System.Net.Sockets`) implementiert Core's `ITransport`
und ersetzt im Direct-Modus `SteamSocketsTransport`. Im Game-Layer schaltet ein Direct-Modus jeden Steam-Aufruf ab:
Init, Self-Tests, Persona, Invites und `SteamAvailable`-Gating. Sync, Validierung und Gating von Upstream bleiben
unverändert.

**Tech Stack:** C# 10, .NET SDK 8.0.425 (`global.json`), Core: netstandard2.1 / net472. Game-Layer: net472 gegen
`Cities2_Data/Managed`. Eigenes Test-Framework (`tests/SkylineTogether.Core.Tests/Framework`, `[Test]`, `Assert.*`).
Podman-Container `mcr.microsoft.com/dotnet/sdk:8.0` unter Linux.

**Spec:** `docs/superpowers/specs/2026-11-01-direct-connect-design.md`. Lies sie vor jedem Task. Upstream-Referenz:
`docs/ARCHITECTURE.md` (#transport, #handshake-and-admission, #security-model).

## Global Constraints

- Kein NuGet-Paket, weder referenziert noch restored (ADR `docs/decisions/0001-build-without-nuget.md`).
- `TreatWarningsAsErrors`, Analyzer-Level 8.0, `LangVersion 10.0`, `Nullable disable`, `ImplicitUsings disable`.
  Jede Warnung ist ein Buildfehler.
- Core-Code muss für netstandard2.1 **und** net472 (Mono des Spiels) kompilieren. Keine APIs nur aus .NET 5+.
- Thread-Affinität annotieren wie die Nachbarn (`[ThreadAffinity(ThreadKind.Main|Network|Any)]`).
- Logs maskieren Adressen (`Diagnostics.AddressMask`). Es landet nie eine volle IP im Log.
- Spielversion 1.6.2f1. Beide Spieler haben dasselbe Spielpaket und dieselben DLCs.
- Im Direct-Modus wird **kein** Steamworks-Typ angefasst. Steam-Aufrufe liegen in eigenen Methoden mit
  `[MethodImpl(MethodImplOptions.NoInlining)]` bzw. in eigenen Klassen, damit Mono sie beim JIT des Direct-Pfads nicht
  lädt.
- Standardport **27015**, einstellbar. Direct-Verbindungen nur mit Session-Passwort (Upstream-Regel bleibt).
- Unter Linux wird nie auf NTFS geschrieben. D: wird nur lesend unter `/mnt/d` eingehängt
  (`sudo mount -t ntfs3 -o ro /dev/nvme0n1p2 /mnt/d`).
- Untrusted-Upstream-Code wird unter Linux nur im Container gebaut und getestet (`tools/linux-test.sh`, `--network=none`).
- Git: privates Repo, Arbeit auf `main` erlaubt, Conventional Commits, Footer
  `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- Neue UI- und Locale-Texte auf Englisch und Deutsch (echte Umlaute).
- CI-Minimum `--expect-at-least` in `.github/workflows/ci.yml` wird mit jedem Task auf die neue Testanzahl gehoben.

## Review Focus

1. **Tailscale bricht weg oder der Laptop schläft** (halb offene TCP-Verbindung, kein FIN). Beide Seiten müssen das
   innerhalb des Connected-Timeouts erkennen (30 s, beim Join 120 s, `IConnectionTimeouts`), dann greift Upstream-
   Rejoin. Test: `HalfOpenPeerTimesOut` in Task 2.
2. **Stadt-Download (~44 MB) läuft, während gebaut wird.** Reliable-Nachrichten dürfen nicht hinter Bulk-Bytes
   verhungern. Test: `ReliableIsNotStarvedByQueuedBulk` in Task 2.
3. **Port 27015 ist belegt** (der alte Mod-Server läuft noch). Erwartet wird eine klare Meldung mit Port, kein stilles
   Scheitern. Test: `ListenOnBusyPortThrowsWithPort` in Task 2, Toast in Task 4.
4. **Fremder Traffic auf dem Port** (alter Mod-Client, Portscanner; das Log des alten Mods hatte 320 Handshake-
   Timeouts). Er darf keinen Crash und keinen Speicherberg verursachen, vor der Admission gilt ein Frame-Limit von
   64 KiB. Tests: `GarbagePreambleIsClosed`, `OversizedFrameBeforeAdmissionIsClosed` in Task 2.
5. **Rejoin von derselben Tailscale-IP, während der alte Geist noch verbunden ist.** Der neue ersetzt den alten, es
   gibt keinen doppelten Spieler. Test: `DirectRejoinFromSameIpReplacesGhost` in Task 3.

---

### Task 1: Repo, Linux-Testlauf, grüne Baseline

**Files:**
- Create: Repo unter `~/Documents/IT/cs2-coop-direct` (Upstream-Klon, die bestehenden `docs/superpowers/**` bleiben
  erhalten)
- Create: `tools/linux-test.sh`
- Create: `docs/dev/linux.md`
- Modify: `tests/SkylineTogether.Core.Tests/Diagnostics/LogPrivacyTests.cs:~104`

**Interfaces:**
- Produces: `tools/linux-test.sh [--game]` baut Core und Tests im Container und führt sie aus. Exit-Code = Anzahl der
  Fehlschläge. Mit `--game` wird zusätzlich der Game-Layer gegen `/mnt/d/games/Cities Skylines II` gebaut, read-only
  gemountet.

- [ ] **Step 1: Repo aufsetzen.** `git clone https://github.com/ljkx/SkylineTogether.git /tmp/st` und
  `/tmp/st/.git` sowie alle Dateien nach `~/Documents/IT/cs2-coop-direct/` verschieben, ohne `docs/superpowers` zu
  überschreiben. Danach `git remote rename origin upstream`. Ein privates GitHub-Repo `cs2-coop-direct` anlegen
  (`gh repo create cs2-coop-direct --private`) und als `origin` setzen.
- [ ] **Step 2: `tools/linux-test.sh` schreiben.** Es ruft `podman run --rm --network=none -v <repo>:/src:ro,Z` auf,
  kopiert nach `/tmp/w` und führt dieselben drei Befehle wie die CI-Schritte „Build SkylineTogether.Core“, „Build the
  tests“ und „Run the tests“ aus. Mit `--game` wird zusätzlich `-v /mnt/d:/game:ro` gemountet und
  `dotnet build SkylineTogether.sln -c Release -p:DeployMod=false -p:GameDir=/game/games/Cities\ Skylines\ II` gebaut.
- [ ] **Step 3: Baseline laufen lassen.** `tools/linux-test.sh`. Erwartet: `1491 passed, 1 failed`, der Fehlschlag
  ist `LogPrivacyTests.TheRedactorForThisMachineIsUsable`.
- [ ] **Step 4: Den Test plattformneutral machen.** Unter Nicht-Windows (`Environment.OSVersion.Platform !=
  PlatformID.Win32NT`) erwartet er den Platzhalter, den der Redactor dort tatsächlich liefert (`%USERNAME%`). Unter
  Windows bleibt die Assertion `%USERPROFILE%` unverändert.
- [ ] **Step 5: Erneut laufen lassen.** Erwartet: `1492 passed, 0 failed`.
- [ ] **Step 6: Game-Layer-Build unter Linux probieren.** `tools/linux-test.sh --game`. Das Ergebnis (geht / geht
  nicht + Fehlermeldung) kommt nach `docs/dev/linux.md`. Wenn es nicht geht, steht dort: „Game-Layer baut Windows per
  `tools/build.sh`“. Danach nicht weiter versuchen, das Problem zu lösen.
- [ ] **Step 7: Commit und Push.** `chore: fork setup, linux container test run`, dann `git push -u origin main`.

### Task 2: `DirectTransport` (Core)

**Files:**
- Create: `src/SkylineTogether.Core/Transport/Direct/DirectTransport.cs`
- Create: `src/SkylineTogether.Core/Transport/Direct/DirectConnection.cs`
- Create: `src/SkylineTogether.Core/Transport/Direct/DirectFraming.cs`
- Create: `src/SkylineTogether.Core/Transport/Direct/DirectListenException.cs`
- Modify: `src/SkylineTogether.Core/Contracts/Transport/TransportKind.cs` (neuer Wert `Direct` **am Ende**)
- Test: `tests/SkylineTogether.Core.Tests/Transport/DirectTransportTests.cs`

**Entscheidung (Abweichung von der Spec-Empfehlung, mit Grund):** **Eine** TCP-Verbindung pro Peer für alle drei
Lanes. Der Lane-Wert steht im Frame. Upstream unterstützt genau diesen Fall schon: Wenn Steam-Lanes fehlen, teilt
`PeerOutbox` einen Reliable-Stream und begrenzt B über das Bulk-Window (ARCHITECTURE.md#outbox-lanes-and-rate-control).
Eine zweite TCP- oder UDP-Verbindung bräuchte Verbindungs-Tokens und NAT-Logik, ohne Nutzen im Tailscale-Netz.
`Delivery.Unreliable` heißt hier: wird verworfen (Rückgabe `Ok`), wenn mehr als 256 KiB in der Sendequeue warten.

**Interfaces:**
- Produces:
  - `public sealed class DirectTransport : ITransport` mit `DirectTransport(ILog log, IClock clock, string localName)`
    und `Kind == TransportKind.Direct`.
  - `Listen(ListenOptions o)` bindet einen Dual-Mode-Socket (`IPv6Any`, `DualMode = true`) auf `o.IpPort`. Bei belegtem
    Port wird `DirectListenException` mit `public int Port` geworfen.
  - `Connect(Endpoint e)` nutzt `e.Host`/`e.Port` und verbindet asynchron. Ein Fehlschlag kommt als `OnDisconnected`
    in `Poll`.
  - `public int BoundPort { get; }` ist der tatsächlich gebundene Port (Tests binden auf Port 0).
  - `internal sealed class DirectConnection : IThreadSafeConnection, IConnectionTimeouts, IAdmissionAware`, deren
    `Peer = new PeerIdentity(0, "<ip>:<port>", null, direct: true)`.
  - `DirectFraming`: Preamble `"STD1"` (4 Byte ASCII) in beide Richtungen beim Verbindungsaufbau. Frame:
    `u8 type | u8 lane | u32 LE length | payload`. `type`: 0 = Data, 1 = Ping (u64 Zeitstempel), 2 = Pong, 3 = Close
    (i32 reason, UTF-8 text ≤ 256 B).
- Consumes: `Limits.MaxMessageBytes` (512 KiB), `OutboxConfig.SendBufferBytes` (16 MiB),
  `IConnectionTimeouts.NormalMilliseconds` (30 000) / `JoiningMilliseconds` (120 000), `ListenOptions.Refusal(...)`.

Verhalten, das die Tests festnageln:
- **Send:** `TooLarge` über `MaxMessageBytes`. `Busy`, wenn Reliable- und Bulk-Bytes in der Queue plus `count` über
  `SendBufferBytes` liegen. `Closed` nach `Close`. Ein Writer-Thread pro Verbindung, `NoDelay = true`.
- **Empfang:** Ein Reader-Thread pro Verbindung legt die Daten in eine Inbox. `Poll` liefert Connected, Messages
  (pro Lane in Sendereihenfolge) und Disconnected an den Sink. Connected kommt immer vor der ersten Message.
- **Vor `OnAdmitted()`** gilt ein Frame-Limit von 64 KiB (`Limits` für Hello), danach `MaxMessageBytes`. Eine
  Überschreitung oder eine falsche Preamble führt zu Close mit Grund `ProtocolViolation`. Es wird nicht über den Limit
  hinaus gelesen.
- **Ping:** jede Sekunde. `GetStats().PingMs` ist die letzte RTT, `PendingReliableBytes` die Queue-Größe. Bleiben die
  Bytes länger als der Connected-Timeout aus, folgt `OnDisconnected` mit Text `"timeout"`.
- **`Close(reason, text, linger)`:** Mit `linger` wird die Queue noch höchstens 2 s geleert, dann das Close-Frame
  gesendet. Die Gegenseite bekommt `OnDisconnected(reason, text)`.
- **Pending-Limits:** Über `ListenOptions.Refusal` wird abgelehnt, und zwar per Close-Frame mit dem Refusal-Text.

- [ ] **Step 1: Failing Tests schreiben** in `DirectTransportTests.cs` (echte Sockets auf `127.0.0.1`, Port 0,
  `RecordingSink` aus `Transport/TransportTestSupport.cs`):
  - `ConnectReportsBothEndsAsDirectPeers`: Beide Sinks bekommen Connected. `Peer.Direct == true`,
    `Peer.SteamId == 0`, `Peer.Address` beginnt mit `127.0.0.1:`.
  - `MessagesArriveInOrderPerLaneWithTheirLane`: 1000 Nachrichten auf R, U und B gemischt. Pro Lane kommt die
    Reihenfolge exakt an, und der gemeldete Lane-Wert stimmt.
  - `MaxSizeMessageRoundTripsAndLargerIsTooLarge`: Genau `Limits.MaxMessageBytes` kommt byte-gleich an, mit
    `+1` gibt es `SendResult.TooLarge`.
  - `SendReturnsBusyAboveTheSendBuffer`: Der Empfänger pollt nicht. Es wird gesendet, bis `Busy` kommt, die Summe
    liegt bei `>= SendBufferBytes - MaxMessageBytes`.
  - `ReliableIsNotStarvedByQueuedBulk`: Über `PeerOutbox` werden 40 MiB auf B in die Queue gelegt, danach eine
    R-Nachricht. Sie kommt an, bevor mehr als `OutboxConfig.BulkWindowMaxBytes + SendBufferBytes` Bulk-Bytes empfangen
    wurden.
  - `CloseWithLingerDeliversQueuedThenReason`: 5 Nachrichten senden, dann `Close(7, "bye", true)`. Die Gegenseite
    sieht alle 5, danach Disconnected `(7, "bye")`.
  - `HalfOpenPeerTimesOut`: Über einen Test-Hook (`internal bool SuppressPongs`) und
    `SetConnectedTimeout(1500)` kommt `OnDisconnected` mit `"timeout"` innerhalb von 3 s.
  - `ListenOnBusyPortThrowsWithPort`: Ein zweites `Listen` auf `BoundPort` wirft `DirectListenException`, deren
    `.Port` dem Port entspricht.
  - `GarbagePreambleIsClosed`: Ein roher `TcpClient` schickt `"GET / HTTP/1.1\r\n"`. Der Host meldet kein
    `OnConnected`, der Socket wird innerhalb von 2 s geschlossen.
  - `OversizedFrameBeforeAdmissionIsClosed`: Nach der Preamble kommt ein Frame-Header mit Länge 65 537 vor
    `OnAdmitted`. Ergebnis: Disconnected und keine Allokation über 64 KiB (geprüft über einen Zähler
    `internal long LargestReceiveAllocation`).
  - `PendingLimitRefusesWithText`: Bei `MaxPendingConnections = 1` bekommt der zweite Client Disconnected mit Text
    `"server busy"`.
  - `WorksUnderNetSim`: `NetSimTransport(new DirectTransport(...), new NetSimOptions { LatencyMs = 80, JitterMs = 10 },
    clock)`. Ein R-Roundtrip kommt an, und `PingMs` liegt bei `>= 0`.
- [ ] **Step 2: Laufen lassen.** `tools/linux-test.sh` schlägt beim Kompilieren fehl, weil `DirectTransport` nicht
  definiert ist.
- [ ] **Step 3: `DirectFraming`, `DirectConnection`, `DirectTransport` und `DirectListenException` implementieren**
  wie oben beschrieben. Zusätzlich `TransportKind.Direct` ans Ende des Enums mit dem XML-Kommentar „TCP direct IP
  without Steam (fork)“.
- [ ] **Step 4: Laufen lassen.** `tools/linux-test.sh`. Alle neuen Tests sind grün, ebenso die bestehenden.
- [ ] **Step 5: CI-Minimum heben und committen.** `feat(transport): DirectTransport over TCP without Steam`.

### Task 3: Session-Core für Direct-Peers

**Files:**
- Modify: `src/SkylineTogether.Core/Session/SessionClient.cs:671` (`HostProofRequired`)
- Modify: `src/SkylineTogether.Core/Session/SessionHost.cs:1611-1630` (`ResolveDuplicate`)
- Modify: `src/SkylineTogether.Core/Protocol/ProtocolVersion.cs:37`
- Test: `tests/SkylineTogether.Core.Tests/Session/DirectSessionTests.cs`

**Interfaces:**
- Consumes: `PeerIdentity(ulong, string, string, bool direct)`, `LoopbackTransport.CreatePair(PeerIdentity,
  PeerIdentity)`, die Session-Test-Helfer aus `Session/SessionTests.cs` / `FaultInjection.cs`.
- Produces: `internal static string DirectKey(string address)` in `SessionHost`. Liefert den Host-Teil ohne Port
  (`"100.64.1.2:51000"` → `"100.64.1.2"`, `"[fd7a::1]:5"` → `"fd7a::1"`).

- [ ] **Step 1: Failing Tests schreiben:**
  - `DirectTransportAlwaysRequiresHostProof`: Ein Client über einen Transport mit `Kind == TransportKind.Direct` hat
    `HostProofRequired == true`, auch mit `ExpectPassword = false`.
  - `DirectJoinWithoutHostPasswordIsRefused`: Host ohne Passwort, Direct-Peer joint. Die Refusal-Reason ist die
    Upstream-Reason für „Direct connections need a password“.
  - `DirectRejoinFromSameIpReplacesGhost`: Peer A `("100.64.1.2:51000", direct)` ist live. Peer B
    `("100.64.1.2:51077", direct)` joint mit demselben Passwort. Ergebnis: A wird entfernt (Upstream-Grund für
    „replaced“), B ist live, die Spielerliste hat 2 Einträge (Host + B).
  - `DirectPeersFromDifferentIpsCoexist`: `100.64.1.2` und `100.64.1.3` sind beide live.
  - `DirectKeyStripsPortAndBrackets`: Die beiden Beispiele oben.
  - `ForkProtocolDiffersFromUpstream`: `ProtocolVersion.Current == 1009`.
- [ ] **Step 2: Laufen lassen.** Erwartet: FAIL.
- [ ] **Step 3: Implementieren.**
  - `HostProofRequired`: `Kind == SteamIp || Kind == Direct || ExpectPassword`.
  - `ResolveDuplicate`: Für `p.Direct && !p.SteamAuthenticated` dieselbe Ersetzungslogik wie für Steam-IDs, aber mit
    dem Schlüssel `DirectKey(p.Address)`.
  - `ProtocolVersion.Current = 1009` mit dem Kommentar `// fork: 1000 + upstream protocol 9; never talks to upstream
    builds`.
  - Prüfen, ob Stellen `Current` mit kleinen Zahlen vergleichen (`grep -rn "ProtocolVersion" src`). Sie müssen
    weiterhin stimmen.
- [ ] **Step 4: Laufen lassen.** Alles grün.
- [ ] **Step 5: CI-Minimum heben und committen.** `feat(session): direct peers rejoin by ip, fork protocol 1009`.

### Task 4: Direct-Modus im Game-Layer (ohne Steam)

**Files:**
- Modify: `src/SkylineTogether/Settings/SkylineTogetherSettings.cs` (neue Einstellung `DirectOnly`, Default `true`,
  in die Settings-Liste in Zeile 26–27)
- Modify: `src/SkylineTogether/Mod.cs:84-112` (Steam-Relay-Init und `SteamInvites` nur, wenn `!DirectOnly`)
- Modify: `src/SkylineTogether/Session/SessionManager.cs:186-270, 323-345, 660-670`
- Create: `src/SkylineTogether/Net/SteamFacade.cs`
- Modify: `src/SkylineTogether/Diagnostics/Probes.cs` (die Steam-Probes `P-STEAMSOCKETS`, `P-STEAMLANES` und
  `P-RELAY` werden im Direct-Modus übersprungen und als „off: direct mode“ gemeldet, nicht als FAIL)
- Modify: `src/SkylineTogether/UI/SessionUISystem.cs`, `src/SkylineTogether/UI/InviteUISystem.cs`,
  `ui/SkylineTogether.mjs` (neues Binding `directOnly`. Ist es true, werden Steam-ID kopieren, Invite friends und
  Friends-only ausgeblendet, und das Join-Feld hat den Platzhalter `IP:Port`)
- Modify: Locale-Dateien (en, de) für die neuen Texte
- Modify: `src/SkylineTogether/Properties/PublishConfiguration.xml` (`DisplayName` = `Skyline Together Direct`),
  `Directory.Build.props` (`ModDeployDir` → `…\Mods\SkylineTogetherDirect`, `<Product>`)

**Interfaces:**
- Consumes: `DirectTransport`, `DirectListenException` (Task 2), `TransportKind.Direct`.
- Produces:
  - `internal static class SteamFacade` mit `bool IsAvailable()`, `ulong LocalId()` und `string PersonaName()`.
    Jede Methode ist `[MethodImpl(NoInlining)]`, kapselt `SteamAPI`/`SteamUser`/`SteamFriends` und hat ein
    try/catch, das `false`/`0`/`null` zurückgibt.
  - `SessionManager.DirectOnly => _settings.DirectOnly`.

- [ ] **Step 1: `SteamFacade` anlegen.** `LocalSteamId`, `SteamAvailable` und die Persona-Abfrage in `PlayerName`
  rufen nur noch `SteamFacade` auf, und auch das nur, wenn `!DirectOnly`. Im Direct-Modus gilt: `LocalSteamId = 0`.
  `PlayerName` ist der Settings-Name, sonst `Environment.UserName`, sonst `"Player"`.
- [ ] **Step 2: Gating anpassen.** `CanHost` und `CanJoin` nutzen `(DirectOnly || SteamAvailable)` statt
  `SteamAvailable`.
- [ ] **Step 3: Transportwahl.**
  - In `StartHosting` und `BeginJoin` wird bei `DirectOnly` `new DirectTransport(_log, _clock, PlayerName)` gebaut,
    sonst wie bisher.
  - Beim Hosten im Direct-Modus ist das Passwort Pflicht. Ohne Passwort kommt der vorhandene Toast
    `st.toast.directNeedsPassword`, und das Hosten bricht ab.
  - Der Listen-Port ist `_settings.DirectPort`.
  - `DirectListenException` wird zum neuen Toast `st.toast.portBusy` mit `PORT`: „Port {PORT} ist belegt. Läuft noch
    ein anderer Multiplayer-Server?“ bzw. „Port {PORT} is in use. Is another multiplayer server still running?“.
  - `BackgroundPump = true` bleibt, weil `DirectConnection` `IThreadSafeConnection` ist.
- [ ] **Step 4: Mod-Start.** In `Mod.cs` laufen die Schritte „Steam relay network“ und `Invites = new
  SteamInvites(...)` nur, wenn `!DirectOnly`. Jede Stelle, die `Mod.Invites` benutzt (`SessionDriver.cs`,
  `SessionUISystem.cs`, `InviteUISystem.cs`, `SessionManager.cs:376`), verträgt `null`.
- [ ] **Step 5: Probes.** Im Direct-Modus werden die Steam-Probes nicht ausgeführt. `SteamSocketPair` wird durch
  einen Self-Test `P-DIRECT` ersetzt: zwei `DirectTransport` auf `127.0.0.1`, Port 0, eine Nachricht auf jeder Lane
  hin und zurück, Timeout 3 s. Schlägt er fehl, ist das Feature `direct` aus, und `BlockingFeature()` meldet ihn.
- [ ] **Step 6: UI-Binding und mjs** wie oben unter *Files*. Danach laufen `node --check ui/SkylineTogether.mjs` und
  `node tools/ui_smoke.mjs`. Erwartet: beide exit 0. Neue Smoke-Assertion: Bei `directOnly = true` existieren keine
  Invite-Elemente.
- [ ] **Step 7: Bauen.**
  - Wenn Task 1 Step 6 ergeben hat, dass der Game-Layer unter Linux baut: `tools/linux-test.sh --game`. Erwartet:
    Build ohne Fehler, Tests grün, ApiCheck (`tools/ApiCheck`) ohne fehlende Referenzen.
  - Sonst: nur `tools/linux-test.sh`, und der Game-Layer-Build ist der erste Schritt in `WINDOWS-TEST.md`.
- [ ] **Step 8: Grep-Gate.** Diese Prüfung muss ohne Treffer bleiben:
  `grep -rnE "SteamAPI\.|SteamUser\.|SteamFriends\.|SteamNetworking" src/SkylineTogether --include=*.cs | grep -vE "Net/Steam|Session/Steam|SteamFacade.cs|Diagnostics/Probes.cs|Mod.cs"`.
  Jeder verbleibende Treffer in `Mod.cs` oder `Probes.cs` liegt hinter einem `!DirectOnly`.
- [ ] **Step 9: DLC- und Versions-Check prüfen.** `GameInfoProvider` liest die DLC-Liste über
  `PrefabSystem.GetAvailableContentPrefabs` und `PlatformManager.instance.GetDlcName`, nicht direkt über Steam. Bleibt
  so. Falls `PlatformManager` im Direct-Modus wirft oder eine leere Liste liefert, setzt `TryAdmit` den DLC-Vergleich
  für diesen Join aus und schreibt die Warnung `"DLC list unavailable on <side>; DLC check skipped"`. Das ist kein
  Refuse. Core-Test in `DirectSessionTests`: `EmptyDlcListOnOneSideSkipsTheCheckWithWarning`.
- [ ] **Step 10: Commit.** `feat(game): direct-only mode without steam, port-busy toast, P-DIRECT self-test`.

### Task 5: Windows-Testpaket

**Files:**
- Create: `tools/deploy-windows.ps1` (UTF-8 **mit BOM**, nur ASCII im Code, `-f`-Formatoperator statt
  `"$var"`-Interpolation)
- Create: `docs/WINDOWS-TEST.md`
- Modify: `README.md` (kurzer Fork-Hinweis oben: was anders ist, Link auf `WINDOWS-TEST.md`)
- Modify: `tools/build.sh:12,155,169` und die Staging-Ordner bzw. `tools/release_common.py check-zip`: Mod-Ordner
  `SkylineTogetherDirect` statt `SkylineTogether` beim Deploy und im Zip, Zip-Name `SkylineTogetherDirect-<version>.zip`

**Interfaces:**
- Consumes: `tools/build.sh --deploy` von Upstream, `ModDeployDir` aus Task 4.
- Produces: `deploy-windows.ps1 [-GameDir "D:\games\Cities Skylines II"] [-SkipFirewall]` führt nacheinander aus:
  1. `bash tools/build.sh --game-dir <GameDir> --deploy`
  2. Prüfung, dass `%USERPROFILE%\AppData\LocalLow\Colossal Order\Cities Skylines II\Mods\SkylineTogetherDirect\SkylineTogether.dll` existiert
  3. Falls kein `-SkipFirewall`: Firewall-Regel idempotent anlegen, dabei wird eine bestehende Regel gleichen Namens
     ersetzt. Ein UAC-Prompt erscheint, wenn nicht als Admin gestartet:
     `New-NetFirewallRule -DisplayName 'SkylineTogetherDirect' -Direction Inbound -Protocol TCP -LocalPort 27015 -RemoteAddress 100.64.0.0/10 -Action Allow`
  4. Warnung, wenn der Ordner `…\.cache\Mods\pdx_mods\159499_*` existiert, also der alte Mod abonniert ist.
     Die Warnung sagt: „im Spiel unter Paradox Mods deaktivieren bzw. Abo entfernen und neu starten“.
  5. Ausgabe der Build-ID (SHA-256 der DLL, die ersten 12 Hex-Zeichen), damit beide Spieler sie vergleichen können

- [ ] **Step 1: `deploy-windows.ps1` schreiben.** Die Syntax wird unter Linux geprüft mit
  `podman run --rm --network=none -v "$PWD":/src:ro,Z mcr.microsoft.com/powershell:lts pwsh -NoProfile -Command "[System.Management.Automation.Language.Parser]::ParseFile('/src/tools/deploy-windows.ps1',[ref]\$null,[ref]\$e) | Out-Null; if (\$e) { \$e; exit 1 }"`.
  Erwartet: exit 0. Dazu `head -c3 tools/deploy-windows.ps1 | xxd`, erwartet: `efbbbf`.
- [ ] **Step 2: `docs/WINDOWS-TEST.md` schreiben.** Das ist der Einstieg für die Claude-Code-Session unter Windows,
  mit diesen Abschnitten:
  1. Repo holen: `git clone`/`git pull` von `origin` nach `C:\Users\felix\Documents\IT\cs2-coop-direct`.
  2. Voraussetzungen: .NET SDK 8.0.425+ (`winget install Microsoft.DotNet.SDK.8`), Git Bash, node.
  3. `powershell -ExecutionPolicy Bypass -File tools\deploy-windows.ps1`.
  4. Gleiches beim Freund: dieselbe Build-ID, Mod-Ordner per Zip, die Firewall-Regel braucht er nicht.
  5. Alten Mod 159499 bei beiden deaktivieren.
  6. Ablauf des Testabends:
     - Host: Stadt laden, Panel öffnen, Passwort setzen, „Host this city“.
     - Freund: `<Tailscale-IP von Felix>:27015` + Passwort.
     - Die 30-Minuten-Checkliste aus den Erfolgskriterien der Spec abarbeiten: gleichzeitig bauen, an derselben
       Stelle bauen, Zonen, Abriss, Terrain/Wasser, Geld vergleichen, Meilenstein, Speichern, Tailscale kurz trennen
       → Reconnect.
  7. Danach Logs einsammeln. Pro Spieler: `SkylineTogether.log`, `SkylineTogether-diagnostics-*.txt`
     („Write diagnostics“), mit dem Pfad.
  8. Auswerten: auf diese Logzeilen achten: `refused proposal`, `divergence`, `repair`, `resync`, `timeout`,
     `ProtocolViolation`.
  9. Bugfix-Regel: jeden Fix mit einem Core-Test, wo möglich. Commit und Push nach `origin/main`. Die Linux-Session
     zieht nach.
- [ ] **Step 3: Paket bauen und prüfen.** Unter Linux, wenn möglich: `tools/build.sh --package` im Container mit
  `--game` aus Task 1. Erwartet: `dist/SkylineTogetherDirect-0.3.0.zip` mit dem Ordner `SkylineTogetherDirect`, und
  `release_common.py check-zip` ist grün. Sonst steht
  dieser Schritt in `WINDOWS-TEST.md` (Abschnitt 3).
- [ ] **Step 4: Commit und Push.** `docs: windows test guide and deploy script`, dann `git push origin main`.

---

**Phase 2** (Sync-Fixes nach dem Testabend) wird bewusst nicht geplant, siehe Spec, Abschnitt „Phasen“.
