using System.Text;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();

var portArgument = args
    .Select((value, index) => (value, index))
    .FirstOrDefault(item => string.Equals(item.value, "--port", StringComparison.Ordinal));
var port = portArgument.value is not null && portArgument.index + 1 < args.Length
    ? int.Parse(args[portArgument.index + 1], System.Globalization.CultureInfo.InvariantCulture)
    : 43187;
var mediaArgument = args
    .Select((value, index) => (value, index))
    .FirstOrDefault(item => string.Equals(item.value, "--media", StringComparison.Ordinal));
var mediaPath = mediaArgument.value is not null && mediaArgument.index + 1 < args.Length
    ? Path.GetFullPath(args[mediaArgument.index + 1])
    : null;
if (mediaPath is not null && !File.Exists(mediaPath))
{
    throw new FileNotFoundException("The optional web-video fixture does not exist.", mediaPath);
}
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");

var app = builder.Build();
app.MapGet("/health", () => Results.Text("ok", "text/plain"));
app.MapGet("/", () => Results.Content(
    """
    <!doctype html>
    <html lang="en">
    <head><meta charset="utf-8"><title>DCx accessible fixture</title></head>
    <body>
      <main>
        <h1>DCx accessible fixture</h1>
        <label for="approval-code">Approval code</label>
        <input id="approval-code" autocomplete="off">
        <button id="continue" type="button">Continue</button>
        <a href="/download">Download fixture</a>
        <p id="result" role="status" aria-live="polite"></p>
      </main>
      <script>
        document.getElementById('continue').addEventListener('click', () => {
          document.getElementById('result').textContent = 'Agent resumed after user handback.';
        });
      </script>
    </body>
    </html>
    """,
    "text/html; charset=utf-8"));
app.MapGet("/download", () => Results.File(
    Encoding.UTF8.GetBytes("DCx controlled download fixture.\n"),
    "text/plain",
    "dcx-fixture.txt"));
app.MapGet("/web-video", () => mediaPath is null
    ? Results.NotFound()
    : Results.Content(
        """
        <!doctype html>
        <html lang="en">
        <head><meta charset="utf-8"><title>Accessible web video fixture</title></head>
        <body style="margin:0;background:#fff">
          <main>
            <h1>Accessible web video fixture</h1>
            <video id="fixture-video" controls autoplay muted loop width="640" height="360"
                   aria-label="Solid blue deterministic web video" src="/media-fixture.mp4"></video>
            <p id="video-status" role="status" aria-live="polite">The deterministic web video is ready for a local frame question.</p>
          </main>
        </body>
        </html>
        """,
        "text/html; charset=utf-8"));
app.MapGet("/media-fixture.mp4", () => mediaPath is null
    ? Results.NotFound()
    : Results.File(mediaPath, "video/mp4", enableRangeProcessing: true));
app.MapGet("/social-login", () => Results.Content(
    """
    <!doctype html>
    <html lang="en">
    <head><meta charset="utf-8"><title>Social site login fixture</title></head>
    <body>
      <main>
        <h1>Social site login fixture</h1>
        <p>This is a local test page. It is not Facebook and accepts no real credential.</p>
        <label for="fixture-account">Fixture account name</label>
        <input id="fixture-account" autocomplete="off">
        <button id="login" type="button">Complete manual login handoff</button>
        <p id="status" role="status" aria-live="assertive"></p>
      </main>
      <script>
        document.getElementById('login').addEventListener('click', () => {
          sessionStorage.setItem('dcx-social-fixture-login', 'complete');
          document.getElementById('status').textContent = 'Manual login complete. Returning control to the agent.';
          location.href = '/social-posts?q=taser%20cane';
        });
      </script>
    </body>
    </html>
    """,
    "text/html; charset=utf-8"));
app.MapGet("/social-posts", () => Results.Content(
    """
    <!doctype html>
    <html lang="en">
    <head><meta charset="utf-8"><title>Social post search fixture</title></head>
    <body>
      <main>
        <h1>Search your old posts</h1>
        <p id="login-state" role="status"></p>
        <label for="post-search">Search terms</label>
        <input id="post-search" value="taser cane">
        <button id="search" type="button">Search posts</button>
        <p id="result-status" role="status" aria-live="polite"></p>
        <section id="results" aria-label="Post search results">
          <article data-search="taser cane accessibility mobility" tabindex="-1">
            <h2>Taser cane prototype in action</h2>
            <p><time datetime="2021-06-14">June 14, 2021</time></p>
            <p>I tested my taser cane prototype and documented its accessible controls.</p>
            <img alt="Black mobility cane prototype with a guarded control near the handle" src="data:image/gif;base64,R0lGODlhAQABAAAAACw=">
            <video controls aria-label="Demonstration video of the taser cane prototype"></video>
            <a href="/social-posts#taser-cane-2021">Open this fixture post</a>
          </article>
          <article data-search="garden update" tabindex="-1">
            <h2>Garden update</h2><p><time datetime="2022-04-02">April 2, 2022</time></p>
          </article>
        </section>
      </main>
      <script>
        const loginComplete = sessionStorage.getItem('dcx-social-fixture-login') === 'complete';
        document.getElementById('login-state').textContent = loginComplete
          ? 'Manual login handoff is complete.'
          : 'Fixture login is not complete. Use the login page for the full handoff test.';
        function search() {
          const query = document.getElementById('post-search').value.trim().toLowerCase();
          let count = 0;
          document.querySelectorAll('article').forEach(article => {
            const match = query.length > 0 && article.dataset.search.includes(query);
            article.hidden = !match;
            if (match) count++;
          });
          document.getElementById('result-status').textContent = `${count} matching fixture post${count === 1 ? '' : 's'} found.`;
          if (count === 1) document.querySelector('article:not([hidden])').focus();
        }
        document.getElementById('search').addEventListener('click', search);
        search();
      </script>
    </body>
    </html>
    """,
    "text/html; charset=utf-8"));
app.MapGet("/social-blocked", () => Results.Content(
    """
    <!doctype html>
    <html lang="en">
    <head><meta charset="utf-8"><title>Controlled social-site blocked fixture</title></head>
    <body>
      <main>
        <h1>Assisted access blocked</h1>
        <p role="alert" data-dcx-social-blocked="true">The site reported temporarily blocked automated behavior.</p>
        <p>No historical post or direct address is present on this page.</p>
      </main>
    </body>
    </html>
    """,
    "text/html; charset=utf-8"));

await app.RunAsync();
