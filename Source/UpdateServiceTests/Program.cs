using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using MorphocyteRouter;

var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAILED: " + name);
    checks++;
}
byte[] Source(string text) => Encoding.UTF8.GetBytes(text);
HttpResponseMessage Release(string tag = "v1.0.0", bool draft = false, bool prerelease = false,
    string url = "https://github.com/sample-org/sample-app/releases/tag/v1.0.0") => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(new { tag_name = tag, draft, prerelease, html_url = url })) };

Check(BuildInfo.Version.StartsWith("1.0.0-rc.1", StringComparison.Ordinal), "Informational assembly version");
Check(ReleaseUpdateService.ParseSource(Source("{}" )).Repository is null, "Missing source remains unconfigured");
Check(ReleaseUpdateService.ParseSource(Source("{\"githubRepository\":\"\"}")).Repository is null, "Blank update source");
Check(ReleaseUpdateService.ParseSource(Source("{\"githubRepository\":\" sample-org/sample-app \"}")).Repository == "sample-org/sample-app", "Repository source parsed");
Check(ReleaseUpdateService.ParseSource(Source("{\"githubRepository\":\"https://attacker.example/repo\"}")).Repository is null, "No arbitrary network source");
Check(ReleaseUpdateService.ParseSource(Source("{\"githubRepository\":\"sample-org/../repo\"}")).Repository is null, "Repository path traversal rejected");
Check(ReleaseUpdateService.ParseSource(Source("[]")).Repository is null, "Wrong source JSON root rejected");
Check(ReleaseUpdateService.ParseSource(new byte[17 * 1024]).Repository is null, "Source file is bounded");

using (var client = new HttpClient(new StubHandler((_, _) => throw new Exception("Unexpected request"))))
{
    var result = await ReleaseUpdateService.CheckAsync(client, null, "1.0.0");
    Check(!result.IsConfigured && !result.UpdateAvailable, "No source sends no request");
    result = await ReleaseUpdateService.CheckAsync(client, "https://untrusted.example", "1.0.0");
    Check(!result.IsConfigured, "Invalid source sends no request");
}
using (var client = new HttpClient(new StubHandler((request, _) =>
{
    Check(request.RequestUri!.AbsoluteUri == "https://api.github.com/repos/sample-org/sample-app/releases/latest", "Official HTTPS stable endpoint");
    Check(request.Headers.UserAgent.Any(), "GitHub user-agent supplied");
    Check(request.Headers.Contains("X-GitHub-Api-Version"), "GitHub API version supplied");
    return Task.FromResult(Release());
})))
{
    var result = await ReleaseUpdateService.CheckAsync(client, "sample-org/sample-app", "1.0.0-rc.1+commit");
    Check(result.IsConfigured && result.UpdateAvailable && result.LatestVersion == "1.0.0", "Final release newer than matching release candidate");
}
async Task<UpdateCheckResult> MockCheck(HttpResponseMessage response, string current = "1.0.0")
{
    using var client = new HttpClient(new StubHandler((_, _) => Task.FromResult(response)));
    return await ReleaseUpdateService.CheckAsync(client, "sample-org/sample-app", current);
}
Check(!(await MockCheck(Release())).UpdateAvailable, "Equal stable release does not update");
Check(!(await MockCheck(Release("v0.9.0"))).UpdateAvailable, "No downgrade");
Check((await MockCheck(Release("v1.10.0"), "1.9.0")).UpdateAvailable, "Numeric core version ordering");
Check(!(await MockCheck(Release(prerelease: true))).UpdateAvailable, "Unstable release ignored");
Check(!(await MockCheck(Release(draft: true))).UpdateAvailable, "Draft release ignored");
Check(!(await MockCheck(Release("v1.1.0-rc.1"))).UpdateAvailable, "Unstable tag ignored despite API flags");
Check(!(await MockCheck(Release("not-a-version"))).UpdateAvailable, "Malformed version rejected");
Check((await MockCheck(Release(url: "http://github.com/sample-org/sample-app/releases/tag/v1.0.0"))).ReleaseUrl is null, "Insecure release link rejected");
Check((await MockCheck(Release(url: "https://github.com/other/repo/releases/tag/v1.0.0"))).ReleaseUrl is null, "Other repository link rejected");
Check((await MockCheck(Release(url: "https://github.com@attacker.example/sample-org/sample-app/releases/tag/v1.0.0"))).ReleaseUrl is null, "Untrusted release host rejected");
Check((await MockCheck(new(HttpStatusCode.NotFound))).Message.Contains("не найден"), "No published release is not an up-to-date claim");
Check((await MockCheck(new(HttpStatusCode.TooManyRequests))).Message.Contains("ограничил"), "Rate limit message");
Check((await MockCheck(new(HttpStatusCode.ServiceUnavailable))).Message.Contains("503"), "Service failure message");
Check((await MockCheck(new(HttpStatusCode.Found))).ReleaseUrl is null, "Redirect not treated as release");
Check((await MockCheck(new(HttpStatusCode.OK) { Content = new StringContent("invalid json") })).ReleaseUrl is null, "Invalid JSON handled");
Check((await MockCheck(new(HttpStatusCode.OK) { Content = new StringContent(new string(' ', ReleaseUpdateService.MaximumResponseBytes + 1)) })).ReleaseUrl is null, "Declared oversized response rejected");
var bounded = new ByteArrayContent(new byte[ReleaseUpdateService.MaximumResponseBytes + 1]);
bounded.Headers.ContentLength = null;
Check((await MockCheck(new(HttpStatusCode.OK) { Content = bounded })).ReleaseUrl is null, "Stream without length is still bounded");
using (var client = new HttpClient(new StubHandler((_, _) => throw new HttpRequestException("offline"))))
{
    var result = await ReleaseUpdateService.CheckAsync(client, "sample-org/sample-app", "1.0.0");
    Check(!result.UpdateAvailable && result.Message.Contains("подключиться"), "Offline message");
}
using (var client = new HttpClient(new StubHandler(async (_, token) =>
{
    await Task.Delay(5000, token);
    return Release();
})))
{
    var result = await ReleaseUpdateService.CheckAsync(client, "sample-org/sample-app", "1.0.0", timeout: TimeSpan.FromMilliseconds(20));
    Check(result.Message.Contains("слишком много времени"), "Timeout message");
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    try
    {
        await ReleaseUpdateService.CheckAsync(client, "sample-org/sample-app", "1.0.0", cancellation.Token);
        Check(false, "Caller cancellation propagated");
    }
    catch (OperationCanceledException) { Check(true, "Caller cancellation propagated"); }
}
string[] order = { "1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta", "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0" };
for (var index = 0; index < order.Length - 1; index++)
{
    Check(ReleaseUpdateService.SemanticVersion.TryParse(order[index], out var left)
        && ReleaseUpdateService.SemanticVersion.TryParse(order[index + 1], out var right)
        && left!.CompareTo(right) < 0, "Semver precedence: " + order[index]);
}
Check(!ReleaseUpdateService.SemanticVersion.TryParse("1.0.0-rc.01", out _), "Numeric prerelease leading zero rejected");
Check(!ReleaseUpdateService.SemanticVersion.TryParse("01.0.0", out _), "Core leading zero rejected");
Console.WriteLine($"Passed {checks} update-service checks. No external network requests.");

sealed class StubHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;
    public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) => _respond = respond;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => _respond(request, cancellationToken);
}
