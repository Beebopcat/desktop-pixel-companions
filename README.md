# Desktop Pixel Companions — source edition

A Windows WPF desktop companion and accessibility project by Casey Shingledecker.

This public source snapshot separates the application code from the author's personal character artwork, voice models, settings and reports. It includes the current OCR Boxes source and the neutral public Avatar Toolbox as companion tools. The author's installed personal edition retains its existing artwork and voice setup.

## Current work

OCR Boxes supports ordinary windows and displays, CPU RapidOCR, a Kokoro-matched dark interface and provider-specific voices for each box. The companion launchers target bundled tools first, with per-user installed locations as a fallback. The voice daemon remains separately managed.

## Source/build scope

The application source targets .NET 10 and Windows. Build with `dotnet build app/AzerothCompanion.csproj -c Release`. This is a source distribution, not a complete ready-to-run companion-art release: original licensed artwork and the required catalog data must be supplied for the visual companion features. The OCR Boxes and neutral Toolbox components can be run separately according to their READMEs. No runnable WPF release or animation-accuracy claim is made for the asset-free source snapshot.

The installed Desktop Pixel Companions folder was updated with the newest OCR Boxes; existing desktop shortcuts and personal settings were preserved.

## Rights

Original project source is published for inspection; no additional license is granted. No Warcraft, Arcane, Star Wars or other third-party character assets, voice recordings, model weights or personal catalogs are included. Bundled OCR dependencies retain their own notices.
