# Game data

Icons, item and building names, level data (resource nodes, collectibles, spawners), the game's logo, the
in-game map, the world's height data, a relief map made from both and simplified 3D models of the buildables with reduced textures, the ground textures with the map of where they lie, a ground map drawn from them,
exported from **Satisfactory 1.2.4.1** (build 513885) with

```
dotnet run --project src/SatisfactoryMapRenderer -c Release -- --export-game-data --game <Satisfactory folder>
```

The Docker image ships this folder so the map works without a game installation.

**These files are not covered by this repository's MIT licence.** They are the property of Coffee Stain Studios
and are included here only so that this fan-made tool can display save games of their game. This project is not
affiliated with or endorsed by Coffee Stain Studios. If you are the rights holder and want them removed, please
open an issue.

`ground-map.jpg` and `relief.jpg` are kept here at 32768 px: the ground map drawn with `--render-ground-map`,
the relief map upscaled from the export's 8192 px. An export does not replace them with its smaller ones. The
Docker build cuts them into tiles (`--export-base-maps`).
