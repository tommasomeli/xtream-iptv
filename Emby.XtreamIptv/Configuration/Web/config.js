define(['baseView', 'loading', 'emby-input', 'emby-select', 'emby-checkbox', 'emby-button'],
function (BaseView, loading) {
    'use strict';

    var pluginId = 'c02587d5-61e0-415a-a2d5-3ccd3107d112';
    var STATUS_POLL_MS = 3000;

    // One entry per category picker: API route, config cache property, config selection property.
    var pickerKinds = {
        live:   { route: 'XtreamIptv/Categories/Live',   cache: 'CachedLiveCategories',   selected: 'SelectedLiveCategoryIds' },
        vod:    { route: 'XtreamIptv/Categories/Vod',    cache: 'CachedVodCategories',    selected: 'SelectedVodCategoryIds' },
        series: { route: 'XtreamIptv/Categories/Series', cache: 'CachedSeriesCategories', selected: 'SelectedSeriesCategoryIds' }
    };

    // ---- View ----

    function View(view, params) {
        BaseView.apply(this, arguments);

        this.categories = { live: [], vod: [], series: [] };
        this.selectedIds = { live: [], vod: [], series: [] };
        this.profiles = [];
        this.selectedProfileIds = [];
        this.pollTimer = null;
        this.testRequestId = 0;

        bindTabs(this);
        bindConnection(this);
        bindLiveTv(this);
        bindCatalog(this);
        bindPickers(this);
        bindForm(this);
    }

    Object.assign(View.prototype, BaseView.prototype);

    View.prototype.onResume = function (options) {
        BaseView.prototype.onResume.apply(this, arguments);
        loadConfig(this);
        refreshCatalogStatus(this);
    };

    View.prototype.onPause = function () {
        stopStatusPolling(this);
    };

    // ---- Small helpers ----

    function q(instance, selector) {
        return instance.view.querySelector(selector);
    }

    function qa(instance, selector) {
        return instance.view.querySelectorAll(selector);
    }

    function escapeHtml(text) {
        var div = document.createElement('div');
        div.appendChild(document.createTextNode(text == null ? '' : String(text)));
        return div.innerHTML;
    }

    function setResult(el, kind, message) {
        var icon = kind === 'ok' ? '\u2713' : (kind === 'err' ? '\u2717' : '\u2026');
        el.innerHTML = '<span class="xtPill ' + kind + '">' + icon + ' ' + escapeHtml(message) + '</span>';
    }

    function clearResult(el) {
        el.innerHTML = '';
    }

    function show(el, visible) {
        el.style.display = visible ? '' : 'none';
    }

    function intValue(el, fallback) {
        var n = parseInt(el.value, 10);
        return isNaN(n) ? fallback : n;
    }

    function trimSlashes(value) {
        return (value || '').trim().replace(/\/+$/, '');
    }

    function postJson(route, body) {
        return ApiClient.ajax({
            type: 'POST',
            url: ApiClient.getUrl(route),
            contentType: 'application/json',
            data: JSON.stringify(body || {}),
            dataType: 'json'
        });
    }

    function parseCachedJson(text) {
        if (!text) return [];
        try {
            var parsed = JSON.parse(text);
            return Array.isArray(parsed) ? parsed : [];
        } catch (e) {
            return [];
        }
    }

    // The server reports the time as an ISO string, Unix seconds, Unix milliseconds
    // or .NET ticks depending on the field; accept all of them.
    function formatTime(value) {
        if (!value) return '';
        var date;
        if (typeof value === 'string' && isNaN(Number(value))) {
            date = new Date(value);
        } else {
            var n = Number(value);
            if (n > 1e17) {
                date = new Date((n - 621355968000000000) / 10000);
            } else if (n > 1e11) {
                date = new Date(n);
            } else {
                date = new Date(n * 1000);
            }
        }
        return isNaN(date.getTime()) ? String(value) : date.toLocaleString();
    }

    // ---- Tabs ----

    function bindTabs(instance) {
        var tabs = qa(instance, '.xtTab');
        for (var i = 0; i < tabs.length; i++) {
            tabs[i].addEventListener('click', function () {
                switchTab(instance, this.getAttribute('data-tab'));
            });
        }
    }

    function switchTab(instance, name) {
        var tabs = qa(instance, '.xtTab');
        var panels = qa(instance, '.xtPanel');
        var i;
        for (i = 0; i < tabs.length; i++) {
            tabs[i].classList.toggle('active', tabs[i].getAttribute('data-tab') === name);
        }
        for (i = 0; i < panels.length; i++) {
            panels[i].classList.toggle('active', panels[i].getAttribute('data-panel') === name);
        }
    }

    // ---- Connection tab ----

    function bindConnection(instance) {
        q(instance, '.btnTestConnection').addEventListener('click', function () {
            testConnection(instance);
        });
        q(instance, '.btnTestDispatcharr').addEventListener('click', function () {
            testDispatcharr(instance);
        });
        q(instance, '.chkEnableDispatcharr').addEventListener('change', function () {
            updateDispatcharrVisibility(instance);
        });
        q(instance, '.btnRefreshProfiles').addEventListener('click', function () {
            loadProfiles(instance);
        });
        q(instance, '.btnProfilesAll').addEventListener('click', function () {
            setAllProfiles(instance, true);
        });
        q(instance, '.btnProfilesNone').addEventListener('click', function () {
            setAllProfiles(instance, false);
        });
        q(instance, '.profilesList').addEventListener('change', function (e) {
            var box = e.target;
            if (!box || !box.classList.contains('profileCheckbox')) return;
            toggleId(instance.selectedProfileIds, parseInt(box.getAttribute('data-id'), 10), box.checked);
            updateProfilesCount(instance);
        });

        var warningInputs = ['.selDispatcharrStreamEndpoint', '.txtBaseUrl', '.txtDispatcharrUrl'];
        for (var i = 0; i < warningInputs.length; i++) {
            var el = q(instance, warningInputs[i]);
            el.addEventListener('input', function () { updateStreamEndpointWarning(instance); });
            el.addEventListener('change', function () { updateStreamEndpointWarning(instance); });
        }
    }

    function updateDispatcharrVisibility(instance) {
        show(q(instance, '.dispatcharrSettings'), q(instance, '.chkEnableDispatcharr').checked);
    }

    // The Xtream stream URL is only used when the Xtream server is the Dispatcharr server;
    // otherwise the plugin keeps the proxy URL, so say so here instead of only in the log.
    function updateStreamEndpointWarning(instance) {
        var wantsXtream = q(instance, '.selDispatcharrStreamEndpoint').value === 'xtream';
        var same = false;
        try {
            var a = new URL(q(instance, '.txtBaseUrl').value.trim());
            var b = new URL(q(instance, '.txtDispatcharrUrl').value.trim());
            same = a.origin === b.origin && trimSlashes(a.pathname) === trimSlashes(b.pathname);
        } catch (e) {
            same = false;
        }
        show(q(instance, '.streamEndpointWarning'), wantsXtream && !same);
    }

    function testConnection(instance) {
        var resultEl = q(instance, '.connectionResult');
        var body = {
            BaseUrl: trimSlashes(q(instance, '.txtBaseUrl').value),
            Username: q(instance, '.txtUsername').value,
            Password: q(instance, '.txtPassword').value,
            HttpUserAgent: q(instance, '.txtHttpUserAgent').value.trim()
        };
        if (!body.BaseUrl || !body.Username || !body.Password) {
            setResult(resultEl, 'err', 'Enter server URL, username and password first.');
            return;
        }
        // Only the latest test may report, so a slow earlier reply cannot overwrite it.
        var requestId = ++instance.testRequestId;
        setResult(resultEl, 'info', 'Testing connection');
        postJson('XtreamIptv/TestConnection', body).then(function (result) {
            if (requestId !== instance.testRequestId) return;
            setResult(resultEl, result.Success ? 'ok' : 'err', result.Message || (result.Success ? 'Connected.' : 'Connection failed.'));
        }).catch(function () {
            if (requestId !== instance.testRequestId) return;
            setResult(resultEl, 'err', 'Test request failed. Check the server log.');
        });
    }

    function testDispatcharr(instance) {
        var resultEl = q(instance, '.dispatcharrResult');
        var body = {
            DispatcharrUrl: trimSlashes(q(instance, '.txtDispatcharrUrl').value),
            DispatcharrUser: q(instance, '.txtDispatcharrUser').value,
            DispatcharrPass: q(instance, '.txtDispatcharrPass').value
        };
        if (!body.DispatcharrUrl) {
            setResult(resultEl, 'err', 'Enter the Dispatcharr URL first.');
            return;
        }
        setResult(resultEl, 'info', 'Testing Dispatcharr');
        postJson('XtreamIptv/TestDispatcharr', body).then(function (result) {
            setResult(resultEl, result.Success ? 'ok' : 'err', result.Message || (result.Success ? 'Connected.' : 'Connection failed.'));
        }).catch(function () {
            setResult(resultEl, 'err', 'Test request failed. Check the server log.');
        });
    }

    // ---- Dispatcharr profiles ----

    function loadProfiles(instance) {
        var listEl = q(instance, '.profilesList');
        listEl.innerHTML = '<div class="xtMuted">Loading profiles</div>';
        ApiClient.getJSON(ApiClient.getUrl('XtreamIptv/DispatcharrProfiles')).then(function (profiles) {
            instance.profiles = Array.isArray(profiles) ? profiles : [];
            renderProfiles(instance);
            if (instance.profiles.length === 0) {
                listEl.innerHTML = '<div class="xtMuted">No profiles found. Check the Dispatcharr settings.</div>';
            }
        }).catch(function () {
            listEl.innerHTML = '<div class="xtMuted">Failed to load profiles. Save the Dispatcharr settings first, then try again.</div>';
        });
    }

    function renderProfiles(instance) {
        var listEl = q(instance, '.profilesList');
        var html = '';
        for (var i = 0; i < instance.profiles.length; i++) {
            var p = instance.profiles[i];
            var checked = instance.selectedProfileIds.indexOf(p.Id) >= 0 ? ' checked' : '';
            html += '<label class="xtRow">';
            html += '<input type="checkbox" class="profileCheckbox" data-id="' + p.Id + '"' + checked + ' />';
            html += '<span>' + escapeHtml(p.Name || ('Profile ' + p.Id)) + '</span>';
            html += '</label>';
        }
        listEl.innerHTML = html;
        var hasProfiles = instance.profiles.length > 0;
        q(instance, '.btnProfilesAll').disabled = !hasProfiles;
        q(instance, '.btnProfilesNone').disabled = !hasProfiles;
        updateProfilesCount(instance);
    }

    function setAllProfiles(instance, checked) {
        instance.selectedProfileIds = checked
            ? instance.profiles.map(function (p) { return p.Id; })
            : [];
        renderProfiles(instance);
    }

    function updateProfilesCount(instance) {
        var el = q(instance, '.profilesCount');
        var total = instance.profiles.length;
        if (total === 0) {
            el.textContent = '';
            return;
        }
        var selected = instance.selectedProfileIds.length;
        el.textContent = selected === 0
            ? 'No profile ticked: all channels shown'
            : selected + ' / ' + total + ' profiles selected';
    }

    // ---- Live TV tab ----

    function bindLiveTv(instance) {
        q(instance, '.chkEnableLiveTv').addEventListener('change', function () {
            updateLiveTvVisibility(instance);
        });
        q(instance, '.selEpgSource').addEventListener('change', function () {
            updateEpgVisibility(instance);
        });
        q(instance, '.chkEnableChannelNameCleaning').addEventListener('change', function () {
            updateChannelCleaningVisibility(instance);
        });
        q(instance, '.btnRefreshCache').addEventListener('click', function () {
            refreshCache(instance);
        });
        q(instance, '.btnSyncGuideMappings').addEventListener('click', function () {
            syncGuideMappings(instance);
        });
    }

    function updateLiveTvVisibility(instance) {
        show(q(instance, '.liveTvSettings'), q(instance, '.chkEnableLiveTv').checked);
    }

    function updateEpgVisibility(instance) {
        var source = intValue(q(instance, '.selEpgSource'), 0);
        show(q(instance, '.epgCustomUrlSettings'), source === 1);
        show(q(instance, '.epgSettings'), source !== 2);
    }

    function updateChannelCleaningVisibility(instance) {
        show(q(instance, '.channelCleaningSettings'), q(instance, '.chkEnableChannelNameCleaning').checked);
    }

    function refreshCache(instance) {
        var resultEl = q(instance, '.maintenanceResult');
        setResult(resultEl, 'info', 'Refreshing cache');
        ApiClient.ajax({ type: 'POST', url: ApiClient.getUrl('XtreamIptv/RefreshCache') }).then(function () {
            setResult(resultEl, 'ok', 'Channel and guide cache refreshed.');
        }).catch(function () {
            setResult(resultEl, 'err', 'Failed to refresh the cache.');
        });
    }

    function syncGuideMappings(instance) {
        var btn = q(instance, '.btnSyncGuideMappings');
        var resultEl = q(instance, '.maintenanceResult');
        btn.disabled = true;
        setResult(resultEl, 'info', 'Syncing guide mappings');
        ApiClient.ajax({ type: 'POST', url: ApiClient.getUrl('XtreamIptv/SyncGuideMappings'), dataType: 'json' }).then(function (result) {
            btn.disabled = false;
            setResult(resultEl, result.Success ? 'ok' : 'err', result.Message || 'Done.');
        }).catch(function () {
            btn.disabled = false;
            setResult(resultEl, 'err', 'Sync request failed. Check the server log.');
        });
    }

    // ---- Category pickers (shared by Live, VOD and Series) ----

    function pickerRoot(instance, kind) {
        return q(instance, '.xtPicker[data-kind="' + kind + '"]');
    }

    function bindPickers(instance) {
        var roots = qa(instance, '.xtPicker');
        for (var i = 0; i < roots.length; i++) {
            bindPicker(instance, roots[i], roots[i].getAttribute('data-kind'));
        }
    }

    function bindPicker(instance, root, kind) {
        root.querySelector('.btnPickerRefresh').addEventListener('click', function () {
            loadCategories(instance, kind);
        });
        root.querySelector('.btnPickerAll').addEventListener('click', function () {
            setVisibleCategories(instance, kind, true);
        });
        root.querySelector('.btnPickerNone').addEventListener('click', function () {
            setVisibleCategories(instance, kind, false);
        });
        root.querySelector('.pickerFilter').addEventListener('input', function () {
            applyPickerFilter(instance, kind);
        });
        root.querySelector('.pickerList').addEventListener('change', function (e) {
            var box = e.target;
            if (!box || !box.classList.contains('categoryCheckbox')) return;
            toggleId(instance.selectedIds[kind], parseInt(box.getAttribute('data-id'), 10), box.checked);
            updatePickerCount(instance, kind);
        });
    }

    function toggleId(list, id, present) {
        var index = list.indexOf(id);
        if (present && index < 0) list.push(id);
        if (!present && index >= 0) list.splice(index, 1);
    }

    function loadCategories(instance, kind) {
        var root = pickerRoot(instance, kind);
        var listEl = root.querySelector('.pickerList');
        listEl.innerHTML = '<div class="xtMuted">Loading categories</div>';
        ApiClient.getJSON(ApiClient.getUrl(pickerKinds[kind].route)).then(function (categories) {
            instance.categories[kind] = Array.isArray(categories) ? categories : [];
            renderCategories(instance, kind);
            if (instance.categories[kind].length === 0) {
                listEl.innerHTML = '<div class="xtMuted">No categories found. Check the Xtream connection settings.</div>';
            }
        }).catch(function () {
            listEl.innerHTML = '<div class="xtMuted">Failed to load categories. Save the connection settings first, then try again.</div>';
        });
    }

    function renderCategories(instance, kind) {
        var root = pickerRoot(instance, kind);
        var listEl = root.querySelector('.pickerList');
        var categories = instance.categories[kind];
        var selected = instance.selectedIds[kind];
        var html = '';
        for (var i = 0; i < categories.length; i++) {
            var cat = categories[i];
            var checked = selected.indexOf(cat.CategoryId) >= 0 ? ' checked' : '';
            html += '<label class="xtRow" data-name="' + escapeHtml((cat.CategoryName || '').toLowerCase()) + '">';
            html += '<input type="checkbox" class="categoryCheckbox" data-id="' + cat.CategoryId + '"' + checked + ' />';
            html += '<span>' + escapeHtml(cat.CategoryName) + '</span>';
            html += '</label>';
        }
        listEl.innerHTML = html;
        var hasCategories = categories.length > 0;
        root.querySelector('.btnPickerAll').disabled = !hasCategories;
        root.querySelector('.btnPickerNone').disabled = !hasCategories;
        applyPickerFilter(instance, kind);
    }

    function applyPickerFilter(instance, kind) {
        var root = pickerRoot(instance, kind);
        var filter = root.querySelector('.pickerFilter').value.trim().toLowerCase();
        var rows = root.querySelectorAll('.xtRow');
        for (var i = 0; i < rows.length; i++) {
            show(rows[i], !filter || rows[i].getAttribute('data-name').indexOf(filter) >= 0);
        }
        updatePickerCount(instance, kind);
    }

    // Select all / none act on the rows matching the current filter.
    function setVisibleCategories(instance, kind, checked) {
        var root = pickerRoot(instance, kind);
        var rows = root.querySelectorAll('.xtRow');
        for (var i = 0; i < rows.length; i++) {
            if (rows[i].style.display === 'none') continue;
            var box = rows[i].querySelector('.categoryCheckbox');
            box.checked = checked;
            toggleId(instance.selectedIds[kind], parseInt(box.getAttribute('data-id'), 10), checked);
        }
        updatePickerCount(instance, kind);
    }

    function updatePickerCount(instance, kind) {
        var root = pickerRoot(instance, kind);
        var el = root.querySelector('.pickerCount');
        var total = instance.categories[kind].length;
        if (total === 0) {
            el.textContent = '';
            return;
        }
        var selected = instance.selectedIds[kind].length;
        var text = selected === 0
            ? 'All ' + total + ' categories included (none ticked)'
            : selected + ' / ' + total + ' categories selected';
        var filter = root.querySelector('.pickerFilter').value.trim();
        if (filter) {
            var visible = 0;
            var rows = root.querySelectorAll('.xtRow');
            for (var i = 0; i < rows.length; i++) {
                if (rows[i].style.display !== 'none') visible++;
            }
            text += ', ' + visible + ' shown by the filter';
        }
        el.textContent = text;
    }

    // ---- Catalogue tab ----

    function bindCatalog(instance) {
        q(instance, '.chkSyncMovies').addEventListener('change', function () {
            updateCatalogVisibility(instance);
        });
        q(instance, '.chkSyncSeries').addEventListener('change', function () {
            updateCatalogVisibility(instance);
        });
        q(instance, '.txtLibraryPath').addEventListener('input', function () {
            updateLibraryPaths(instance);
        });
        q(instance, '.btnValidatePath').addEventListener('click', function () {
            validatePath(instance);
        });
        q(instance, '.btnSyncNow').addEventListener('click', function () {
            startSync(instance);
        });
        q(instance, '.btnRefreshStatus').addEventListener('click', function () {
            refreshCatalogStatus(instance);
        });
    }

    function updateCatalogVisibility(instance) {
        show(q(instance, '.movieSettings'), q(instance, '.chkSyncMovies').checked);
        show(q(instance, '.seriesSettings'), q(instance, '.chkSyncSeries').checked);
    }

    function updateLibraryPaths(instance) {
        var root = trimSlashes(q(instance, '.txtLibraryPath').value) || '/config/xtream';
        q(instance, '.libMoviesPath').textContent = root + '/Movies';
        q(instance, '.libShowsPath').textContent = root + '/Shows';
    }

    function validatePath(instance) {
        var resultEl = q(instance, '.pathResult');
        var path = trimSlashes(q(instance, '.txtLibraryPath').value);
        if (!path) {
            setResult(resultEl, 'err', 'Enter a library folder first.');
            return;
        }
        setResult(resultEl, 'info', 'Checking folder');
        postJson('XtreamIptv/Catalog/ValidatePath', { Path: path }).then(function (result) {
            setResult(resultEl, result.Success ? 'ok' : 'err', result.Message || (result.Success ? 'Folder is writable.' : 'Folder is not writable.'));
        }).catch(function () {
            setResult(resultEl, 'err', 'Validation request failed. Check the server log.');
        });
    }

    function startSync(instance) {
        var resultEl = q(instance, '.syncResult');
        var btn = q(instance, '.btnSyncNow');
        btn.disabled = true;
        setResult(resultEl, 'info', 'Starting sync');
        postJson('XtreamIptv/Catalog/Sync', {}).then(function (result) {
            btn.disabled = false;
            if (result.Started) {
                setResult(resultEl, 'ok', result.Message || 'Sync started. Save any unsaved changes first, they are not used by a running sync.');
            } else {
                setResult(resultEl, 'err', result.Message || 'A sync is already running.');
            }
            refreshCatalogStatus(instance);
        }).catch(function () {
            btn.disabled = false;
            setResult(resultEl, 'err', 'Sync request failed. Check the server log.');
        });
    }

    function refreshCatalogStatus(instance) {
        ApiClient.getJSON(ApiClient.getUrl('XtreamIptv/Catalog/Status')).then(function (status) {
            renderCatalogStatus(instance, status || {});
            if (status && status.Running) {
                startStatusPolling(instance);
            } else {
                stopStatusPolling(instance);
            }
        }).catch(function () {
            stopStatusPolling(instance);
            setText(q(instance, '.statusState'), 'Status unavailable', true);
        });
    }

    function startStatusPolling(instance) {
        if (instance.pollTimer) return;
        instance.pollTimer = setInterval(function () {
            refreshCatalogStatus(instance);
        }, STATUS_POLL_MS);
    }

    function stopStatusPolling(instance) {
        if (!instance.pollTimer) return;
        clearInterval(instance.pollTimer);
        instance.pollTimer = null;
    }

    function setText(el, text, muted) {
        el.textContent = text;
        el.classList.toggle('xtMuted', !!muted);
    }

    function renderCatalogStatus(instance, status) {
        var running = !!status.Running;
        var phase = status.Phase || '';
        setText(q(instance, '.statusState'), running ? 'Running' + (phase ? ': ' + phase : '') : 'Idle', !running);

        var progressRow = q(instance, '.statusProgressRow');
        var total = Number(status.Total) || 0;
        var processed = Number(status.Processed) || 0;
        show(progressRow, running);
        if (running) {
            var pct = total > 0 ? Math.min(100, Math.round(processed * 100 / total)) : 0;
            q(instance, '.statusProgress').textContent = total > 0
                ? processed + ' / ' + total + ' (' + pct + '%)'
                : processed + ' processed';
        }

        // Empty values keep what the config already showed (last run and summary).
        var lastRun = formatTime(status.LastRunUtc);
        if (lastRun) setText(q(instance, '.statusLastRun'), lastRun, false);

        var summary = status.LastSummary || '';
        if (summary) setText(q(instance, '.statusSummary'), summary, false);

        var hasCounts = status.Movies != null || status.Series != null || status.Episodes != null;
        setText(q(instance, '.statusCounts'), hasCounts
            ? (Number(status.Movies) || 0) + ' movies, ' + (Number(status.Series) || 0) + ' series, ' + (Number(status.Episodes) || 0) + ' episodes'
            : 'Unknown', !hasCounts);
    }

    // ---- Load and save ----

    function loadConfig(instance) {
        loading.show();
        ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            applyConfig(instance, config);
            loading.hide();
        }).catch(function () {
            loading.hide();
            Dashboard.alert('Failed to load the plugin configuration.');
        });
    }

    function applyConfig(instance, config) {
        // Connection
        q(instance, '.txtBaseUrl').value = config.BaseUrl || '';
        q(instance, '.txtUsername').value = config.Username || '';
        q(instance, '.txtPassword').value = config.Password || '';
        q(instance, '.txtHttpUserAgent').value = config.HttpUserAgent || '';

        // Dispatcharr
        q(instance, '.chkEnableDispatcharr').checked = !!config.EnableDispatcharr;
        q(instance, '.txtDispatcharrUrl').value = config.DispatcharrUrl || '';
        q(instance, '.txtDispatcharrUser').value = config.DispatcharrUser || '';
        q(instance, '.txtDispatcharrPass').value = config.DispatcharrPass || '';
        q(instance, '.chkDispatcharrFallback').checked = config.DispatcharrFallbackToXtream !== false;
        q(instance, '.selDispatcharrStreamEndpoint').value = config.DispatcharrStreamEndpoint || 'proxy';
        q(instance, '.selDispatcharrCodecSource').value = config.DispatcharrVideoCodecSource || 'auto';
        instance.selectedProfileIds = (config.SelectedDispatcharrProfileIds || []).slice();
        instance.profiles = parseCachedJson(config.CachedDispatcharrProfiles);
        if (instance.profiles.length > 0) renderProfiles(instance);

        // Live TV
        q(instance, '.chkEnableLiveTv').checked = config.EnableLiveTv !== false;
        q(instance, '.selOutputFormat').value = config.LiveTvOutputFormat || 'ts';
        q(instance, '.txtFallbackTranscodeBitrate').value = config.FallbackTranscodeBitrateMbps || 0;
        q(instance, '.chkIncludeAdultChannels').checked = !!config.IncludeAdultChannels;
        q(instance, '.chkEnableChannelNameCleaning').checked = config.EnableChannelNameCleaning !== false;
        q(instance, '.txtChannelRemoveTerms').value = config.ChannelRemoveTerms || '';
        q(instance, '.selEpgSource').value = epgSourceToValue(config.EpgSource);
        q(instance, '.txtCustomEpgUrl').value = config.CustomEpgUrl || '';
        q(instance, '.chkDeferEpgToGuideData').checked = config.DeferEpgToGuideData !== false;
        q(instance, '.txtEpgDaysToFetch').value = config.EpgDaysToFetch || 2;
        q(instance, '.txtEpgCacheMinutes').value = config.EpgCacheMinutes || 30;
        q(instance, '.txtM3UCacheMinutes').value = config.M3UCacheMinutes || 15;
        q(instance, '.chkDeclareDvbSubtitles').checked = !!config.DeclareDvbSubtitles;
        q(instance, '.chkForceAudioTranscode').checked = !!config.ForceAudioTranscode;

        // Catalogue
        q(instance, '.chkSyncMovies').checked = config.SyncMovies !== false;
        q(instance, '.chkSyncSeries').checked = config.SyncSeries !== false;
        q(instance, '.txtLibraryPath').value = config.LibraryPath || '/config/xtream';
        q(instance, '.chkIncludeAdultContent').checked = !!config.IncludeAdultContent;
        q(instance, '.chkAutoIncludeNewCategories').checked = config.AutoIncludeNewCategories !== false;
        q(instance, '.chkMergeMovieVersions').checked = config.MergeMovieVersions !== false;
        q(instance, '.chkDetailOnOpen').checked = config.DetailOnOpen !== false;
        q(instance, '.chkActorPhotosOnOpen').checked = config.ActorPhotosOnOpen !== false;
        q(instance, '.txtDetailWaitSeconds').value = typeof config.DetailWaitSeconds === 'number' ? config.DetailWaitSeconds : 8;
        q(instance, '.txtExcludedCategoryTerms').value = config.ExcludedCategoryTerms || '';
        q(instance, '.txtContentRemoveTerms').value = config.ContentRemoveTerms || '';
        q(instance, '.txtSyncParallelism').value = config.SyncParallelism || 4;
        q(instance, '.txtXtreamRequestsPerSecond').value = config.XtreamRequestsPerSecond || 0;
        var threshold = typeof config.RemovalSafetyThreshold === 'number' ? config.RemovalSafetyThreshold : 0.2;
        q(instance, '.txtRemovalSafetyThreshold').value = Math.round(threshold * 100);

        // Read-only last sync info, shown until the status route answers with its own values.
        var lastRunEl = q(instance, '.statusLastRun');
        var lastRun = formatTime(config.LastCatalogSyncTimestamp);
        if (lastRun && lastRunEl.classList.contains('xtMuted')) setText(lastRunEl, lastRun, false);
        var summaryEl = q(instance, '.statusSummary');
        if (config.LastCatalogSyncSummary && summaryEl.classList.contains('xtMuted')) {
            setText(summaryEl, config.LastCatalogSyncSummary, false);
        }

        // Category pickers: selection from config, list from the cache when present.
        for (var kind in pickerKinds) {
            if (!pickerKinds.hasOwnProperty(kind)) continue;
            instance.selectedIds[kind] = (config[pickerKinds[kind].selected] || []).slice();
            instance.categories[kind] = parseCachedJson(config[pickerKinds[kind].cache]);
            if (instance.categories[kind].length > 0) renderCategories(instance, kind);
        }

        updateDispatcharrVisibility(instance);
        updateStreamEndpointWarning(instance);
        updateLiveTvVisibility(instance);
        updateEpgVisibility(instance);
        updateChannelCleaningVisibility(instance);
        updateCatalogVisibility(instance);
        updateLibraryPaths(instance);
    }

    // The server may serialize the enum as its name or as its number.
    function epgSourceToValue(value) {
        var names = { XtreamServer: '0', CustomUrl: '1', Disabled: '2' };
        if (names[value] !== undefined) return names[value];
        var n = parseInt(value, 10);
        return isNaN(n) ? '0' : String(n);
    }

    function bindForm(instance) {
        q(instance, '.xtreamIptvForm').addEventListener('submit', function (e) {
            e.preventDefault();
            saveConfig(instance);
            return false;
        });
    }

    function saveConfig(instance) {
        loading.show();
        // Re-read the stored configuration so server-side fields written since the page
        // loaded (last sync summary, refreshed caches) are kept.
        ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            collectConfig(instance, config);
            return ApiClient.updatePluginConfiguration(pluginId, config).then(function () {
                Dashboard.processPluginConfigurationUpdateResult();
            });
        }).catch(function () {
            loading.hide();
            Dashboard.alert('Failed to save the configuration.');
        });
    }

    function collectConfig(instance, config) {
        // Connection
        config.BaseUrl = trimSlashes(q(instance, '.txtBaseUrl').value);
        config.Username = q(instance, '.txtUsername').value.trim();
        var password = q(instance, '.txtPassword').value;
        if (password) config.Password = password;
        config.HttpUserAgent = q(instance, '.txtHttpUserAgent').value.trim();

        // Dispatcharr
        config.EnableDispatcharr = q(instance, '.chkEnableDispatcharr').checked;
        config.DispatcharrUrl = trimSlashes(q(instance, '.txtDispatcharrUrl').value);
        config.DispatcharrUser = q(instance, '.txtDispatcharrUser').value.trim();
        var dispatcharrPass = q(instance, '.txtDispatcharrPass').value;
        if (dispatcharrPass) config.DispatcharrPass = dispatcharrPass;
        config.DispatcharrFallbackToXtream = q(instance, '.chkDispatcharrFallback').checked;
        config.DispatcharrStreamEndpoint = q(instance, '.selDispatcharrStreamEndpoint').value;
        config.DispatcharrVideoCodecSource = q(instance, '.selDispatcharrCodecSource').value;
        config.SelectedDispatcharrProfileIds = instance.selectedProfileIds.slice();
        if (instance.profiles.length > 0) {
            config.CachedDispatcharrProfiles = JSON.stringify(instance.profiles);
        }

        // Live TV
        config.EnableLiveTv = q(instance, '.chkEnableLiveTv').checked;
        config.LiveTvOutputFormat = q(instance, '.selOutputFormat').value;
        config.FallbackTranscodeBitrateMbps = Math.max(0, intValue(q(instance, '.txtFallbackTranscodeBitrate'), 0));
        config.IncludeAdultChannels = q(instance, '.chkIncludeAdultChannels').checked;
        config.EnableChannelNameCleaning = q(instance, '.chkEnableChannelNameCleaning').checked;
        config.ChannelRemoveTerms = q(instance, '.txtChannelRemoveTerms').value.trim();
        config.EpgSource = intValue(q(instance, '.selEpgSource'), 0);
        config.CustomEpgUrl = q(instance, '.txtCustomEpgUrl').value.trim();
        config.DeferEpgToGuideData = q(instance, '.chkDeferEpgToGuideData').checked;
        config.EpgDaysToFetch = Math.max(1, intValue(q(instance, '.txtEpgDaysToFetch'), 2));
        config.EpgCacheMinutes = Math.max(1, intValue(q(instance, '.txtEpgCacheMinutes'), 30));
        config.M3UCacheMinutes = Math.max(1, intValue(q(instance, '.txtM3UCacheMinutes'), 15));
        config.DeclareDvbSubtitles = q(instance, '.chkDeclareDvbSubtitles').checked;
        config.ForceAudioTranscode = q(instance, '.chkForceAudioTranscode').checked;

        // Catalogue
        config.SyncMovies = q(instance, '.chkSyncMovies').checked;
        config.SyncSeries = q(instance, '.chkSyncSeries').checked;
        config.LibraryPath = trimSlashes(q(instance, '.txtLibraryPath').value) || '/config/xtream';
        config.IncludeAdultContent = q(instance, '.chkIncludeAdultContent').checked;
        config.AutoIncludeNewCategories = q(instance, '.chkAutoIncludeNewCategories').checked;
        config.MergeMovieVersions = q(instance, '.chkMergeMovieVersions').checked;
        config.DetailOnOpen = q(instance, '.chkDetailOnOpen').checked;
        config.ActorPhotosOnOpen = q(instance, '.chkActorPhotosOnOpen').checked;
        config.DetailWaitSeconds = Math.min(30, Math.max(0, intValue(q(instance, '.txtDetailWaitSeconds'), 8)));
        config.ExcludedCategoryTerms = q(instance, '.txtExcludedCategoryTerms').value.trim();
        config.ContentRemoveTerms = q(instance, '.txtContentRemoveTerms').value.trim();
        config.SyncParallelism = Math.max(1, intValue(q(instance, '.txtSyncParallelism'), 4));
        config.XtreamRequestsPerSecond = Math.max(0, intValue(q(instance, '.txtXtreamRequestsPerSecond'), 0));
        var thresholdPct = Math.min(100, Math.max(0, intValue(q(instance, '.txtRemovalSafetyThreshold'), 20)));
        config.RemovalSafetyThreshold = thresholdPct / 100;

        // Category selections and caches
        for (var kind in pickerKinds) {
            if (!pickerKinds.hasOwnProperty(kind)) continue;
            config[pickerKinds[kind].selected] = instance.selectedIds[kind].slice();
            if (instance.categories[kind].length > 0) {
                config[pickerKinds[kind].cache] = JSON.stringify(instance.categories[kind]);
            }
        }
    }

    return View;
});
