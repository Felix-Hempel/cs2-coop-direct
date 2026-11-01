# Linux-Testlauf

Upstream-Code gilt als nicht vertrauenswürdig. Unter Linux wird er nur im Container gebaut und getestet
(`podman`, `--network=none`, Quellen read-only gemountet, Kopie nach `/tmp/w`). Nie `dotnet` direkt auf dem Host.

```bash
tools/linux-test.sh          # baut Core + Tests, führt die Tests aus; Exit-Code = Anzahl Fehlschläge
tools/linux-test.sh --game   # zusätzlich Game-Layer-Build gegen /mnt/d/games/Cities Skylines II
```

Erwartet: `1492 passed, 0 failed`.

## Game-Layer unter Linux (--game)

Vorher D: read-only einhängen (nie rw, nie darauf schreiben):

```bash
sudo mkdir -p /mnt/d && sudo mount -t ntfs3 -o ro /dev/nvme0n1p2 /mnt/d
# danach: sudo umount /mnt/d
```

Ergebnis (2026-11-01): geht nicht. `dotnet build SkylineTogether.sln -c Release -p:DeployMod=false
"-p:GameDir=/game/games/Cities Skylines II"` bricht ab mit:

```
error NU1201: Project SkylineTogether.Core is not compatible with net472 (.NETFramework,Version=v4.7.2).
Project SkylineTogether.Core supports: netstandard2.1 (.NETStandard,Version=v2.1)
```

Game-Layer baut Windows per `tools/build.sh`. Auf Linux wurde nicht weiter versucht, das zu lösen.
