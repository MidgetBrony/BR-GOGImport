# BR-GOGImport

Experimental GOG library importer for BOXROOM.

It signs in to GOG through your browser, reads the games owned by your account, retrieves BOXROOM metadata in batches from the Boxroom Studio API, and writes custom game entries into BOXROOM's `steam_cache_v2` folder.

> [!WARNING]
> This tool is experimental. It may break, miss games, or change how imported games are stored.

## Sign-in

When GOG sign-in finishes, copy the **full redirect URL** from the browser's address bar and paste the complete URL into BR-GOGImport. The importer stores the refreshed GOG token locally in its own `GOG` folder.

## BOXROOM cache

On Windows, BR-GOGImport uses BOXROOM's standard cache location under `AppData/LocalLow/NestedLoop/BOXROOM/steam_cache_v2`.

On Linux, it finds `owned_games.json` in native Unity, Steam, custom Steam-library, and Flatpak Steam locations. Set `BOXROOM_CACHE_PATH` to a specific `steam_cache_v2` folder if automatic discovery cannot find it.

## GOG launching

Imported games use `goggalaxy://openGameView/<product-id>`. FamilyOwned 2.2.0 or newer converts that address into GOG Galaxy's direct `runGame` command when Galaxy is installed.

## Current limitations

- GOG Galaxy must be installed to launch imported games through Galaxy.
- Linux and Proton behavior still needs live Steam Deck testing.
- Metadata and artwork availability depend on GOG and the Boxroom Studio API.
