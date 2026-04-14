using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Template.Api.Controllers;

/// <summary>
/// Demo endpoint intended for integration with the Plugin Pages sidebar plugin.
/// </summary>
[ApiController]
[Authorize]
[Route("PluginTemplate/Demo")]
public class PluginPagesDemoController : ControllerBase
{
    /// <summary>
    /// Returns a standalone GIF generator page for Plugin Pages.
    /// </summary>
    /// <returns>Static HTML page.</returns>
    [HttpGet("Hello")]
    [Produces("text/html")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<string> Hello()
    {
        const string html = """
                            <!doctype html>
                            <html lang="en">
                            <head>
                                <meta charset="utf-8">
                                <meta name="viewport" content="width=device-width,initial-scale=1">
                                <title>GIF Generator</title>
                                <style>
                                    :root {
                                        color-scheme: dark;
                                    }

                                    body {
                                        margin: 0;
                                        padding: 1rem;
                                        background: #101214;
                                        color: #fff;
                                        font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
                                    }

                                    .container {
                                        max-width: 820px;
                                        margin: 0 auto;
                                    }

                                    .inputContainer {
                                        margin: 0.75rem 0;
                                    }

                                    label {
                                        display: block;
                                        margin-bottom: 0.25rem;
                                        color: #c9d1d9;
                                    }

                                    input, select, button {
                                        box-sizing: border-box;
                                        width: 100%;
                                        padding: 0.6rem;
                                        border: 1px solid rgba(255, 255, 255, 0.2);
                                        border-radius: 4px;
                                        background: #171b1f;
                                        color: #fff;
                                    }

                                    button {
                                        cursor: pointer;
                                        background: #2f81f7;
                                        border: none;
                                        width: auto;
                                        min-width: 9rem;
                                    }

                                    .row {
                                        display: flex;
                                        gap: 0.6rem;
                                        flex-wrap: wrap;
                                        margin-top: 0.75rem;
                                    }

                                    #GifItemSearchResults {
                                        margin-top: 0.35rem;
                                        border: 1px solid rgba(255, 255, 255, 0.2);
                                        border-radius: 4px;
                                        max-height: 220px;
                                        overflow-y: auto;
                                        padding: 0.4rem;
                                    }

                                    .result-button {
                                        width: 100%;
                                        text-align: left;
                                        margin: 0;
                                        background: transparent;
                                        border-radius: 3px;
                                        padding: 0.35rem;
                                    }

                                    .result-button:hover {
                                        background: rgba(255, 255, 255, 0.1);
                                    }

                                    #GifStatus {
                                        margin-top: 1rem;
                                        color: #7ee787;
                                    }

                                    #GifResult img {
                                        display: block;
                                        margin-top: 0.5rem;
                                        max-width: 100%;
                                        border-radius: 4px;
                                    }

                                    .hint {
                                        color: #8b949e;
                                        font-size: 0.95rem;
                                    }
                                </style>
                            </head>
                            <body>
                                <div class="container">
                                    <h2>Create GIF</h2>
                                    <p class="hint">Search for a video item, set timing/options, optionally load subtitles, then generate.</p>

                                    <form id="GifGeneratorCreateForm">
                                        <div class="inputContainer">
                                            <label for="GifItemSearch">Item Search</label>
                                            <input id="GifItemSearch" type="text" placeholder="Search movies, episodes, videos..." autocomplete="off">
                                            <div id="GifItemSearchResults" role="listbox" aria-label="Item search results">Type at least 2 characters to search video items.</div>
                                            <div id="GifItemSelectedLabel" class="hint" style="margin-top:.5rem;">Selected: none</div>
                                        </div>

                                        <div class="inputContainer">
                                            <label for="GifItemId">Item ID (auto-filled)</label>
                                            <input id="GifItemId" type="text" readonly aria-readonly="true">
                                        </div>

                                        <div class="inputContainer">
                                            <label for="GifStartTime">Start Time (mm:ss or hh:mm:ss)</label>
                                            <input id="GifStartTime" type="text" value="00:00" required>
                                        </div>

                                        <div class="inputContainer">
                                            <label for="GifLength">Length (seconds)</label>
                                            <input id="GifLength" type="number" min="0.1" step="0.1" value="3" required>
                                        </div>

                                        <div class="inputContainer">
                                            <label for="GifFps">FPS (0 = plugin default)</label>
                                            <input id="GifFps" type="number" min="0" step="1" value="0">
                                        </div>

                                        <div class="inputContainer">
                                            <label for="GifWidth">Width (0 = plugin default)</label>
                                            <input id="GifWidth" type="number" min="0" step="1" value="0">
                                        </div>

                                        <div class="inputContainer">
                                            <label for="GifSubtitle">Subtitle Stream</label>
                                            <select id="GifSubtitle">
                                                <option value="">No subtitles</option>
                                            </select>
                                        </div>

                                        <div class="row">
                                            <button id="LoadSubtitlesButton" type="button">Load Subtitles</button>
                                            <button type="submit">Create GIF</button>
                                        </div>

                                        <div id="GifStatus"></div>
                                        <div id="GifResult" style="margin-top:1rem;"></div>
                                    </form>
                                </div>

                                <script>
                                    (function () {
                                        'use strict';

                                        var currentResults = [];
                                        var searchDebounce = null;
                                        var currentBlobUrl = null;

                                        function setStatus(text, isError) {
                                            var node = document.getElementById('GifStatus');
                                            node.textContent = text;
                                            node.style.color = isError ? '#ff7b72' : '#7ee787';
                                        }

                                        function getAccessToken() {
                                            if (!window.ApiClient) {
                                                return '';
                                            }

                                            if (typeof window.ApiClient.accessToken === 'function') {
                                                return window.ApiClient.accessToken() || '';
                                            }

                                            if (window.ApiClient._serverInfo && window.ApiClient._serverInfo.AccessToken) {
                                                return window.ApiClient._serverInfo.AccessToken;
                                            }

                                            return '';
                                        }

                                        function getCurrentUserId() {
                                            if (!window.ApiClient) {
                                                return '';
                                            }

                                            if (typeof window.ApiClient.getCurrentUserId === 'function') {
                                                return window.ApiClient.getCurrentUserId() || '';
                                            }

                                            if (typeof window.ApiClient.getCurrentUser === 'function') {
                                                var user = window.ApiClient.getCurrentUser();
                                                return user && user.Id ? user.Id : '';
                                            }

                                            if (window.ApiClient._serverInfo && window.ApiClient._serverInfo.UserId) {
                                                return window.ApiClient._serverInfo.UserId;
                                            }

                                            return '';
                                        }

                                        function requireSession() {
                                            var token = getAccessToken();
                                            var userId = getCurrentUserId();
                                            if (!token || !userId) {
                                                throw new Error('Missing Jellyfin session. Sign in and open this page again.');
                                            }

                                            return { token: token, userId: userId };
                                        }

                                        function getBaseUrl() {
                                            if (window.ApiClient && window.ApiClient._serverAddress) {
                                                return String(window.ApiClient._serverAddress).replace(/\/+$/, '');
                                            }

                                            var webIndex = window.location.pathname.indexOf('/web/');
                                            if (webIndex >= 0) {
                                                return (window.location.origin + window.location.pathname.slice(0, webIndex)).replace(/\/+$/, '');
                                            }

                                            return window.location.origin.replace(/\/+$/, '');
                                        }

                                        function buildAuthHeaders(extra) {
                                            var session = requireSession();
                                            var headers = Object.assign({}, extra || {});
                                            var authHeader;

                                            headers['X-Emby-Token'] = session.token;

                                            if (window.ApiClient && typeof window.ApiClient.getAuthorizationHeader === 'function') {
                                                authHeader = window.ApiClient.getAuthorizationHeader();
                                                headers.Authorization = authHeader;
                                                headers['X-Emby-Authorization'] = authHeader;
                                            } else {
                                                authHeader = 'MediaBrowser Client="PluginTemplate", Device="Plugin Pages", DeviceId="plugin-pages", Version="1.0.0", Token="' + session.token + '"';
                                                headers['X-Emby-Authorization'] = authHeader;
                                            }

                                            return headers;
                                        }

                                        function apiFetch(path, options) {
                                            var opts = options || {};
                                            return fetch(getBaseUrl() + (path.charAt(0) === '/' ? path : '/' + path), {
                                                method: opts.method || 'GET',
                                                headers: buildAuthHeaders(opts.headers),
                                                body: opts.body,
                                                credentials: 'same-origin'
                                            }).then(function (response) {
                                                if (!response.ok) {
                                                    return response.text().then(function (t) {
                                                        throw new Error(t || ('Request failed (' + response.status + ')'));
                                                    });
                                                }

                                                return response;
                                            });
                                        }

                                        function parseTimecode(raw) {
                                            var value = String(raw || '').trim();
                                            var parts = value.split(':');
                                            var hours = 0;
                                            var minutes;
                                            var seconds;

                                            if (parts.length !== 2 && parts.length !== 3) {
                                                throw new Error('Start time must use mm:ss or hh:mm:ss format.');
                                            }

                                            if (parts.length === 3) {
                                                hours = Number(parts[0]);
                                                minutes = Number(parts[1]);
                                                seconds = Number(parts[2]);
                                            } else {
                                                minutes = Number(parts[0]);
                                                seconds = Number(parts[1]);
                                            }

                                            if ([hours, minutes, seconds].some(function (n) { return Number.isNaN(n) || n < 0; }) || minutes >= 60 || seconds >= 60) {
                                                throw new Error('Invalid start time value.');
                                            }

                                            return (hours * 3600) + (minutes * 60) + seconds;
                                        }

                                        function itemLabel(item) {
                                            var title = item.Name || 'Untitled';
                                            var year = item.ProductionYear ? ' (' + item.ProductionYear + ')' : '';
                                            return title + year;
                                        }

                                        function setSelectedItem(item) {
                                            var id = item ? (item.Id || item.id || '') : '';
                                            document.getElementById('GifItemId').value = id;
                                            document.getElementById('GifItemSelectedLabel').textContent = id ? ('Selected: ' + itemLabel(item)) : 'Selected: none';
                                        }

                                        function renderResults(items) {
                                            var node = document.getElementById('GifItemSearchResults');
                                            node.innerHTML = '';
                                            currentResults = items;

                                            if (!items.length) {
                                                node.textContent = 'No matching video items found.';
                                                return;
                                            }

                                            items.forEach(function (item, index) {
                                                var b = document.createElement('button');
                                                b.type = 'button';
                                                b.className = 'result-button';
                                                b.setAttribute('role', 'option');
                                                b.textContent = itemLabel(item);
                                                b.addEventListener('click', function () {
                                                    setSelectedItem(currentResults[index]);
                                                    node.textContent = 'Item selected. Search again to choose a different one.';
                                                });
                                                node.appendChild(b);
                                            });
                                        }

                                        function searchItems(query) {
                                            var session;
                                            if (!query || query.length < 2) {
                                                document.getElementById('GifItemSearchResults').textContent = 'Type at least 2 characters to search video items.';
                                                return;
                                            }

                                            session = requireSession();
                                            document.getElementById('GifItemSearchResults').textContent = 'Loading...';

                                            return apiFetch('/Users/' + encodeURIComponent(session.userId) + '/Items?Recursive=true&SearchTerm=' + encodeURIComponent(query) + '&IncludeItemTypes=Movie,Episode,Video,MusicVideo,Trailer&Fields=MediaType,ProductionYear&Limit=20&SortBy=SortName')
                                                .then(function (r) { return r.json(); })
                                                .then(function (payload) {
                                                    var items = payload.Items || [];
                                                    var videos = items.filter(function (item) {
                                                        var mediaType = String(item.MediaType || '').toLowerCase();
                                                        var type = String(item.Type || '').toLowerCase();
                                                        return mediaType === 'video' || ['movie', 'episode', 'video', 'musicvideo', 'trailer'].indexOf(type) >= 0;
                                                    });
                                                    renderResults(videos);
                                                })
                                                .catch(function (err) {
                                                    setStatus('Search failed: ' + err.message, true);
                                                    document.getElementById('GifItemSearchResults').textContent = 'Search failed.';
                                                });
                                        }

                                        function loadSubtitles() {
                                            var itemId = document.getElementById('GifItemId').value.trim();
                                            var select = document.getElementById('GifSubtitle');

                                            if (!itemId) {
                                                setStatus('Select an item before loading subtitles.', true);
                                                return;
                                            }

                                            setStatus('Loading subtitles...', false);
                                            select.innerHTML = '<option value="">No subtitles</option>';

                                            apiFetch('/Plugins/GifGenerator/Subtitles/' + encodeURIComponent(itemId))
                                                .then(function (r) { return r.json(); })
                                                .then(function (payload) {
                                                    var subtitles = payload.subtitles || payload.Subtitles || [];
                                                    subtitles.forEach(function (s) {
                                                        var option = document.createElement('option');
                                                        var idx = s.streamIndex != null ? s.streamIndex : s.StreamIndex;
                                                        option.value = idx;
                                                        option.textContent = (s.language || s.Language || 'und') + ' - ' + (s.displayTitle || s.DisplayTitle || ('Stream ' + idx));
                                                        select.appendChild(option);
                                                    });
                                                    setStatus('Subtitles loaded.', false);
                                                })
                                                .catch(function (err) {
                                                    setStatus('Could not load subtitles: ' + err.message, true);
                                                });
                                        }

                                        function clearResult() {
                                            if (currentBlobUrl) {
                                                URL.revokeObjectURL(currentBlobUrl);
                                                currentBlobUrl = null;
                                            }

                                            document.getElementById('GifResult').innerHTML = '';
                                        }

                                        function showResult(fileName) {
                                            return apiFetch('/Plugins/GifGenerator/Download/' + encodeURIComponent(fileName))
                                                .then(function (r) { return r.blob(); })
                                                .then(function (blob) {
                                                    var node = document.getElementById('GifResult');
                                                    var url = URL.createObjectURL(blob);
                                                    var link = document.createElement('a');
                                                    var img = document.createElement('img');

                                                    clearResult();
                                                    currentBlobUrl = url;

                                                    link.href = url;
                                                    link.download = fileName;
                                                    link.textContent = 'Download ' + fileName;

                                                    img.src = url;
                                                    img.alt = 'Generated GIF preview';

                                                    node.appendChild(link);
                                                    node.appendChild(img);
                                                });
                                        }

                                        function createGif(e) {
                                            var itemId;
                                            var payload;
                                            var subtitleValue;
                                            var startSeconds;

                                            e.preventDefault();
                                            try {
                                                requireSession();
                                            } catch (err) {
                                                setStatus(err.message, true);
                                                return false;
                                            }

                                            itemId = document.getElementById('GifItemId').value.trim();
                                            if (!itemId) {
                                                setStatus('Item ID is required.', true);
                                                return false;
                                            }

                                            try {
                                                startSeconds = parseTimecode(document.getElementById('GifStartTime').value);
                                            } catch (err) {
                                                setStatus(err.message, true);
                                                return false;
                                            }

                                            payload = {
                                                itemId: itemId,
                                                startSeconds: startSeconds,
                                                lengthSeconds: Number(document.getElementById('GifLength').value),
                                                fps: Number(document.getElementById('GifFps').value) || 0,
                                                width: Number(document.getElementById('GifWidth').value) || 0
                                            };

                                            subtitleValue = document.getElementById('GifSubtitle').value;
                                            if (subtitleValue !== '') {
                                                payload.subtitleStreamIndex = Number(subtitleValue);
                                            }

                                            clearResult();
                                            setStatus('Generating GIF...', false);

                                            apiFetch('/Plugins/GifGenerator/Create', {
                                                method: 'POST',
                                                headers: { 'Content-Type': 'application/json' },
                                                body: JSON.stringify(payload)
                                            }).then(function (r) {
                                                return r.json();
                                            }).then(function (data) {
                                                var fileName = data.fileName || data.FileName;
                                                if (!fileName) {
                                                    throw new Error('Create response did not include a file name.');
                                                }

                                                return showResult(fileName);
                                            }).then(function () {
                                                setStatus('GIF generated.', false);
                                            }).catch(function (err) {
                                                setStatus('Failed: ' + err.message, true);
                                            });

                                            return false;
                                        }

                                        function hydrateFromQuery() {
                                            var params = new URLSearchParams(window.location.search || '');
                                            var itemId = (params.get('itemId') || params.get('id') || '').trim();
                                            var start = (params.get('start') || params.get('startTime') || params.get('t') || '').trim();

                                            if (itemId) {
                                                setSelectedItem({ Id: itemId, Name: itemId });
                                            }

                                            if (start) {
                                                try {
                                                    parseTimecode(start);
                                                    document.getElementById('GifStartTime').value = start;
                                                } catch (err) {
                                                    setStatus('Start-time prefill ignored: ' + err.message, true);
                                                }
                                            }
                                        }

                                        function init() {
                                            try {
                                                requireSession();
                                                setStatus('Ready.', false);
                                            } catch (err) {
                                                setStatus(err.message, true);
                                            }

                                            hydrateFromQuery();

                                            document.getElementById('GifGeneratorCreateForm').addEventListener('submit', createGif);
                                            document.getElementById('LoadSubtitlesButton').addEventListener('click', loadSubtitles);
                                            document.getElementById('GifItemSearch').addEventListener('input', function (e) {
                                                var query = e.target.value.trim();
                                                clearTimeout(searchDebounce);
                                                searchDebounce = setTimeout(function () {
                                                    searchItems(query);
                                                }, 300);
                                            });
                                        }

                                        init();
                                    })();
                                </script>
                            </body>
                            </html>
                            """;

        return Content(html, "text/html");
    }
}
