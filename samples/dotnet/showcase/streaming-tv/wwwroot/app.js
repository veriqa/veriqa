/*
 * Nova Stream — the television client.
 *
 * Target syntax is ES2018, and that is a hard limit rather than a preference: Tizen 5.5 and webOS 5
 * televisions run Chromium 68-69, where optional chaining (?.), nullish coalescing (??), top-level
 * await and an optional catch binding are PARSE errors — the whole file fails to load, not the one
 * line that used them. The eslint block for samples/dotnet/showcase in the repository root pins
 * ecmaVersion to 2018 so the limit is checked rather than remembered.
 */

(function () {
  'use strict';

  var API_SESSION = '/api/session';
  var API_WELCOME_SEEN = '/api/session/welcome-seen';
  var API_CATALOG = '/api/catalog';
  var API_PURCHASE = '/api/catalog/{id}/purchase';
  var API_PURCHASE_RESULT = '/api/purchases/';

  /* The query parameter the server puts on the address when a sign-in ended in an error (SampleRoutes.SignInFailed). */
  var SIGN_IN_PARAM = 'signin';
  var SIGN_IN_FAILED = 'failed';

  /* How often the page asks the backend whether the confirmation has ended. */
  var POLL_INTERVAL_MS = 2000;

  /* How many polls in a row may fail before the window gives up and offers a retry. A poll that
     answers resets the count, so a television that loses its network for a moment recovers by
     itself; sixty seconds of silence is a lost connection, not a slow person. */
  var MAX_CONSECUTIVE_POLL_FAILURES = 30;

  /* How long the window stays up after a purchase goes through: long enough to read that it did. */
  var PURCHASED_CLOSE_DELAY_MS = 2000;

  /* Key codes of the "back" button on the two television platforms, next to the desktop Backspace. */
  var KEY_BACK_TIZEN = 10009;
  var KEY_BACK_WEBOS = 461;

  /* Enter, and the OK button of a remote, which the platforms deliver as the same key code. */
  var KEY_ENTER = 13;

  /* The refusal of a purchase the viewer's age closes (SampleErrors.AgeRestricted on the server). */
  var ERROR_AGE_RESTRICTED = 'age_restricted';

  /* The channel type of Email as the session reports it (ChannelTypes.Email in the core). */
  var CHANNEL_EMAIL = 'email';

  var state = {
    profile: null,
    welcomeSeen: false,
    titles: [],
    poll: null,
    autoClose: null
  };

  /* ---------------------------------------------------------------- plumbing */

  function byId(id) {
    return document.getElementById(id);
  }

  function request(url, options) {
    var settings = options || {};
    settings.credentials = 'same-origin';
    settings.headers = settings.headers || {};

    return fetch(url, settings).then(function (response) {
      if (response.status === 204) {
        return null;
      }

      return response.text().then(function (body) {
        var parsed;
        try {
          parsed = body ? JSON.parse(body) : null;
        } catch (error) {
          parsed = null;
        }

        if (!response.ok) {
          var failure = new Error('HTTP ' + response.status);
          failure.status = response.status;
          failure.body = parsed;
          throw failure;
        }

        return parsed;
      });
    });
  }

  function clear(node) {
    while (node.firstChild) {
      node.removeChild(node.firstChild);
    }
  }

  function initialsOf(displayName) {
    var parts = String(displayName || '').split(/\s+/).filter(function (part) {
      return part.length > 0;
    });

    if (parts.length === 0) {
      return '?';
    }

    if (parts.length === 1) {
      return parts[0].charAt(0).toUpperCase();
    }

    return (parts[0].charAt(0) + parts[parts.length - 1].charAt(0)).toUpperCase();
  }

  /* Draws the avatar, or the person's initials when the channel gave no picture. A Telegram account
     with a hidden profile photo is the ordinary case, not an error. */
  function renderAvatar(container, profile) {
    clear(container);

    if (profile.avatar) {
      var image = document.createElement('img');
      image.src = profile.avatar;
      image.alt = '';
      container.appendChild(image);
      return;
    }

    var initials = document.createElement('div');
    initials.className = 'initials';
    initials.textContent = initialsOf(profile.displayName);
    container.appendChild(initials);
  }

  /* What the viewer told Veriqa on its form during the sign-in, as one line: "Age 34 · Likes Sci-Fi".
     The page is given the age and never the date it was worked out from; either part may be missing,
     and with both missing the line is empty. */
  function traitsOf(profile) {
    var parts = [];

    if (profile.age !== null && profile.age !== undefined) {
      parts.push('Age ' + profile.age);
    }

    if (profile.preferredGenre) {
      parts.push('Likes ' + profile.preferredGenre);
    }

    return parts.join(' · ');
  }

  /* The username line of the card. A messenger hands over a handle, shown as "@handle". Email hands
     over the alias of the sender ("Anna <anna@example.com>") or, with no alias, the part of the
     address before "@" — that one only repeats the address, and neither is a handle. */
  function usernameText(profile) {
    if (!profile.username) {
      return null;
    }

    if (profile.channelType !== CHANNEL_EMAIL) {
      return '@' + profile.username;
    }

    var address = String(profile.email || profile.displayName || '');
    var localPart = address.substring(0, address.lastIndexOf('@'));

    return profile.username === localPart ? null : profile.username;
  }

  /* The card of the person, one line per field the channel actually gave. A field that came back
     null is left out entirely rather than drawn as an empty line. */
  function renderProfileFields(container, profile) {
    clear(container);

    var name = document.createElement('p');
    name.className = 'field-name';
    name.textContent = profile.displayName;
    container.appendChild(name);

    var fullName = [profile.givenName, profile.familyName].filter(Boolean).join(' · ');
    var lines = [
      { text: fullName, className: 'field' },
      { text: profile.channelType, className: 'field field-channel' },
      { text: usernameText(profile), className: 'field' },
      { text: profile.channelUserIdMasked ? 'ID ' + profile.channelUserIdMasked : null, className: 'field' },
      { text: profile.phoneMasked, className: 'field' },
      { text: profile.email, className: 'field' },
      { text: traitsOf(profile), className: 'field' }
    ];

    lines.forEach(function (line) {
      /* A field equal to the name above is not drawn twice: Email gives its address as both. */
      if (!line.text || line.text === profile.displayName) {
        return;
      }

      var node = document.createElement('p');
      node.className = line.className;
      node.textContent = line.text;
      container.appendChild(node);
    });
  }

  /* ---------------------------------------------------------------- cover art */

  /* Covers are generated, not downloaded: the sample loads nothing from outside its own origin, so
     there are no image files and no font files anywhere in wwwroot. The same title always gets the
     same cover because the hue comes from a hash of its id. */
  function hashOf(value) {
    var hash = 5381;
    for (var index = 0; index < value.length; index++) {
      hash = ((hash << 5) + hash + value.charCodeAt(index)) & 0xffffffff;
    }

    return Math.abs(hash);
  }

  function coverSvg(title) {
    var hash = hashOf(title.id);
    var hue = hash % 360;
    var secondHue = (hue + 40 + (hash % 80)) % 360;
    var gradientId = 'cover-' + title.id;
    var bandTop = 20 + (hash % 40);
    var circleX = 30 + (hash % 45);

    /* No xmlns attribute: assigning this to innerHTML runs the HTML parser, which puts <svg> into
       the SVG namespace by itself. Spelling the namespace out would also put the only absolute URL
       in the whole of wwwroot into a file that is meant to load nothing from outside its origin. */
    return '<svg viewBox="0 0 320 220" preserveAspectRatio="none">'
      + '<defs><linearGradient id="' + gradientId + '" x1="0" y1="0" x2="1" y2="1">'
      + '<stop offset="0%" stop-color="hsl(' + hue + ', 62%, 36%)"/>'
      + '<stop offset="100%" stop-color="hsl(' + secondHue + ', 58%, 16%)"/>'
      + '</linearGradient></defs>'
      + '<rect width="320" height="220" fill="url(#' + gradientId + ')"/>'
      + '<circle cx="' + circleX + '" cy="' + bandTop + '" r="70" fill="hsl(' + secondHue + ', 70%, 60%)" opacity="0.18"/>'
      + '<rect x="0" y="' + (140 + (hash % 30)) + '" width="320" height="6" fill="#ffffff" opacity="0.12"/>'
      + '</svg>';
  }

  /* ---------------------------------------------------------------- screens */

  var SCREENS = ['screen-splash', 'screen-welcome', 'screen-catalog'];

  function showScreen(id) {
    SCREENS.forEach(function (screenId) {
      byId(screenId).hidden = screenId !== id;
    });

    focusFirst();
  }

  function renderCatalog() {
    var grid = byId('catalog-grid');
    clear(grid);

    state.titles.forEach(function (title) {
      var tile = document.createElement('div');
      tile.className = 'tile';

      var cover = document.createElement('div');
      cover.className = 'tile-cover';
      cover.innerHTML = coverSvg(title);
      tile.appendChild(cover);

      /* The server has already put the titles of the viewer's favourite genre first; the badge says
         why they are there. */
      if (title.recommended) {
        var badge = document.createElement('span');
        badge.className = 'tile-badge';
        badge.textContent = '★ For you';
        tile.appendChild(badge);
      }

      var body = document.createElement('div');
      body.className = 'tile-body';

      var name = document.createElement('p');
      name.className = 'tile-title';
      name.textContent = title.title;
      body.appendChild(name);

      var meta = document.createElement('p');
      meta.className = 'tile-meta';
      meta.textContent = title.genre + ' · ' + title.year
        + (title.minimumAge > 0 ? ' · ' + title.minimumAge + '+' : '');
      body.appendChild(meta);

      if (title.owned) {
        var owned = document.createElement('span');
        owned.className = 'tile-owned';
        owned.textContent = '✓ In your library';
        body.appendChild(owned);
      } else if (title.restricted) {
        /* Still a control the remote lands on — a tile the focus skipped would look broken — but it
           explains instead of buying. The server refuses the purchase whatever is drawn here. */
        var locked = document.createElement('button');
        locked.className = 'tile-locked';
        locked.type = 'button';
        locked.setAttribute('data-focusable', '');
        locked.textContent = title.minimumAge + '+ only';
        locked.addEventListener('click', function () {
          showRestricted(title);
        });
        body.appendChild(locked);
      } else {
        var buy = document.createElement('button');
        buy.className = 'tile-buy';
        buy.type = 'button';
        buy.setAttribute('data-focusable', '');
        buy.textContent = 'Buy · ' + title.price + ' ' + title.currency;
        buy.addEventListener('click', function () {
          startPurchase(title);
        });
        body.appendChild(buy);
      }

      tile.appendChild(body);
      grid.appendChild(tile);
    });
  }

  function loadCatalog() {
    return request(API_CATALOG).then(function (answer) {
      state.titles = answer.titles;
      renderCatalog();
    });
  }

  function enterCatalog() {
    return loadCatalog().then(function () {
      showScreen('screen-catalog');
    });
  }

  function loadSession() {
    return request(API_SESSION).then(function (session) {
      state.profile = session.profile;
      state.welcomeSeen = session.welcomeSeen;

      if (!session.authenticated) {
        byId('signin-failed').hidden = new URLSearchParams(window.location.search).get(SIGN_IN_PARAM) !== SIGN_IN_FAILED;
        showScreen('screen-splash');
        return null;
      }

      renderAvatar(byId('catalog-avatar'), session.profile);
      byId('catalog-viewer').textContent = session.profile.displayName;
      byId('catalog-traits').textContent = traitsOf(session.profile);

      if (session.welcomeSeen) {
        return enterCatalog();
      }

      byId('welcome-registered').textContent = 'You are registered as ' + session.profile.displayName;
      renderAvatar(byId('welcome-avatar'), session.profile);
      renderProfileFields(byId('welcome-fields'), session.profile);
      showScreen('screen-welcome');

      return null;
    });
  }

  /* ---------------------------------------------------------------- purchase */

  function modalButton(label, onClick) {
    var button = document.createElement('button');
    button.className = 'button button-ghost';
    button.type = 'button';
    button.setAttribute('data-focusable', '');
    button.textContent = label;
    button.addEventListener('click', onClick);

    return button;
  }

  function setStatus(text, tone) {
    var status = byId('modal-status');
    status.className = 'modal-status' + (tone ? ' is-' + tone : '');
    status.textContent = text;
  }

  /* Why a title is closed to this viewer. Two different reasons read differently: an age below the
     limit, and no age at all — the sign-in gave no date of birth the service could use. */
  function restrictionText(title) {
    var age = state.profile ? state.profile.age : null;

    if (age === null || age === undefined) {
      return '“' + title.title + '” is for viewers aged ' + title.minimumAge
        + ' and over, and Nova Stream does not know your age.';
    }

    return '“' + title.title + '” is for viewers aged ' + title.minimumAge + ' and over.';
  }

  /* The window of a closed title: the same box as a purchase, with no code to scan. */
  function showRestricted(title) {
    byId('modal-title').textContent = title.title + ' — ' + title.minimumAge + '+';
    byId('modal-qr').hidden = true;
    setStatus(restrictionText(title), 'warn');

    var actions = byId('modal-actions');
    clear(actions);
    actions.appendChild(modalButton('Close', closeModal));

    byId('modal').hidden = false;
    focusFirst();
  }

  function openModal(title) {
    byId('modal-title').textContent = 'Buy “' + title.title + '” — ' + title.price + ' ' + title.currency;
    byId('modal-qr').hidden = false;
    clear(byId('modal-qr'));
    setStatus('Creating the confirmation…', null);

    var actions = byId('modal-actions');
    clear(actions);
    actions.appendChild(modalButton('Cancel', closeModal));

    byId('modal').hidden = false;
    focusFirst();
  }

  /* Closing the window stops the polling. The transaction itself lives out its time inside Veriqa —
     buying the same title again simply creates a new one. */
  function closeModal() {
    if (state.poll) {
      window.clearTimeout(state.poll);
      state.poll = null;
    }

    if (state.autoClose) {
      window.clearTimeout(state.autoClose);
      state.autoClose = null;
    }

    byId('modal').hidden = true;
    focusFirst();
  }

  function renderChannelEntry(started) {
    var qr = byId('modal-qr');
    clear(qr);

    if (started.qr) {
      var image = document.createElement('img');
      image.src = started.qr;
      image.alt = 'QR code opening the confirmation';
      qr.appendChild(image);
    } else {
      var empty = document.createElement('div');
      empty.className = 'modal-qr-empty';
      empty.textContent = 'No code to show — try again';
      qr.appendChild(empty);
    }

    setStatus('Scan the code and confirm in ' + (started.channelType || 'your messaging app') + '…', null);
  }

  function startPurchase(title) {
    openModal(title);

    request(API_PURCHASE.replace('{id}', encodeURIComponent(title.id)), { method: 'POST' })
      .then(function (started) {
        renderChannelEntry(started);
        pollPurchase(started.transactionId, title, 0);
      })
      .catch(function (error) {
        if (error.status === 409) {
          setStatus('You already own this title.', 'warn');
        } else if (error.status === 403 && error.body && error.body.error === ERROR_AGE_RESTRICTED) {
          /* The page offered the purchase and the server refused it: what is on screen is older than
             the session (it changed in another tab). The catalogue is read again so the tile shows
             the title as closed. */
          byId('modal-qr').hidden = true;
          setStatus(restrictionText(title), 'warn');
          loadCatalog();
        } else {
          setStatus('The confirmation could not be created.', 'bad');
        }

        var actions = byId('modal-actions');
        clear(actions);
        actions.appendChild(modalButton('Close', closeModal));
        focusFirst();
      });
  }

  function finishPurchase(purchase, title) {
    var actions = byId('modal-actions');
    clear(actions);

    if (purchase.state === 'purchased') {
      setStatus('Confirmed. “' + title.title + '” is in your library.', 'ok');
      var closeAndRefresh = function () {
        closeModal();
        loadCatalog();
      };
      actions.appendChild(modalButton('Close', closeAndRefresh));
      state.autoClose = window.setTimeout(closeAndRefresh, PURCHASED_CLOSE_DELAY_MS);
    } else if (purchase.state === 'mismatched') {
      setStatus('Confirmed by another account — the purchase was not applied.', 'bad');
      actions.appendChild(modalButton('Close', closeModal));
    } else {
      setStatus('Not confirmed (' + purchase.outcome + ').', 'warn');
      actions.appendChild(modalButton('Try again', function () {
        startPurchase(title);
      }));
      actions.appendChild(modalButton('Close', closeModal));
    }

    focusFirst();
  }

  function pollPurchase(transactionId, title, failures) {
    state.poll = window.setTimeout(function () {
      request(API_PURCHASE_RESULT + encodeURIComponent(transactionId))
        .then(function (purchase) {
          if (purchase.state === 'pending') {
            pollPurchase(transactionId, title, 0);
            return;
          }

          state.poll = null;
          finishPurchase(purchase, title);
        })
        .catch(function () {
          if (failures + 1 >= MAX_CONSECUTIVE_POLL_FAILURES) {
            state.poll = null;
            setStatus('No answer about the confirmation — check the connection.', 'bad');

            var actions = byId('modal-actions');
            clear(actions);
            actions.appendChild(modalButton('Try again', function () {
              startPurchase(title);
            }));
            actions.appendChild(modalButton('Close', closeModal));
            focusFirst();
            return;
          }

          pollPurchase(transactionId, title, failures + 1);
        });
    }, POLL_INTERVAL_MS);
  }

  /* ---------------------------------------------------------------- remote control */

  /* What the remote can land on right now: the window when it is open, otherwise the visible screen. */
  function focusables() {
    var modal = byId('modal');
    var root = modal.hidden ? byId('app') : modal;
    var found = [];

    Array.prototype.forEach.call(root.querySelectorAll('[data-focusable]'), function (node) {
      if (node.offsetParent !== null) {
        found.push(node);
      }
    });

    return found;
  }

  function markFocus(node) {
    Array.prototype.forEach.call(document.querySelectorAll('.is-focused'), function (previous) {
      previous.classList.remove('is-focused');
    });

    if (node) {
      node.classList.add('is-focused');
      node.focus();
    }
  }

  /* Where the focus lands when a screen opens. A screen may name its content with
     data-focus-region, and then the focus starts there rather than on the first control in DOM
     order — on the catalogue that is the sign-out link in the top bar, which is the last thing a
     viewer reaching for the remote wants under the cursor. */
  function focusFirst() {
    var candidates = focusables();
    if (candidates.length === 0) {
      markFocus(null);
      return;
    }

    var region = document.querySelector('[data-focus-region]');
    var preferred = null;

    if (region) {
      candidates.forEach(function (candidate) {
        if (!preferred && region.contains(candidate)) {
          preferred = candidate;
        }
      });
    }

    markFocus(preferred || candidates[0]);
  }

  function centreOf(node) {
    var box = node.getBoundingClientRect();

    return { x: box.left + (box.width / 2), y: box.top + (box.height / 2) };
  }

  /* Moving the focus geometrically rather than by DOM order: the same rule then serves the
     catalogue grid, where up and down mean a row, and the linear screens, where they mean the next
     control. Among the elements lying in the chosen direction the nearest one wins, with the drift
     across the direction of travel counted double so the focus keeps its column. */
  function moveFocus(direction) {
    var candidates = focusables();
    if (candidates.length === 0) {
      return;
    }

    var current = document.querySelector('.is-focused');
    if (!current || candidates.indexOf(current) < 0) {
      markFocus(candidates[0]);
      return;
    }

    var from = centreOf(current);
    var best = null;
    var bestScore = Infinity;

    candidates.forEach(function (candidate) {
      if (candidate === current) {
        return;
      }

      var to = centreOf(candidate);
      var along = (to.x - from.x) * direction.x + (to.y - from.y) * direction.y;
      if (along <= 1) {
        return;
      }

      var across = Math.abs((to.x - from.x) * direction.y) + Math.abs((to.y - from.y) * direction.x);
      var score = along + (across * 2);

      if (score < bestScore) {
        bestScore = score;
        best = candidate;
      }
    });

    if (best) {
      markFocus(best);
      if (best.scrollIntoView) {
        best.scrollIntoView({ block: 'nearest' });
      }
    }
  }

  var DIRECTIONS = {
    ArrowLeft: { x: -1, y: 0 },
    ArrowRight: { x: 1, y: 0 },
    ArrowUp: { x: 0, y: -1 },
    ArrowDown: { x: 0, y: 1 },
    Left: { x: -1, y: 0 },
    Right: { x: 1, y: 0 },
    Up: { x: 0, y: -1 },
    Down: { x: 0, y: 1 }
  };

  /* Enter presses what the highlight is on, by the page's own hand. Leaving it to the browser works
     only while the highlighted control also holds the browser's focus, and on a television it often
     does not: the page opens without focus, and the pointer of an LG Magic Remote takes it away —
     the highlight stays where it was and Enter then presses nothing. */
  function activateFocused() {
    var current = document.querySelector('.is-focused');
    if (!current || focusables().indexOf(current) < 0) {
      focusFirst();
      current = document.querySelector('.is-focused');
    }

    if (current) {
      current.click();
    }
  }

  function onKeyDown(event) {
    var direction = DIRECTIONS[event.key];
    if (direction) {
      event.preventDefault();
      moveFocus(direction);
      return;
    }

    if (event.key === 'Enter' || event.keyCode === KEY_ENTER) {
      event.preventDefault();
      activateFocused();
      return;
    }

    var isBack = event.key === 'Backspace'
      || event.keyCode === KEY_BACK_TIZEN
      || event.keyCode === KEY_BACK_WEBOS;

    if (isBack) {
      /* Backspace must never navigate the browser away from the page — on a television that would
         leave the viewer with no way back. */
      event.preventDefault();
      if (!byId('modal').hidden) {
        closeModal();
      }

      return;
    }

    if (event.key === 'f' || event.key === 'F') {
      event.preventDefault();
      toggleFullscreen();
    }
  }

  /* ---------------------------------------------------------------- full screen */

  /* On a television the page already fills the screen. On a desktop browser the Fullscreen API
     insists on a gesture of the person's own, which is what the F key is for. */
  function fullscreenElement() {
    return document.fullscreenElement || document.webkitFullscreenElement || null;
  }

  function toggleFullscreen() {
    var root = document.documentElement;

    if (fullscreenElement()) {
      if (document.exitFullscreen) {
        document.exitFullscreen();
      } else if (document.webkitExitFullscreen) {
        document.webkitExitFullscreen();
      }

      return;
    }

    if (root.requestFullscreen) {
      root.requestFullscreen();
    } else if (root.webkitRequestFullscreen) {
      root.webkitRequestFullscreen();
    }
  }

  /* ---------------------------------------------------------------- start */

  byId('welcome-continue').addEventListener('click', function () {
    request(API_WELCOME_SEEN, { method: 'POST' }).then(enterCatalog);
  });

  document.addEventListener('keydown', onKeyDown);

  loadSession();
})();
