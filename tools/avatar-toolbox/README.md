# Avatar Toolbox

A Windows PowerShell 5.1 / WPF tool launcher and status dashboard by Casey Shingledecker.

## Current edition — 2026-10-20

- OCR Boxes is the first tool, using a portable per-user installation path and manual start.
- Charcoal panels and cream text coordinate with Kokoro Player.
- Start/stop controls, read-only status checks, optional repair helpers and avatar selection.
- The public edition uses an original neutral vector mascot when no avatar artwork is installed. Personal character art, settings, reports and voice models are excluded.

Run `powershell.exe -NoProfile -STA -File Avatar-Toolbox.ps1`. Nothing is started automatically in the supplied configuration. Edit `tools.json` to use your own tools. Repair operations may change Windows settings; review each action and its confirmation before invoking it.

The generated script is built from `source/hub.template.ps1` and `source/repair.template.ps1` using `python source/build_toolbox.py`. Edit templates, then rebuild. The OCR tool path resolves to the copy bundled with Desktop Pixel Companions; change it if you installed OCR Boxes elsewhere.

## Validation and scope

The local edition passed PowerShell parsing and six file-presence checks, with five informational status results and no failures. Public neutral-mascot snapshot verification is recorded separately in the release notes. These checks do not validate every repair action. No repair, service restart or audio playback was performed as part of release preparation.

Original project source is published for inspection; no additional source license or third-party character-art rights are granted by this repository.
