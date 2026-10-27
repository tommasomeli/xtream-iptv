# Xtream IPTV for Emby: design

One Emby plugin that serves an Xtream account the way an IPTV player does: Live TV with guide, and a movie and series catalogue whose metadata comes from the Xtream lists, with the remaining detail asked on demand when an item is opened. Nothing is downloaded in bulk from third-party metadata sites. Emby's own metadata and image fetchers stay available for the user's other, local libraries: the plugin's libraries are configured with no fetchers at all.

The project started as a trimmed copy of firestaerter3/emby-xtream (Xtream Tuner v1.4.111). Live TV is kept; the STRM sync, TMDB lookups, update checker, folder mapping, per-item exclusions and the dashboard are gone.

## Identity

- Plugin name `Xtream IPTV`, assembly `Emby.XtreamIptv.dll`, namespace `Emby.XtreamIptv`.
- Plugin GUID `c02587d5-61e0-415a-a2d5-3ccd3107d112` (new; the old one must not be reused).
- Config pages: html `xtreamiptv`, js `xtreamiptvjs`, menu icon `live_tv`, alias page `XtreamIPTV` (Emby derives it from the name without spaces).
- Tuner host type string stays `xtream-tuner`, so an existing tuner keeps working.
- No image provider: poster and backdrop URLs are stored on the items and Emby downloads them on first view.
- API prefix `/XtreamIptv/...` (was `/XtreamTuner/...`).
- Target: netstandard2.0, C# 7.3, built against `lib/emby4_9` (the single DLL also runs on 4.10, see the old ADR-017). No new NuGet packages. The shared folder must stay C# 7.3 compatible because of this target.

## Emby facts the design rests on (verified in decompiled 4.10.2)

- Emby shows only what is in its database. Live TV channels come from the tuner host; movies and series must exist as library items. The Channels plugin API in 4.9/4.10 is a background crawl that persists everything and queues internet metadata for every item, so it is not used.
- `GET /Users/{UserId}/Items/{Id}` (the detail page in every client) is handled by the core `UserLibraryService`; a plugin route with the same path is silently ignored and there are no global request filters. The one point that runs on every authenticated request before the handler is the static `MediaBrowser.Controller.Net.AuthenticatedAttribute.AuthService` (exists in 4.9 and 4.10). A plugin can wrap it with a decorator; whatever the decorator writes into the item before returning is in that same response, because `DtoService` reads the item fresh.
- Remote image URLs are stored as image paths (`item.SetImage(new ItemImageInfo { Path = url, Type = ... }, 0)`, as the channel manager does) and downloaded only when a client asks for them (`LibraryManager.ConvertImageToLocal`). No image provider is needed.
- A library scan (`BaseItem.ValidateChildrenInternal`) removes a database child of a file-protocol folder only when the child has a non-empty Path that was not found on disk; children with an empty Path are kept and recursed into. Items the plugin writes under a library therefore survive scans as long as the folders it creates have no Path and the playable items use an http URL as Path.
- An item whose Path is an http URL gets its media source from `BaseItem.GetVersionInfo` (Protocol Http, Container = item.Container) and is probed by ffprobe on first play, exactly like a .strm; the client plays through the Emby server. Videos that share a `PresentationUniqueKey` are one entry with a version picker (`LibraryManager.MergeItems` does nothing else).
- Dispatcharr sits between Emby and the provider (plugin `BaseUrl` is the Dispatcharr Xtream output). Its `get_vod_streams` list already carries `plot, cast, director, genre, year, rating, tmdb_id, trailer` when it has them, `get_series` carries `plot, cast, director, genre, release_date, rating, backdrop_path, tmdb_id, last_modified, episode_run_time`, and `get_vod_info` / `get_series_info` fetch missing detail from the provider on demand and cache it for 24 h. Measured: 60 205 movies in 4 s, 7 983 series in 1 s, one detail call 0 to 0.5 s.

## Layout and ownership

```
XtreamIptv.Shared/               host-neutral, compiled into both plugins (namespaces XtreamIptv.*)
  PluginConfiguration.cs         final, do not restructure
  XtreamHost.cs                  configuration + HttpClient hooks each host sets at startup
  Logging/ILog.cs                the logging surface of the shared code (EmbyLog / JellyfinLog adapt it)
  Client/XtreamClient.cs         typed player_api calls shared by catalog code
  Client/XtreamUrls.cs           URL builders
  Client/DispatcharrClient.cs    Dispatcharr REST (live parts)
  Client/Models/*                JSON models + tolerant converters
  Catalog/CatalogNaming.cs       title cleaning, years, version grouping, pure logic
  Catalog/CatalogState.cs        JSON state in the plugin configuration folder
  LiveTv/XmltvParser.cs, ChannelNameCleaner.cs, StreamProfileCodec.cs
  Service/XtreamConnectionTester.cs, XtreamRateLimitHandler.cs
Emby.XtreamIptv/                 the Emby host layer
  Plugin.cs, EmbyLog.cs          identity, pages, hooks
  LiveTv/*                       tuner host, live service, live stream
  Catalog/CatalogSyncService.cs  list-only incremental sync into Emby's database
  Catalog/EmbyCatalog.cs         the ILibraryManager layer: roots, items, people, images
  Catalog/DetailOnOpenService.cs movie detail and series episodes on demand
  Catalog/DetailOnOpenEntryPoint.cs IServerEntryPoint wrapping AuthService
  Catalog/CatalogSyncTask.cs     IScheduledTask "Sync Xtream catalogue"
  Catalog/CatalogServices.cs     shared lazy instances (client, library, state, services)
  Catalog/XtreamVersionSourceProvider.cs, EmbyCompat.cs
  Api/XtreamApi.cs               connection + Live TV routes
  Api/CatalogApi.cs              catalogue routes, separate IService class
  Configuration/Web/config.html, config.js
Emby.XtreamIptv.Tests/           xunit (covers the shared code too)
Jellyfin.Plugin.XtreamIptv/      the Jellyfin host layer (see the Jellyfin section at the end)
Jellyfin.Plugin.XtreamIptv.Tests/
```

Agents work concurrently on disjoint files. A build error in a file you do not own is someone else's in-progress work: ignore it and keep going; only errors in your own files count. The final build is done by the coordinator.

## Catalogue: how it works

No file is written: the catalogue lives in Emby's library database, like the channel list of an IPTV app. Libraries: `<LibraryPath>/Movies` (Emby library type Movies, name "Film") and `<LibraryPath>/Shows` (TV shows, "Serie"), two empty folders that exist only so that Emby has libraries to hang the items under. Both have metadata fetchers and image fetchers empty.

Layout in the database (`EmbyCatalog`): the physical folder item of the library (the Emby item whose Path is the location on disk, found through `CollectionFolder.GetPhysicalFolderItems`) -> one plugin `Folder` named "Xtream" with Path null and `ProviderIds["xtream"]` = `xtream-movies` / `xtream-shows` (created once with `ILibraryManager.CreateItem(folder, parent, collectionFolders)`, found again by that provider id) -> the items. Movies: `Movie` items with Path = the stream URL (`XtreamUrls.Stream(config, "movie", id, ext)`), `Container` = `container_extension`. Series: `Series` (Path null) -> `Season` (Path null, `IndexNumber`) -> `Episode` (Path = stream URL with kind `series`). Every item carries `ProviderIds["xtream"]` = the provider id, which is how the sync finds it again; Path-less items also get an `ExternalId` so Emby does not treat them as virtual.

Ids are stable: `Id = libraryManager.GetNewItemId("xtream-movie-<stream_id>", typeof(Movie))` (same for series, season, episode), so a re-created item keeps its Emby id. Item fields: Name, Overview, `SetGenres`, CommunityRating, ProductionYear, PremiereDate, DateCreated (from the list `added` or `last_modified` unix time, so "Latest" works), DateModified, ProviderIds Tmdb + xtream, Primary image = `stream_icon` / `cover`, Backdrop = first `backdrop_path`, `IsVirtualItem = false`, `SetParent(parent)`. People: `ILibraryManager.UpdatePeople(item, people, isNewItem)` with `PersonInfo { Name, Type = PersonType.Actor | Director }`, written only by detail on open (one `UpdatePeople` per item is what bound the first sync to 21 minutes; without it the run is bound by `CreateItems` alone). Writes: `CreateItems(batch of 500, parent, null, collectionFolders, triggerItemAdded: true, ct)`, `UpdateItem(item, parent, ItemUpdateType.MetadataDownload)`, `DeleteItem(item, new DeleteOptions { DeleteFileLocation = false }, parent, false)`.

### Sync (scheduled task, default daily 04:30, also "Sync now" from the config page)

Movies:
1. `get_vod_categories`, keep `SelectedVodCategoryIds` (empty = all). `get_vod_streams` with no category (one call for the whole account), keep rows whose `category_id` is selected, drop `is_adult = 1` and categories whose name contains adult words unless `IncludeAdultContent`, and always drop categories whose name contains one of `ExcludedCategoryTerms` (comma-separated, case-insensitive; the same list applies to series). Dedupe by `stream_id`.
2. Title cleaning: strip quality tags (`FHD, UHD, 4K, HDR, HEVC, H.265, 2160p, 1080p, 720p, HD, SD`, in brackets or bare), the user's `ContentRemoveTerms`, repeated spaces and dangling separators. Year = the first `(YYYY)` anywhere in the name (removed from the title), else the `year` field.
   Language markers at the end of the name (`ITA`, `ENG`, `SUB ITA`, `MULTI`, `DUAL`, `EN`, `IT` and the like, built-in list, end of name only) are stripped too and kept as part of the version label.
3. `MergeMovieVersions`: rows with the same cleaned title and year become one movie (the key is case-insensitive and ignores punctuation). A release without a year joins the one dated release with the same title; with two or more dated releases of that title (remakes) it stays apart. A version is named `<Title> - <quality + language label>`, or the container when the release carries no tag. One `Movie` item per movie, built from the primary (untagged) release. The other releases are stored on the item as `ProviderIds["xtream-versions"] = "id.ext:label|..."` (`CatalogNaming.VersionsString` / `ParseVersions`) and `XtreamVersionSourceProvider` (`IMediaSourceProvider`, discovered by Emby) turns them into extra media sources named `<Title> - <label>` whenever Emby asks for the item's playback sources, so the version picker shows them and nothing else in the library is duplicated. The source objects are cached per release so the stream info Emby probes at the first play stays with them; name and URL follow the item and the current connection on every call. (Earlier versions created one item per release grouped by `PresentationUniqueKey`; Emby picks an arbitrary member as the group's face, which showed the "ITA EN" item instead of the primary.)
4. Incremental: `CatalogState` keeps per `stream_id` a signature (cleaned name, year, category, `added`, hash of the metadata fields, the version set, the stream base = hash of server + user + password) and the Emby internal ids. The existing items are read once (`GetItemList` on the plugin folder, indexed by the xtream provider id); only new or changed entries are created or updated (a changed row also leaves the detailed set, so the next open refetches plot and cast). Rows gone from the list are deleted, but never more than `RemovalSafetyThreshold` of the existing items in one run (with at least 10 existing), and nothing is removed when the list call failed or came back empty. No `get_vod_info` during the sync, ever.

Auto-include (`AutoIncludeNewCategories`, default on, one switch for movies and series): the state keeps the category ids seen by a sync (`KnownVodCategoryIds` / `KnownSeriesCategoryIds`, learned as a baseline on the first run without adding anything). When a selection is in place, a category the provider added since (not excluded) is added to `Selected*CategoryIds` and the picker cache, and the config is saved, so the new titles come in at that same sync. With the option off the known set is not updated, so switching it on later picks up everything that appeared meanwhile.

Series:
1. `get_series_categories`, `get_series` (all), filter like movies. Only `Series` items are written: name, year, plot, genres, rating, tmdb, poster, first backdrop, DateCreated from `last_modified`. No people.
2. `CatalogState` keeps per `series_id` the `last_modified`, the row signature (which includes the stream base), the Emby id, a `Stale` flag and the episode count. A new series starts stale; any change to the row (`last_modified` or signature) marks it stale again. No `get_series_info` during the sync: episodes are loaded when the series is opened.
3. Removal with the same safety threshold.

The whole run is bound by the database: a few minutes for 22k movies and 7k series on first run, seconds afterwards. Progress (phase, processed, total) and the last summary are exposed through the status route and saved in `LastCatalogSyncTimestamp` / `LastCatalogSyncSummary`. The sync refuses to touch a library whose Emby library does not exist (clear message in Status and Sync).

Connection change: `Plugin.UpdateConfiguration` compares the stream base before and after a save and starts a background sync when it differs, so every movie path is rewritten and every series is marked stale (its episodes are rewritten with the current URL at the next open). Dispatcharr is optional: `BaseUrl` can be the provider itself.

### Detail on open

`DetailOnOpenEntryPoint` (`IServerEntryPoint`) replaces `AuthenticatedAttribute.AuthService` with a decorator at startup and restores it on dispose. The decorator calls the inner `Authenticate` first, then, when `DetailOnOpen` is enabled and the verb is GET, it takes the item id from `request.PathInfo` matching `^/(emby/|mediabrowser/)?Users/[^/]+/Items/([^/?]+)/?$`, `^/(emby/|mediabrowser/)?Shows/([^/?]+)/(Seasons|Episodes)/?$`, or the `ParentId` query parameter of `^/(emby/|mediabrowser/)?(Users/[^/]+/)?Items/?$` (all case-insensitive), resolves it with `ILibraryManager.GetItemById` (long, else Guid) and calls `DetailOnOpenService.EnsureAsync(item)` synchronously with a cap of `DetailWaitSeconds` (default 8, 0 to 30). Past the cap the response goes out as it is and `LateDetailNotifier` continues on the fetch: when it lands, it sends Emby's own `LibraryChanged { ItemsUpdated = [item, its seasons] }` websocket message (`ISessionManager.SendMessageToUserSessions`, ids from `BaseItem.GetClientId()`) to every user session at once, instead of the 40 s batch of Emby's `LibraryChangedNotifier`; the web client's item page reloads itself on that message (`item.js`: `data.ItemsUpdated.includes(currentItem.Id)` -> `refreshData()`), so the user never has to close and reopen. One notification per item however many requests waited. Everything is wrapped in try/catch and logged at debug level: a failure must never break the request.

`EnsureAsync(item)`:
- `Movie` with an xtream provider id not yet in the state's detailed set (one in-flight fetch per item, negative results remembered for 1 hour): `get_vod_info?vod_id=<stream id>` -> Overview, people (`UpdatePeople`, replace: this is the only place people are written), genres, CommunityRating, PremiereDate, ProductionYear, RunTimeTicks from `duration_secs`, ProductionLocations from `country`, Backdrop image, Tmdb id -> `UpdateItem`, then the id is marked detailed. The response that follows carries plot, people, genres and the backdrop.
- `Series` (or the series of a `Season`) that is stale or has no episodes yet: `get_series_info` -> the series' people from the info object (`UpdatePeople`, replace), plot, backdrop and genres it still lacks, then create or update `Season` items (name "Season N" / "Specials", IndexNumber, `SeriesId`, `SeriesName`, `SeriesPresentationUniqueKey` = the series key) and `Episode` items (Name with a repeated "Name - SNNEMM" prefix removed, IndexNumber, ParentIndexNumber, Overview, PremiereDate from `releasedate`, RunTimeTicks from `duration_secs`, CommunityRating, Primary image `movie_image`, Path = series stream URL, Container, `AlbumId` = the season, `PresentationUniqueKey` = `<series key>-SSS - EEEE` as Emby computes it), delete episodes and seasons that vanished, clear the stale flag. One call of 0 to 0.5 s, well within the cap.

### Actor photos on open

Xtream carries no actor photos. With `ActorPhotosOnOpen` (default on), once a title's detail is written (or at once when it already is), `DetailOnOpenService.SchedulePeoplePhotos` runs in the background: `ILibraryManager.GetItemPeople(item)`, the `Person` items without a Primary image and not refreshed in the last day (`NeedsPhoto`), at most 15 per title, refreshed with `person.RefreshMetadata(MetadataRefreshOptions { FullRefresh, IsAutomated = false })` (only a non-automated refresh lets the person providers search by name) three at a time (one global gate), which lets Emby's own person providers (TheMovieDb) resolve the name and download the photo and biography. When at least one photo landed, `LateDetailNotifier.NotifyNow(item)` sends `LibraryChanged` so the open page refreshes. One run per title per hour. The request never waits for this. Person items are global, so a photo fetched for one title serves every other.

## API contract (all routes `[Authenticated(Roles = "Admin")]`)

Connection and Live TV (`Api/XtreamApi.cs`, ported from the old file, same behaviour, new prefix):
- `GET  /XtreamIptv/Epg` XMLTV for the tuner
- `GET  /XtreamIptv/LiveTv` M3U for the tuner
- `GET  /XtreamIptv/Categories/Live` -> `List<Category>`
- `POST /XtreamIptv/RefreshCache`
- `POST /XtreamIptv/TestConnection` body `{BaseUrl, Username, Password, HttpUserAgent}` -> `{Success, Message}`
- `POST /XtreamIptv/TestDispatcharr` body `{DispatcharrUrl, DispatcharrUser, DispatcharrPass}` -> `{Success, Message}`
- `GET  /XtreamIptv/DispatcharrProfiles`
- `GET  /XtreamIptv/StreamStats`
- `POST /XtreamIptv/SyncGuideMappings`
Removed: Dashboard, Content DELETE, BrowsePath, ValidateStrmPath, CheckUpdate, InstallUpdate, RestartEmby, Logs, Sync/*, Items/*.

Catalogue (`Api/CatalogApi.cs`):
- `GET  /XtreamIptv/Categories/Vod` -> `List<Category>` (refreshes `CachedVodCategories`)
- `GET  /XtreamIptv/Categories/Series` -> `List<Category>`
- `POST /XtreamIptv/Catalog/Sync` starts a sync in the background and returns at once `{Started: bool, Message}`; 409-like `{Started:false}` when one is running
- `GET  /XtreamIptv/Catalog/Status` -> `{Running, Phase, Processed, Total, LastRunUtc, LastSummary, Movies, Series, Episodes}` (counts of files currently in the library folders)
- `POST /XtreamIptv/Catalog/ValidatePath` body `{Path}` -> `{Success, Message}` (writable check)

`Category` is the existing model (`CategoryId`, `CategoryName`, `ParentId`).

## Config page

Tabs: Connection (BaseUrl, Username, Password, HttpUserAgent, test button, Dispatcharr block with URL, user, pass, test, fallback, stream endpoint, codec source, profiles), Live TV (enable, output format, categories picker with select all / Italian preset not needed, adult toggle, EPG source, days, cache minutes, defer to guide data, name cleaning terms, DVB subtitles, force audio transcode, fallback bitrate, Sync guide mappings button), Catalogue (enable movies, enable series, LibraryPath + validate, movie categories picker, series categories picker, IncludeAdultContent, MergeMovieVersions, DetailOnOpen, ContentRemoveTerms, SyncParallelism, XtreamRequestsPerSecond, RemovalSafetyThreshold, "Sync now" button, live status line with phase and progress, last run summary, and the two library paths to add in Emby with a short note on the required library options). Plain Emby dashboard markup (`emby-input`, `emby-checkbox`, `emby-select`, `emby-button`, `paper-icon-button-light` as the old page used), one `define([...])` module, `ApiClient.getPluginConfiguration` / `updatePluginConfiguration` with the GUID above. English only, no em-dashes anywhere (code, UI, comments).

## Build and deploy

```
cd Emby.XtreamIptv
docker run --rm -v "$PWD/..":/src -w /src/Emby.XtreamIptv mcr.microsoft.com/dotnet/sdk:10.0 dotnet publish -c Release -o /src/out --no-self-contained -p:Version=1.0.0
```
Tests: `docker run --rm -v "$PWD/..":/src -w /src/Emby.XtreamIptv.Tests mcr.microsoft.com/dotnet/sdk:10.0 dotnet test`.
Deploy: copy `out/Emby.XtreamIptv.dll` to the Emby config `plugins/` folder, remove `Emby.Xtream.Plugin.dll`, restart Emby.

## Coding rules

English identifiers, strings and comments; compact professional comments only where the why is not obvious; no em-dashes; keep the file and folder layout above; no reflection on Emby internals beyond the documented `AuthService` static; every network call has a timeout and a cancellation token; log at Info for sync start/end and counts, Debug for per-item work, Warn for recoverable failures; never log credentials or full stream URLs.

## Jellyfin port (Jellyfin.Plugin.XtreamIptv, Jellyfin 12.4)

The same plugin for Jellyfin, written against Jellyfin 12.4 (net10.0, Jellyfin.Controller 12.4.0). The host-neutral code moved to `XtreamIptv.Shared/` (namespaces `XtreamIptv.*`: `PluginConfiguration`, `XtreamHost` with the configuration and HttpClient hooks each host sets, `Logging/ILog` with one adapter per host, `Client/*`, `Catalog/CatalogNaming`, `Catalog/CatalogState`, `LiveTv/` parsers, `Service/*`) and is compiled into both plugins with a `Compile Include`; every behaviour described above is the contract for both hosts. The Emby test suite covers the shared code.

### Identity and packaging

- Same GUID and name, assembly `Jellyfin.Plugin.XtreamIptv.dll`, namespace `Jellyfin.Plugin.XtreamIptv`. Plugin folder `<data>/plugins/XtreamIptv_<version>/` with the DLL and `meta.json` (`targetAbi` 12.4.0.0, `assemblies`). Build: `docker run --rm -v "$PWD":/src -w /src/Jellyfin.Plugin.XtreamIptv mcr.microsoft.com/dotnet/sdk:10.0 dotnet publish -c Release -o /src/out/jellyfin --no-self-contained -p:Version=X`; `build-jellyfin.sh` writes the folder with `meta.json`.
- Config page `xtreamiptv` (`configurationpage?name=xtreamiptv`): one HTML with an inline script (Jellyfin has no AMD pages), Jellyfin dashboard markup, `ApiClient.getPluginConfiguration` / `updatePluginConfiguration`, same fields as the Emby page plus "Create libraries" and "Register tuner and guide" buttons.
- DI: `PluginServiceRegistrator : IPluginServiceRegistrator` registers the shared `ILog` and calls `CatalogRegistration.Register` and `LiveTvRegistration.Register`. Host classes take their dependencies by constructor. Controllers: `[ApiController] [Route("XtreamIptv")] [Authorize(Policy = Policies.RequiresElevation)]`, same routes and response shapes as the Emby API contract, plus `POST Catalog/CreateLibraries`, `POST LiveTv/Register`, `GET LiveTv/Status`.
- `Plugin.UpdateConfiguration` raises `ConnectionChanged` when the stream base differs; the catalogue's hosted service starts a sync on it.

### Jellyfin facts the port rests on (verified in the 12.4 source)

- `Folder.ValidateChildrenInternal2` removes a database child missing from disk only when `item.IsFileProtocol`; children with Path null or an http URL survive every scan. A library folder that is empty on disk is skipped by every scan (`IsLibraryFolderAccessible`: "inaccessible or empty") and its folder item is never created, so `CatalogLayout.EnsureFolder` keeps a small `xtream-catalogue.txt` in each library folder (ignored by the resolvers) and `CreateLibraries` runs `ValidateTopLibraryFolders` so the folder items exist before the first sync. `LocationType` of a path-less item is Virtual, which the web client only treats specially for non-folders, so path-less `Series`/`Season` are fine.
- `ProbeProvider` skips items that are not file protocol unless `MetadataRefreshOptions.EnableRemoteContentProbe`; `MediaSourceManager.GetPlaybackMediaSources` sets it and refreshes an http item lacking a video stream at the first play, then merges `IMediaSourceProvider` sources (providers are discovered from plugin assemblies, `OpenToken`/`LiveStreamId` get a provider prefix). Library scans therefore never probe the catalogue.
- `UserLibraryController.GetItem` refreshes only `Person` items on demand. There is no AuthService hook: the detail-on-open hook is a global MVC action filter registered with `services.Configure<MvcOptions>(o => o.Filters.AddService<DetailOnOpenFilter>())`, which runs after authorization for every controller, including the core ones.
- `ILibraryManager`: `CreateItems(IReadOnlyList<BaseItem>, parent, ct)`, `UpdateItemAsync(item, parent, ItemUpdateType.MetadataDownload, ct)`, `DeleteItem(item, DeleteOptions { DeleteFileLocation = false }, parent, false)`, `GetNewItemId(key, type)`, `GetItemList(InternalItemsQuery { ParentId | AncestorIds, IncludeItemTypes = BaseItemKind[] })`, `UpdatePeopleAsync(item, people, ct)` (creates the `Person` items), `GetPeople(item)`, `GetPerson(name)`. Remote images: `item.SetImage(new ItemImageInfo { Path = url, Type = type }, 0)`, converted to local on first request (`ImageController` -> `ConvertImageToLocal`). `Genres`, `Studios`, `ProductionLocations` are string arrays; dates are `DateTime`.
- Late notification: `ISessionManager.SendMessageToUserSessions(userIds, SessionMessageType.LibraryChanged, new LibraryUpdateInfo { ItemsUpdated = ids }, ct)` for every `IUserManager.Users` id. jellyfin-web refreshes item lists on it but the item page itself does not reload, so the synchronous `DetailWaitSeconds` cap is what carries the open; the message is still sent for the other clients.
- Actor photos: `person.RefreshMetadata(new MetadataRefreshOptions(new DirectoryService(fileSystem)) { MetadataRefreshMode = FullRefresh, ImageRefreshMode = FullRefresh, IsAutomated = false }, ct)`; Jellyfin's bundled TMDB plugin resolves the name (Person metadata settings are server-wide).
- `IScheduledTask` implementations are discovered from the plugin assembly; triggers are `TaskTriggerInfo { Type = TaskTriggerInfoType.DailyTrigger, TimeOfDayTicks }`.
- Live TV: `BaseTunerHost`, `LiveStream` and `SharedHttpStream` live in `Jellyfin.LiveTv`, which is not on NuGet, so the plugin implements `ITunerHost` and `ILiveStream` (+ `IDirectStreamProvider`) itself, following those classes. `ITunerHost` and `IListingsProvider` are injected as `IEnumerable` and must be registered in DI. Tuner entries come from `IConfigurationManager.GetConfiguration<LiveTvOptions>("livetv").TunerHosts` filtered by `Type`. `ListingsManager` pairs a tuner channel with an EPG channel by `Id`, then `TunerChannelId`, `Number`, `Name`, and asks `GetProgramsAsync(info, epgChannel.Id, start, end)`.

### Jellyfin layout

```
Jellyfin.Plugin.XtreamIptv/
  Plugin.cs, PluginServiceRegistrator.cs, JellyfinLog.cs
  Configuration/configPage.html
  Catalog/JellyfinCatalog.cs            the ILibraryManager layer (same roles as EmbyCatalog)
  Catalog/CatalogSyncService.cs         same algorithm as the Emby one
  Catalog/CatalogSyncTask.cs, CatalogHost.cs (IHostedService), CatalogRegistration.cs
  Catalog/DetailOnOpenService.cs, DetailOnOpenFilter.cs, LateDetailNotifier.cs
  Catalog/XtreamVersionSourceProvider.cs
  Controllers/CatalogController.cs
  LiveTv/XtreamTunerHost.cs             ITunerHost "xtream-tuner"
  LiveTv/XtreamListingsProvider.cs      IListingsProvider "xtream-epg"
  LiveTv/XtreamLiveStream.cs, LiveTvService.cs, LiveTvRegistration.cs
  Controllers/LiveTvController.cs
Jellyfin.Plugin.XtreamIptv.Tests/       xunit, net10.0
```

Libraries: `POST /XtreamIptv/Catalog/CreateLibraries` creates the disk folders and the "Film" (movies) and "Serie" (tvshows) libraries through `ILibraryManager.AddVirtualFolder` with `LibraryOptions` that disable every fetcher (`TypeOptions` for Movie, Series, Season, Episode with empty `MetadataFetchers` / `ImageFetchers`), real-time monitoring, automatic refresh, trickplay and chapter extraction, when they do not exist yet. Live TV: `POST /XtreamIptv/LiveTv/Register` saves a `TunerHostInfo { Type = "xtream-tuner", FriendlyName = "Xtream IPTV" }` through `ITunerHostManager.SaveTunerHost` and a `ListingsProviderInfo { Type = "xtream-epg", EnableAllTuners = true }` through `IListingsManager.SaveListingProvider` when missing.

## Releases

One version for both plugins, in `VERSION` (three parts; Jellyfin sees it with a fourth `.0`). `build-emby.sh` writes `out/emby/Emby.XtreamIptv.dll`; `build-jellyfin.sh` writes `out/jellyfin/XtreamIptv_<v>/` and the zip Jellyfin repositories serve. The workflow `.github/workflows/release.yml` runs on every push to the `release` branch: tests, both builds, GitHub release `v<version>` with the DLL and the zip, then `scripts/update-manifest.py` adds the version (MD5 of the zip, `targetAbi` from the build script) to `manifest.json` and commits it on `release`; that file at `raw.githubusercontent.com/<owner>/<repo>/release/manifest.json` is the repository URL Jellyfin users add. The workflow refuses a version that already has a release. The raw URL only works on a public repository.
