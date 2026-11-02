# Project Integration References

This folder contains context for reading the personal implementation excerpts, not a standalone project or an automated migration package.

- `PJH_RuntimeHooks.patch` records only changes from merged PJH pull requests in shared controllers, PlayerState, AI/controller reset hooks, recent-attacker reset hooks and project configuration. It does not claim the surrounding team classes as personal implementations.
- `ShootingArena.Build.cs` is the previously included module reference. Restore the original project structure and its dependencies before building.
- `PackageAll.bat` is the PJH packaging-script snapshot. In the original project it lives beside `ShootingArena.uproject`; its engine/output paths are project-local examples. Do not run this excerpt directly from this folder. It packages the client/server and copies the data-editing Manifest alongside the output.
- PathLink remains the frozen portfolio version. Validator bridge additions may reference APIs of the original project that are intentionally not imported into the frozen excerpt.
- Outline and nickname source, settings JSON and their Manifest entries are deferred for this refresh.

`JsonAssetSync/Examples` contains selected committed JSON exports and settings examples. They depend on the original game assets and are not a complete, runnable game-data distribution.
