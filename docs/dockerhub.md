# Satisfactory Map Renderer

**An interactive map of your Satisfactory save game, straight from a file or a running dedicated server.**

The whole factory on a zoomable map: foundations, buildings, belts, pipes, railways and power, plus
players, vehicles, resource nodes with purity, collectibles and more, with search and
click-to-inspect. One container renders the map and serves it.

Source, full documentation and issues: **https://github.com/Tunefish92/SatisfactoryMapRenderer**

## Quick start with Docker Compose

Save this as `docker-compose.yml`, set a password, and run `docker compose up -d`:

```yaml
services:
  map:
    image: tunefish92/satisfactorymaprenderer:latest
    restart: unless-stopped
    environment:
      SMR_ADMIN_PASSWORD: change-me-please
    ports:
      - "8080:8080"
    volumes:
      - ./map-data:/data
      # Optional, read-only: your Satisfactory installation, for the game's icons and map
      # - /path/to/Satisfactory:/game:ro
```

Open `http://localhost:8080/`, click **Admin login** in the *Save game* tab of the side panel, and choose the
save game:

* **Browse / drag and drop** a `.sav` file.
* **Dedicated server (HTTPS API)**: host, port and an API token (run `server.GenerateAPIToken` in the
  server console). Needs a server running game version 1.0 or newer; "Check version" tells you
  whether yours is compatible.
* **SSH**: `user@host` and the path of the save file or save folder. Put the private key into
  `map-data/home/.ssh/`.

For the server and SSH sources a **Refresh snapshot** button fetches and renders the current state
again. Visitors who are not logged in only see the map.

## Game icons and the in-game map

The image contains no game files. Mount your Satisfactory installation at `/game` (read-only): on the
first start the icons, item names, resource purities and the in-game map are exported into the data
folder, and the mount can be removed again. Without it the map still works, with plain markers on
the explored-area outline.

## Settings

| Variable | Default | Meaning |
|---|---|---|
| `SMR_ADMIN_PASSWORD` | – | **Required**, at least 8 characters. Protects the save game source. |
| `SMR_MAX_ZOOM` | `9` | Highest rendered zoom level (3–9). Each level less renders about four times faster. |
| `PUID` / `PGID` | `1000` | Owner of the data folder; the app runs as this user. |

| Path | Meaning |
|---|---|
| `/data` | Rendered map, exported game data, settings (including the server's API token). |
| `/game` | Optional, read-only: a Satisfactory installation. |

The container speaks plain HTTP on port 8080. To reach it from the internet, put it behind a reverse
proxy that adds HTTPS, so the admin password is not sent unencrypted.

Reads saves from Update 5 up to 1.2.4. Not affiliated with Coffee Stain Studios.
