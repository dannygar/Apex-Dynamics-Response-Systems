using System.Net;
using Xunit;

namespace Evaluation.Tests;

public sealed class SpaTests : IDisposable
{
    private readonly string root = Path.Combine(AppContext.BaseDirectory, "spa-fixture", Guid.NewGuid().ToString("N"));
    private readonly ApiFactory factory;
    private readonly HttpClient client;
    public SpaTests()
    {
        Directory.CreateDirectory(Path.Combine(root, "assets"));
        File.WriteAllText(Path.Combine(root, "index.html"), "<!doctype html><div id=\"root\">spa-index</div>");
        File.WriteAllText(Path.Combine(root, "redirect.html"), "<!doctype html><p>bridge</p>");
        // A stylesheet (not .js) so repository linters never pick up the transient fixture.
        File.WriteAllText(Path.Combine(root, "assets", "app-abc123.css"), "body{}");
        factory = new ApiFactory(root);
        client = factory.CreateClient();
        factory.Initialize();
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/index.html")]
    [InlineData("/evaluation/deep-link")]
    [InlineData("/Apex-Dynamics-Response-Systems/")]
    public async Task PagesServeIndexWithFramingProtection(string path)
    {
        var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType!.MediaType);
        Assert.Contains("spa-index", await response.Content.ReadAsStringAsync());
        Assert.Equal("frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.False(response.Headers.Contains("Cross-Origin-Opener-Policy"));
    }

    [Theory]
    [InlineData("/redirect.html")]
    [InlineData("/Apex-Dynamics-Response-Systems/redirect.html")]
    public async Task RedirectBridgeIsStaticSameOriginFrameableAndHasNoCoop(string path)
    {
        var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("bridge", await response.Content.ReadAsStringAsync());
        Assert.Equal("frame-ancestors 'self'", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.False(response.Headers.Contains("Cross-Origin-Opener-Policy"));
    }

    [Theory]
    [InlineData("/assets/app-abc123.css")]
    [InlineData("/Apex-Dynamics-Response-Systems/assets/app-abc123.css")]
    public async Task HashedAssetsAreCacheable(string path)
    {
        var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("public, max-age=31536000, immutable", response.Headers.CacheControl!.ToString());
    }

    [Theory]
    [InlineData("/api/not-a-route")]
    [InlineData("/api/teams/00000000-0000-0000-0000-000000000001/unknown")]
    [InlineData("/assets/missing.js")]
    [InlineData("/healthz.json")]
    public async Task FallbackNeverShadowsApiOrMissingFiles(string path)
    {
        var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("spa-index", await response.Content.ReadAsStringAsync());
    }

    [Fact] public async Task ApiAndHealthStillRoute()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/teams")).StatusCode);
        client.DefaultRequestHeaders.Add("X-Fixture-Subject", ApiFactory.Member);
        var teams = await client.GetAsync("/api/teams");
        Assert.Equal(HttpStatusCode.OK, teams.StatusCode);
        Assert.Equal("application/json", teams.Content.Headers.ContentType!.MediaType);
        var health = await client.GetStringAsync("/health");
        Assert.Contains("running", health);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/evaluation/deep-link", null)).StatusCode);
    }

    public void Dispose()
    {
        client.Dispose(); factory.Dispose();
        try { Directory.Delete(root, true); } catch (IOException) { }
    }
}
