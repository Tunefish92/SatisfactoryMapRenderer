#!/bin/sh
# Starts the map server as an unprivileged user that owns the data folder.
#
# A host folder bind-mounted to /data is often owned by root (Docker creates missing folders as
# root) or by the host's own user (Unraid's appdata is 99:100). Started as root, this script hands
# the folder to PUID:PGID (default 1000:1000) and then drops to that user. Started as non-root
# (docker run --user, rootless setups), it runs the app as-is and the folder must already be
# writable by that user.
#
# Layout of the data folder:
#   map/        the rendered map that is served
#   gamedata/   your own export of the game's icons and level data (optional; replaces the bundled one)
#   maps/       game-map.jpg and realistic-map.jpg (optional)
#   cache/      save game source settings, fetched saves
#   home/       home of the app user; home/.ssh holds the keys for the SSH source
set -e

DATA_PATH="${SMR_DATA_PATH:-/data}"

if [ -z "$SMR_ADMIN_PASSWORD" ]; then
    echo "Satisfactory Map Renderer: SMR_ADMIN_PASSWORD must be set (at least 8 characters)." >&2
    echo "It protects the panel that chooses the save game; visitors only see the map." >&2
    exit 1
fi

run_app() {
    # The defaults for --maps and --game-data are relative to the working directory.
    cd "$DATA_PATH"
    export HOME="$DATA_PATH/home"

    # The image brings the game's icons, names, level data and in-game map (/app/gamedata), so the
    # map works out of the box. A game folder mounted at /game is exported once into the data
    # folder, e.g. after a game update that the image does not know yet; an export in the data
    # folder always wins over the bundled one.
    if [ -d /game/FactoryGame ] && [ ! -f "$DATA_PATH/gamedata/gamedata.json" ]; then
        echo "Exporting game data from /game (one-time)..."
        "$@" --export-game-data --game /game --game-data "$DATA_PATH/gamedata" --cache "$DATA_PATH/cache" \
            || echo "Could not export the game data; using the bundled game data." >&2
    fi
    if [ -f "$DATA_PATH/gamedata/gamedata.json" ]; then
        GAME_DATA="$DATA_PATH/gamedata"
    else
        GAME_DATA=/app/gamedata
    fi

    exec "$@" --serve 8080 --bind '*' -o "$DATA_PATH/map" --cache "$DATA_PATH/cache" --game-data "$GAME_DATA" \
        -z "${SMR_MAX_ZOOM:-9}" $SMR_EXTRA_ARGS
}

if [ "$(id -u)" = "0" ]; then
    PUID="${PUID:-1000}"
    PGID="${PGID:-1000}"

    mkdir -p "$DATA_PATH/home"

    # Only walk the whole tree when the top folder isn't ours yet (first start, or PUID changed);
    # everything the app creates afterwards already has the right owner.
    if [ "$(stat -c '%u:%g' "$DATA_PATH")" != "$PUID:$PGID" ]; then
        echo "Setting owner of $DATA_PATH to $PUID:$PGID"
        chown -R "$PUID:$PGID" "$DATA_PATH"
    fi
    chown "$PUID:$PGID" "$DATA_PATH/home"

    run_app setpriv --reuid="$PUID" --regid="$PGID" --clear-groups dotnet /app/SatisfactoryMapRenderer.dll
fi

mkdir -p "$DATA_PATH/home"
run_app dotnet /app/SatisfactoryMapRenderer.dll
