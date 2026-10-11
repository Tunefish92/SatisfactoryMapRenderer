# Changelog

## Unreleased

* 3D view: the world as a landscape built from the game's height data, with the in-game map on it and the
  factory in the game's own 3D models and textures (548 building types), belts, pipes and rails at their real
  heights, and labels for players, landmarks and stations. Opened with the 3D button on the map.
* 3D view, realistic ground: the terrain carries the game's own ground textures (sand, grass, forest, gravel,
  cliff, …), placed with the game's landscape data, and the sea, lakes and rivers from the game's water
  surfaces. "Realistic ground" in the 3D view switches back to the in-game map.
* 3D view: terrain heights calibrated with the game's landscape (they were up to 5 m too low on high ground);
  the wheel zooms towards the mouse, the wheel button moves the view, the right button turns it.
* Relief map: a second base map, the in-game map with the terrain shaded from the game's height data. It is
  made during the game data export and chosen in the *Map* dropdown.
* Nicer admin login and logout: a login card with show/hide for the password and a signed-in bar with a
  Log out button.

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
