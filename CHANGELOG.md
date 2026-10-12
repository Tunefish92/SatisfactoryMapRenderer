# Changelog

## Unreleased

* 3D view: the planes the game draws smoke, fog and other effects on are no longer part of the models. The
  Fuel-Powered Generator and the Nuclear Power Plant showed one as a grey slab 95 m high.
* Server tab: when the save game comes from a dedicated server's API, the side panel shows the server's
  health, game state (session, players, tech tier, phase, milestone, play time, tick rate) and options. Admins
  also get a console for server commands and buttons that shut the server down.
* README: how to get the dedicated server's API token.

## 0.2.2 – 2026-10-12

* Sharp base maps out of the box: the ground map and the relief map are bundled at 32768 px (about 23 cm per
  pixel) instead of 8192 px. In the Docker image they come cut into tiles, so a render has nothing to do for
  them and the browser just loads them. The image grows by about 300 MB.
* After an update, the page shows once per browser what is new: every version since the one seen last, taken
  from this changelog. It also says whether the map still has to be rendered again to show it.

## 0.2.1 – 2026-10-12

* After an update, a map that an older version rendered is rendered again at start from the save kept in the
  cache, so that the new base maps and the 3D view appear without loading the save again.
* Docker: game data in the data folder that an older version exported is no longer used in place of the
  bundled data (it has no ground map and no 3D models); with the game mounted at `/game` it is exported again.

## 0.2.0 – 2026-10-12

### 3D view

* New: the whole world as a 3D landscape, opened with the **3D view** button beside the zoom buttons. The
  terrain is built from the game's height data, calibrated with the game's landscape.
* Realistic ground: the terrain carries the game's own ground textures (sand, grass, forest, gravel, cliff, …)
  where the game's landscape has them painted, and the sea, lakes and rivers from the game's water surfaces.
  "Realistic ground" switches back to the in-game map.
* The factory in the game's own models and textures: about 590 building types, with the standard paint
  colours. Conveyor belts, pipes, hypertubes and railway tracks are laid along their paths piece by piece,
  conveyor lifts are built from their end and middle pieces, and machines show their arms and other moving
  parts in the pose they rest in.
* The resource nodes of the world (ore rocks, coal, quartz, SAM, sulfur, limestone, fracking vents) and the
  players as models.
* Click a building or a resource node for the same details as on the 2D map.
* Drag to rotate, wheel-button drag or Shift+drag to move, wheel to zoom towards the mouse. Every layer can be
  switched off.

### Maps

* Ground map: a new base map of the 2D view and its default, the world from above without any factory, made
  from the game's ground textures, water surfaces and heights. Bundled at 8192 px; `--render-ground-map` draws
  it at any size, and `maps/ground-map.jpg` replaces the bundled one.
* Relief map: a further base map, the in-game map with the terrain shaded from the game's height data.
* Removed: the "Realistic map" base map and the `--realistic` option; the ground map takes its place.

### Other

* Nicer admin login and logout: a login card with show/hide for the password and a signed-in bar with a
  Log out button.
* The bundled game data grew to about 130 MB (models, textures, ground data), and the Docker image with it.

## 0.1.0 – 2026-10-10

First public version.

### The map

* The whole factory on a zoomable map: foundations and walls, buildings coloured by category with their game
  icons when zoomed in, conveyor belts by tier, pipes and hypertubes, railways and the power grid. Tiles are
  rendered down to about 6 cm per pixel.
* Markers with details: players with their account names, vehicles and trains, creatures and creature spawners,
  landmarks (HUB, Space Elevator, M.A.M., AWESOME Sink), named train, truck and drone stations, beacons and map
  markers, resource nodes with purity and whether they are in use, wells and geysers, resource deposits, power
  slugs, somersloops and mercer spheres, crash sites, berries, nuts and mushrooms, dropped items and hazards.
* Click a building or marker for its details: recipe, clock speed, production boost, storage contents.
* The in-game map as background, with the fog of war as an optional layer. A second base map (for example a
  realistic one) can be added as an image and chosen in a dropdown.
* Markers grow with the zoom level, so they stay readable close up without cluttering the overview.

### The page

* Side panel with four tabs: *Layers* (search, layer groups that fold, show how many layers are on and switch a
  whole group at once, legends and filters), *Save game*, *Statistics* (object counts with filter) and
  *Settings*.
* Status bar on the map: session and save name, when the save was written and where it came from, with a
  refresh button for admins.
* Search for players, stations, resources and items; shareable links to a position and zoom level.
* Light and dark theme, following the system setting by default.
* A placeholder page with instructions while no save game has been loaded yet.

### Save game sources

* Browse for a `.sav` file or drop it anywhere on the page.
* Dedicated server (game version 1.0 or newer) through its HTTPS API with an API token: optionally saves the
  running game first, with or without certificate check. *Check version* shows the server's name, build and game
  version and whether it is supported.
* SSH to the machine that holds the saves, taking a save file or the newest save in a folder.
* *Refresh snapshot* fetches and renders the save again. Connection settings are stored as soon as they are
  entered.
* A progress window in the middle of the page shows each step of fetching and rendering, and the reason if
  something fails.

### Access

* Admin login: only admins can choose or refresh the save game; visitors just see the map and learn nothing about
  where the save comes from beyond its kind.
* The admin password may contain any characters. Five wrong passwords block the login for a minute.
* The server rejects requests from other web pages.

### Save games

* Reads every compressed save format from Update 5 (save version 21) up to 1.2.4 (save version 60): the single
  body of Update 5, the leveled bodies of Update 6 to 8 and 1.x, UE 5.4 property tags and lightweight buildables.
* Everything is decoded; what cannot be decoded is kept as raw bytes and counted, never silently skipped.

### Docker

* One image for amd64 and arm64, on GHCR and Docker Hub, with a template for Unraid.
* Works out of the box: the game's icons, names, level data and in-game map (Satisfactory 1.2.4) are part of the
  image. A mounted Satisfactory installation can replace them with newer ones.
* Runs as an unprivileged user that owns the data folder (`PUID`/`PGID`), with a health check.
* The repository's compose file takes the admin password from `admin.env`, passed on unchanged, so `$` and other
  special characters need no escaping.
* The page tells admins when a newer release is out, optionally including pre-releases.
