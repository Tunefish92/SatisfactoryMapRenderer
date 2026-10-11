# Game data

Icons, item and building names, level data (resource nodes, collectibles, spawners), the game's logo, the
in-game map, the world's height data, a relief map made from both and simplified 3D models of the buildables with reduced textures,
exported from **Satisfactory 1.2.4** (build 502094) with

```
dotnet run --project src/SatisfactoryMapRenderer -c Release -- --export-game-data --game <Satisfactory folder>
```

The Docker image ships this folder so the map works without a game installation.

**These files are not covered by this repository's MIT licence.** They are the property of Coffee Stain Studios
and are included here only so that this fan-made tool can display save games of their game. This project is not
affiliated with or endorsed by Coffee Stain Studios. If you are the rights holder and want them removed, please
open an issue.
