(function () {
    'use strict';

    var MODAL_ID = 'gifGeneratorItemActionModal';
    var BUTTON_CLASS = 'gifGeneratorItemActionButton';
    var STATE = {
        itemId: null,
        itemInfo: null,
        subtitles: []
    };

    function getApiToken() {
        if (window.ApiClient && typeof window.ApiClient.accessToken === 'function') {
            return window.ApiClient.accessToken() || '';
        }

        return '';
    }

    function buildApiUrl(path) {
        var token = getApiToken();
        var separator = path.indexOf('?') === -1 ? '?' : '&';
        return token ? path + separator + 'api_key=' + encodeURIComponent(token) : path;
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
        return fetch(buildApiUrl('/Plugins/GifGenerator/Subtitles/' + encodeURIComponent(itemId)), {
            method: 'GET',
            credentials: 'same-origin'
        }).then(function (response) {
            if (!response.ok) {
                return response.text().then(function (text) {
                    throw new Error(text || ('Failed to load subtitles (' + response.status + ')'));
                });
            }

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
        styles.textContent = '\n            .' + BUTTON_CLASS + ' { margin-inline-start: .75em; }\n            #' + MODAL_ID + ' { border: 0; border-radius: 8px; max-width: 560px; width: calc(100vw - 2rem); background: var(--theme-body-background, #111); color: var(--theme-body-text-color, #fff); }\n            #' + MODAL_ID + '::backdrop { background: rgba(0, 0, 0, 0.55); }\n            #' + MODAL_ID + ' .gifGeneratorFormRow { display: flex; gap: .75em; flex-wrap: wrap; }\n            #' + MODAL_ID + ' .gifGeneratorFormRow .inputContainer { flex: 1 1 200px; min-width: 180px; }\n            #' + MODAL_ID + ' .gifGeneratorActions { display: flex; justify-content: flex-end; gap: .75em; margin-top: 1rem; }\n            #' + MODAL_ID + ' .gifGeneratorResult { margin-top: 1rem; word-break: break-word; }\n            #' + MODAL_ID + ' .gifGeneratorResult img { margin-top: .5rem; max-width: 100%; border-radius: 6px; }\n        ';

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
        modal.innerHTML = '\n            <form method="dialog" id="gifGeneratorItemActionForm">\n                <h2 style="margin-top:0;">Create GIF</h2>\n                <div class="fieldDescription" id="gifGeneratorItemDescription"></div>\n                <div class="gifGeneratorFormRow">\n                    <div class="inputContainer">\n                        <label class="inputLabel" for="gifGeneratorStart">Start time</label>\n                        <input id="gifGeneratorStart" is="emby-input" type="text" value="00:00" placeholder="mm:ss or hh:mm:ss" required />\n                    </div>\n                    <div class="inputContainer">\n                        <label class="inputLabel" for="gifGeneratorLength">Length (seconds)</label>\n                        <input id="gifGeneratorLength" is="emby-input" type="number" min="0.1" step="0.1" value="3" required />\n                    </div>\n                </div>\n                <div class="gifGeneratorFormRow">\n                    <div class="inputContainer">\n                        <label class="inputLabel" for="gifGeneratorSubtitle">Subtitle</label>\n                        <select id="gifGeneratorSubtitle" is="emby-select">\n                            <option value="">No subtitles</option>\n                        </select>\n                    </div>\n                    <div class="inputContainer">\n                        <label class="inputLabel" for="gifGeneratorFps">FPS</label>\n                        <input id="gifGeneratorFps" is="emby-input" type="number" min="0" step="1" placeholder="0 = plugin default" value="0" />\n                    </div>\n                    <div class="inputContainer">\n                        <label class="inputLabel" for="gifGeneratorWidth">Width</label>\n                        <input id="gifGeneratorWidth" is="emby-input" type="number" min="0" step="1" placeholder="0 = plugin default" value="0" />\n                    </div>\n                </div>\n                <div class="fieldDescription" id="gifGeneratorStatus"></div>\n                <div class="gifGeneratorResult" id="gifGeneratorResult"></div>\n                <div class="gifGeneratorActions">\n                    <button type="button" is="emby-button" class="emby-button button-cancel" id="gifGeneratorCloseBtn"><span>Close</span></button>\n                    <button type="submit" is="emby-button" class="emby-button raised button-submit"><span>Generate</span></button>\n                </div>\n            </form>\n        ';

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
        img.src = downloadUrl + '&_=' + Date.now();

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

        setStatus('Generating GIF...', false);
        document.getElementById('gifGeneratorResult').innerHTML = '';

        fetch(buildApiUrl('/Plugins/GifGenerator/Create'), {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json'
            },
            credentials: 'same-origin',
            body: JSON.stringify(payload)
        }).then(function (response) {
            if (!response.ok) {
                return response.text().then(function (text) {
                    throw new Error(text || ('Create failed (' + response.status + ')'));
                });
            }

            return response.json();
        }).then(function (data) {
            var path = data.downloadUrl || data.DownloadUrl;
            var fileName = data.fileName || data.FileName;
            var normalizedPath = path.charAt(0) === '/' ? path : '/' + path;
            var downloadUrl = buildApiUrl(normalizedPath);
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
            '.detailPagePrimaryContainer .detailPagePrimaryActions',
            '.itemDetailPage .detailPagePrimaryActions'
        ];

        for (var i = 0; i < selectors.length; i += 1) {
            var found = document.querySelector(selectors[i]);
            if (found) {
                return found;
            }
        }

        return null;
    }

    function upsertActionButton(itemId, itemInfo) {
        var host = findActionHost();
        if (!host) {
            return;
        }

        var existing = host.querySelector('.' + BUTTON_CLASS);
        if (existing) {
            existing.dataset.itemId = itemId;
            return;
        }

        var button = document.createElement('button');
        button.className = 'detailButton emby-button ' + BUTTON_CLASS;
        button.type = 'button';
        button.dataset.itemId = itemId;
        button.innerHTML = '<span>Create GIF</span>';
        button.addEventListener('click', function () {
            openModalForItem(itemId, itemInfo);
        });

        host.appendChild(button);
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
                    return;
                }

                upsertActionButton(itemId, itemInfo);
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
