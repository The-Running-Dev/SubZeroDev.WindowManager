# Sample looks

Two presets ship with the app to show how far a theme can move the same desktop. Both use the layout `subzerodev.sample`: one bottom panel with the launcher at the start, the window list in the centre and the clock at the end. Switching between them restyles the panel in place. No plugin restarts and no plugin setting changes.

To switch, pick the preset in **Settings → Appearance**, or set `activePreset` in `settings.json`.

| | `subzerodev.sample-glass` | `subzerodev.sample-retro` |
| --- | --- | --- |
| Panel shape | Floating rounded pill, inset from the screen edge | Square, edge to edge, bevelled |
| Backdrop | Acrylic | None (opaque) |
| Font | Segoe UI Variable Display | Tahoma |
| Accent | `#5CE1E6` cyan | `#000080` navy |
| Button states | Eased fades (80–400 ms) | Instant, with a pushed-in bevel |
| Icons | `subzerodev.sample-glass`: smooth outline | `subzerodev.sample-retro`: pixel art |

Each preset is four files, and a theme author can copy them as a starting point:

- `content/themes/<id>/`: `theme.json` (tokens, backdrop) and `Panel.xaml` (styles)
- `content/palettes/<id>.json`: the colours that override the theme's tokens
- `content/assetpacks/<id>/`: `assetpack.json` and the icon images
- `content/presets/<id>.json`: ties layout, theme, palette and asset pack together

## Screenshots

Take both screenshots with the same layout and the same open windows.

### Glass

_Screenshot to be added: `docs/images/sample-glass.png`._

### Retro

_Screenshot to be added: `docs/images/sample-retro.png`._
