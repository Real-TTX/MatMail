/* The backup pages: the progress of a run, the upload of a backup file, waiting while MatMail restarts for a restore. */
(function () {
  'use strict';

  var texts = {};
  var holder = document.getElementById('backup-i18n');
  if (holder) {
    try { texts = JSON.parse(holder.textContent || '{}'); } catch (e) { texts = {}; }
  }

  function t(key, fallback) { return texts[key] || fallback || key; }

  function size(bytes) {
    var units = ['B', 'KB', 'MB', 'GB', 'TB'];
    var value = bytes, unit = 0;
    while (value >= 1024 && unit < units.length - 1) { value /= 1024; unit++; }
    return (unit === 0 ? String(bytes) : value.toFixed(value >= 100 ? 0 : 1)) + ' ' + units[unit];
  }

  function stageText(activity) {
    var text = t(activity.stage, activity.stage);
    if (activity.item) { text += ' – ' + activity.item; }
    if (activity.bytes > 0) { text += ' (' + t('bytes', '{0} so far').replace('{0}', size(activity.bytes)) + ')'; }
    return text;
  }

  function setProgress(bar, activity) {
    if (!bar) { return; }
    var fill = bar.firstElementChild;
    if (activity && activity.total > 0) {
      bar.classList.remove('progress--busy');
      fill.style.width = Math.max(3, Math.round(100 * activity.done / activity.total)) + '%';
    } else {
      bar.classList.add('progress--busy');
      fill.style.width = '';
    }
  }

  function getJson(url) {
    return fetch(url, { cache: 'no-store', credentials: 'same-origin', headers: { Accept: 'application/json' } }).then(function (r) {
      if (!r.ok) { throw new Error(String(r.status)); }
      return r.json();
    });
  }

  // ---- The run that is going on (overview page): shown while it runs, the page reloads when it is over.
  var box = document.getElementById('backup-activity');
  if (box) {
    var running = box.getAttribute('data-running') === 'true';
    var statusUrl = box.getAttribute('data-status-url');
    var bar = document.getElementById('backup-progress');
    var line = document.getElementById('backup-activity-text');
    var title = document.getElementById('backup-activity-title');

    var tick = function () {
      getJson(statusUrl).then(function (state) {
        var activity = state.activity;
        if (!activity) {
          if (running) { location.reload(); return; }
          // somebody started one in the meantime
          return;
        }
        running = true;
        box.hidden = false;
        title.textContent = t('running', 'A backup is running');
        line.textContent = stageText(activity);
        setProgress(bar, activity);
      }).catch(function () { /* the server is busy: ask again */ }).then(function () {
        setTimeout(tick, running ? 1500 : 5000);
      });
    };
    setProgress(bar, null);
    tick();
  }

  // ---- Restart: wait until the program is gone and back (or the progress page of the restore answers), then go there.
  function waitForRestart() {
    var seenDown = false;
    var ask = function () {
      fetch('/healthz', { cache: 'no-store' }).then(function (r) {
        if (!r.ok) { throw new Error('down'); }
        return r.json();
      }).then(function (state) {
        if (state && (state.status === 'restoring' || (state.status === 'ok' && seenDown))) { location.href = '/'; }
      }).catch(function () { seenDown = true; }).then(function () { setTimeout(ask, 1500); });
    };
    ask();
  }

  // ---- Preparing a restore (confirmation page): progress, failure, restart.
  var prep = document.getElementById('restore-preparation');
  if (prep) {
    var prepUrl = prep.getAttribute('data-status-url');
    var prepBar = document.getElementById('restore-progress');
    var prepText = document.getElementById('restore-progress-text');
    var restarting = false;
    var poll = function () {
      getJson(prepUrl).then(function (state) {
        var p = state.preparation || {};
        if (p.state === 'Restarting') {
          if (!restarting) {
            restarting = true;
            setProgress(prepBar, null);
            prepText.textContent = t('restarting');
            waitForRestart();
          }
          return;
        }
        if (p.state === 'Failed') {
          prepText.textContent = (t('failed') + ' ' + (p.message || '')).trim();
          prepText.classList.add('is-danger');
          prepBar.hidden = true;
          var back = document.getElementById('restore-back');
          if (back) { back.hidden = false; }
          return;
        }
        if (state.activity) {
          prepText.textContent = stageText(state.activity);
          setProgress(prepBar, state.activity);
        }
      }).catch(function () { }).then(function () { if (!restarting) { setTimeout(poll, 1000); } });
    };
    poll();
  }

  // ---- Upload of a backup file (restore page): the file is sent as it is, the page shows how far it is.
  var upload = document.getElementById('backup-upload');
  if (upload) {
    var fileInput = upload.querySelector('input[type=file]');
    var button = upload.querySelector('button[data-upload]');
    var uploadBar = upload.querySelector('.progress');
    var uploadText = upload.querySelector('[data-upload-text]');
    var token = document.querySelector('input[name=__RequestVerificationToken]');

    button.addEventListener('click', function () {
      var file = fileInput.files && fileInput.files[0];
      if (!file) { fileInput.click(); return; }
      button.disabled = true;
      fileInput.disabled = true;
      uploadBar.hidden = false;
      uploadText.textContent = t('uploading');

      var xhr = new XMLHttpRequest();
      xhr.open('POST', upload.getAttribute('data-url') + '&name=' + encodeURIComponent(file.name));
      if (token) { xhr.setRequestHeader('X-CSRF-TOKEN', token.value); }
      xhr.setRequestHeader('Content-Type', 'application/octet-stream');
      xhr.upload.onprogress = function (e) {
        if (e.lengthComputable) {
          setProgress(uploadBar, { total: e.total, done: e.loaded });
          uploadText.textContent = t('uploading') + ' ' + size(e.loaded) + ' / ' + size(e.total);
        }
      };
      xhr.onload = function () {
        if (xhr.status >= 200 && xhr.status < 300) {
          var result = JSON.parse(xhr.responseText);
          location.href = upload.getAttribute('data-next') + '?upload=' + encodeURIComponent(result.id);
        } else {
          uploadText.textContent = t('uploadFailed') + ' ' + (xhr.responseText || xhr.status);
          button.disabled = false;
          fileInput.disabled = false;
        }
      };
      xhr.onerror = function () {
        uploadText.textContent = t('uploadFailed');
        button.disabled = false;
        fileInput.disabled = false;
      };
      xhr.send(file);
    });
  }

  // ---- A backup that is made to be downloaded: progress, then the link.
  var download = document.getElementById('backup-download');
  if (download) {
    var downloadUrl = download.getAttribute('data-status-url');
    var downloadBar = document.getElementById('backup-download-progress');
    var downloadText = document.getElementById('backup-download-text');
    var link = document.getElementById('backup-download-link');
    var again = function () {
      getJson(downloadUrl).then(function (state) {
        var d = state.download;
        if (d && d.state === 'Ready') {
          setProgress(downloadBar, null);
          downloadBar.hidden = true;
          downloadText.textContent = t('ready') + ' ' + (d.fileName || '') + ' (' + size(d.bytes) + ')';
          link.hidden = false;
          return;
        }
        if (d && d.state === 'Failed') {
          downloadBar.hidden = true;
          downloadText.textContent = d.message || t('failed');
          downloadText.classList.add('is-danger');
          return;
        }
        if (state.activity) {
          downloadText.textContent = stageText(state.activity);
          setProgress(downloadBar, state.activity);
        }
        setTimeout(again, 1000);
      }).catch(function () { setTimeout(again, 2000); });
    };
    again();
  }
}());
