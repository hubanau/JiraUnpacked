# Module C — Connection & Semantic Layer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the validated, encrypted Jira connection plus the per-project semantic configuration (projects, JQL queries, sync frequency, field/status mapping) that the sync engine will later consume.

**Architecture:** New tenant-owned EF entities (`JiraProject`, `JiraQuery`, `FieldMapping`, `StatusSemantic`) extend the existing two-layer isolation (EF global query filter + Postgres RLS). A typed `HttpClient` Jira API client validates credentials and supplies discovery metadata; the API token is encrypted at rest behind an `ITokenProtector` abstraction (AES-GCM now, KMS later). Configuration writes that touch Jira (connection validation, project-key check, JQL parse) fail fast.

**Tech Stack:** ASP.NET Core 8 Web API, EF Core 8 + Npgsql, System.Security.Cryptography (AES-GCM), `IHttpClientFactory` typed clients, xUnit + WireMock.Net.

## Global Constraints

- Target framework **net8.0**; EF Core and Npgsql pinned to **8.0.11**; `Microsoft.EntityFrameworkCore.Design` **8.0.11** (avoid CS1705 version skew).
- Every tenant-owned entity implements `JiraUnpacked.Domain.Abstractions.ITenantOwned` (`Guid EndUserId`), gets a global query filter `HasQueryFilter(x => x.EndUserId == _currentTenantId)` in `AppDbContext`, and gets Postgres RLS (ENABLE + FORCE + `tenant_isolation` policy + GRANT to `jiraunpacked_app`).
- Enums persisted **as strings** (`.HasConversion<string>()`).
- The Jira API token is **never logged and never returned** in any response.
- EF commands run with **both** `--project` and `--startup-project` set to `src/JiraUnpacked.Infrastructure` (the Design package and `IDesignTimeDbContextFactory` live there).
- Integration tests run through `JiraUnpacked.Tests.Integration.JiraUnpackedApiFactory` (Development environment; runtime `AppDbContext` uses the RLS-enforced `jiraunpacked_app` role). Migrations are applied by `EnsureDatabaseAsync()` as the privileged `jiraunpacked` role.
- Jira HTTP is exercised in tests via **WireMock.Net**; the per-connection `BaseUrl` points at the WireMock server URL, so the real typed client is exercised end-to-end.
- One commit per task. The controller (human/orchestrator) pushes before opening a PR.
- All new endpoints are `[Authorize]` (End User), tenant-scoped.

**Shared namespaces (used across tasks):**
- Domain abstractions/POCOs: `JiraUnpacked.Domain.Abstractions` (interfaces) and `JiraUnpacked.Domain.Abstractions.Jira` (Jira DTOs/exceptions).
- Entities: `JiraUnpacked.Domain.Entities`; enums: `JiraUnpacked.Domain.Enums`.
- EF config/migrations: `JiraUnpacked.Infrastructure.Persistence.*`.
- Service impls: `JiraUnpacked.Infrastructure.Services`; crypto/jira impls: `JiraUnpacked.Infrastructure.Security` / `JiraUnpacked.Infrastructure.Jira`.
- API: `JiraUnpacked.Api.Controllers`, DTOs under `JiraUnpacked.Api.Contracts.*`.

---

## Task 1 (CSL1): Token protection — `ITokenProtector` + AES-GCM

**Files:**
- Create: `src/JiraUnpacked.Domain/Abstractions/ITokenProtector.cs`
- Create: `src/JiraUnpacked.Infrastructure/Security/TokenProtectionOptions.cs`
- Create: `src/JiraUnpacked.Infrastructure/Security/AesGcmTokenProtector.cs`
- Modify: `src/JiraUnpacked.Infrastructure/DependencyInjection.cs`
- Modify: `src/JiraUnpacked.Api/appsettings.Development.json`
- Test: `tests/JiraUnpacked.Tests/Security/AesGcmTokenProtectorTests.cs`

**Interfaces:**
- Produces: `ITokenProtector { string Protect(string plaintext); string Unprotect(string protectedValue); }`; `AesGcmTokenProtector` (singleton); config section `TokenProtection:Key` (32-byte base64).

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/JiraUnpacked.Tests/Security/AesGcmTokenProtectorTests.cs
using System.Security.Cryptography;
using JiraUnpacked.Infrastructure.Security;
using Microsoft.Extensions.Options;

namespace JiraUnpacked.Tests.Security;

public class AesGcmTokenProtectorTests
{
    private static AesGcmTokenProtector New(out byte[] key)
    {
        key = RandomNumberGenerator.GetBytes(32);
        var opts = Options.Create(new TokenProtectionOptions { Key = Convert.ToBase64String(key) });
        return new AesGcmTokenProtector(opts);
    }

    [Fact]
    public void Protect_then_Unprotect_round_trips()
    {
        var p = New(out _);
        var cipher = p.Protect("super-secret-token");
        Assert.NotEqual("super-secret-token", cipher);
        Assert.Equal("super-secret-token", p.Unprotect(cipher));
    }

    [Fact]
    public void Protect_uses_a_fresh_nonce_each_call()
    {
        var p = New(out _);
        Assert.NotEqual(p.Protect("x"), p.Protect("x"));
    }

    [Fact]
    public void Unprotect_fails_on_tampered_ciphertext()
    {
        var p = New(out _);
        var bytes = Convert.FromBase64String(p.Protect("x"));
        bytes[^1] ^= 0xFF; // flip a ciphertext byte
        Assert.ThrowsAny<CryptographicException>(() => p.Unprotect(Convert.ToBase64String(bytes)));
    }

    [Fact]
    public void Unprotect_fails_with_a_different_key()
    {
        var a = New(out _);
        var cipher = a.Protect("x");
        var b = New(out _); // different random key
        Assert.ThrowsAny<CryptographicException>(() => b.Unprotect(cipher));
    }

    [Fact]
    public void Ctor_rejects_a_key_that_is_not_32_bytes()
    {
        var opts = Options.Create(new TokenProtectionOptions { Key = Convert.ToBase64String(new byte[16]) });
        Assert.Throws<InvalidOperationException>(() => new AesGcmTokenProtector(opts));
    }
}
```

- [ ] **Step 2: Run the tests, verify they fail to compile/fail**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~AesGcmTokenProtectorTests`
Expected: FAIL (types `ITokenProtector`/`AesGcmTokenProtector`/`TokenProtectionOptions` do not exist).

- [ ] **Step 3: Add the interface and options**

```csharp
// src/JiraUnpacked.Domain/Abstractions/ITokenProtector.cs
namespace JiraUnpacked.Domain.Abstractions;

public interface ITokenProtector
{
    string Protect(string plaintext);
    string Unprotect(string protectedValue);
}
```

```csharp
// src/JiraUnpacked.Infrastructure/Security/TokenProtectionOptions.cs
namespace JiraUnpacked.Infrastructure.Security;

public sealed class TokenProtectionOptions
{
    public const string SectionName = "TokenProtection";
    public string? Key { get; set; } // base64 of a 32-byte key
}
```

- [ ] **Step 4: Implement the protector**

```csharp
// src/JiraUnpacked.Infrastructure/Security/AesGcmTokenProtector.cs
using System.Security.Cryptography;
using System.Text;
using JiraUnpacked.Domain.Abstractions;
using Microsoft.Extensions.Options;

namespace JiraUnpacked.Infrastructure.Security;

public sealed class AesGcmTokenProtector : ITokenProtector
{
    private const int NonceSize = 12; // AES-GCM standard nonce
    private const int TagSize = 16;
    private readonly byte[] _key;

    public AesGcmTokenProtector(IOptions<TokenProtectionOptions> options)
    {
        var raw = options.Value.Key;
        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidOperationException("TokenProtection:Key is not configured.");
        byte[] key;
        try { key = Convert.FromBase64String(raw); }
        catch (FormatException) { throw new InvalidOperationException("TokenProtection:Key is not valid base64."); }
        if (key.Length != 32)
            throw new InvalidOperationException("TokenProtection:Key must decode to exactly 32 bytes (AES-256).");
        _key = key;
    }

    public string Protect(string plaintext)
    {
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plain, cipher, tag);

        var combined = new byte[NonceSize + TagSize + cipher.Length];
        Buffer.BlockCopy(nonce, 0, combined, 0, NonceSize);
        Buffer.BlockCopy(tag, 0, combined, NonceSize, TagSize);
        Buffer.BlockCopy(cipher, 0, combined, NonceSize + TagSize, cipher.Length);
        return Convert.ToBase64String(combined);
    }

    public string Unprotect(string protectedValue)
    {
        var combined = Convert.FromBase64String(protectedValue);
        if (combined.Length < NonceSize + TagSize)
            throw new CryptographicException("Ciphertext is too short.");
        var nonce = combined.AsSpan(0, NonceSize);
        var tag = combined.AsSpan(NonceSize, TagSize);
        var cipher = combined.AsSpan(NonceSize + TagSize);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(nonce, cipher, tag, plain); // throws CryptographicException on auth failure
        return Encoding.UTF8.GetString(plain);
    }
}
```

- [ ] **Step 5: Register + fail-fast config**

In `DependencyInjection.AddInfrastructure`, after the existing service registrations add:

```csharp
services.AddOptions<TokenProtectionOptions>()
    .Bind(configuration.GetSection(TokenProtectionOptions.SectionName))
    .Validate(o => !string.IsNullOrWhiteSpace(o.Key) && IsThirtyTwoByteBase64(o.Key),
        "TokenProtection:Key must be a base64-encoded 32-byte key.")
    .ValidateOnStart();
services.AddSingleton<ITokenProtector, AesGcmTokenProtector>();
```

Add this private helper to the `DependencyInjection` class:

```csharp
private static bool IsThirtyTwoByteBase64(string value)
{
    try { return Convert.FromBase64String(value).Length == 32; }
    catch (FormatException) { return false; }
}
```

Add `using JiraUnpacked.Domain.Abstractions;` and `using JiraUnpacked.Infrastructure.Security;` if not present.

- [ ] **Step 6: Add a dev key to `appsettings.Development.json`**

Generate a 32-byte key (run `openssl rand -base64 32`, or PowerShell `[Convert]::ToBase64String((1..32 | ForEach-Object { Get-Random -Maximum 256 }))`) and add a top-level section (sibling of `ConnectionStrings`/`Jwt`/`Seed`):

```json
"TokenProtection": {
  "Key": "<paste the generated 44-char base64 here>"
}
```

This dev/CI key is consumed by integration tests (which run in the Development environment). It must be rotated out-of-band in production.

- [ ] **Step 7: Run tests + commit**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~AesGcmTokenProtectorTests`
Expected: PASS (5 tests).

```bash
git add src/JiraUnpacked.Domain/Abstractions/ITokenProtector.cs \
        src/JiraUnpacked.Infrastructure/Security/ \
        src/JiraUnpacked.Infrastructure/DependencyInjection.cs \
        src/JiraUnpacked.Api/appsettings.Development.json \
        tests/JiraUnpacked.Tests/Security/AesGcmTokenProtectorTests.cs
git commit -m "feat: ITokenProtector + AES-GCM token protector (Module C, FR-C1)"
```

---

## Task 2 (CSL2): Jira API client core + WireMock harness

**Files:**
- Create: `src/JiraUnpacked.Domain/Abstractions/Jira/JiraCredentials.cs`
- Create: `src/JiraUnpacked.Domain/Abstractions/Jira/JiraValidationResult.cs`
- Create: `src/JiraUnpacked.Domain/Abstractions/Jira/JiraApiException.cs`
- Create: `src/JiraUnpacked.Domain/Abstractions/IJiraApiClient.cs`
- Create: `src/JiraUnpacked.Infrastructure/Jira/JiraApiClient.cs`
- Modify: `src/JiraUnpacked.Infrastructure/JiraUnpacked.Infrastructure.csproj` (add `Microsoft.Extensions.Http`)
- Modify: `src/JiraUnpacked.Infrastructure/DependencyInjection.cs`
- Modify: `tests/JiraUnpacked.Tests/JiraUnpacked.Tests.csproj` (add WireMock.Net)
- Test: `tests/JiraUnpacked.Tests/Jira/JiraApiClientValidateTests.cs`

**Interfaces:**
- Produces: `record JiraCredentials(string BaseUrl, string Email, string ApiToken)`; `enum JiraValidationResult { Valid, InvalidCredentials, Unreachable }`; `JiraApiException(JiraErrorKind Kind, string message)` with `enum JiraErrorKind { Unauthorized, NotFound, RateLimited, Upstream }`; `IJiraApiClient { Task<JiraValidationResult> ValidateAsync(JiraCredentials, CancellationToken); }` (discovery methods added in CSL3); `JiraApiClient` typed `HttpClient`.

- [ ] **Step 1: Add packages**

In `src/JiraUnpacked.Infrastructure/JiraUnpacked.Infrastructure.csproj` add to the package `ItemGroup`:

```xml
<PackageReference Include="Microsoft.Extensions.Http" Version="8.0.1" />
```

In `tests/JiraUnpacked.Tests/JiraUnpacked.Tests.csproj` add:

```xml
<PackageReference Include="WireMock.Net" Version="1.5.62" />
```

Run `dotnet restore` and confirm it resolves.

- [ ] **Step 2: Write the failing test (WireMock-backed)**

```csharp
// tests/JiraUnpacked.Tests/Jira/JiraApiClientValidateTests.cs
using System.Net;
using System.Text;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Abstractions.Jira;
using JiraUnpacked.Infrastructure.Jira;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace JiraUnpacked.Tests.Jira;

public sealed class JiraApiClientValidateTests : IDisposable
{
    private readonly WireMockServer _server = WireMockServer.Start();
    private static IJiraApiClient Client() => new JiraApiClient(new HttpClient());

    public void Dispose() => _server.Stop();

    private JiraCredentials Creds() => new(_server.Url!, "ops@acme.com", "secret-token");

    [Fact]
    public async Task ValidateAsync_returns_Valid_and_sends_basic_auth_on_200()
    {
        _server.Given(Request.Create().WithPath("/rest/api/3/myself").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(200)
                   .WithBodyAsJson(new { accountId = "abc", displayName = "Ops" }));

        var result = await Client().ValidateAsync(Creds(), CancellationToken.None);

        Assert.Equal(JiraValidationResult.Valid, result);
        var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("ops@acme.com:secret-token"));
        var log = Assert.Single(_server.LogEntries);
        Assert.Equal(expected, log.RequestMessage.Headers!["Authorization"][0]);
    }

    [Fact]
    public async Task ValidateAsync_returns_InvalidCredentials_on_401()
    {
        _server.Given(Request.Create().WithPath("/rest/api/3/myself").UsingGet())
               .RespondWith(Response.Create().WithStatusCode((int)HttpStatusCode.Unauthorized));
        Assert.Equal(JiraValidationResult.InvalidCredentials,
            await Client().ValidateAsync(Creds(), CancellationToken.None));
    }

    [Fact]
    public async Task ValidateAsync_returns_Unreachable_on_500()
    {
        _server.Given(Request.Create().WithPath("/rest/api/3/myself").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(500));
        Assert.Equal(JiraValidationResult.Unreachable,
            await Client().ValidateAsync(Creds(), CancellationToken.None));
    }
}
```

- [ ] **Step 3: Run, verify failure**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~JiraApiClientValidateTests`
Expected: FAIL (`IJiraApiClient`/`JiraApiClient` not defined).

- [ ] **Step 4: Add the abstraction types**

```csharp
// src/JiraUnpacked.Domain/Abstractions/Jira/JiraCredentials.cs
namespace JiraUnpacked.Domain.Abstractions.Jira;

public sealed record JiraCredentials(string BaseUrl, string Email, string ApiToken);
```

```csharp
// src/JiraUnpacked.Domain/Abstractions/Jira/JiraValidationResult.cs
namespace JiraUnpacked.Domain.Abstractions.Jira;

public enum JiraValidationResult { Valid, InvalidCredentials, Unreachable }
```

```csharp
// src/JiraUnpacked.Domain/Abstractions/Jira/JiraApiException.cs
namespace JiraUnpacked.Domain.Abstractions.Jira;

public enum JiraErrorKind { Unauthorized, NotFound, RateLimited, Upstream }

public sealed class JiraApiException : Exception
{
    public JiraErrorKind Kind { get; }
    public JiraApiException(JiraErrorKind kind, string message) : base(message) => Kind = kind;
}
```

```csharp
// src/JiraUnpacked.Domain/Abstractions/IJiraApiClient.cs
using JiraUnpacked.Domain.Abstractions.Jira;

namespace JiraUnpacked.Domain.Abstractions;

public interface IJiraApiClient
{
    Task<JiraValidationResult> ValidateAsync(JiraCredentials creds, CancellationToken ct = default);
    // discovery methods added in CSL3
}
```

- [ ] **Step 5: Implement the client core**

```csharp
// src/JiraUnpacked.Infrastructure/Jira/JiraApiClient.cs
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Abstractions.Jira;

namespace JiraUnpacked.Infrastructure.Jira;

public sealed partial class JiraApiClient : IJiraApiClient
{
    private readonly HttpClient _http;

    public JiraApiClient(HttpClient http)
    {
        _http = http;
        if (_http.Timeout == Timeout.InfiniteTimeSpan || _http.Timeout > TimeSpan.FromSeconds(30))
            _http.Timeout = TimeSpan.FromSeconds(30);
    }

    public async Task<JiraValidationResult> ValidateAsync(JiraCredentials creds, CancellationToken ct = default)
    {
        try
        {
            using var req = Build(HttpMethod.Get, creds, "/rest/api/3/myself");
            using var resp = await _http.SendAsync(req, ct);
            return resp.StatusCode switch
            {
                HttpStatusCode.OK => JiraValidationResult.Valid,
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => JiraValidationResult.InvalidCredentials,
                _ => JiraValidationResult.Unreachable
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return JiraValidationResult.Unreachable;
        }
    }

    // Builds a request to {baseUrl}{path} with Basic auth + JSON accept.
    private static HttpRequestMessage Build(HttpMethod method, JiraCredentials creds, string path)
    {
        var uri = new Uri(new Uri(creds.BaseUrl, UriKind.Absolute), path);
        var req = new HttpRequestMessage(method, uri);
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{creds.Email}:{creds.ApiToken}"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return req;
    }
}
```

(`partial` so CSL3 can add discovery methods in a second file.)

- [ ] **Step 6: Register the typed client**

In `DependencyInjection.AddInfrastructure` add:

```csharp
services.AddHttpClient<IJiraApiClient, JiraApiClient>();
```

Add `using JiraUnpacked.Infrastructure.Jira;`.

- [ ] **Step 7: Run tests + commit**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~JiraApiClientValidateTests`
Expected: PASS (3 tests).

```bash
git add src/JiraUnpacked.Domain/Abstractions/ src/JiraUnpacked.Infrastructure/Jira/JiraApiClient.cs \
        src/JiraUnpacked.Infrastructure/JiraUnpacked.Infrastructure.csproj \
        src/JiraUnpacked.Infrastructure/DependencyInjection.cs \
        tests/JiraUnpacked.Tests/JiraUnpacked.Tests.csproj \
        tests/JiraUnpacked.Tests/Jira/JiraApiClientValidateTests.cs
git commit -m "feat: Jira API client core + credential validation (Module C, FR-C1)"
```

---

## Task 3 (CSL3): Jira client discovery methods

**Files:**
- Create: `src/JiraUnpacked.Domain/Abstractions/Jira/JiraMetadata.cs` (DTOs)
- Modify: `src/JiraUnpacked.Domain/Abstractions/IJiraApiClient.cs`
- Create: `src/JiraUnpacked.Infrastructure/Jira/JiraApiClient.Discovery.cs`
- Test: `tests/JiraUnpacked.Tests/Jira/JiraApiClientDiscoveryTests.cs`

**Interfaces:**
- Consumes: `JiraCredentials`, `JiraApiException`, the `partial JiraApiClient` + `Build(...)` from CSL2.
- Produces on `IJiraApiClient`:
  - `Task<IReadOnlyList<JiraField>> GetFieldsAsync(JiraCredentials, CancellationToken)`
  - `Task<IReadOnlyList<JiraProjectSummary>> GetProjectsAsync(JiraCredentials, CancellationToken)`
  - `Task<JiraProjectSummary?> GetProjectAsync(JiraCredentials, string projectKey, CancellationToken)` (null on 404)
  - `Task<IReadOnlyList<JiraStatus>> GetStatusesAsync(JiraCredentials, string projectKey, CancellationToken)`
  - `Task<JqlValidationResult> ParseJqlAsync(JiraCredentials, string jql, CancellationToken)`
  - DTOs: `record JiraField(string Id, string Name, bool Custom)`; `record JiraProjectSummary(string Id, string Key, string Name)`; `record JiraStatus(string Id, string Name)`; `record JqlValidationResult(bool Valid, IReadOnlyList<string> Errors)`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/JiraUnpacked.Tests/Jira/JiraApiClientDiscoveryTests.cs
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Abstractions.Jira;
using JiraUnpacked.Infrastructure.Jira;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace JiraUnpacked.Tests.Jira;

public sealed class JiraApiClientDiscoveryTests : IDisposable
{
    private readonly WireMockServer _server = WireMockServer.Start();
    private IJiraApiClient Client() => new JiraApiClient(new HttpClient());
    private JiraCredentials Creds() => new(_server.Url!, "ops@acme.com", "t");
    public void Dispose() => _server.Stop();

    [Fact]
    public async Task GetFieldsAsync_maps_id_name_custom()
    {
        _server.Given(Request.Create().WithPath("/rest/api/3/field").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new object[]
            {
                new { id = "summary", name = "Summary", custom = false },
                new { id = "customfield_10020", name = "Sprint", custom = true }
            }));

        var fields = await Client().GetFieldsAsync(Creds(), CancellationToken.None);

        Assert.Equal(2, fields.Count);
        Assert.Contains(fields, f => f.Id == "customfield_10020" && f.Name == "Sprint" && f.Custom);
    }

    [Fact]
    public async Task GetProjectsAsync_reads_the_values_array()
    {
        _server.Given(Request.Create().WithPath("/rest/api/3/project/search").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                values = new object[] { new { id = "10001", key = "ACME", name = "Acme" } }
            }));

        var projects = await Client().GetProjectsAsync(Creds(), CancellationToken.None);
        var p = Assert.Single(projects);
        Assert.Equal(("10001", "ACME", "Acme"), (p.Id, p.Key, p.Name));
    }

    [Fact]
    public async Task GetProjectAsync_returns_summary_on_200_and_null_on_404()
    {
        _server.Given(Request.Create().WithPath("/rest/api/3/project/ACME").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBodyAsJson(new { id = "10001", key = "ACME", name = "Acme" }));
        _server.Given(Request.Create().WithPath("/rest/api/3/project/NOPE").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));

        Assert.Equal("10001", (await Client().GetProjectAsync(Creds(), "ACME", CancellationToken.None))!.Id);
        Assert.Null(await Client().GetProjectAsync(Creds(), "NOPE", CancellationToken.None));
    }

    [Fact]
    public async Task GetStatusesAsync_flattens_and_dedupes_across_issue_types()
    {
        _server.Given(Request.Create().WithPath("/rest/api/3/project/ACME/statuses").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new object[]
            {
                new { id = "10", name = "Story", statuses = new object[] {
                    new { id = "1", name = "To Do" }, new { id = "3", name = "Done" } } },
                new { id = "11", name = "Bug", statuses = new object[] {
                    new { id = "1", name = "To Do" }, new { id = "2", name = "In Progress" } } }
            }));

        var statuses = await Client().GetStatusesAsync(Creds(), "ACME", CancellationToken.None);

        Assert.Equal(3, statuses.Count); // 1,3,2 deduped by id
        Assert.Contains(statuses, s => s.Id == "2" && s.Name == "In Progress");
    }

    [Fact]
    public async Task ParseJqlAsync_reports_valid_and_invalid()
    {
        _server.Given(Request.Create().WithPath("/rest/api/3/jql/parse").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                queries = new object[] { new { query = "project = ACME", errors = new string[0] } }
            }));
        var ok = await Client().ParseJqlAsync(Creds(), "project = ACME", CancellationToken.None);
        Assert.True(ok.Valid);

        _server.Reset();
        _server.Given(Request.Create().WithPath("/rest/api/3/jql/parse").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                queries = new object[] { new { query = "bad jql", errors = new[] { "Unable to parse" } } }
            }));
        var bad = await Client().ParseJqlAsync(Creds(), "bad jql", CancellationToken.None);
        Assert.False(bad.Valid);
        Assert.Contains("Unable to parse", bad.Errors);
    }

    [Fact]
    public async Task Discovery_throws_JiraApiException_on_401()
    {
        _server.Given(Request.Create().WithPath("/rest/api/3/field").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(401));
        var ex = await Assert.ThrowsAsync<JiraApiException>(
            () => Client().GetFieldsAsync(Creds(), CancellationToken.None));
        Assert.Equal(JiraErrorKind.Unauthorized, ex.Kind);
    }
}
```

- [ ] **Step 2: Run, verify failure**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~JiraApiClientDiscoveryTests`
Expected: FAIL (methods/DTOs not defined).

- [ ] **Step 3: Add the DTOs and extend the interface**

```csharp
// src/JiraUnpacked.Domain/Abstractions/Jira/JiraMetadata.cs
namespace JiraUnpacked.Domain.Abstractions.Jira;

public sealed record JiraField(string Id, string Name, bool Custom);
public sealed record JiraProjectSummary(string Id, string Key, string Name);
public sealed record JiraStatus(string Id, string Name);
public sealed record JqlValidationResult(bool Valid, IReadOnlyList<string> Errors);
```

Extend `IJiraApiClient` (replace the `// discovery methods added in CSL3` comment):

```csharp
    Task<IReadOnlyList<JiraField>> GetFieldsAsync(JiraCredentials creds, CancellationToken ct = default);
    Task<IReadOnlyList<JiraProjectSummary>> GetProjectsAsync(JiraCredentials creds, CancellationToken ct = default);
    Task<JiraProjectSummary?> GetProjectAsync(JiraCredentials creds, string projectKey, CancellationToken ct = default);
    Task<IReadOnlyList<JiraStatus>> GetStatusesAsync(JiraCredentials creds, string projectKey, CancellationToken ct = default);
    Task<JqlValidationResult> ParseJqlAsync(JiraCredentials creds, string jql, CancellationToken ct = default);
```

- [ ] **Step 4: Implement discovery (second partial file)**

```csharp
// src/JiraUnpacked.Infrastructure/Jira/JiraApiClient.Discovery.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using JiraUnpacked.Domain.Abstractions.Jira;

namespace JiraUnpacked.Infrastructure.Jira;

public sealed partial class JiraApiClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<JiraField>> GetFieldsAsync(JiraCredentials creds, CancellationToken ct = default)
    {
        var dto = await GetAsync<List<FieldDto>>(creds, "/rest/api/3/field", ct);
        return dto.Select(f => new JiraField(f.Id, f.Name, f.Custom)).ToList();
    }

    public async Task<IReadOnlyList<JiraProjectSummary>> GetProjectsAsync(JiraCredentials creds, CancellationToken ct = default)
    {
        var dto = await GetAsync<ProjectSearchDto>(creds, "/rest/api/3/project/search", ct);
        return dto.Values.Select(p => new JiraProjectSummary(p.Id, p.Key, p.Name)).ToList();
    }

    public async Task<JiraProjectSummary?> GetProjectAsync(JiraCredentials creds, string projectKey, CancellationToken ct = default)
    {
        using var req = Build(HttpMethod.Get, creds, $"/rest/api/3/project/{Uri.EscapeDataString(projectKey)}");
        using var resp = await SendAsync(req, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        EnsureOk(resp);
        var p = (await resp.Content.ReadFromJsonAsync<ProjectDto>(Json, ct))!;
        return new JiraProjectSummary(p.Id, p.Key, p.Name);
    }

    public async Task<IReadOnlyList<JiraStatus>> GetStatusesAsync(JiraCredentials creds, string projectKey, CancellationToken ct = default)
    {
        var dto = await GetAsync<List<IssueTypeStatusesDto>>(
            creds, $"/rest/api/3/project/{Uri.EscapeDataString(projectKey)}/statuses", ct);
        var byId = new Dictionary<string, JiraStatus>();
        foreach (var it in dto)
            foreach (var s in it.Statuses)
                byId.TryAdd(s.Id, new JiraStatus(s.Id, s.Name));
        return byId.Values.ToList();
    }

    public async Task<JqlValidationResult> ParseJqlAsync(JiraCredentials creds, string jql, CancellationToken ct = default)
    {
        using var req = Build(HttpMethod.Post, creds, "/rest/api/3/jql/parse");
        req.Content = JsonContent.Create(new { queries = new[] { jql } }, options: Json);
        using var resp = await SendAsync(req, ct);
        EnsureOk(resp);
        var dto = (await resp.Content.ReadFromJsonAsync<JqlParseDto>(Json, ct))!;
        var errors = dto.Queries.FirstOrDefault()?.Errors ?? new List<string>();
        return new JqlValidationResult(errors.Count == 0, errors);
    }

    private async Task<T> GetAsync<T>(JiraCredentials creds, string path, CancellationToken ct)
    {
        using var req = Build(HttpMethod.Get, creds, path);
        using var resp = await SendAsync(req, ct);
        EnsureOk(resp);
        return (await resp.Content.ReadFromJsonAsync<T>(Json, ct))!;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        try { return await _http.SendAsync(req, ct); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        { throw new JiraApiException(JiraErrorKind.Upstream, "Jira is unreachable."); }
    }

    private static void EnsureOk(HttpResponseMessage resp)
    {
        if (resp.IsSuccessStatusCode) return;
        throw resp.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                new JiraApiException(JiraErrorKind.Unauthorized, "Jira rejected the credentials."),
            HttpStatusCode.NotFound => new JiraApiException(JiraErrorKind.NotFound, "Jira resource not found."),
            HttpStatusCode.TooManyRequests => new JiraApiException(JiraErrorKind.RateLimited, "Jira rate limit hit."),
            _ => new JiraApiException(JiraErrorKind.Upstream, $"Jira returned {(int)resp.StatusCode}.")
        };
    }

    private sealed record FieldDto([property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("custom")] bool Custom);
    private sealed record ProjectDto(string Id, string Key, string Name);
    private sealed record ProjectSearchDto(List<ProjectDto> Values);
    private sealed record IssueTypeStatusesDto(List<StatusDto> Statuses);
    private sealed record StatusDto(string Id, string Name);
    private sealed record JqlParseDto(List<JqlQueryDto> Queries);
    private sealed record JqlQueryDto(string Query, List<string> Errors);
}
```

Note: the DTOs rely on web-default (case-insensitive) JSON binding; `FieldDto` adds explicit `JsonPropertyName` only for clarity. Unmapped Jira response fields (e.g. `structure` on the JQL parse result) are ignored.

- [ ] **Step 5: Run tests + commit**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~JiraApiClientDiscoveryTests`
Expected: PASS (6 tests).

```bash
git add src/JiraUnpacked.Domain/Abstractions/ src/JiraUnpacked.Infrastructure/Jira/JiraApiClient.Discovery.cs \
        tests/JiraUnpacked.Tests/Jira/JiraApiClientDiscoveryTests.cs
git commit -m "feat: Jira discovery (fields/projects/statuses/jql-parse) (Module C, FR-C5)"
```

---

## Task 4 (CSL4): Harden connection create — validate + encrypt

**Files:**
- Modify: `src/JiraUnpacked.Domain/Abstractions/IJiraConnectionService.cs` (new return type for create)
- Modify: `src/JiraUnpacked.Infrastructure/Services/JiraConnectionService.cs`
- Modify: `src/JiraUnpacked.Api/Controllers/ConnectionsController.cs`
- Modify: `tests/JiraUnpacked.Tests/Tenancy/TenantIsolationGateTests.cs` (connection create now needs a Jira stub)
- Test: `tests/JiraUnpacked.Tests/Integration/ConnectionHardeningTests.cs`

**Interfaces:**
- Consumes: `ITokenProtector` (CSL1), `IJiraApiClient.ValidateAsync` (CSL2), `JiraCredentials`, `JiraValidationResult`.
- Produces: `enum CreateConnectionStatus { Created, InvalidCredentials, JiraUnreachable }`; `record CreateConnectionResult(CreateConnectionStatus Status, ConnectionView? View)`; `IJiraConnectionService.CreateAsync` now returns `Task<CreateConnectionResult>`.

- [ ] **Step 1: Write the failing integration tests**

```csharp
// tests/JiraUnpacked.Tests/Integration/ConnectionHardeningTests.cs
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using JiraUnpacked.Domain.Abstractions;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace JiraUnpacked.Tests.Integration;

public sealed class ConnectionHardeningTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime, IDisposable
{
    private readonly JiraUnpackedApiFactory _factory;
    private readonly WireMockServer _jira = WireMockServer.Start();
    public ConnectionHardeningTests(JiraUnpackedApiFactory factory) => _factory = factory;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;
    public void Dispose() => _jira.Stop();

    private sealed record TokenResponseDto(string AccessToken, DateTime ExpiresAtUtc, string RefreshToken);
    private sealed record ConnectionDto(Guid Id, string BaseUrl, string Email, DateTime CreatedAtUtc);

    private async Task<HttpClient> AuthedClientAsync()
    {
        var unique = Guid.NewGuid().ToString("N");
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
            db.Users.Add(new User { Id = Guid.NewGuid(), Email = $"u-{unique}@x.com", Username = $"u-{unique}",
                PasswordHash = hasher.Hash("Correct-Horse-9!"), Role = UserRole.EndUser,
                Status = UserStatus.Active, CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        var c = _factory.CreateClient();
        var login = await c.PostAsJsonAsync("/api/auth/login",
            new { emailOrUsername = $"u-{unique}@x.com", password = "Correct-Horse-9!" });
        var tok = await login.Content.ReadFromJsonAsync<TokenResponseDto>();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tok!.AccessToken);
        return c;
    }

    [Fact]
    public async Task Create_with_valid_creds_stores_an_encrypted_token()
    {
        _jira.Given(Request.Create().WithPath("/rest/api/3/myself").UsingGet())
             .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { accountId = "a" }));
        var client = await AuthedClientAsync();

        var resp = await client.PostAsJsonAsync("/api/connections",
            new { baseUrl = _jira.Url, email = "ops@acme.com", apiToken = "secret-token" });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var view = (await resp.Content.ReadFromJsonAsync<ConnectionDto>())!;

        // Token at rest is ciphertext, never the plaintext, and round-trips via the protector.
        using var scope = _factory.Services.CreateScope();
        var protector = scope.ServiceProvider.GetRequiredService<ITokenProtector>();
        await using var conn = new Npgsql.NpgsqlConnection(JiraUnpackedApiFactory.TestConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT ""ApiTokenEncrypted"" FROM ""JiraConnections"" WHERE ""Id"" = @id";
        cmd.Parameters.AddWithValue("id", view.Id);
        var stored = (string)(await cmd.ExecuteScalarAsync())!;
        Assert.NotEqual("secret-token", stored);
        Assert.Equal("secret-token", protector.Unprotect(stored));
    }

    [Fact]
    public async Task Create_with_bad_creds_is_rejected_and_stores_nothing()
    {
        _jira.Given(Request.Create().WithPath("/rest/api/3/myself").UsingGet())
             .RespondWith(Response.Create().WithStatusCode(401));
        var client = await AuthedClientAsync();

        var resp = await client.PostAsJsonAsync("/api/connections",
            new { baseUrl = _jira.Url, email = "ops@acme.com", apiToken = "bad" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        var list = await (await client.GetAsync("/api/connections")).Content.ReadFromJsonAsync<List<ConnectionDto>>();
        Assert.Empty(list!);
    }
}
```

- [ ] **Step 2: Run, verify failure**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~ConnectionHardeningTests`
Expected: FAIL (create still returns a plain `ConnectionView`; no validation; token stored plaintext).

- [ ] **Step 3: Change the service contract**

In `src/JiraUnpacked.Domain/Abstractions/IJiraConnectionService.cs`, add the result types and change the create signature:

```csharp
public enum CreateConnectionStatus { Created, InvalidCredentials, JiraUnreachable }
public sealed record CreateConnectionResult(CreateConnectionStatus Status, ConnectionView? View);
```

Change:
```csharp
Task<ConnectionView> CreateAsync(string baseUrl, string email, string apiToken, CancellationToken ct = default);
```
to:
```csharp
Task<CreateConnectionResult> CreateAsync(string baseUrl, string email, string apiToken, CancellationToken ct = default);
```

- [ ] **Step 4: Implement validation + encryption**

Replace `JiraConnectionService.CreateAsync` (and add the two new dependencies) in `src/JiraUnpacked.Infrastructure/Services/JiraConnectionService.cs`:

```csharp
using JiraUnpacked.Domain.Abstractions.Jira;
// ...
public sealed class JiraConnectionService : IJiraConnectionService
{
    private readonly AppDbContext _db;
    private readonly IClock _clock;
    private readonly IJiraApiClient _jira;
    private readonly ITokenProtector _protector;

    public JiraConnectionService(AppDbContext db, IClock clock, IJiraApiClient jira, ITokenProtector protector)
    {
        _db = db; _clock = clock; _jira = jira; _protector = protector;
    }

    public async Task<CreateConnectionResult> CreateAsync(string baseUrl, string email, string apiToken, CancellationToken ct = default)
    {
        var validation = await _jira.ValidateAsync(new JiraCredentials(baseUrl, email, apiToken), ct);
        if (validation == JiraValidationResult.InvalidCredentials)
            return new CreateConnectionResult(CreateConnectionStatus.InvalidCredentials, null);
        if (validation == JiraValidationResult.Unreachable)
            return new CreateConnectionResult(CreateConnectionStatus.JiraUnreachable, null);

        var connection = new JiraConnection
        {
            Id = Guid.NewGuid(),
            BaseUrl = baseUrl,
            Email = email,
            ApiTokenEncrypted = _protector.Protect(apiToken),
            CreatedAt = _clock.UtcNow
        };
        _db.JiraConnections.Add(connection);
        await _db.SaveChangesAsync(ct);
        return new CreateConnectionResult(CreateConnectionStatus.Created, ToView(connection));
    }
    // ListAsync / GetAsync / ToView unchanged
}
```

- [ ] **Step 5: Map the result in the controller**

Replace `ConnectionsController.Create`:

```csharp
[HttpPost]
public async Task<IActionResult> Create(CreateConnectionRequest request, CancellationToken ct)
{
    var result = await _connections.CreateAsync(request.BaseUrl, request.Email, request.ApiToken, ct);
    return result.Status switch
    {
        CreateConnectionStatus.Created =>
            CreatedAtAction(nameof(Get), new { id = result.View!.Id }, ToResponse(result.View)),
        CreateConnectionStatus.InvalidCredentials =>
            Problem(statusCode: StatusCodes.Status400BadRequest, title: "Jira credentials could not be validated."),
        _ => Problem(statusCode: StatusCodes.Status502BadGateway, title: "Jira could not be reached.")
    };
}
```

Add `using JiraUnpacked.Domain.Abstractions;` for the enum if needed.

- [ ] **Step 6: Fix the existing gate test (create now requires a Jira stub)**

In `tests/JiraUnpacked.Tests/Tenancy/TenantIsolationGateTests.cs`: add a class-level WireMock server, stub `/rest/api/3/myself` → 200, dispose it, and point the connection `baseUrl` at it. Change the class to `IDisposable`, add:

```csharp
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
// ...
private readonly WireMock.Server.WireMockServer _jira = WireMock.Server.WireMockServer.Start();
// in constructor body or field init, stub once:
//   _jira.Given(Request.Create().WithPath("/rest/api/3/myself").UsingGet())
//        .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { accountId = "a" }));
public void Dispose() => _jira.Stop();
```

Add the stub in the constructor (after assigning `_factory`). Then change `CreateConnectionAsync` to post `baseUrl = <the test's jira url>`:

```csharp
private async Task<ConnectionDto> CreateConnectionAsync(HttpClient client, string baseUrl)
{
    var resp = await client.PostAsJsonAsync("/api/connections",
        new { baseUrl, email = "ops@acme.com", apiToken = "secret-token" });
    Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
    return (await resp.Content.ReadFromJsonAsync<ConnectionDto>())!;
}
```

Update the two call sites to pass `_jira.Url!`. (The `IClassFixture`/`IAsyncLifetime` members stay; just add `IDisposable`.)

- [ ] **Step 7: Run the affected tests + commit**

Run:
```
dotnet test tests/JiraUnpacked.Tests --filter "FullyQualifiedName~ConnectionHardeningTests|FullyQualifiedName~TenantIsolationGateTests|FullyQualifiedName~CurrentTenantStampTests"
```
Expected: PASS (hardening 2, gate 1, stamp 2).

```bash
git add src/JiraUnpacked.Domain/Abstractions/IJiraConnectionService.cs \
        src/JiraUnpacked.Infrastructure/Services/JiraConnectionService.cs \
        src/JiraUnpacked.Api/Controllers/ConnectionsController.cs \
        tests/JiraUnpacked.Tests/Tenancy/TenantIsolationGateTests.cs \
        tests/JiraUnpacked.Tests/Integration/ConnectionHardeningTests.cs
git commit -m "feat: validate Jira creds + encrypt token on connection create (Module C, FR-C1)"
```

---

## Task 5 (CSL5): Connection discovery endpoints + Jira-exception filter

**Files:**
- Create: `src/JiraUnpacked.Api/Filters/JiraApiExceptionFilter.cs`
- Modify: `src/JiraUnpacked.Api/Program.cs` (register the filter)
- Modify: `src/JiraUnpacked.Domain/Abstractions/IJiraConnectionService.cs`
- Modify: `src/JiraUnpacked.Infrastructure/Services/JiraConnectionService.cs`
- Modify: `src/JiraUnpacked.Api/Controllers/ConnectionsController.cs`
- Test: `tests/JiraUnpacked.Tests/Integration/ConnectionDiscoveryTests.cs`

**Interfaces:**
- Consumes: `IJiraApiClient.GetFieldsAsync/GetProjectsAsync` (CSL3), `ITokenProtector.Unprotect`, `JiraApiException`.
- Produces on `IJiraConnectionService`:
  - `Task<IReadOnlyList<JiraField>?> GetFieldsAsync(Guid connectionId, CancellationToken)` (null when the connection isn't found for this tenant)
  - `Task<IReadOnlyList<JiraProjectSummary>?> GetAvailableProjectsAsync(Guid connectionId, CancellationToken)`
- Produces: global `JiraApiExceptionFilter` mapping `JiraApiException` → 502/401.

- [ ] **Step 1: Write the failing integration test**

```csharp
// tests/JiraUnpacked.Tests/Integration/ConnectionDiscoveryTests.cs
using System.Net;
using System.Net.Http.Json;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace JiraUnpacked.Tests.Integration;

public sealed class ConnectionDiscoveryTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime, IDisposable
{
    private readonly JiraUnpackedApiFactory _factory;
    private readonly WireMockServer _jira = WireMockServer.Start();
    public ConnectionDiscoveryTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;
    public void Dispose() => _jira.Stop();

    private sealed record ConnectionDto(Guid Id, string BaseUrl, string Email, DateTime CreatedAtUtc);
    private sealed record FieldDto(string Id, string Name, bool Custom);

    [Fact]
    public async Task Lists_jira_fields_for_a_connection()
    {
        _jira.Given(Request.Create().WithPath("/rest/api/3/myself").UsingGet())
             .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { accountId = "a" }));
        _jira.Given(Request.Create().WithPath("/rest/api/3/field").UsingGet())
             .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new object[]
             { new { id = "customfield_10020", name = "Sprint", custom = true } }));

        var client = await TestData.AuthedClientAsync(_factory);
        var conn = (await (await client.PostAsJsonAsync("/api/connections",
            new { baseUrl = _jira.Url, email = "ops@acme.com", apiToken = "t" }))
            .Content.ReadFromJsonAsync<ConnectionDto>())!;

        var fields = await (await client.GetAsync($"/api/connections/{conn.Id}/fields"))
            .Content.ReadFromJsonAsync<List<FieldDto>>();
        Assert.Contains(fields!, f => f.Id == "customfield_10020");
    }

    [Fact]
    public async Task Unknown_connection_returns_404()
    {
        var client = await TestData.AuthedClientAsync(_factory);
        var resp = await client.GetAsync($"/api/connections/{Guid.NewGuid()}/fields");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
```

Add a small shared helper (used by this and later tasks) to avoid repeating the login boilerplate:

```csharp
// tests/JiraUnpacked.Tests/Integration/TestData.cs
using System.Net.Http.Headers;
using System.Net.Http.Json;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace JiraUnpacked.Tests.Integration;

internal static class TestData
{
    private sealed record TokenResponseDto(string AccessToken, DateTime ExpiresAtUtc, string RefreshToken);

    public static async Task<HttpClient> AuthedClientAsync(JiraUnpackedApiFactory factory)
    {
        var unique = Guid.NewGuid().ToString("N");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
            db.Users.Add(new User { Id = Guid.NewGuid(), Email = $"u-{unique}@x.com", Username = $"u-{unique}",
                PasswordHash = hasher.Hash("Correct-Horse-9!"), Role = UserRole.EndUser,
                Status = UserStatus.Active, CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        var c = factory.CreateClient();
        var login = await c.PostAsJsonAsync("/api/auth/login",
            new { emailOrUsername = $"u-{unique}@x.com", password = "Correct-Horse-9!" });
        var tok = await login.Content.ReadFromJsonAsync<TokenResponseDto>();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tok!.AccessToken);
        return c;
    }
}
```

- [ ] **Step 2: Run, verify failure**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~ConnectionDiscoveryTests`
Expected: FAIL (endpoints + service methods missing).

- [ ] **Step 3: Add the exception filter**

```csharp
// src/JiraUnpacked.Api/Filters/JiraApiExceptionFilter.cs
using JiraUnpacked.Domain.Abstractions.Jira;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace JiraUnpacked.Api.Filters;

public sealed class JiraApiExceptionFilter : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        if (context.Exception is not JiraApiException ex) return;
        var status = ex.Kind == JiraErrorKind.Unauthorized ? 401 : 502;
        context.Result = new ObjectResult(new ProblemDetails
        {
            Status = status,
            Title = ex.Kind == JiraErrorKind.Unauthorized
                ? "Jira rejected the stored credentials."
                : "Jira could not be reached."
        }) { StatusCode = status };
        context.ExceptionHandled = true;
    }
}
```

Register it globally in `Program.cs`, and at the same time add a `JsonStringEnumConverter` so enum
DTO members (the `FieldRole`/`StatusClass` used by the mapping endpoints in CSL10) bind and serialize
as **strings** in both requests and responses. Change `builder.Services.AddControllers();` to:

```csharp
builder.Services
    .AddControllers(o => o.Filters.Add<JiraUnpacked.Api.Filters.JiraApiExceptionFilter>())
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(
        new System.Text.Json.Serialization.JsonStringEnumConverter()));
```

> If any **existing** test asserts an enum field as a number in a JSON response, update it to the
> string form — this converter makes all enums serialize as strings API-wide (consistent with the
> enums-as-strings DB convention). Run the full suite after this task to catch any such case.

- [ ] **Step 4: Add the service methods**

In `IJiraConnectionService`:

```csharp
Task<IReadOnlyList<JiraField>?> GetFieldsAsync(Guid connectionId, CancellationToken ct = default);
Task<IReadOnlyList<JiraProjectSummary>?> GetAvailableProjectsAsync(Guid connectionId, CancellationToken ct = default);
```
(add `using JiraUnpacked.Domain.Abstractions.Jira;`)

In `JiraConnectionService`:

```csharp
public async Task<IReadOnlyList<JiraField>?> GetFieldsAsync(Guid connectionId, CancellationToken ct = default)
{
    var creds = await CredsAsync(connectionId, ct);
    return creds is null ? null : await _jira.GetFieldsAsync(creds, ct);
}

public async Task<IReadOnlyList<JiraProjectSummary>?> GetAvailableProjectsAsync(Guid connectionId, CancellationToken ct = default)
{
    var creds = await CredsAsync(connectionId, ct);
    return creds is null ? null : await _jira.GetProjectsAsync(creds, ct);
}

// Loads a tenant-scoped connection and decrypts its token; null if not found.
private async Task<JiraCredentials?> CredsAsync(Guid connectionId, CancellationToken ct)
{
    var c = await _db.JiraConnections.FirstOrDefaultAsync(x => x.Id == connectionId, ct);
    return c is null ? null : new JiraCredentials(c.BaseUrl, c.Email, _protector.Unprotect(c.ApiTokenEncrypted));
}
```

- [ ] **Step 5: Add the controller endpoints**

In `ConnectionsController`:

```csharp
[HttpGet("{id:guid}/fields")]
public async Task<IActionResult> Fields(Guid id, CancellationToken ct)
{
    var fields = await _connections.GetFieldsAsync(id, ct);
    return fields is null ? NotFound() : Ok(fields);
}

[HttpGet("{id:guid}/projects-available")]
public async Task<IActionResult> AvailableProjects(Guid id, CancellationToken ct)
{
    var projects = await _connections.GetAvailableProjectsAsync(id, ct);
    return projects is null ? NotFound() : Ok(projects);
}
```

- [ ] **Step 6: Run tests + commit**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~ConnectionDiscoveryTests`
Expected: PASS (2 tests).

```bash
git add src/JiraUnpacked.Api/Filters/JiraApiExceptionFilter.cs src/JiraUnpacked.Api/Program.cs \
        src/JiraUnpacked.Domain/Abstractions/IJiraConnectionService.cs \
        src/JiraUnpacked.Infrastructure/Services/JiraConnectionService.cs \
        src/JiraUnpacked.Api/Controllers/ConnectionsController.cs \
        tests/JiraUnpacked.Tests/Integration/ConnectionDiscoveryTests.cs \
        tests/JiraUnpacked.Tests/Integration/TestData.cs
git commit -m "feat: connection discovery endpoints (fields, available projects) (Module C, FR-C5)"
```

---

## Task 6 (CSL6): Semantic-layer entities + EF migration

**Files:**
- Create: `src/JiraUnpacked.Domain/Enums/FieldRole.cs`, `src/JiraUnpacked.Domain/Enums/StatusClass.cs`
- Create: `src/JiraUnpacked.Domain/Entities/JiraProject.cs`, `JiraQuery.cs`, `FieldMapping.cs`, `StatusSemantic.cs`
- Create: `src/JiraUnpacked.Infrastructure/Persistence/Configurations/JiraProjectConfiguration.cs` (+ Query/FieldMapping/StatusSemantic configs)
- Modify: `src/JiraUnpacked.Infrastructure/Persistence/AppDbContext.cs` (DbSets + query filters)
- Create (generated): `src/JiraUnpacked.Infrastructure/Persistence/Migrations/<ts>_AddSemanticLayerTables.cs`
- Test: `tests/JiraUnpacked.Tests/Persistence/SemanticLayerModelTests.cs`

**Interfaces:**
- Produces: entities `JiraProject(Id, EndUserId, JiraConnectionId, JiraProjectKey, JiraProjectId, SyncIntervalMinutes, CreatedAt)`, `JiraQuery(Id, EndUserId, JiraProjectId, Jql, CreatedAt)`, `FieldMapping(Id, EndUserId, JiraProjectId, Role, JiraFieldId)`, `StatusSemantic(Id, EndUserId, JiraProjectId, JiraStatusId, StatusName, Class)`; enums `FieldRole { Team, Assignee, Sprint, StoryPoints, Status }`, `StatusClass { Active, Done, Other }`.

- [ ] **Step 1: Write the failing model test**

```csharp
// tests/JiraUnpacked.Tests/Persistence/SemanticLayerModelTests.cs
using JiraUnpacked.Infrastructure.Persistence;
using JiraUnpacked.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace JiraUnpacked.Tests.Persistence;

public class SemanticLayerModelTests
{
    private static AppDbContext Ctx()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=model-only;Username=u;Password=p").Options;
        return new AppDbContext(options, new NullCurrentTenant());
    }

    [Fact]
    public void Model_includes_the_four_semantic_entities()
    {
        using var ctx = Ctx();
        var names = ctx.Model.GetEntityTypes().Select(e => e.ClrType.Name).ToHashSet();
        Assert.Contains(nameof(Domain.Entities.JiraProject), names);
        Assert.Contains(nameof(Domain.Entities.JiraQuery), names);
        Assert.Contains(nameof(Domain.Entities.FieldMapping), names);
        Assert.Contains(nameof(Domain.Entities.StatusSemantic), names);
    }

    [Fact]
    public void FieldMapping_is_unique_per_project_and_role()
    {
        using var ctx = Ctx();
        var e = ctx.Model.FindEntityType(typeof(Domain.Entities.FieldMapping))!;
        Assert.Contains(e.GetIndexes(), i => i.IsUnique
            && i.Properties.Any(p => p.Name == nameof(Domain.Entities.FieldMapping.JiraProjectId))
            && i.Properties.Any(p => p.Name == nameof(Domain.Entities.FieldMapping.Role)));
    }

    [Fact]
    public void Enums_are_stored_as_strings()
    {
        using var ctx = Ctx();
        var fm = ctx.Model.FindEntityType(typeof(Domain.Entities.FieldMapping))!;
        Assert.Equal(typeof(string), fm.FindProperty(nameof(Domain.Entities.FieldMapping.Role))!.GetProviderClrType());
        var ss = ctx.Model.FindEntityType(typeof(Domain.Entities.StatusSemantic))!;
        Assert.Equal(typeof(string), ss.FindProperty(nameof(Domain.Entities.StatusSemantic.Class))!.GetProviderClrType());
    }
}
```

- [ ] **Step 2: Run, verify failure**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~SemanticLayerModelTests`
Expected: FAIL (entities not defined).

- [ ] **Step 3: Add enums + entities**

```csharp
// src/JiraUnpacked.Domain/Enums/FieldRole.cs
namespace JiraUnpacked.Domain.Enums;
public enum FieldRole { Team, Assignee, Sprint, StoryPoints, Status }
```
```csharp
// src/JiraUnpacked.Domain/Enums/StatusClass.cs
namespace JiraUnpacked.Domain.Enums;
public enum StatusClass { Active, Done, Other }
```
```csharp
// src/JiraUnpacked.Domain/Entities/JiraProject.cs
using JiraUnpacked.Domain.Abstractions;
namespace JiraUnpacked.Domain.Entities;

public sealed class JiraProject : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid EndUserId { get; set; }
    public Guid JiraConnectionId { get; set; }
    public string JiraProjectKey { get; set; } = default!;
    public string JiraProjectId { get; set; } = default!; // Jira's stable id
    public int SyncIntervalMinutes { get; set; }
    public DateTime CreatedAt { get; set; }
}
```
```csharp
// src/JiraUnpacked.Domain/Entities/JiraQuery.cs
using JiraUnpacked.Domain.Abstractions;
namespace JiraUnpacked.Domain.Entities;

public sealed class JiraQuery : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid EndUserId { get; set; }
    public Guid JiraProjectId { get; set; }
    public string Jql { get; set; } = default!;
    public DateTime CreatedAt { get; set; }
}
```
```csharp
// src/JiraUnpacked.Domain/Entities/FieldMapping.cs
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Enums;
namespace JiraUnpacked.Domain.Entities;

public sealed class FieldMapping : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid EndUserId { get; set; }
    public Guid JiraProjectId { get; set; }
    public FieldRole Role { get; set; }
    public string JiraFieldId { get; set; } = default!;
}
```
```csharp
// src/JiraUnpacked.Domain/Entities/StatusSemantic.cs
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Enums;
namespace JiraUnpacked.Domain.Entities;

public sealed class StatusSemantic : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid EndUserId { get; set; }
    public Guid JiraProjectId { get; set; }
    public string JiraStatusId { get; set; } = default!;
    public string StatusName { get; set; } = default!;
    public StatusClass Class { get; set; }
}
```

- [ ] **Step 4: Add EF configurations**

```csharp
// src/JiraUnpacked.Infrastructure/Persistence/Configurations/JiraProjectConfiguration.cs
using JiraUnpacked.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace JiraUnpacked.Infrastructure.Persistence.Configurations;

public class JiraProjectConfiguration : IEntityTypeConfiguration<JiraProject>
{
    public void Configure(EntityTypeBuilder<JiraProject> b)
    {
        b.ToTable("JiraProjects");
        b.HasKey(x => x.Id);
        b.Property(x => x.JiraProjectKey).IsRequired().HasMaxLength(256);
        b.Property(x => x.JiraProjectId).IsRequired().HasMaxLength(64);
        b.Property(x => x.SyncIntervalMinutes).IsRequired();
        b.Property(x => x.CreatedAt).IsRequired();
        b.HasIndex(x => x.EndUserId);
        b.HasIndex(x => new { x.JiraConnectionId, x.JiraProjectKey }).IsUnique();
        b.HasOne<JiraConnection>().WithMany().HasForeignKey(x => x.JiraConnectionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
```
```csharp
// src/JiraUnpacked.Infrastructure/Persistence/Configurations/JiraQueryConfiguration.cs
using JiraUnpacked.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace JiraUnpacked.Infrastructure.Persistence.Configurations;

public class JiraQueryConfiguration : IEntityTypeConfiguration<JiraQuery>
{
    public void Configure(EntityTypeBuilder<JiraQuery> b)
    {
        b.ToTable("JiraQueries");
        b.HasKey(x => x.Id);
        b.Property(x => x.Jql).IsRequired().HasMaxLength(4096);
        b.Property(x => x.CreatedAt).IsRequired();
        b.HasIndex(x => x.EndUserId);
        b.HasIndex(x => x.JiraProjectId);
        b.HasOne<JiraProject>().WithMany().HasForeignKey(x => x.JiraProjectId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
```
```csharp
// src/JiraUnpacked.Infrastructure/Persistence/Configurations/FieldMappingConfiguration.cs
using JiraUnpacked.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace JiraUnpacked.Infrastructure.Persistence.Configurations;

public class FieldMappingConfiguration : IEntityTypeConfiguration<FieldMapping>
{
    public void Configure(EntityTypeBuilder<FieldMapping> b)
    {
        b.ToTable("FieldMappings");
        b.HasKey(x => x.Id);
        b.Property(x => x.Role).IsRequired().HasConversion<string>().HasMaxLength(32);
        b.Property(x => x.JiraFieldId).IsRequired().HasMaxLength(128);
        b.HasIndex(x => x.EndUserId);
        b.HasIndex(x => new { x.JiraProjectId, x.Role }).IsUnique();
        b.HasOne<JiraProject>().WithMany().HasForeignKey(x => x.JiraProjectId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
```
```csharp
// src/JiraUnpacked.Infrastructure/Persistence/Configurations/StatusSemanticConfiguration.cs
using JiraUnpacked.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace JiraUnpacked.Infrastructure.Persistence.Configurations;

public class StatusSemanticConfiguration : IEntityTypeConfiguration<StatusSemantic>
{
    public void Configure(EntityTypeBuilder<StatusSemantic> b)
    {
        b.ToTable("StatusSemantics");
        b.HasKey(x => x.Id);
        b.Property(x => x.JiraStatusId).IsRequired().HasMaxLength(64);
        b.Property(x => x.StatusName).IsRequired().HasMaxLength(256);
        b.Property(x => x.Class).IsRequired().HasConversion<string>().HasMaxLength(16);
        b.HasIndex(x => x.EndUserId);
        b.HasIndex(x => new { x.JiraProjectId, x.JiraStatusId }).IsUnique();
        b.HasOne<JiraProject>().WithMany().HasForeignKey(x => x.JiraProjectId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
```

- [ ] **Step 5: DbSets + query filters in `AppDbContext`**

Add DbSets:
```csharp
public DbSet<JiraProject> JiraProjects => Set<JiraProject>();
public DbSet<JiraQuery> JiraQueries => Set<JiraQuery>();
public DbSet<FieldMapping> FieldMappings => Set<FieldMapping>();
public DbSet<StatusSemantic> StatusSemantics => Set<StatusSemantic>();
```
In `OnModelCreating`, after the existing `JiraConnection` filter:
```csharp
modelBuilder.Entity<JiraProject>().HasQueryFilter(x => x.EndUserId == _currentTenantId);
modelBuilder.Entity<JiraQuery>().HasQueryFilter(x => x.EndUserId == _currentTenantId);
modelBuilder.Entity<FieldMapping>().HasQueryFilter(x => x.EndUserId == _currentTenantId);
modelBuilder.Entity<StatusSemantic>().HasQueryFilter(x => x.EndUserId == _currentTenantId);
```

- [ ] **Step 6: Generate the migration**

Run:
```
dotnet ef migrations add AddSemanticLayerTables \
  --project src/JiraUnpacked.Infrastructure --startup-project src/JiraUnpacked.Infrastructure
```
Expected: a new `*_AddSemanticLayerTables.cs` creating the four tables with the unique indexes. Inspect it to confirm `FieldMappings.Role` and `StatusSemantics.Class` are `text` columns (string conversion) and the unique indexes exist.

- [ ] **Step 7: Run tests (incl. drift check) + commit**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~SemanticLayerModelTests` → PASS (3).
Run: `dotnet ef migrations has-pending-model-changes --project src/JiraUnpacked.Infrastructure --startup-project src/JiraUnpacked.Infrastructure` → "No changes".

```bash
git add src/JiraUnpacked.Domain/Enums/ src/JiraUnpacked.Domain/Entities/ \
        src/JiraUnpacked.Infrastructure/Persistence/Configurations/ \
        src/JiraUnpacked.Infrastructure/Persistence/AppDbContext.cs \
        src/JiraUnpacked.Infrastructure/Persistence/Migrations/ \
        tests/JiraUnpacked.Tests/Persistence/SemanticLayerModelTests.cs
git commit -m "feat: semantic-layer entities + migration (Module C, FR-C2..C5)"
```

---

## Task 7 (CSL7): RLS for the new tables + gate-test extension  *(critical — opus review)*

**Files:**
- Create (generated, then hand-edited): `src/JiraUnpacked.Infrastructure/Persistence/Migrations/<ts>_AddSemanticLayerRls.cs`
- Modify: `tests/JiraUnpacked.Tests/Tenancy/TenantIsolationGateTests.cs`

**Interfaces:**
- Consumes: the four tables from CSL6; the `jiraunpacked_app` role + `tenant_isolation` policy convention from `AddTenantRls`.
- Produces: RLS (ENABLE + FORCE + policy + GRANT) on `JiraProjects`, `JiraQueries`, `FieldMappings`, `StatusSemantics`; the gate test now also proves no cross-tenant read on `JiraProjects`.

- [ ] **Step 1: Generate an empty migration**

Run:
```
dotnet ef migrations add AddSemanticLayerRls \
  --project src/JiraUnpacked.Infrastructure --startup-project src/JiraUnpacked.Infrastructure
```

- [ ] **Step 2: Replace Up/Down with raw SQL (mirror `AddTenantRls`)**

Replace the generated `Up`/`Down` bodies:

```csharp
protected override void Up(MigrationBuilder migrationBuilder)
{
    migrationBuilder.Sql(@"
GRANT SELECT, INSERT, UPDATE, DELETE ON ""JiraProjects""   TO jiraunpacked_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ""JiraQueries""    TO jiraunpacked_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ""FieldMappings""  TO jiraunpacked_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ""StatusSemantics"" TO jiraunpacked_app;
");
    foreach (var table in new[] { "JiraProjects", "JiraQueries", "FieldMappings", "StatusSemantics" })
    {
        migrationBuilder.Sql($@"
ALTER TABLE ""{table}"" ENABLE ROW LEVEL SECURITY;
ALTER TABLE ""{table}"" FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON ""{table}""
    USING (""EndUserId"" = NULLIF(current_setting('app.current_tenant', true), '')::uuid)
    WITH CHECK (""EndUserId"" = NULLIF(current_setting('app.current_tenant', true), '')::uuid);
");
    }
}

protected override void Down(MigrationBuilder migrationBuilder)
{
    foreach (var table in new[] { "JiraProjects", "JiraQueries", "FieldMappings", "StatusSemantics" })
    {
        migrationBuilder.Sql($@"
DROP POLICY IF EXISTS tenant_isolation ON ""{table}"";
ALTER TABLE ""{table}"" NO FORCE ROW LEVEL SECURITY;
ALTER TABLE ""{table}"" DISABLE ROW LEVEL SECURITY;
");
    }
}
```

- [ ] **Step 3: Extend the gate test to cover a new table**

Append a second `[Fact]` to `TenantIsolationGateTests` proving `JiraProjects` is RLS-isolated at the DB. It seeds one project per tenant directly via the privileged connection (the gate proves the *read* path is closed for the app role), then checks counts under each tenant GUC as `jiraunpacked_app`:

```csharp
[Fact]
public async Task Tenant_cannot_read_another_tenants_projects_via_rls()
{
    // Two tenants with one project row each (seed via privileged conn; capture ids).
    var (tenantA, projA) = await SeedProjectAsync();
    var (tenantB, projB) = await SeedProjectAsync();

    await using var conn = new Npgsql.NpgsqlConnection(JiraUnpackedApiFactory.TestAppConnectionString);
    await conn.OpenAsync();
    await SetTenantAsync(conn, tenantB);

    Assert.Equal(0L, await CountProjectById(conn, projA)); // A hidden from B
    Assert.Equal(1L, await CountProjectById(conn, projB)); // B sees its own
}

// Inserts a JiraProject for a fresh tenant via the privileged (RLS-bypassing) connection.
private async Task<(Guid tenant, Guid project)> SeedProjectAsync()
{
    var tenant = Guid.NewGuid();
    var project = Guid.NewGuid();
    var connectionId = Guid.NewGuid();
    await using var conn = new Npgsql.NpgsqlConnection(JiraUnpackedApiFactory.TestConnectionString);
    await conn.OpenAsync();
    await using (var c1 = conn.CreateCommand())
    {
        c1.CommandText = @"INSERT INTO ""JiraConnections""
            (""Id"",""EndUserId"",""BaseUrl"",""Email"",""ApiTokenEncrypted"",""CreatedAt"")
            VALUES (@id,@t,'https://x','e','c',now())";
        c1.Parameters.AddWithValue("id", connectionId);
        c1.Parameters.AddWithValue("t", tenant);
        await c1.ExecuteNonQueryAsync();
    }
    await using (var c2 = conn.CreateCommand())
    {
        c2.CommandText = @"INSERT INTO ""JiraProjects""
            (""Id"",""EndUserId"",""JiraConnectionId"",""JiraProjectKey"",""JiraProjectId"",""SyncIntervalMinutes"",""CreatedAt"")
            VALUES (@id,@t,@conn,'ACME','10001',60,now())";
        c2.Parameters.AddWithValue("id", project);
        c2.Parameters.AddWithValue("t", tenant);
        c2.Parameters.AddWithValue("conn", connectionId);
        await c2.ExecuteNonQueryAsync();
    }
    return (tenant, project);
}

private static async Task<long> CountProjectById(Npgsql.NpgsqlConnection conn, Guid id)
{
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"SELECT count(*) FROM ""JiraProjects"" WHERE ""Id"" = @id";
    cmd.Parameters.AddWithValue("id", id);
    return (long)(await cmd.ExecuteScalarAsync())!;
}
```

(Reuse the existing `SetTenantAsync` helper already in the file. Ensure `using Npgsql;` is present or use the fully-qualified name as shown.)

- [ ] **Step 4: Run the gate + a quick suite, then commit**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~TenantIsolationGateTests` → PASS (2).
Run: `dotnet ef migrations has-pending-model-changes --project src/JiraUnpacked.Infrastructure --startup-project src/JiraUnpacked.Infrastructure` → "No changes".

```bash
git add src/JiraUnpacked.Infrastructure/Persistence/Migrations/ \
        tests/JiraUnpacked.Tests/Tenancy/TenantIsolationGateTests.cs
git commit -m "feat: RLS on semantic-layer tables + gate covers JiraProjects (Module C, FR-B2)"
```

---

## Task 8 (CSL8): Project endpoints (FR-C2 / FR-C4)

**Files:**
- Create: `src/JiraUnpacked.Domain/Abstractions/IJiraProjectService.cs`
- Create: `src/JiraUnpacked.Infrastructure/Services/JiraProjectService.cs`
- Create: `src/JiraUnpacked.Api/Contracts/Projects/*.cs` (requests/responses)
- Create: `src/JiraUnpacked.Api/Controllers/ProjectsController.cs`
- Modify: `src/JiraUnpacked.Infrastructure/DependencyInjection.cs`
- Test: `tests/JiraUnpacked.Tests/Integration/ProjectEndpointsTests.cs`

**Interfaces:**
- Consumes: `IJiraApiClient.GetProjectAsync` (CSL3), `ITokenProtector` (CSL1), `AppDbContext.JiraProjects/JiraConnections`.
- Produces:
  - `enum AddProjectStatus { Created, ConnectionNotFound, UnknownProjectKey, Duplicate, InvalidInterval, JiraUnreachable }`
  - `record AddProjectResult(AddProjectStatus Status, ProjectView? View)`
  - `record ProjectView(Guid Id, Guid JiraConnectionId, string JiraProjectKey, string JiraProjectId, int SyncIntervalMinutes, DateTime CreatedAtUtc)`
  - `IJiraProjectService { AddAsync(Guid connectionId, string projectKey, int syncIntervalMinutes, CancellationToken); ListAsync(Guid connectionId, CancellationToken); GetAsync(Guid projectId, CancellationToken); UpdateIntervalAsync(Guid projectId, int minutes, CancellationToken); }`

- [ ] **Step 1: Write the failing integration tests**

```csharp
// tests/JiraUnpacked.Tests/Integration/ProjectEndpointsTests.cs
using System.Net;
using System.Net.Http.Json;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace JiraUnpacked.Tests.Integration;

public sealed class ProjectEndpointsTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime, IDisposable
{
    private readonly JiraUnpackedApiFactory _factory;
    private readonly WireMockServer _jira = WireMockServer.Start();
    public ProjectEndpointsTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;
    public void Dispose() => _jira.Stop();

    private sealed record ConnectionDto(Guid Id, string BaseUrl, string Email, DateTime CreatedAtUtc);
    private sealed record ProjectDto(Guid Id, Guid JiraConnectionId, string JiraProjectKey, string JiraProjectId, int SyncIntervalMinutes, DateTime CreatedAtUtc);

    private void StubValid() => _jira.Given(Request.Create().WithPath("/rest/api/3/myself").UsingGet())
        .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { accountId = "a" }));

    private async Task<(HttpClient client, Guid connId)> ConnectedAsync()
    {
        StubValid();
        var client = await TestData.AuthedClientAsync(_factory);
        var conn = (await (await client.PostAsJsonAsync("/api/connections",
            new { baseUrl = _jira.Url, email = "ops@acme.com", apiToken = "t" }))
            .Content.ReadFromJsonAsync<ConnectionDto>())!;
        return (client, conn.Id);
    }

    [Fact]
    public async Task Add_project_validates_the_key_and_captures_the_stable_id()
    {
        var (client, connId) = await ConnectedAsync();
        _jira.Given(Request.Create().WithPath("/rest/api/3/project/ACME").UsingGet())
             .RespondWith(Response.Create().WithStatusCode(200)
                 .WithBodyAsJson(new { id = "10001", key = "ACME", name = "Acme" }));

        var resp = await client.PostAsJsonAsync($"/api/connections/{connId}/projects",
            new { projectKey = "ACME", syncIntervalMinutes = 60 });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var p = (await resp.Content.ReadFromJsonAsync<ProjectDto>())!;
        Assert.Equal("10001", p.JiraProjectId);
        Assert.Equal(60, p.SyncIntervalMinutes);
    }

    [Fact]
    public async Task Unknown_project_key_is_rejected()
    {
        var (client, connId) = await ConnectedAsync();
        _jira.Given(Request.Create().WithPath("/rest/api/3/project/NOPE").UsingGet())
             .RespondWith(Response.Create().WithStatusCode(404));
        var resp = await client.PostAsJsonAsync($"/api/connections/{connId}/projects",
            new { projectKey = "NOPE", syncIntervalMinutes = 60 });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Interval_below_60_is_rejected()
    {
        var (client, connId) = await ConnectedAsync();
        var resp = await client.PostAsJsonAsync($"/api/connections/{connId}/projects",
            new { projectKey = "ACME", syncIntervalMinutes = 30 });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Duplicate_project_key_returns_409()
    {
        var (client, connId) = await ConnectedAsync();
        _jira.Given(Request.Create().WithPath("/rest/api/3/project/ACME").UsingGet())
             .RespondWith(Response.Create().WithStatusCode(200)
                 .WithBodyAsJson(new { id = "10001", key = "ACME", name = "Acme" }));
        var body = new { projectKey = "ACME", syncIntervalMinutes = 60 };
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync($"/api/connections/{connId}/projects", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/connections/{connId}/projects", body)).StatusCode);
    }
}
```

- [ ] **Step 2: Run, verify failure**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~ProjectEndpointsTests`
Expected: FAIL (no endpoints).

- [ ] **Step 3: Service interface + result types**

```csharp
// src/JiraUnpacked.Domain/Abstractions/IJiraProjectService.cs
namespace JiraUnpacked.Domain.Abstractions;

public enum AddProjectStatus { Created, ConnectionNotFound, UnknownProjectKey, Duplicate, InvalidInterval, JiraUnreachable }
public sealed record ProjectView(Guid Id, Guid JiraConnectionId, string JiraProjectKey, string JiraProjectId, int SyncIntervalMinutes, DateTime CreatedAtUtc);
public sealed record AddProjectResult(AddProjectStatus Status, ProjectView? View);

public interface IJiraProjectService
{
    Task<AddProjectResult> AddAsync(Guid connectionId, string projectKey, int syncIntervalMinutes, CancellationToken ct = default);
    Task<IReadOnlyList<ProjectView>> ListAsync(Guid connectionId, CancellationToken ct = default);
    Task<ProjectView?> GetAsync(Guid projectId, CancellationToken ct = default);
    Task<bool?> UpdateIntervalAsync(Guid projectId, int syncIntervalMinutes, CancellationToken ct = default); // null = not found, false = invalid interval, true = updated
}
```

- [ ] **Step 4: Implement the service**

```csharp
// src/JiraUnpacked.Infrastructure/Services/JiraProjectService.cs
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Abstractions.Jira;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace JiraUnpacked.Infrastructure.Services;

public sealed class JiraProjectService : IJiraProjectService
{
    private const int MinIntervalMinutes = 60;
    private readonly AppDbContext _db;
    private readonly IJiraApiClient _jira;
    private readonly ITokenProtector _protector;
    private readonly IClock _clock;

    public JiraProjectService(AppDbContext db, IJiraApiClient jira, ITokenProtector protector, IClock clock)
    { _db = db; _jira = jira; _protector = protector; _clock = clock; }

    public async Task<AddProjectResult> AddAsync(Guid connectionId, string projectKey, int syncIntervalMinutes, CancellationToken ct = default)
    {
        if (syncIntervalMinutes < MinIntervalMinutes)
            return new AddProjectResult(AddProjectStatus.InvalidInterval, null);

        var conn = await _db.JiraConnections.FirstOrDefaultAsync(c => c.Id == connectionId, ct);
        if (conn is null) return new AddProjectResult(AddProjectStatus.ConnectionNotFound, null);

        var creds = new JiraCredentials(conn.BaseUrl, conn.Email, _protector.Unprotect(conn.ApiTokenEncrypted));
        var jiraProject = await _jira.GetProjectAsync(creds, projectKey, ct);
        if (jiraProject is null) return new AddProjectResult(AddProjectStatus.UnknownProjectKey, null);

        var entity = new JiraProject
        {
            Id = Guid.NewGuid(),
            JiraConnectionId = connectionId,
            JiraProjectKey = jiraProject.Key,
            JiraProjectId = jiraProject.Id,
            SyncIntervalMinutes = syncIntervalMinutes,
            CreatedAt = _clock.UtcNow
        };
        _db.JiraProjects.Add(entity);
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        { return new AddProjectResult(AddProjectStatus.Duplicate, null); }

        return new AddProjectResult(AddProjectStatus.Created, ToView(entity));
    }

    public async Task<IReadOnlyList<ProjectView>> ListAsync(Guid connectionId, CancellationToken ct = default) =>
        await _db.JiraProjects.Where(p => p.JiraConnectionId == connectionId)
            .OrderBy(p => p.CreatedAt)
            .Select(p => new ProjectView(p.Id, p.JiraConnectionId, p.JiraProjectKey, p.JiraProjectId, p.SyncIntervalMinutes, p.CreatedAt))
            .ToListAsync(ct);

    public async Task<ProjectView?> GetAsync(Guid projectId, CancellationToken ct = default)
    {
        var p = await _db.JiraProjects.FirstOrDefaultAsync(x => x.Id == projectId, ct);
        return p is null ? null : ToView(p);
    }

    public async Task<bool?> UpdateIntervalAsync(Guid projectId, int syncIntervalMinutes, CancellationToken ct = default)
    {
        if (syncIntervalMinutes < MinIntervalMinutes) return false;
        var p = await _db.JiraProjects.FirstOrDefaultAsync(x => x.Id == projectId, ct);
        if (p is null) return null;
        p.SyncIntervalMinutes = syncIntervalMinutes;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    private static ProjectView ToView(JiraProject p) =>
        new(p.Id, p.JiraConnectionId, p.JiraProjectKey, p.JiraProjectId, p.SyncIntervalMinutes, p.CreatedAt);
}
```

- [ ] **Step 5: DTOs + controller + DI**

```csharp
// src/JiraUnpacked.Api/Contracts/Projects/AddProjectRequest.cs
namespace JiraUnpacked.Api.Contracts.Projects;
public sealed record AddProjectRequest(string ProjectKey, int SyncIntervalMinutes);
```
```csharp
// src/JiraUnpacked.Api/Contracts/Projects/UpdateProjectRequest.cs
namespace JiraUnpacked.Api.Contracts.Projects;
public sealed record UpdateProjectRequest(int SyncIntervalMinutes);
```
```csharp
// src/JiraUnpacked.Api/Controllers/ProjectsController.cs
using JiraUnpacked.Api.Contracts.Projects;
using JiraUnpacked.Domain.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JiraUnpacked.Api.Controllers;

[ApiController]
[Authorize]
public sealed class ProjectsController : ControllerBase
{
    private readonly IJiraProjectService _projects;
    public ProjectsController(IJiraProjectService projects) => _projects = projects;

    [HttpPost("api/connections/{connectionId:guid}/projects")]
    public async Task<IActionResult> Add(Guid connectionId, AddProjectRequest request, CancellationToken ct)
    {
        var result = await _projects.AddAsync(connectionId, request.ProjectKey, request.SyncIntervalMinutes, ct);
        return result.Status switch
        {
            AddProjectStatus.Created => CreatedAtAction(nameof(Get), new { projectId = result.View!.Id }, result.View),
            AddProjectStatus.ConnectionNotFound => NotFound(),
            AddProjectStatus.UnknownProjectKey => Problem(statusCode: 400, title: "Unknown Jira project key."),
            AddProjectStatus.InvalidInterval => Problem(statusCode: 400, title: "Sync interval must be at least 60 minutes."),
            AddProjectStatus.Duplicate => Conflict(),
            _ => Problem(statusCode: 502, title: "Jira could not be reached.")
        };
    }

    [HttpGet("api/connections/{connectionId:guid}/projects")]
    public async Task<IActionResult> List(Guid connectionId, CancellationToken ct) =>
        Ok(await _projects.ListAsync(connectionId, ct));

    [HttpGet("api/projects/{projectId:guid}")]
    public async Task<IActionResult> Get(Guid projectId, CancellationToken ct)
    {
        var view = await _projects.GetAsync(projectId, ct);
        return view is null ? NotFound() : Ok(view);
    }

    [HttpPatch("api/projects/{projectId:guid}")]
    public async Task<IActionResult> Update(Guid projectId, UpdateProjectRequest request, CancellationToken ct)
    {
        var result = await _projects.UpdateIntervalAsync(projectId, request.SyncIntervalMinutes, ct);
        return result switch
        {
            null => NotFound(),
            false => Problem(statusCode: 400, title: "Sync interval must be at least 60 minutes."),
            _ => NoContent()
        };
    }
}
```
Register in `DependencyInjection`: `services.AddScoped<IJiraProjectService, JiraProjectService>();`

- [ ] **Step 6: Run tests + commit**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~ProjectEndpointsTests` → PASS (4).

```bash
git add src/JiraUnpacked.Domain/Abstractions/IJiraProjectService.cs \
        src/JiraUnpacked.Infrastructure/Services/JiraProjectService.cs \
        src/JiraUnpacked.Api/Contracts/Projects/ src/JiraUnpacked.Api/Controllers/ProjectsController.cs \
        src/JiraUnpacked.Infrastructure/DependencyInjection.cs \
        tests/JiraUnpacked.Tests/Integration/ProjectEndpointsTests.cs
git commit -m "feat: project endpoints with key validation + frequency (Module C, FR-C2/C4)"
```

---

## Task 9 (CSL9): Query endpoints (FR-C3)

**Files:**
- Create: `src/JiraUnpacked.Domain/Abstractions/IJiraQueryService.cs`
- Create: `src/JiraUnpacked.Infrastructure/Services/JiraQueryService.cs`
- Create: `src/JiraUnpacked.Api/Contracts/Queries/AddQueryRequest.cs`
- Create: `src/JiraUnpacked.Api/Controllers/QueriesController.cs`
- Modify: `src/JiraUnpacked.Infrastructure/DependencyInjection.cs`
- Test: `tests/JiraUnpacked.Tests/Integration/QueryEndpointsTests.cs`

**Interfaces:**
- Consumes: `IJiraApiClient.ParseJqlAsync` (CSL3), `ITokenProtector`, `AppDbContext.JiraQueries/JiraProjects/JiraConnections`, `IClock`.
- Produces:
  - `enum AddQueryStatus { Created, ProjectNotFound, InvalidJql, JiraUnreachable }`
  - `record QueryView(Guid Id, Guid JiraProjectId, string Jql, DateTime CreatedAtUtc)`
  - `record AddQueryResult(AddQueryStatus Status, QueryView? View, IReadOnlyList<string> Errors)`
  - `IJiraQueryService { AddAsync(Guid projectId, string jql, CancellationToken); ListAsync(Guid projectId, CancellationToken); DeleteAsync(Guid queryId, CancellationToken); }`

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/JiraUnpacked.Tests/Integration/QueryEndpointsTests.cs
using System.Net;
using System.Net.Http.Json;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace JiraUnpacked.Tests.Integration;

public sealed class QueryEndpointsTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime, IDisposable
{
    private readonly JiraUnpackedApiFactory _factory;
    private readonly WireMockServer _jira = WireMockServer.Start();
    public QueryEndpointsTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;
    public void Dispose() => _jira.Stop();

    private sealed record ConnectionDto(Guid Id, string BaseUrl, string Email, DateTime CreatedAtUtc);
    private sealed record ProjectDto(Guid Id, Guid JiraConnectionId, string JiraProjectKey, string JiraProjectId, int SyncIntervalMinutes, DateTime CreatedAtUtc);
    private sealed record QueryDto(Guid Id, Guid JiraProjectId, string Jql, DateTime CreatedAtUtc);

    private async Task<(HttpClient client, Guid projectId)> WithProjectAsync()
    {
        _jira.Given(Request.Create().WithPath("/rest/api/3/myself").UsingGet())
             .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { accountId = "a" }));
        _jira.Given(Request.Create().WithPath("/rest/api/3/project/ACME").UsingGet())
             .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { id = "10001", key = "ACME", name = "Acme" }));
        var client = await TestData.AuthedClientAsync(_factory);
        var conn = (await (await client.PostAsJsonAsync("/api/connections",
            new { baseUrl = _jira.Url, email = "ops@acme.com", apiToken = "t" })).Content.ReadFromJsonAsync<ConnectionDto>())!;
        var project = (await (await client.PostAsJsonAsync($"/api/connections/{conn.Id}/projects",
            new { projectKey = "ACME", syncIntervalMinutes = 60 })).Content.ReadFromJsonAsync<ProjectDto>())!;
        return (client, project.Id);
    }

    [Fact]
    public async Task Valid_jql_is_accepted()
    {
        var (client, projectId) = await WithProjectAsync();
        _jira.Given(Request.Create().WithPath("/rest/api/3/jql/parse").UsingPost())
             .RespondWith(Response.Create().WithStatusCode(200)
                 .WithBodyAsJson(new { queries = new object[] { new { query = "project = ACME", errors = new string[0] } } }));

        var resp = await client.PostAsJsonAsync($"/api/projects/{projectId}/queries", new { jql = "project = ACME" });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var q = (await resp.Content.ReadFromJsonAsync<QueryDto>())!;
        Assert.Equal("project = ACME", q.Jql);
    }

    [Fact]
    public async Task Invalid_jql_is_rejected_400()
    {
        var (client, projectId) = await WithProjectAsync();
        _jira.Given(Request.Create().WithPath("/rest/api/3/jql/parse").UsingPost())
             .RespondWith(Response.Create().WithStatusCode(200)
                 .WithBodyAsJson(new { queries = new object[] { new { query = "bad", errors = new[] { "Unable to parse" } } } }));

        var resp = await client.PostAsJsonAsync($"/api/projects/{projectId}/queries", new { jql = "bad" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Add_to_unknown_project_returns_404()
    {
        var (client, _) = await WithProjectAsync();
        var resp = await client.PostAsJsonAsync($"/api/projects/{Guid.NewGuid()}/queries", new { jql = "x" });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
```

- [ ] **Step 2: Run, verify failure**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~QueryEndpointsTests` → FAIL.

- [ ] **Step 3: Interface + result types**

```csharp
// src/JiraUnpacked.Domain/Abstractions/IJiraQueryService.cs
namespace JiraUnpacked.Domain.Abstractions;

public enum AddQueryStatus { Created, ProjectNotFound, InvalidJql, JiraUnreachable }
public sealed record QueryView(Guid Id, Guid JiraProjectId, string Jql, DateTime CreatedAtUtc);
public sealed record AddQueryResult(AddQueryStatus Status, QueryView? View, IReadOnlyList<string> Errors);

public interface IJiraQueryService
{
    Task<AddQueryResult> AddAsync(Guid projectId, string jql, CancellationToken ct = default);
    Task<IReadOnlyList<QueryView>> ListAsync(Guid projectId, CancellationToken ct = default);
    Task<bool> DeleteAsync(Guid queryId, CancellationToken ct = default);
}
```

- [ ] **Step 4: Implement the service**

```csharp
// src/JiraUnpacked.Infrastructure/Services/JiraQueryService.cs
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Abstractions.Jira;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiraUnpacked.Infrastructure.Services;

public sealed class JiraQueryService : IJiraQueryService
{
    private readonly AppDbContext _db;
    private readonly IJiraApiClient _jira;
    private readonly ITokenProtector _protector;
    private readonly IClock _clock;

    public JiraQueryService(AppDbContext db, IJiraApiClient jira, ITokenProtector protector, IClock clock)
    { _db = db; _jira = jira; _protector = protector; _clock = clock; }

    public async Task<AddQueryResult> AddAsync(Guid projectId, string jql, CancellationToken ct = default)
    {
        var project = await _db.JiraProjects.FirstOrDefaultAsync(p => p.Id == projectId, ct);
        if (project is null) return new AddQueryResult(AddQueryStatus.ProjectNotFound, null, Array.Empty<string>());

        var conn = await _db.JiraConnections.FirstOrDefaultAsync(c => c.Id == project.JiraConnectionId, ct);
        if (conn is null) return new AddQueryResult(AddQueryStatus.ProjectNotFound, null, Array.Empty<string>());

        var creds = new JiraCredentials(conn.BaseUrl, conn.Email, _protector.Unprotect(conn.ApiTokenEncrypted));
        var parse = await _jira.ParseJqlAsync(creds, jql, ct);
        if (!parse.Valid) return new AddQueryResult(AddQueryStatus.InvalidJql, null, parse.Errors);

        var entity = new JiraQuery { Id = Guid.NewGuid(), JiraProjectId = projectId, Jql = jql, CreatedAt = _clock.UtcNow };
        _db.JiraQueries.Add(entity);
        await _db.SaveChangesAsync(ct);
        return new AddQueryResult(AddQueryStatus.Created,
            new QueryView(entity.Id, entity.JiraProjectId, entity.Jql, entity.CreatedAt), Array.Empty<string>());
    }

    public async Task<IReadOnlyList<QueryView>> ListAsync(Guid projectId, CancellationToken ct = default) =>
        await _db.JiraQueries.Where(q => q.JiraProjectId == projectId).OrderBy(q => q.CreatedAt)
            .Select(q => new QueryView(q.Id, q.JiraProjectId, q.Jql, q.CreatedAt)).ToListAsync(ct);

    public async Task<bool> DeleteAsync(Guid queryId, CancellationToken ct = default)
    {
        var q = await _db.JiraQueries.FirstOrDefaultAsync(x => x.Id == queryId, ct);
        if (q is null) return false;
        _db.JiraQueries.Remove(q);
        await _db.SaveChangesAsync(ct);
        return true;
    }
}
```

- [ ] **Step 5: DTO + controller + DI**

```csharp
// src/JiraUnpacked.Api/Contracts/Queries/AddQueryRequest.cs
namespace JiraUnpacked.Api.Contracts.Queries;
public sealed record AddQueryRequest(string Jql);
```
```csharp
// src/JiraUnpacked.Api/Controllers/QueriesController.cs
using JiraUnpacked.Api.Contracts.Queries;
using JiraUnpacked.Domain.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JiraUnpacked.Api.Controllers;

[ApiController]
[Authorize]
public sealed class QueriesController : ControllerBase
{
    private readonly IJiraQueryService _queries;
    public QueriesController(IJiraQueryService queries) => _queries = queries;

    [HttpPost("api/projects/{projectId:guid}/queries")]
    public async Task<IActionResult> Add(Guid projectId, AddQueryRequest request, CancellationToken ct)
    {
        var result = await _queries.AddAsync(projectId, request.Jql, ct);
        return result.Status switch
        {
            AddQueryStatus.Created => CreatedAtAction(nameof(List), new { projectId }, result.View),
            AddQueryStatus.ProjectNotFound => NotFound(),
            AddQueryStatus.InvalidJql => Problem(statusCode: 400, title: "Invalid JQL.", detail: string.Join("; ", result.Errors)),
            _ => Problem(statusCode: 502, title: "Jira could not be reached.")
        };
    }

    [HttpGet("api/projects/{projectId:guid}/queries")]
    public async Task<IActionResult> List(Guid projectId, CancellationToken ct) =>
        Ok(await _queries.ListAsync(projectId, ct));

    [HttpDelete("api/queries/{queryId:guid}")]
    public async Task<IActionResult> Delete(Guid queryId, CancellationToken ct) =>
        await _queries.DeleteAsync(queryId, ct) ? NoContent() : NotFound();
}
```
Register: `services.AddScoped<IJiraQueryService, JiraQueryService>();`

- [ ] **Step 6: Run tests + commit**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~QueryEndpointsTests` → PASS (3).

```bash
git add src/JiraUnpacked.Domain/Abstractions/IJiraQueryService.cs \
        src/JiraUnpacked.Infrastructure/Services/JiraQueryService.cs \
        src/JiraUnpacked.Api/Contracts/Queries/ src/JiraUnpacked.Api/Controllers/QueriesController.cs \
        src/JiraUnpacked.Infrastructure/DependencyInjection.cs \
        tests/JiraUnpacked.Tests/Integration/QueryEndpointsTests.cs
git commit -m "feat: JQL query endpoints with parse validation (Module C, FR-C3)"
```

---

## Task 10 (CSL10): Field/status mapping endpoints (FR-C5)

**Files:**
- Create: `src/JiraUnpacked.Domain/Abstractions/IProjectMappingService.cs`
- Create: `src/JiraUnpacked.Infrastructure/Services/ProjectMappingService.cs`
- Create: `src/JiraUnpacked.Api/Contracts/Mappings/*.cs`
- Create: `src/JiraUnpacked.Api/Controllers/MappingsController.cs`
- Modify: `src/JiraUnpacked.Infrastructure/DependencyInjection.cs`
- Test: `tests/JiraUnpacked.Tests/Integration/MappingEndpointsTests.cs`

**Interfaces:**
- Consumes: `IJiraApiClient.GetStatusesAsync` (CSL3), `ITokenProtector`, `AppDbContext.FieldMappings/StatusSemantics/JiraProjects/JiraConnections`, enums `FieldRole`/`StatusClass`.
- Produces:
  - `record FieldMappingInput(FieldRole Role, string JiraFieldId)`, `record StatusSemanticInput(string JiraStatusId, string StatusName, StatusClass Class)`
  - `IProjectMappingService { GetStatusesAsync(projectId, ct); ReplaceFieldMappingsAsync(projectId, items, ct); GetFieldMappingsAsync(projectId, ct); ReplaceStatusSemanticsAsync(projectId, items, ct); GetStatusSemanticsAsync(projectId, ct); }` (replace returns `bool?`: null = project not found, true = saved)

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/JiraUnpacked.Tests/Integration/MappingEndpointsTests.cs
using System.Net;
using System.Net.Http.Json;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace JiraUnpacked.Tests.Integration;

public sealed class MappingEndpointsTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime, IDisposable
{
    private readonly JiraUnpackedApiFactory _factory;
    private readonly WireMockServer _jira = WireMockServer.Start();
    public MappingEndpointsTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;
    public void Dispose() => _jira.Stop();

    private sealed record ConnectionDto(Guid Id, string BaseUrl, string Email, DateTime CreatedAtUtc);
    private sealed record ProjectDto(Guid Id, Guid JiraConnectionId, string JiraProjectKey, string JiraProjectId, int SyncIntervalMinutes, DateTime CreatedAtUtc);
    private sealed record StatusDto(string Id, string Name);
    private sealed record FieldMappingDto(string Role, string JiraFieldId);

    private async Task<(HttpClient client, Guid projectId)> WithProjectAsync()
    {
        _jira.Given(Request.Create().WithPath("/rest/api/3/myself").UsingGet())
             .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { accountId = "a" }));
        _jira.Given(Request.Create().WithPath("/rest/api/3/project/ACME").UsingGet())
             .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { id = "10001", key = "ACME", name = "Acme" }));
        var client = await TestData.AuthedClientAsync(_factory);
        var conn = (await (await client.PostAsJsonAsync("/api/connections",
            new { baseUrl = _jira.Url, email = "ops@acme.com", apiToken = "t" })).Content.ReadFromJsonAsync<ConnectionDto>())!;
        var project = (await (await client.PostAsJsonAsync($"/api/connections/{conn.Id}/projects",
            new { projectKey = "ACME", syncIntervalMinutes = 60 })).Content.ReadFromJsonAsync<ProjectDto>())!;
        return (client, project.Id);
    }

    [Fact]
    public async Task Discovers_project_statuses()
    {
        var (client, projectId) = await WithProjectAsync();
        _jira.Given(Request.Create().WithPath("/rest/api/3/project/ACME/statuses").UsingGet())
             .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new object[]
             { new { id = "10", name = "Story", statuses = new object[] { new { id = "3", name = "Done" } } } }));

        var statuses = await (await client.GetAsync($"/api/projects/{projectId}/jira-statuses"))
            .Content.ReadFromJsonAsync<List<StatusDto>>();
        Assert.Contains(statuses!, s => s.Id == "3" && s.Name == "Done");
    }

    [Fact]
    public async Task Field_mappings_replace_and_read_back()
    {
        var (client, projectId) = await WithProjectAsync();
        var put = await client.PutAsJsonAsync($"/api/projects/{projectId}/field-mappings", new
        {
            mappings = new[]
            {
                new { role = "Sprint", jiraFieldId = "customfield_10020" },
                new { role = "Status", jiraFieldId = "status" }
            }
        });
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        var read = await (await client.GetAsync($"/api/projects/{projectId}/field-mappings"))
            .Content.ReadFromJsonAsync<List<FieldMappingDto>>();
        Assert.Equal(2, read!.Count);
        Assert.Contains(read, m => m.Role == "Sprint" && m.JiraFieldId == "customfield_10020");

        // Replace semantics: PUT again with one mapping -> set becomes exactly that one.
        await client.PutAsJsonAsync($"/api/projects/{projectId}/field-mappings", new
        { mappings = new[] { new { role = "Assignee", jiraFieldId = "assignee" } } });
        var read2 = await (await client.GetAsync($"/api/projects/{projectId}/field-mappings"))
            .Content.ReadFromJsonAsync<List<FieldMappingDto>>();
        Assert.Single(read2!);
        Assert.Equal("Assignee", read2![0].Role);
    }

    [Fact]
    public async Task Status_semantics_persist()
    {
        var (client, projectId) = await WithProjectAsync();
        var put = await client.PutAsJsonAsync($"/api/projects/{projectId}/status-semantics", new
        {
            statuses = new[]
            {
                new { jiraStatusId = "3", statusName = "Done", @class = "Done" },
                new { jiraStatusId = "1", statusName = "To Do", @class = "Active" }
            }
        });
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        var read = await (await client.GetAsync($"/api/projects/{projectId}/status-semantics")).Content.ReadAsStringAsync();
        Assert.Contains("Done", read);
    }
}
```

- [ ] **Step 2: Run, verify failure**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~MappingEndpointsTests` → FAIL.

- [ ] **Step 3: Interface + inputs**

```csharp
// src/JiraUnpacked.Domain/Abstractions/IProjectMappingService.cs
using JiraUnpacked.Domain.Abstractions.Jira;
using JiraUnpacked.Domain.Enums;

namespace JiraUnpacked.Domain.Abstractions;

public sealed record FieldMappingInput(FieldRole Role, string JiraFieldId);
public sealed record StatusSemanticInput(string JiraStatusId, string StatusName, StatusClass Class);

public interface IProjectMappingService
{
    Task<IReadOnlyList<JiraStatus>?> GetStatusesAsync(Guid projectId, CancellationToken ct = default);
    Task<bool?> ReplaceFieldMappingsAsync(Guid projectId, IReadOnlyList<FieldMappingInput> items, CancellationToken ct = default);
    Task<IReadOnlyList<FieldMappingInput>?> GetFieldMappingsAsync(Guid projectId, CancellationToken ct = default);
    Task<bool?> ReplaceStatusSemanticsAsync(Guid projectId, IReadOnlyList<StatusSemanticInput> items, CancellationToken ct = default);
    Task<IReadOnlyList<StatusSemanticInput>?> GetStatusSemanticsAsync(Guid projectId, CancellationToken ct = default);
}
```

- [ ] **Step 4: Implement the service**

```csharp
// src/JiraUnpacked.Infrastructure/Services/ProjectMappingService.cs
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Abstractions.Jira;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiraUnpacked.Infrastructure.Services;

public sealed class ProjectMappingService : IProjectMappingService
{
    private readonly AppDbContext _db;
    private readonly IJiraApiClient _jira;
    private readonly ITokenProtector _protector;

    public ProjectMappingService(AppDbContext db, IJiraApiClient jira, ITokenProtector protector)
    { _db = db; _jira = jira; _protector = protector; }

    public async Task<IReadOnlyList<JiraStatus>?> GetStatusesAsync(Guid projectId, CancellationToken ct = default)
    {
        var project = await _db.JiraProjects.FirstOrDefaultAsync(p => p.Id == projectId, ct);
        if (project is null) return null;
        var conn = await _db.JiraConnections.FirstOrDefaultAsync(c => c.Id == project.JiraConnectionId, ct);
        if (conn is null) return null;
        var creds = new JiraCredentials(conn.BaseUrl, conn.Email, _protector.Unprotect(conn.ApiTokenEncrypted));
        return await _jira.GetStatusesAsync(creds, project.JiraProjectKey, ct);
    }

    public async Task<bool?> ReplaceFieldMappingsAsync(Guid projectId, IReadOnlyList<FieldMappingInput> items, CancellationToken ct = default)
    {
        var project = await _db.JiraProjects.FirstOrDefaultAsync(p => p.Id == projectId, ct);
        if (project is null) return null;

        var existing = await _db.FieldMappings.Where(m => m.JiraProjectId == projectId).ToListAsync(ct);
        _db.FieldMappings.RemoveRange(existing);
        foreach (var i in items)
            _db.FieldMappings.Add(new FieldMapping { Id = Guid.NewGuid(), JiraProjectId = projectId, Role = i.Role, JiraFieldId = i.JiraFieldId });
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<FieldMappingInput>?> GetFieldMappingsAsync(Guid projectId, CancellationToken ct = default)
    {
        if (!await _db.JiraProjects.AnyAsync(p => p.Id == projectId, ct)) return null;
        return await _db.FieldMappings.Where(m => m.JiraProjectId == projectId)
            .Select(m => new FieldMappingInput(m.Role, m.JiraFieldId)).ToListAsync(ct);
    }

    public async Task<bool?> ReplaceStatusSemanticsAsync(Guid projectId, IReadOnlyList<StatusSemanticInput> items, CancellationToken ct = default)
    {
        var project = await _db.JiraProjects.FirstOrDefaultAsync(p => p.Id == projectId, ct);
        if (project is null) return null;

        var existing = await _db.StatusSemantics.Where(s => s.JiraProjectId == projectId).ToListAsync(ct);
        _db.StatusSemantics.RemoveRange(existing);
        foreach (var i in items)
            _db.StatusSemantics.Add(new StatusSemantic { Id = Guid.NewGuid(), JiraProjectId = projectId,
                JiraStatusId = i.JiraStatusId, StatusName = i.StatusName, Class = i.Class });
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<StatusSemanticInput>?> GetStatusSemanticsAsync(Guid projectId, CancellationToken ct = default)
    {
        if (!await _db.JiraProjects.AnyAsync(p => p.Id == projectId, ct)) return null;
        return await _db.StatusSemantics.Where(s => s.JiraProjectId == projectId)
            .Select(s => new StatusSemanticInput(s.JiraStatusId, s.StatusName, s.Class)).ToListAsync(ct);
    }
}
```

Note: the field-mapping replace deletes-then-inserts within one `SaveChangesAsync` (single transaction), so the unique `(JiraProjectId, Role)` index is never violated mid-operation.

- [ ] **Step 5: DTOs + controller + DI**

```csharp
// src/JiraUnpacked.Api/Contracts/Mappings/FieldMappingsRequest.cs
using JiraUnpacked.Domain.Enums;
namespace JiraUnpacked.Api.Contracts.Mappings;

public sealed record FieldMappingItem(FieldRole Role, string JiraFieldId);
public sealed record FieldMappingsRequest(IReadOnlyList<FieldMappingItem> Mappings);
```
```csharp
// src/JiraUnpacked.Api/Contracts/Mappings/StatusSemanticsRequest.cs
using JiraUnpacked.Domain.Enums;
namespace JiraUnpacked.Api.Contracts.Mappings;

public sealed record StatusSemanticItem(string JiraStatusId, string StatusName, StatusClass Class);
public sealed record StatusSemanticsRequest(IReadOnlyList<StatusSemanticItem> Statuses);
```
```csharp
// src/JiraUnpacked.Api/Controllers/MappingsController.cs
using JiraUnpacked.Api.Contracts.Mappings;
using JiraUnpacked.Domain.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JiraUnpacked.Api.Controllers;

[ApiController]
[Authorize]
public sealed class MappingsController : ControllerBase
{
    private readonly IProjectMappingService _mappings;
    public MappingsController(IProjectMappingService mappings) => _mappings = mappings;

    [HttpGet("api/projects/{projectId:guid}/jira-statuses")]
    public async Task<IActionResult> Statuses(Guid projectId, CancellationToken ct)
    {
        var statuses = await _mappings.GetStatusesAsync(projectId, ct);
        return statuses is null ? NotFound() : Ok(statuses);
    }

    [HttpPut("api/projects/{projectId:guid}/field-mappings")]
    public async Task<IActionResult> PutFieldMappings(Guid projectId, FieldMappingsRequest request, CancellationToken ct)
    {
        var items = request.Mappings.Select(m => new FieldMappingInput(m.Role, m.JiraFieldId)).ToList();
        var result = await _mappings.ReplaceFieldMappingsAsync(projectId, items, ct);
        return result is null ? NotFound() : NoContent();
    }

    [HttpGet("api/projects/{projectId:guid}/field-mappings")]
    public async Task<IActionResult> GetFieldMappings(Guid projectId, CancellationToken ct)
    {
        var items = await _mappings.GetFieldMappingsAsync(projectId, ct);
        return items is null ? NotFound() : Ok(items);
    }

    [HttpPut("api/projects/{projectId:guid}/status-semantics")]
    public async Task<IActionResult> PutStatusSemantics(Guid projectId, StatusSemanticsRequest request, CancellationToken ct)
    {
        var items = request.Statuses.Select(s => new StatusSemanticInput(s.JiraStatusId, s.StatusName, s.Class)).ToList();
        var result = await _mappings.ReplaceStatusSemanticsAsync(projectId, items, ct);
        return result is null ? NotFound() : NoContent();
    }

    [HttpGet("api/projects/{projectId:guid}/status-semantics")]
    public async Task<IActionResult> GetStatusSemantics(Guid projectId, CancellationToken ct)
    {
        var items = await _mappings.GetStatusSemanticsAsync(projectId, ct);
        return items is null ? NotFound() : Ok(items);
    }
}
```
Register: `services.AddScoped<IProjectMappingService, ProjectMappingService>();`

- [ ] **Step 6: Run the full suite + drift check, then commit**

Run: `dotnet test JiraUnpacked.sln` → all green (existing + new).
Run: `dotnet ef migrations has-pending-model-changes --project src/JiraUnpacked.Infrastructure --startup-project src/JiraUnpacked.Infrastructure` → "No changes".

```bash
git add src/JiraUnpacked.Domain/Abstractions/IProjectMappingService.cs \
        src/JiraUnpacked.Infrastructure/Services/ProjectMappingService.cs \
        src/JiraUnpacked.Api/Contracts/Mappings/ src/JiraUnpacked.Api/Controllers/MappingsController.cs \
        src/JiraUnpacked.Infrastructure/DependencyInjection.cs \
        tests/JiraUnpacked.Tests/Integration/MappingEndpointsTests.cs
git commit -m "feat: field/status mapping endpoints (Module C, FR-C5)"
```

---

## Final verification (controller, after all tasks)

- [ ] `dotnet build JiraUnpacked.sln -c Release` → 0 warnings / 0 errors.
- [ ] `dotnet test JiraUnpacked.sln -c Release` → all green.
- [ ] `dotnet ef migrations has-pending-model-changes --project src/JiraUnpacked.Infrastructure --startup-project src/JiraUnpacked.Infrastructure` → "No changes".
- [ ] Confirm the **`TokenProtection:Key`** dev value is present in `appsettings.Development.json` and that **CI provides the key** (add the same key to the CI workflow env / `appsettings` used by CI; the integration tests run in the Development environment and need it). If CI uses environment variables, set `TokenProtection__Key`.
- [ ] Push `feat/semantic-layer`; verify `origin == local HEAD`; open PR → `develop`; confirm CI (Build & Test) green; squash-merge; verify `develop` has the code; close the Module C issues.
- [ ] Cut the `develop → main` release PR per the established cadence; CI; merge-commit.

## Notes for execution
- **CI token key:** the integration tests now require `TokenProtection:Key`. Before pushing, ensure the CI workflow sets it (env var `TokenProtection__Key` or a CI `appsettings`), or the WireMock-backed integration tests will fail `ValidateOnStart`. Surface this in the PR description.
- **Each new tenant table must keep RLS** — never add a tenant-owned entity without extending both the query filter (CSL6 pattern) and an RLS migration (CSL7 pattern). The gate test is the backstop.
- **Token discipline:** never add logging that serializes a `JiraConnection`, `JiraCredentials`, or the decrypted token.
