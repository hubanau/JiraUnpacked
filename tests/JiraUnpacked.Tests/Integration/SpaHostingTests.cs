using System.Net;
using Xunit;

namespace JiraUnpacked.Tests.Integration;

// wwwroot is gitignored and the .NET CI job never runs `npm run build`, so
// without a real file there these assertions would hold regardless of what
// Program.cs's routing does: UseStaticFiles no-ops on a missing directory and
// MapFallbackToFile 404s either way. To make this a real test, this class
// seeds a marker index.html into the API's ACTUAL web root before the shared
// WebApplicationFactory ever builds its host, and restores whatever was
// there afterward.
//
// It has to be the real, convention-resolved <ContentRoot>/wwwroot, not an
// isolated throwaway directory passed via configuration:
//   - JiraUnpackedApiFactory is `sealed`, so it can't be subclassed to hook
//     host construction with a redirected web root.
//   - WebApplicationFactory<Program>.WithWebHostBuilder(b => b.UseWebRoot(x))
//     was tried and does NOT work for this app - verified empirically (it
//     kept serving the real wwwroot regardless of the throwaway directory
//     passed to UseWebRoot). That's because Program.cs uses the minimal
//     hosting model (`WebApplication.CreateBuilder(args)`), which resolves
//     IWebHostEnvironment.WebRootPath/WebRootFileProvider eagerly inside
//     CreateBuilder() itself, before WithWebHostBuilder's IWebHostBuilder
//     hook ever runs - so UseWebRoot there is silently a no-op.
// Writing straight into the real path is safe here because the test
// assembly disables parallelization (see AssemblyInfo.cs: `[assembly:
// CollectionBehavior(DisableTestParallelization = true)]`), so no other test
// class can be mid-request against a differently-seeded wwwroot while this
// class's tests run. The constructor - which runs before every test method
// in this class, hence before the shared factory's first CreateClient() call
// - seeds the file before IWebHostEnvironment initialization ever checks
// whether the directory exists; Dispose restores the prior state (or removes
// what it created) after each test.
public sealed class SpaHostingTests : IClassFixture<JiraUnpackedApiFactory>, IDisposable
{
    private const string ShellMarker = "spa-hosting-test-shell";

    private readonly JiraUnpackedApiFactory _factory;
    private readonly string _indexPath;
    private readonly bool _wwwrootPreexisted;
    private readonly string? _originalIndexHtml;

    public SpaHostingTests(JiraUnpackedApiFactory factory)
    {
        _factory = factory;

        // WebApplicationFactory<Program> resolves ContentRootPath to the API
        // project's SOURCE directory (not the test project's bin output), so
        // its default web root is <repo>/src/JiraUnpacked.Api/wwwroot.
        // Locate it relative to this test assembly's own output directory
        // (tests/JiraUnpacked.Tests/bin/{Config}/net8.0/) rather than
        // hardcoding an absolute path, with a sanity check so a repo/output
        // layout change fails loudly here instead of silently writing to the
        // wrong place.
        var apiProjectDir = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "JiraUnpacked.Api"));
        if (!File.Exists(Path.Combine(apiProjectDir, "JiraUnpacked.Api.csproj")))
        {
            throw new InvalidOperationException(
                $"SpaHostingTests expected to find JiraUnpacked.Api.csproj at " +
                $"'{apiProjectDir}' (computed relative to the test assembly's own " +
                "output directory). If the repo or build output layout changed, " +
                "update that computation.");
        }

        var wwwroot = Path.Combine(apiProjectDir, "wwwroot");
        _wwwrootPreexisted = Directory.Exists(wwwroot);
        Directory.CreateDirectory(wwwroot);
        _indexPath = Path.Combine(wwwroot, "index.html");
        _originalIndexHtml = File.Exists(_indexPath) ? File.ReadAllText(_indexPath) : null;

        File.WriteAllText(_indexPath, $"<!doctype html><html><body>{ShellMarker}</body></html>");
    }

    public void Dispose()
    {
        if (_originalIndexHtml is not null)
        {
            File.WriteAllText(_indexPath, _originalIndexHtml);
        }
        else if (File.Exists(_indexPath))
        {
            File.Delete(_indexPath);
        }

        var wwwroot = Path.GetDirectoryName(_indexPath)!;
        if (!_wwwrootPreexisted && Directory.Exists(wwwroot) && Directory.GetFileSystemEntries(wwwroot).Length == 0)
        {
            Directory.Delete(wwwroot);
        }
    }

    // An unknown /api path must stay a JSON-shaped 404, not the SPA shell -
    // proving Program.cs's `app.Map("/api/{**rest}", ...)` catch-all still
    // wins over the SPA fallback (delete that line and this starts failing).
    // A non-/api path must fall through to the seeded shell with 200.
    [Fact]
    public async Task Unknown_api_route_returns_404_and_non_api_route_serves_the_spa_shell()
    {
        var client = _factory.CreateClient();

        var apiResponse = await client.GetAsync("/api/definitely-not-a-route");
        Assert.Equal(HttpStatusCode.NotFound, apiResponse.StatusCode);
        var apiBody = await apiResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain(ShellMarker, apiBody, StringComparison.Ordinal);

        var spaResponse = await client.GetAsync("/some/spa/route");
        Assert.Equal(HttpStatusCode.OK, spaResponse.StatusCode);
        var spaBody = await spaResponse.Content.ReadAsStringAsync();
        Assert.Contains(ShellMarker, spaBody, StringComparison.Ordinal);
    }

    // A real endpoint must still win over the catch-all. Unauthenticated =>
    // 401, which proves routing reached the controller rather than the fallback.
    [Fact]
    public async Task Real_api_route_still_reaches_its_controller()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
