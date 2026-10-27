# Xtream IPTV

A plugin for Emby and Jellyfin that turns an Xtream Codes account into Live TV with a guide and a movie and series catalogue, the way an IPTV app does.

- Live TV: channels, logos and EPG from the Xtream account, with optional Dispatcharr integration (stream profiles, stream stats, codec declaration).
- Catalogue: movies and series written straight into the server's library database from the Xtream lists. No files on disk, no bulk metadata downloads: plot, cast, episodes and backdrops are fetched from the provider the first time a title is opened, and actor photos come from the server's own person providers in the background.
- Movie releases that differ only by quality or language tags become one title with a version picker.
- Category selection, excluded category terms, automatic inclusion of new provider categories, title cleaning, incremental daily sync with a removal safety threshold.

Dispatcharr is optional: the plugin talks to any Xtream server. Design and server internals are documented in [DESIGN.md](DESIGN.md).

## Layout

| Folder | Content |
|---|---|
| `XtreamIptv.Shared/` | Host-neutral code compiled into both plugins: Xtream client, Dispatcharr client, naming and merge rules, catalogue state, EPG parsers, configuration |
| `Emby.XtreamIptv/` | Emby host layer (tuner host, library writer, detail on open, API, config page) |
| `Jellyfin.Plugin.XtreamIptv/` | Jellyfin host layer (same roles, Jellyfin 12 API) |
| `*.Tests/` | xunit tests; the Emby suite covers the shared code |
| `lib/` | Emby SDK assemblies (4.9 and 4.10) |

Builds: `./build-emby.sh` and `./build-jellyfin.sh` (local .NET 10 SDK, or the SDK container when absent). A push to the `release` branch publishes the version in `VERSION` as a GitHub release and updates the Jellyfin repository manifest.

## Emby

Tested up to Emby 4.10.2 (also runs on 4.9).

1. Download `Emby.XtreamIptv.dll` from the [latest release](../../releases/latest).
2. Copy it into Emby's `plugins/` folder and restart the server.
3. Open **Xtream IPTV** in the server menu: enter the Xtream server, user and password (Connection tab), pick the Live TV categories, then on the Catalogue tab choose the movie and series categories.
4. Add two libraries in Emby, Movies and TV shows, pointing at `<Library path>/Movies` and `<Library path>/Shows` (the path shown on the Catalogue tab), with every metadata and image fetcher disabled.
5. Press **Sync now**. The catalogue appears within minutes; it refreshes itself daily.

Update: replace the DLL with the one from the new release and restart.

## Jellyfin

Tested up to Jellyfin 12.4.0 (requires 12.4 or newer).

1. Dashboard, Plugins, Repositories: add a repository with this URL

   ```
   https://raw.githubusercontent.com/tommasomeli/xtream-iptv/release/manifest.json
   ```

2. Catalog: install **Xtream IPTV** and restart the server. Updates arrive through the same catalog.
3. Dashboard, **Xtream IPTV**: enter the Xtream server, user and password (Connection tab), pick the categories.
4. Catalogue tab: **Create libraries** makes the "Film" and "Serie" libraries with the right options. Live TV tab: **Register tuner and guide** adds the tuner and the guide provider.
5. Press **Sync now**.

Manual install: download the Jellyfin zip from the [latest release](../../releases/latest), unpack it into `plugins/XtreamIptv_<version>/` inside Jellyfin's data folder and restart.
