(function () {
    'use strict';

    // Optional enhancement entry-point only.
    // This helper must stay non-blocking and must never replace the canonical #!/gif-generator route.
    var BUTTON_CLASS = 'gifGeneratorItemActionButton';

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
                return found;
            }
        }

        return null;
    }

    function createActionButton() {
        var button = document.createElement('button');
        button.className = 'detailButton emby-button ' + BUTTON_CLASS;
        button.type = 'button';
        button.dataset.itemId = '';
        button.innerHTML = '<span>Create GIF</span>';
        button.addEventListener('click', function (event) {
            var sourceButton = event.currentTarget;
            var currentItemId = sourceButton && sourceButton.dataset && sourceButton.dataset.itemId
                ? sourceButton.dataset.itemId
                : getCurrentItemId();

            if (currentItemId) {
                navigateToGeneratorPage(currentItemId);
            }
        });

        return button;
    }

    function removePrimaryButtons() {
        var primaryButtons = document.querySelectorAll('.' + BUTTON_CLASS);
        primaryButtons.forEach(function (button) {
            button.remove();
        });
    }

    function upsertActionButton(itemId) {
        var host = findActionHost();
        if (!host || !itemId) {
            removePrimaryButtons();
            return;
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
            return;
        }

        var button = createActionButton();
        button.dataset.itemId = itemId;
        host.appendChild(button);
    }

    function refreshActionButton() {
        upsertActionButton(getCurrentItemId());
    }

    window.addEventListener('hashchange', refreshActionButton);
    document.addEventListener('viewshow', refreshActionButton, true);

    refreshActionButton();
})();
