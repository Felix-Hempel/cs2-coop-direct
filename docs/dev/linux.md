# Linux-Testlauf

Upstream-Code gilt als nicht vertrauenswürdig. Unter Linux wird er nur im Container gebaut und getestet
(`podman`, `--network=none`, Quellen read-only gemountet, Kopie nach `/tmp/w`). Nie `dotnet` direkt auf dem Host.

```bash
tools/linux-test.sh          # baut Core + Tests, führt die Tests aus; Exit-Code = Anzahl Fehlschläge
tools/linux-test.sh --game   # zusätzlich Game-Layer-Build gegen /mnt/d/games/Cities Skylines II
```

Erwartet: `1516 passed, 0 failed` (Stand Task 4). Die Testflags entsprechen der CI (`--expect-at-least` wird aus
`.github/workflows/ci.yml` gelesen, `--max-skipped 0`). Exit-Codes: Anzahl Fehlschläge; `125` = Game-Layer-Build
fehlgeschlagen (`--game`).

## Game-Layer unter Linux (--game)

Vorher D: read-only einhängen (nie rw, nie darauf schreiben):

```bash
sudo mkdir -p /mnt/d && sudo mount -t ntfs3 -o ro /dev/nvme0n1p2 /mnt/d
# danach: sudo umount /mnt/d
```

Ergebnis: geht (seit Task 4). `--game` mountet den Spielordner selbst nach `/game` (Pfad ohne Leerzeichen), baut die
ganze Solution gegen `/game/Cities2_Data/Managed`, prüft die Typliste `Data/mod-types.generated.txt` (nur Hinweis) und
lässt `tools/ApiCheck` gegen die Spiel-Assemblies laufen. Exit-Code `125`, wenn Build oder API-Check scheitern.

Die frühere Ursache für `NU1201: Project SkylineTogether.Core is not compatible with net472` war nicht das Leerzeichen
im Pfad, sondern SELinux: Der read-only NTFS-Mount lässt sich nicht mit `:Z` umlabeln, der Container bekam auf
`/game` nur `Permission denied`, `Exists('$(GameManagedPath)\mscorlib.dll')` war deshalb false und Core fiel auf
netstandard2.1 zurück. Für `--game` läuft der Container deshalb mit `--security-opt label=disable` (weiterhin
`--network=none`, Quellen und Spielordner read-only).

Die gebauten DLLs bleiben im Container (`/tmp/w`). Ein deploybarer Mod-Ordner entsteht weiterhin unter Windows mit
`tools/build.sh --deploy` (Ziel `Mods\SkylineTogetherDirect`).
