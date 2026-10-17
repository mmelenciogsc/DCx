using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using DCx.Core.Content;
using DCx.Core.LocalModels;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace DCx.Desktop.Media;

public sealed record WebVideoFrameDescriptionResult(
    OllamaVisionResult Vision,
    Uri PageUri,
    double CurrentTimeSeconds,
    double? DurationSeconds);

public sealed class WebVideoFrameException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

public sealed class WebVideoFrameDescriptionService
{
    private const int MaximumScreenshotBytes = 20 * 1024 * 1024;
    private const int MaximumScreenshotResponseCharacters = 28 * 1024 * 1024;
    private const double MaximumMediaSeconds = 7 * 24 * 60 * 60;
    private const double MaximumPageCoordinate = 10_000_000;
    private const double MaximumCaptureDimension = 4096;
    private const double MaximumCaptureArea = 8_388_608;
    private static ReadOnlySpan<byte> PngSignature =>
        [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 4,
    };

    private const string PauseScript =
        "(() => {" +
        "const candidates=Array.from(document.querySelectorAll('video')).map(video=>({video,rect:video.getBoundingClientRect()}))" +
        ".filter(item=>item.video.readyState>=2&&item.rect.width>=32&&item.rect.height>=32&&item.rect.bottom>0&&item.rect.right>0&&item.rect.top<innerHeight&&item.rect.left<innerWidth)" +
        ".sort((left,right)=>(right.rect.width*right.rect.height)-(left.rect.width*left.rect.height));" +
        "if(candidates.length===0)return {schemaVersion:1,ok:false,code:'web_video_missing',currentTimeSeconds:0,durationSeconds:null,pageX:0,pageY:0,width:0,height:0,paused:false,readyState:0};" +
        "const video=candidates[0].video;video.pause();video.scrollIntoView({behavior:'instant',block:'center',inline:'center'});" +
        "const rect=video.getBoundingClientRect();const left=Math.max(0,rect.left);const top=Math.max(0,rect.top);" +
        "const right=Math.min(innerWidth,rect.right);const bottom=Math.min(innerHeight,rect.bottom);" +
        "return {schemaVersion:1,ok:video.paused,code:video.paused?'ok':'web_video_pause_failed'," +
        "currentTimeSeconds:Number.isFinite(video.currentTime)?video.currentTime:0," +
        "durationSeconds:Number.isFinite(video.duration)?video.duration:null,pageX:scrollX+left,pageY:scrollY+top," +
        "width:Math.max(0,right-left),height:Math.max(0,bottom-top),paused:video.paused,readyState:video.readyState};})()";

    private const string PlayScript =
        "(() => {" +
        "const candidates=Array.from(document.querySelectorAll('video')).map(video=>({video,rect:video.getBoundingClientRect()}))" +
        ".filter(item=>item.video.readyState>=2&&item.rect.width>=32&&item.rect.height>=32&&item.rect.bottom>0&&item.rect.right>0&&item.rect.top<innerHeight&&item.rect.left<innerWidth)" +
        ".sort((left,right)=>(right.rect.width*right.rect.height)-(left.rect.width*left.rect.height));" +
        "if(candidates.length===0)return false;candidates[0].video.play().catch(()=>{});return true;})()";

    private const string IsPlayingScript =
        "(() => {const videos=Array.from(document.querySelectorAll('video')).filter(video=>video.readyState>=2);" +
        "if(videos.length===0)return false;videos.sort((left,right)=>(right.clientWidth*right.clientHeight)-(left.clientWidth*left.clientHeight));" +
        "return !videos[0].paused&&!videos[0].ended;})()";

    public async Task<WebVideoFrameDescriptionResult> DescribeAsync(
        WebView2 browser,
        string model,
        string modelDigest,
        string modelLicenseSha256,
        string? question,
        CancellationToken cancellationToken = default)
    {
        var pageUri = RequireWebPage(browser);
        var prompt = new MediaFrameQuestionPolicy().Evaluate(question);
        if (!prompt.IsAllowed) throw new WebVideoFrameException("web_video_question_invalid");

        var pause = await WaitForReadyVideoAsync(browser, cancellationToken).ConfigureAwait(true);
        if (!pause.Ok || !pause.Paused || pause.Code != "ok")
        {
            throw new WebVideoFrameException(pause.Code switch
            {
                "web_video_missing" => "web_video_missing",
                "web_video_pause_failed" => "web_video_pause_failed",
                _ => "web_video_pause_failed",
            });
        }

        ValidatePauseResult(pause);
        await Task.Delay(180, cancellationToken).ConfigureAwait(true);
        if (browser.Source is null || !SamePage(pageUri, browser.Source))
        {
            throw new WebVideoFrameException("web_video_page_changed");
        }

        var devToolsParameters = JsonSerializer.Serialize(new
        {
            format = "png",
            fromSurface = true,
            captureBeyondViewport = false,
            clip = new
            {
                x = pause.PageX,
                y = pause.PageY,
                width = pause.Width,
                height = pause.Height,
                scale = 1,
            },
        }, JsonOptions);
        string screenshotResponse;
        try
        {
            screenshotResponse = await browser.CoreWebView2.CallDevToolsProtocolMethodAsync(
                "Page.captureScreenshot",
                devToolsParameters);
        }
        catch
        {
            throw new WebVideoFrameException("web_video_capture_unavailable");
        }

        if (string.IsNullOrWhiteSpace(screenshotResponse) ||
            screenshotResponse.Length > MaximumScreenshotResponseCharacters)
        {
            throw new WebVideoFrameException("web_video_capture_invalid");
        }

        ScreenshotResponse? encoded;
        try
        {
            encoded = JsonSerializer.Deserialize<ScreenshotResponse>(screenshotResponse, JsonOptions);
        }
        catch (JsonException)
        {
            throw new WebVideoFrameException("web_video_capture_invalid");
        }

        if (encoded is null || string.IsNullOrWhiteSpace(encoded.Data) ||
            encoded.Data.Length > MaximumScreenshotResponseCharacters)
        {
            throw new WebVideoFrameException("web_video_capture_invalid");
        }

        byte[] screenshot;
        try
        {
            screenshot = Convert.FromBase64String(encoded.Data);
        }
        catch (FormatException)
        {
            throw new WebVideoFrameException("web_video_capture_invalid");
        }

        var operationRoot = Path.Combine(
            Path.GetTempPath(),
            $"DCx-web-video-frame-{Guid.NewGuid():N}");
        var framePath = Path.Combine(operationRoot, "paused-frame.png");
        try
        {
            if (screenshot.Length is <= 8 or > MaximumScreenshotBytes ||
                !screenshot.AsSpan(0, 8).SequenceEqual(PngSignature))
            {
                throw new WebVideoFrameException("web_video_capture_invalid");
            }

            Directory.CreateDirectory(operationRoot);
            if ((File.GetAttributes(operationRoot) & FileAttributes.ReparsePoint) != 0)
            {
                throw new WebVideoFrameException("web_video_capture_storage_unsafe");
            }

            await using (var writer = new FileStream(
                             framePath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await writer.WriteAsync(screenshot, cancellationToken).ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            CryptographicOperations.ZeroMemory(screenshot);

            await using var readLease = new FileStream(
                framePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            using var ollama = new OllamaLocalClient(
                new Uri("http://localhost:11434/"),
                TimeSpan.FromMinutes(3));
            var vision = await ollama.DescribeImagePinnedAsync(
                model,
                modelDigest,
                modelLicenseSha256,
                framePath,
                prompt.Prompt,
                cancellationToken).ConfigureAwait(false);
            return new WebVideoFrameDescriptionResult(
                vision,
                pageUri,
                pause.CurrentTimeSeconds,
                pause.DurationSeconds);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (WebVideoFrameException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new WebVideoFrameException("web_video_capture_storage_failed");
        }
        catch (ArgumentException)
        {
            throw new WebVideoFrameException("web_video_model_binding_invalid");
        }
        catch (Exception exception) when (
            exception is HttpRequestException or InvalidOperationException or JsonException)
        {
            throw new WebVideoFrameException("web_video_model_failed");
        }
        catch
        {
            throw new WebVideoFrameException("web_video_processing_failed");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(screenshot);
            try
            {
                if (Directory.Exists(operationRoot)) Directory.Delete(operationRoot, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The random per-operation directory remains private and is never reported.
            }
        }
    }

    public async Task ResumeAsync(WebView2 browser, Uri expectedPage, CancellationToken cancellationToken = default)
    {
        var current = RequireWebPage(browser);
        if (!SamePage(current, expectedPage)) throw new WebVideoFrameException("web_video_page_changed");

        var requested = await browser.CoreWebView2.ExecuteScriptAsync(PlayScript);
        if (!string.Equals(requested, "true", StringComparison.Ordinal))
        {
            throw new WebVideoFrameException("web_video_resume_unavailable");
        }

        await Task.Delay(350, cancellationToken).ConfigureAwait(true);
        var playing = await browser.CoreWebView2.ExecuteScriptAsync(IsPlayingScript);
        if (!string.Equals(playing, "true", StringComparison.Ordinal))
        {
            throw new WebVideoFrameException("web_video_resume_blocked");
        }
    }

    private static async Task<PauseResponse> PauseAsync(WebView2 browser, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string json;
        try
        {
            json = await browser.CoreWebView2.ExecuteScriptAsync(PauseScript);
        }
        catch
        {
            throw new WebVideoFrameException("web_video_page_script_unavailable");
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return JsonSerializer.Deserialize<PauseResponse>(json, JsonOptions)
                   ?? throw new WebVideoFrameException("web_video_page_response_invalid");
        }
        catch (JsonException)
        {
            throw new WebVideoFrameException("web_video_page_response_invalid");
        }
    }

    private static async Task<PauseResponse> WaitForReadyVideoAsync(
        WebView2 browser,
        CancellationToken cancellationToken)
    {
        PauseResponse? last = null;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            last = await PauseAsync(browser, cancellationToken).ConfigureAwait(true);
            if (last.Code != "web_video_missing") return last;
            await Task.Delay(250, cancellationToken).ConfigureAwait(true);
        }

        return last ?? throw new WebVideoFrameException("web_video_page_response_invalid");
    }

    private static Uri RequireWebPage(WebView2 browser)
    {
        ArgumentNullException.ThrowIfNull(browser);
        if (browser.CoreWebView2 is null || browser.Source is null || !browser.Source.IsAbsoluteUri ||
            (browser.Source.Scheme != Uri.UriSchemeHttp && browser.Source.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(browser.Source.UserInfo) || browser.Source.AbsoluteUri.Length > 2048)
        {
            throw new WebVideoFrameException("web_video_page_unsupported");
        }

        return browser.Source;
    }

    private static void ValidatePauseResult(PauseResponse value)
    {
        if (value.SchemaVersion != 1 || value.ReadyState is < 2 or > 4 ||
            !IsFiniteBetween(value.CurrentTimeSeconds, 0, MaximumMediaSeconds) ||
            value.DurationSeconds is { } duration && !IsFiniteBetween(duration, 0, MaximumMediaSeconds) ||
            !IsFiniteBetween(value.PageX, 0, MaximumPageCoordinate) ||
            !IsFiniteBetween(value.PageY, 0, MaximumPageCoordinate) ||
            !IsFiniteBetween(value.Width, 32, MaximumCaptureDimension) ||
            !IsFiniteBetween(value.Height, 32, MaximumCaptureDimension) ||
            value.Width * value.Height > MaximumCaptureArea)
        {
            throw new WebVideoFrameException("web_video_geometry_invalid");
        }
    }

    private static bool IsFiniteBetween(double value, double minimum, double maximum) =>
        double.IsFinite(value) && value >= minimum && value <= maximum;

    private static bool SamePage(Uri left, Uri right) =>
        string.Equals(left.AbsoluteUri, right.AbsoluteUri, StringComparison.Ordinal);

    private sealed record PauseResponse(
        int SchemaVersion,
        bool Ok,
        string Code,
        double CurrentTimeSeconds,
        double? DurationSeconds,
        double PageX,
        double PageY,
        double Width,
        double Height,
        bool Paused,
        int ReadyState);

    private sealed record ScreenshotResponse(string Data);
}
