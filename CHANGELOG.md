# Changelog

## 0.1.0

First public version.

* Interactive map of a Satisfactory save game: factory layers, players, vehicles, creatures, resources,
  collectibles, with search, click-to-inspect, statistics and light/dark theme.
* Save games from a file (browse, drag and drop), a dedicated server's HTTPS API, or SSH; refresh with one click.
* Admin login; visitors only see the map.
* Works out of the box: the game's icons, names, level data and in-game map (Satisfactory 1.2.4) are part of the
  image. A mounted Satisfactory installation can replace them with newer ones.
* Reads saves from Update 5 up to 1.2.4.
* Docker image for amd64 and arm64, Unraid template.
* The compose file takes the admin password from `admin.env`, passed on unchanged, so it may contain any
  character including `$`.
