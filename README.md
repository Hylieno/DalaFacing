# DalaFacing

Dalamud plugin for Final Fantasy XIV that draws a perspective-correct volumetric world-space arrow above one selected battle NPC.

Current stable baseline: **v0.2.0**.

## Branches

- `dev` — active development and testing.
- `release` — stable code intended for releases.
- `main` — bootstrap/default branch; development should happen on `dev` and stable promotions should target `release`.

## Features

- Attach the arrow to the current target with the UI or `/dalafacing select`.
- The arrow stays attached when the player's target changes.
- Configurable height measured from the actor's ground position.
- True 3D geometry: an extruded shaft and arrowhead with shaded vertical faces.
- Configurable arrow length, width, 3D thickness, fill colour, opacity, and outline.
- Remove the selection with the UI or `/dalafacing clear`.

The selection refers to the current in-instance object, not every NPC sharing its name. It naturally becomes unavailable when that object leaves the instance.

## Development flow

1. Make changes on `dev`.
2. Push or open a PR against `dev`; CI builds the plugin and refreshes the `dev-latest` prerelease.
3. Test the development build in game.
4. Run the **Promote dev to release** workflow after validation.
5. Merge the generated PR into `release`; the declared version is then published as a GitHub Release.

Always bump `<Version>` in `DalaFacing.csproj` before promoting a new stable version.

## Build

Requires the .NET 10 SDK and the current Dalamud development environment.

```powershell
dotnet build -c Release
```

The packaged plugin is produced in `bin/Release/DalaFacing/latest/`.

## Disclaimer

This is an unofficial third-party FFXIV plugin. Use of third-party tools is governed by Square Enix's rules and policies.
