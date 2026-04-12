(function () {
    'use strict';

    var MODAL_ID = 'gifGeneratorItemActionModal';
    var BUTTON_CLASS = 'gifGeneratorItemActionButton';
    var MENU_ACTION_CLASS = 'gifGeneratorItemActionMenuItem';
    var FALLBACK_BUTTON_ID = 'gifGeneratorFallbackActionButton';
    var FALLBACK_NOTICE_ID = 'gifGeneratorFallbackNotice';
    var DEBUG_LOGGING = false;
    // Admin debug toggle:
    // 1) Open browser devtools on Jellyfin web.
    // 2) Run: localStorage.setItem('gifGeneratorDebug', '1')
    // 3) Refresh page to enable verbose [GifGeneratorItemAction] console logs.
    // Disable with: localStorage.removeItem('gifGeneratorDebug')
    if (window.localStorage && window.localStorage.getItem('gifGeneratorDebug') === '1') {
        DEBUG_LOGGING = true;
    }
    var STATE = {
        itemId: null,
        itemInfo: null,
        subtitles: []
    };

    function debugLog() {
        if (!DEBUG_LOGGING || !window.console || typeof window.console.debug !== 'function') {
            return;
        }

        var args = Array.prototype.slice.call(arguments);
        args.unshift('[GifGeneratorItemAction]');
        window.console.debug.apply(window.console, args);
    }

    function normalizeApiPath(path) {
        return path.charAt(0) === '/' ? path.slice(1) : path;
    }

    function getApiUrl(path) {
        if (window.ApiClient && typeof window.ApiClient.getUrl === 'function') {
            return window.ApiClient.getUrl(normalizeApiPath(path));
        }

        return path;
    }

    function appendCacheBuster(url) {
        var separator = url.indexOf('?') === -1 ? '?' : '&';
        return url + separator + '_=' + Date.now();
    }

    function getAuthHeaders(headers) {
        var merged = Object.assign({}, headers || {});

        if (window.ApiClient && typeof window.ApiClient.getAuthorizationHeader === 'function') {
            var authorizationHeader = window.ApiClient.getAuthorizationHeader();
            merged.Authorization = authorizationHeader;
            merged['X-Emby-Authorization'] = authorizationHeader;
        } else if (window.ApiClient && typeof window.ApiClient.accessToken === 'function') {
            merged['X-Emby-Token'] = window.ApiClient.accessToken() || '';
        }

        return merged;
    }

    function parseErrorMessage(response) {
        if (response.status === 401 || response.status === 403) {
            return Promise.resolve('Session expired or unauthorized — please sign in again.');
        }

        return response.text().then(function (text) {
            return text || ('Request failed (' + response.status + ')');
        });
    }

    function apiRequest(path, options) {
        var requestOptions = options || {};

        return fetch(getApiUrl(path), {
            method: requestOptions.method || 'GET',
            headers: getAuthHeaders(requestOptions.headers),
            body: requestOptions.body,
            credentials: requestOptions.credentials
        }).then(function (response) {
            if (!response.ok) {
                return parseErrorMessage(response).then(function (message) {
                    throw new Error(message);
                });
            }

            return response;
        });
    }

    function parseItemIdFromHash() {
        var hash = window.location.hash || '';
        var qIndex = hash.indexOf('?');
        if (qIndex === -1) {
            return null;
        }

        var query = hash.slice(qIndex + 1);
        var params = new URLSearchParams(query);
        return params.get('id');
    }

    function parseItemIdFromContext() {
        var candidates = [
            window.Dashboard && window.Dashboard.getCurrentItemId && window.Dashboard.getCurrentItemId(),
            window.ViewManager && window.ViewManager._currentView && window.ViewManager._currentView.itemId,
            window.context && window.context.item && window.context.item.Id
        ];

        for (var i = 0; i < candidates.length; i += 1) {
            if (typeof candidates[i] === 'string' && candidates[i].length > 0) {
                return candidates[i];
            }
        }

        return null;
    }

    function getCurrentItemId() {
        return parseItemIdFromContext() || parseItemIdFromHash();
    }

    function navigateToGeneratorPage(itemId) {
        var route = '#!/gif-generator';
        if (itemId) {
            route += '?itemId=' + encodeURIComponent(itemId);
        }

        window.location.hash = route;
    }

    function isVideoItem(item) {
        if (!item) {
            return false;
        }

        var mediaType = item.MediaType || item.mediaType;
        var type = item.Type || item.type;
        return mediaType === 'Video' || type === 'Movie' || type === 'Episode' || type === 'Video';
    }

    function fetchItem(itemId) {
        return window.ApiClient.getItem(window.ApiClient.getCurrentUserId(), itemId);
    }

    function fetchSubtitles(itemId) {
        return apiRequest('/Plugins/GifGenerator/Subtitles/' + encodeURIComponent(itemId), {
            method: 'GET'
        }).then(function (response) {
            return response.json();
        }).then(function (payload) {
            return payload.subtitles || payload.Subtitles || [];
        });
    }

    function ensureStyles() {
        if (document.getElementById('gifGeneratorItemActionStyles')) {
            return;
        }

        var styles = document.createElement('style');
        styles.id = 'gifGeneratorItemActionStyles';
        styles.textContent = '\n            .' + BUTTON_CLASS + ' { margin-inline-start: .75em; }\n            #' + MODAL_ID + ' { border: 0; border-radius: 8px; max-width: 560px; width: calc(100vw - 2rem); background: var(--theme-body-background, #111); color: var(--theme-body-text-color, #fff); }\n            #' + MODAL_ID + '::backdrop { background: rgba(0, 0, 0, 0.55); }\n            #' + MODAL_ID + ' .gifGeneratorFormRow { display: flex; gap: .75em; flex-wrap: wrap; }\n            #' + MODAL_ID + ' .gifGeneratorFormRow .inputContainer { flex: 1 1 200px; min-width: 180px; }\n            #' + MODAL_ID + ' .gifGeneratorActions { display: flex; justify-content: flex-end; gap: .75em; margin-top: 1rem; }\n            #' + MODAL_ID + ' .gifGeneratorResult { margin-top: 1rem; word-break: break-word; }\n            #' + MODAL_ID + ' .gifGeneratorResult img { margin-top: .5rem; max-width: 100%; border-radius: 6px; }\n            #' + FALLBACK_BUTTON_ID + ' { position: fixed; right: 1rem; bottom: 1rem; z-index: 2147483640; border-radius: 999px; padding: .5rem .9rem; box-shadow: 0 4px 16px rgba(0,0,0,.35); font-size: .92rem; }\n            #' + FALLBACK_BUTTON_ID + '[hidden] { display: none !important; }\n            #' + FALLBACK_NOTICE_ID + ' { position: fixed; right: 1rem; bottom: 3.75rem; z-index: 2147483640; background: rgba(17, 17, 17, 0.92); color: var(--theme-body-text-color, #fff); border: 1px solid rgba(255,255,255,.2); border-radius: 6px; padding: .4rem .6rem; max-width: 320px; font-size: .82rem; }\n            #' + FALLBACK_NOTICE_ID + '[hidden] { display: none !important; }\n        ';

        document.head.appendChild(styles);
    }

    function parseTimecode(input) {
        var raw = input.trim();
        if (!raw) {
            throw new Error('Start time is required.');
        }

        var parts = raw.split(':');
        if (parts.length < 2 || parts.length > 3) {
            throw new Error('Start time must use mm:ss or hh:mm:ss format.');
        }

        var s = Number(parts[parts.length - 1]);
        var m = Number(parts[parts.length - 2]);
        var h = Number(parts.length === 3 ? parts[0] : 0);

        if (Number.isNaN(s) || Number.isNaN(m) || Number.isNaN(h) || s >= 60 || m >= 60 || s < 0 || m < 0 || h < 0) {
            throw new Error('Invalid start time.');
        }

        return (h * 3600) + (m * 60) + s;
    }

    function getOrCreateModal() {
        var existing = document.getElementById(MODAL_ID);
        if (existing) {
            return existing;
        }

        var modal = document.createElement('dialog');
        modal.id = MODAL_ID;
        modal.innerHTML = '\n            <form method="dialog" id="gifGeneratorItemActionForm">\n                <h2 style="margin-top:0;">Create GIF</h2>\n                <div class="fieldDescription" id="gifGeneratorItemDescription"></div>\n                <div class="gifGeneratorFormRow">\n                    <div class="inputContainer">\n                        <label class="inputLabel" for="gifGeneratorStart">Start time</label>\n                        <input id="gifGeneratorStart" is="emby-input" type="text" value="00:00" placeholder="mm:ss or hh:mm:ss" required />\n                    </div>\n                    <div class="inputContainer">\n                        <label class="inputLabel" for="gifGeneratorLength">Length (seconds)</label>\n                        <input id="gifGeneratorLength" is="emby-input" type="number" min="0.1" step="0.1" value="3" required />\n                    </div>\n                </div>\n                <div class="gifGeneratorFormRow">\n                    <div class="inputContainer">\n                        <label class="inputLabel" for="gifGeneratorSubtitle">Subtitle</label>\n                        <select id="gifGeneratorSubtitle" is="emby-select">\n                            <option value="">No subtitles</option>\n                        </select>\n                    </div>\n                    <div class="inputContainer">\n                        <label class="inputLabel" for="gifGeneratorSubtitleFontSize">Subtitle font size</label>\n                        <input id="gifGeneratorSubtitleFontSize" is="emby-input" type="number" min="1" step="1" placeholder="Plugin default when empty" />\n                    </div>\n                    <div class="inputContainer">\n                        <label class="inputLabel" for="gifGeneratorSubtitleTimingOffset">Subtitle timing offset</label>\n                        <input id="gifGeneratorSubtitleTimingOffset" is="emby-input" type="text" placeholder="Examples: 00:00:00.500, -00:00:01.250" />\n                    </div>\n                </div>\n                <div class="gifGeneratorFormRow">\n                    <div class="inputContainer">\n                        <label class="inputLabel" for="gifGeneratorFps">FPS</label>\n                        <input id="gifGeneratorFps" is="emby-input" type="number" min="0" step="1" placeholder="0 = plugin default" value="0" />\n                    </div>\n                    <div class="inputContainer">\n                        <label class="inputLabel" for="gifGeneratorWidth">Width</label>\n                        <input id="gifGeneratorWidth" is="emby-input" type="number" min="0" step="1" placeholder="0 = plugin default" value="0" />\n                    </div>\n                </div>\n                <div class="fieldDescription" id="gifGeneratorStatus"></div>\n                <div class="gifGeneratorResult" id="gifGeneratorResult"></div>\n                <div class="gifGeneratorActions">\n                    <button type="button" is="emby-button" class="emby-button button-cancel" id="gifGeneratorCloseBtn"><span>Close</span></button>\n                    <button type="submit" is="emby-button" class="emby-button raised button-submit"><span>Generate</span></button>\n                </div>\n            </form>\n        ';

        document.body.appendChild(modal);

        modal.querySelector('#gifGeneratorCloseBtn').addEventListener('click', function () {
            modal.close();
        });

        modal.querySelector('#gifGeneratorItemActionForm').addEventListener('submit', function (event) {
            event.preventDefault();
            submitCreateGif();
        });

        return modal;
    }

    function subtitleLabel(stream) {
        var parts = [];
        if (stream.language || stream.Language) {
            parts.push(stream.language || stream.Language);
        }

        if (stream.displayTitle || stream.DisplayTitle) {
            parts.push(stream.displayTitle || stream.DisplayTitle);
        }

        if ((stream.isDefault || stream.IsDefault) === true) {
            parts.push('Default');
        }

        if ((stream.isForced || stream.IsForced) === true) {
            parts.push('Forced');
        }

        return parts.length ? parts.join(' - ') : 'Subtitle stream ' + (stream.streamIndex || stream.StreamIndex);
    }

    function setStatus(text, error) {
        var node = document.getElementById('gifGeneratorStatus');
        node.textContent = text;
        node.style.color = error ? 'var(--theme-danger, #ff6666)' : 'var(--theme-primary-color, #52b54b)';
    }

    function setResult(downloadUrl, fileName) {
        var result = document.getElementById('gifGeneratorResult');
        result.innerHTML = '';

        var anchor = document.createElement('a');
        anchor.href = downloadUrl;
        anchor.target = '_blank';
        anchor.rel = 'noopener';
        anchor.textContent = 'Download ' + fileName;

        var img = document.createElement('img');
        img.alt = 'Generated GIF preview';
        img.src = appendCacheBuster(downloadUrl);

        result.appendChild(anchor);
        result.appendChild(img);
    }

    function populateSubtitleSelect(subtitles) {
        var select = document.getElementById('gifGeneratorSubtitle');
        select.innerHTML = '<option value="">No subtitles</option>';

        subtitles.forEach(function (stream) {
            var option = document.createElement('option');
            var idx = stream.streamIndex ?? stream.StreamIndex;
            var textBased = stream.isTextBased ?? stream.IsTextBased;
            option.value = idx;
            option.textContent = subtitleLabel(stream);
            if (textBased === false) {
                option.disabled = true;
            }

            select.appendChild(option);
        });
    }

    function openModalForItem(itemId, itemInfo) {
        var modal = getOrCreateModal();
        STATE.itemId = itemId;
        STATE.itemInfo = itemInfo;

        document.getElementById('gifGeneratorItemDescription').textContent = 'Item: ' + (itemInfo.Name || itemInfo.name || itemId);
        document.getElementById('gifGeneratorResult').innerHTML = '';
        setStatus('Loading subtitles...', false);
        populateSubtitleSelect([]);

        fetchSubtitles(itemId).then(function (subtitles) {
            STATE.subtitles = subtitles;
            populateSubtitleSelect(subtitles);
            setStatus('Ready.', false);
        }).catch(function (error) {
            setStatus('Could not load subtitles: ' + error.message, true);
        });

        if (!modal.open) {
            modal.showModal();
        }
    }

    function submitCreateGif() {
        if (!STATE.itemId) {
            setStatus('No current item selected.', true);
            return;
        }

        var startSeconds;
        try {
            startSeconds = parseTimecode(document.getElementById('gifGeneratorStart').value);
        } catch (error) {
            setStatus(error.message, true);
            return;
        }

        var payload = {
            itemId: STATE.itemId,
            startSeconds: startSeconds,
            lengthSeconds: Number(document.getElementById('gifGeneratorLength').value),
            fps: Number(document.getElementById('gifGeneratorFps').value) || 0,
            width: Number(document.getElementById('gifGeneratorWidth').value) || 0
        };

        var subtitle = document.getElementById('gifGeneratorSubtitle').value;
        if (subtitle !== '') {
            payload.subtitleStreamIndex = Number(subtitle);
        }

        var subtitleFontSize = document.getElementById('gifGeneratorSubtitleFontSize').value;
        if (subtitleFontSize !== '') {
            payload.subtitleFontSize = Number(subtitleFontSize);
        }

        var subtitleTimingOffset = document.getElementById('gifGeneratorSubtitleTimingOffset').value.trim();
        if (subtitleTimingOffset !== '') {
            payload.subtitleTimingOffset = subtitleTimingOffset;
        }

        setStatus('Generating GIF...', false);
        document.getElementById('gifGeneratorResult').innerHTML = '';

        apiRequest('/Plugins/GifGenerator/Create', {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json'
            },
            body: JSON.stringify(payload)
        }).then(function (response) {
            return response.json();
        }).then(function (data) {
            var path = data.downloadUrl || data.DownloadUrl;
            var fileName = data.fileName || data.FileName;
            var normalizedPath = path.charAt(0) === '/' ? path : '/' + path;
            var downloadUrl = getApiUrl(normalizedPath);
            setStatus('GIF generated.', false);
            setResult(downloadUrl, fileName);
        }).catch(function (error) {
            setStatus('Failed: ' + error.message, true);
        });
    }

    function findActionHost() {
        var selectors = [
            '.detailPagePrimaryContainer .mainDetailButtons',
            '.itemDetailPage .mainDetailButtons',
            '.itemDetailsPage .mainDetailButtons',
            '.detailPageContent .mainDetailButtons',
            '.detailPagePrimaryContainer .detailPagePrimaryActions',
            '.itemDetailPage .detailPagePrimaryActions',
            '.itemDetailsPage .detailPagePrimaryActions',
            '.itemDetailPage .detailPageButtons',
            '.detailPagePrimaryContainer .detailPageButtons',
            '.itemDetailsPage .detailPageButtons',
            '.detailPageContent .detailPageButtons',
            '.itemDetailPage .detailActions',
            '.detailPagePrimaryContainer .detailActions',
            '.itemDetailsPage .detailActions',
            '.itemDetailsPage .detailPagePrimaryContainer .buttonContainer'
        ];

        for (var i = 0; i < selectors.length; i += 1) {
            var found = document.querySelector(selectors[i]);
            if (found) {
                debugLog('Found primary host with selector:', selectors[i]);
                return found;
            }
        }

        debugLog('No primary action host found.');
        return null;
    }

    function createActionButton(className) {
        var button = document.createElement('button');
        button.className = className;
        button.type = 'button';
        button.dataset.itemId = '';
        button.innerHTML = '<span>Create GIF</span>';
        button.addEventListener('click', function (event) {
            var sourceButton = event.currentTarget;
            var currentItemId = sourceButton && sourceButton.dataset && sourceButton.dataset.itemId
                ? sourceButton.dataset.itemId
                : getCurrentItemId();

            if (!currentItemId) {
                return;
            }
            navigateToGeneratorPage(currentItemId);
        });

        return button;
    }

    function removeMenuAction() {
        var menuActions = document.querySelectorAll('.' + MENU_ACTION_CLASS);
        menuActions.forEach(function (action) {
            action.remove();
        });
    }

    function removePrimaryButtons() {
        var primaryButtons = document.querySelectorAll('.' + BUTTON_CLASS);
        primaryButtons.forEach(function (button) {
            button.remove();
        });
    }

    function getOrCreateFallbackButton() {
        var existing = document.getElementById(FALLBACK_BUTTON_ID);
        if (existing) {
            return existing;
        }

        var button = createActionButton('emby-button button-submit');
        button.id = FALLBACK_BUTTON_ID;
        button.hidden = true;
        button.setAttribute('aria-label', 'Create GIF');
        document.body.appendChild(button);
        return button;
    }

    function getOrCreateFallbackNotice() {
        var existing = document.getElementById(FALLBACK_NOTICE_ID);
        if (existing) {
            return existing;
        }

        var notice = document.createElement('div');
        notice.id = FALLBACK_NOTICE_ID;
        notice.hidden = true;
        document.body.appendChild(notice);
        return notice;
    }

    function hideFallbackAction() {
        var fallback = document.getElementById(FALLBACK_BUTTON_ID);
        if (fallback) {
            fallback.hidden = true;
        }

        var notice = document.getElementById(FALLBACK_NOTICE_ID);
        if (notice) {
            notice.hidden = true;
        }
    }

    function showFallbackAction(itemId, message) {
        var fallback = getOrCreateFallbackButton();
        fallback.dataset.itemId = itemId;
        fallback.hidden = false;

        var notice = getOrCreateFallbackNotice();
        if (message) {
            notice.textContent = message;
            notice.hidden = false;
        } else {
            notice.hidden = true;
        }
    }

    function upsertActionButton(itemId) {
        var host = findActionHost();
        if (!host) {
            return false;
        }

        var allButtons = document.querySelectorAll('.' + BUTTON_CLASS);
        allButtons.forEach(function (button) {
            if (!host.contains(button)) {
                button.remove();
            }
        });

        var existing = host.querySelector('.' + BUTTON_CLASS);
        if (existing) {
            existing.dataset.itemId = itemId;
            removeMenuAction();
            return true;
        }

        var button = createActionButton('detailButton emby-button ' + BUTTON_CLASS);
        button.dataset.itemId = itemId;
        host.appendChild(button);
        removeMenuAction();
        return true;
    }

    function getVisibleMenuHosts() {
        var selectors = [
            '.actionSheet.open .items',
            '.actionSheet.open',
            '.actionSheetMenu .items',
            '.paperMenu .items',
            '.mainDrawer .items',
            '.mainDrawer .actions',
            '.dashboardDocument .popupVisible [role="menu"]',
            '.dashboardDocument .popupVisible .items',
            '.dashboardDocument .dialog-opened [role="menu"]',
            '[role="menu"].visible',
            '[role="menu"]'
        ];

        for (var i = 0; i < selectors.length; i += 1) {
            var nodes = document.querySelectorAll(selectors[i]);
            for (var j = nodes.length - 1; j >= 0; j -= 1) {
                var node = nodes[j];
                if (!node || node.closest('#' + MODAL_ID)) {
                    continue;
                }

                var hasActionChildren = node.querySelector('button, a, .listItem, .actionSheetMenuItem');
                if (hasActionChildren) {
                    return node;
                }
            }
        }

        return null;
    }

    function upsertOverflowMenuAction(itemId) {
        var menuHost = getVisibleMenuHosts();
        if (!menuHost) {
            debugLog('No overflow menu host found for item', itemId);
            return false;
        }

        var existing = menuHost.querySelector('.' + MENU_ACTION_CLASS);
        if (existing) {
            existing.dataset.itemId = itemId;
            return true;
        }

        var action = createActionButton('listItem-button emby-button ' + MENU_ACTION_CLASS);
        action.dataset.itemId = itemId;
        action.style.width = '100%';
        action.style.textAlign = 'start';
        menuHost.appendChild(action);
        debugLog('Injected Create GIF into overflow menu.');
        return true;
    }

    function bindMoreButtons(itemId) {
        var moreSelectors = [
            '.itemDetailPage .btnMoreCommands',
            '.detailPagePrimaryContainer .btnMoreCommands',
            '.itemDetailPage button[title="More"]',
            '.itemDetailPage [aria-label="More"]',
            '.detailPagePrimaryContainer button[title="More"]',
            '.detailPagePrimaryContainer [aria-label="More"]'
        ];
        var selectors = moreSelectors.join(', ');
        var moreButtons = document.querySelectorAll(selectors);

        moreButtons.forEach(function (button) {
            button.dataset.gifGeneratorItemId = itemId;
            if (button.dataset.gifGeneratorBound === '1') {
                return;
            }

            button.dataset.gifGeneratorBound = '1';
            button.addEventListener('click', function () {
                var currentItemId = button.dataset.gifGeneratorItemId || getCurrentItemId();
                [10, 60, 180].forEach(function (delay) {
                    window.setTimeout(function () {
                        upsertOverflowMenuAction(currentItemId);
                    }, delay);
                });
            });
        });

        if (moreButtons.length === 0) {
            debugLog('No More/overflow buttons found for current detail view.');
        }
    }

    var refreshTimer = null;
    function refreshActionButton() {
        if (refreshTimer) {
            window.clearTimeout(refreshTimer);
        }

        refreshTimer = window.setTimeout(function () {
            var itemId = getCurrentItemId();
            if (!itemId || !window.ApiClient || !window.ApiClient.getCurrentUserId) {
                return;
            }

            fetchItem(itemId).then(function (itemInfo) {
                if (!isVideoItem(itemInfo)) {
                    removePrimaryButtons();
                    removeMenuAction();
                    hideFallbackAction();
                    return;
                }

                var hasPrimaryAction = upsertActionButton(itemId);
                if (!hasPrimaryAction) {
                    var fallbackMessage = 'Create GIF button could not be attached to this page layout. Using floating fallback action.';
                    debugLog(fallbackMessage, 'Item:', itemId);
                    bindMoreButtons(itemId);
                    showFallbackAction(itemId, fallbackMessage);
                    if (window.console && typeof window.console.warn === 'function') {
                        window.console.warn('[GifGeneratorItemAction] ' + fallbackMessage);
                    }
                    if (window.Dashboard && typeof window.Dashboard.alert === 'function') {
                        window.Dashboard.alert(fallbackMessage);
                    }
                    return;
                }

                hideFallbackAction();
            }).catch(function () {
                // Ignore route transitions where item metadata is unavailable.
            });
        }, 100);
    }

    ensureStyles();
    window.addEventListener('hashchange', refreshActionButton);
    document.addEventListener('viewshow', refreshActionButton, true);

    var observer = new MutationObserver(refreshActionButton);
    observer.observe(document.body, { childList: true, subtree: true });

    refreshActionButton();
})();
