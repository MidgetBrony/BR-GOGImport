# BR-GOGImport v0.1.1 Experimental

- Register imported GOG custom IDs in BOXROOM's `owned_games.json` so the games appear in the library.
- Preserve all existing Steam and custom IDs while merging GOG entries.
- Write the updated index atomically to avoid leaving a partial file.
- Retain batched metadata, compact progress, cache discovery, launch metadata, and the Experimental warning.

Restart BOXROOM after importing so it reloads the updated library index.
