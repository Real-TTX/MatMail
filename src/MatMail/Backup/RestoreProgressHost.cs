using System.Text.Json;
using MatMail.Configuration;
using MatMail.Services;
using Microsoft.AspNetCore.Builder;

namespace MatMail.Backup;

/// <summary>
/// While a restore runs at start-up nothing else is up: this is a tiny web server on the port of the web interface that shows how far
/// it is (and answers the container health check), so that a browser that waits sees a progress page and not "connection refused".
/// It is gone before the real application starts.
/// </summary>
public sealed class RestoreProgressHost : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly CertificateProvider _certificates;
    private readonly object _lock = new();
    private string _state = "running";
    private string _stage = "start";
    private string? _item;
    private int _done;
    private int _total;
    private string? _message;

    private RestoreProgressHost(WebApplication app, CertificateProvider certificates)
    {
        _app = app;
        _certificates = certificates;
    }

    /// <summary>Starts the page. Null when the port cannot be used: the restore then runs without it.</summary>
    public static async Task<RestoreProgressHost?> StartAsync(AppConfig config, string dataDir, ILogger logger)
    {
        CertificateProvider? certificates = null;
        try
        {
            certificates = new CertificateProvider(config, dataDir, logger);
            WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureMatMailWebServer(config, certificates);
            WebApplication app = builder.Build();

            var host = new RestoreProgressHost(app, certificates);
            app.MapGet("/restore-status", () => Results.Json(host.Snapshot(), JsonSerializerOptions.Web));
            app.MapGet("/healthz", () => Results.Json(new { status = "restoring" }));
            app.MapFallback(context =>
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                context.Response.Headers.RetryAfter = "5";
                context.Response.Headers.CacheControl = "no-store";
                context.Response.ContentType = "text/html; charset=utf-8";
                return context.Response.WriteAsync(Page);
            });

            await app.StartAsync();
            return host;
        }
        catch (Exception ex)
        {
            logger.LogWarning("The progress page of the restore could not be started ({Reason}); the restore goes on without it.", ex.Message);
            certificates?.Dispose();
            return null;
        }
    }

    public void Report(BackupProgress progress)
    {
        lock (_lock)
        {
            _stage = progress.Stage;
            _item = progress.Item;
            _done = progress.Done;
            _total = progress.Total;
        }
    }

    public void Finish(bool succeeded, string? message)
    {
        lock (_lock)
        {
            _state = succeeded ? "succeeded" : "failed";
            _message = message;
        }
    }

    private object Snapshot()
    {
        lock (_lock)
        {
            return new { state = _state, stage = _stage, item = _item, done = _done, total = _total, message = _message };
        }
    }

    /// <summary>Stops the page after the browsers have had a moment to see how it ended.</summary>
    public async ValueTask DisposeAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(3));
        try
        {
            await _app.StopAsync(TimeSpan.FromSeconds(5));
            await _app.DisposeAsync();
        }
        finally
        {
            _certificates.Dispose();
        }
    }

    // A page of its own (no stylesheets, scripts or texts of the application: none of it is available now). English and German.
    private const string Page = """
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
        <meta name="color-scheme" content="light dark">
        <title>MatMail</title>
        <style>
        :root { --bg:#f5f7fa; --card:#fff; --text:#1f2933; --muted:#616e7c; --accent:#2f6fed; --line:#d9e2ec; --bad:#c0392b; --good:#1e8e5a; }
        @media (prefers-color-scheme: dark) { :root { --bg:#12161c; --card:#1b212b; --text:#e6ebf1; --muted:#9aa8b8; --line:#2b3441; } }
        * { box-sizing: border-box; }
        body { margin:0; min-height:100vh; display:flex; align-items:center; justify-content:center; background:var(--bg); color:var(--text); font:16px/1.5 system-ui,-apple-system,"Segoe UI",Roboto,sans-serif; padding:16px; }
        main { width:100%; max-width:30rem; background:var(--card); border:1px solid var(--line); border-radius:14px; padding:28px 28px 24px; box-shadow:0 8px 30px rgba(0,0,0,.08); }
        h1 { font-size:1.25rem; margin:0 0 6px; }
        p { margin:0 0 16px; color:var(--muted); }
        .bar { height:8px; border-radius:99px; background:var(--line); overflow:hidden; }
        .bar > div { height:100%; width:30%; background:var(--accent); border-radius:99px; transition:width .4s ease; }
        .bar.busy > div { animation:slide 1.4s ease-in-out infinite; }
        @keyframes slide { 0% { margin-left:-30%; } 100% { margin-left:100%; } }
        #stage { margin-top:12px; font-size:.9rem; min-height:1.4em; }
        #msg { margin-top:12px; font-size:.9rem; word-break:break-word; }
        .bad { color:var(--bad); } .good { color:var(--good); }
        </style>
        </head>
        <body>
        <main>
        <h1 id="title"></h1>
        <p id="hint"></p>
        <div class="bar busy" id="bar"><div></div></div>
        <div id="stage"></div>
        <div id="msg"></div>
        </main>
        <script>
        (function () {
          var de = /^de/i.test(navigator.language || '');
          var t = de ? {
            title: 'Ein Backup wird wiederhergestellt',
            hint: 'Das dauert einen Moment. Bitte schalten Sie den Server nicht aus. Diese Seite lädt sich selbst neu, sobald MatMail wieder da ist.',
            start: 'Wird vorbereitet …', files: 'Dateien werden geprüft und vorbereitet', schema: 'Datenbank wird neu aufgebaut',
            database: 'Daten werden geladen', constraints: 'Zusammenhänge werden geprüft', migrate: 'Auf diese Version aktualisieren',
            safety: 'Der aktuelle Stand wird vorher gesichert', done: 'Fertig', ok: 'Das Backup wurde wiederhergestellt. MatMail startet …',
            bad: 'Die Wiederherstellung ist fehlgeschlagen. Es wurde nichts verändert. MatMail startet mit den bisherigen Daten.'
          } : {
            title: 'Restoring a backup',
            hint: 'This takes a moment. Please do not switch the server off. This page reloads by itself as soon as MatMail is back.',
            start: 'Getting ready …', files: 'Checking and preparing the files', schema: 'Rebuilding the database',
            database: 'Loading the data', constraints: 'Checking the consistency', migrate: 'Upgrading to this version',
            safety: 'Saving the current state first', done: 'Done', ok: 'The backup was restored. MatMail is starting …',
            bad: 'The restore failed. Nothing was changed. MatMail starts with the data it had.'
          };
          var $ = function (id) { return document.getElementById(id); };
          $('title').textContent = t.title; $('hint').textContent = t.hint;
          var bar = $('bar'), fill = bar.firstElementChild, finished = false;
          function label(s) {
            var stage = String(s.stage || 'start');
            var key = stage.indexOf('safety-') === 0 ? 'safety' : stage;
            var text = t[key] || '';
            if (s.item) text += ' – ' + s.item;
            return text;
          }
          function tick() {
            fetch('/restore-status', { cache: 'no-store' }).then(function (r) {
              var type = r.headers.get('content-type') || '';
              if (!r.ok || type.indexOf('json') < 0) { location.href = '/'; return null; }
              return r.json();
            }).then(function (s) {
              if (!s) return;
              $('stage').textContent = label(s);
              if (s.total > 0) { bar.classList.remove('busy'); fill.style.width = Math.max(4, Math.round(100 * s.done / s.total)) + '%'; }
              else { bar.classList.add('busy'); }
              if (s.state !== 'running') {
                finished = true; bar.classList.remove('busy'); fill.style.width = '100%';
                $('msg').className = s.state === 'succeeded' ? 'good' : 'bad';
                $('msg').textContent = s.state === 'succeeded' ? t.ok : t.bad + (s.message ? ' (' + s.message + ')' : '');
              }
            }).catch(function () { /* the server is between two programs: keep asking */ })
              .then(function () { setTimeout(tick, 1000); });
          }
          tick();
        })();
        </script>
        </body>
        </html>
        """;
}
