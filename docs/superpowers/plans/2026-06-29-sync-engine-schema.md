# Module D Spec 1 — Sync-engine storage schema Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Create the full Module D storage schema — 10 tenant-owned tables (raw landing, dictionaries, modeled Issues, change history, sync runs) with both isolation layers — and no behavior.

**Architecture:** New EF Core entities implementing `ITenantOwned`, each with an `AppDbContext` global query filter (Layer 1) and Postgres RLS (Layer 2). One schema migration creates the tables; one RLS migration enables/forces RLS + policy + grant on each, mirroring the proven `AddTenantRls`/`AddSemanticLayerRls`. The FR-B2 gate test is extended to prove RLS on `Issues`.

**Tech Stack:** ASP.NET Core 8, EF Core 8 + Npgsql (pinned 8.0.11), Postgres (jsonb, RLS), xUnit.

## Global Constraints

- Target **net8.0**; EF Core + Npgsql pinned **8.0.11**.
- Every tenant-owned entity implements `JiraUnpacked.Domain.Abstractions.ITenantOwned` (`Guid EndUserId`), gets `HasQueryFilter(x => x.EndUserId == _currentTenantId)` in `AppDbContext`, and gets Postgres RLS (ENABLE + FORCE + `tenant_isolation` policy + GRANT to `jiraunpacked_app`).
- Enums persisted **as strings** (`.HasConversion<string>()`).
- EF commands run with **both** `--project` and `--startup-project` = `src/JiraUnpacked.Infrastructure`.
- **Schema only** — no scheduler, no Jira fetch, no derivation logic in this plan.
- 10 new tables: `RawIssueSnapshots`; `Statuses`, `IssueTypes`, `Priorities`, `JiraUsers` (per-connection); `Sprints`, `Teams` (per-project); `Issues`; `IssueChangeHistory`; `SyncRuns`.
- Grains: per-connection dicts `UNIQUE(EndUserId, JiraConnectionId, JiraId)`; per-project dicts `UNIQUE(EndUserId, JiraProjectId, JiraId)`; `RawIssueSnapshots`/`Issues` `UNIQUE(EndUserId, JiraIssueId)`; `IssueChangeHistory` `UNIQUE(EndUserId, JiraIssueId, JiraHistoryId, Field)`.
- Dictionary FKs on `Issues` are nullable `Guid?` with `OnDelete: NoAction`; `Issues` self-FK `ParentIssueId` is `OnDelete: Restrict`; `JiraProjectId`/`JiraConnectionId` FKs cascade.
- One commit per task. Controller pushes before PR.

**Namespaces:** entities `JiraUnpacked.Domain.Entities`; enums `JiraUnpacked.Domain.Enums`; EF configs `JiraUnpacked.Infrastructure.Persistence.Configurations`; migrations `JiraUnpacked.Infrastructure.Persistence.Migrations`; tests `JiraUnpacked.Tests.Persistence` / `JiraUnpacked.Tests.Tenancy`.

---

## Task 1 (SS1): Dictionary entities + configs + filters

**Files:**
- Create: `src/JiraUnpacked.Domain/Entities/Status.cs`, `IssueType.cs`, `Priority.cs`, `JiraUser.cs`, `Sprint.cs`, `Team.cs`
- Create: `src/JiraUnpacked.Infrastructure/Persistence/Configurations/StatusConfiguration.cs` (+ IssueType/Priority/JiraUser/Sprint/Team)
- Modify: `src/JiraUnpacked.Infrastructure/Persistence/AppDbContext.cs` (6 DbSets + 6 query filters)
- Test: `tests/JiraUnpacked.Tests/Persistence/SyncDictionaryModelTests.cs`

**Interfaces:**
- Produces entities: `Status(Id, EndUserId, JiraConnectionId, JiraStatusId, Name, StatusCategory)`, `IssueType(Id, EndUserId, JiraConnectionId, JiraIssueTypeId, Name)`, `Priority(Id, EndUserId, JiraConnectionId, JiraPriorityId, Name)`, `JiraUser(Id, EndUserId, JiraConnectionId, JiraAccountId, DisplayName)`, `Sprint(Id, EndUserId, JiraProjectId, JiraSprintId, Name, State, StartDate, EndDate)`, `Team(Id, EndUserId, JiraProjectId, JiraTeamId, Name)`. All `ITenantOwned`.

- [ ] **Step 1: Write the failing model test**

```csharp
// tests/JiraUnpacked.Tests/Persistence/SyncDictionaryModelTests.cs
using JiraUnpacked.Infrastructure.Persistence;
using JiraUnpacked.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace JiraUnpacked.Tests.Persistence;

public class SyncDictionaryModelTests
{
    private static AppDbContext Ctx()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=model-only;Username=u;Password=p").Options;
        return new AppDbContext(options, new NullCurrentTenant());
    }

    [Fact]
    public void Model_includes_the_six_dictionaries()
    {
        using var ctx = Ctx();
        var names = ctx.Model.GetEntityTypes().Select(e => e.ClrType.Name).ToHashSet();
        foreach (var n in new[] { "Status", "IssueType", "Priority", "JiraUser", "Sprint", "Team" })
            Assert.Contains(n, names);
    }

    [Fact]
    public void Per_connection_dictionaries_are_unique_by_connection_and_jira_id()
    {
        using var ctx = Ctx();
        var status = ctx.Model.FindEntityType(typeof(Domain.Entities.Status))!;
        Assert.Contains(status.GetIndexes(), i => i.IsUnique
            && i.Properties.Any(p => p.Name == "JiraConnectionId")
            && i.Properties.Any(p => p.Name == "JiraStatusId"));
    }

    [Fact]
    public void Per_project_dictionaries_are_unique_by_project_and_jira_id()
    {
        using var ctx = Ctx();
        var sprint = ctx.Model.FindEntityType(typeof(Domain.Entities.Sprint))!;
        Assert.Contains(sprint.GetIndexes(), i => i.IsUnique
            && i.Properties.Any(p => p.Name == "JiraProjectId")
            && i.Properties.Any(p => p.Name == "JiraSprintId"));
    }
}
```

- [ ] **Step 2: Run, verify failure**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~SyncDictionaryModelTests`
Expected: FAIL (entities not defined).

- [ ] **Step 3: Add the six entities**

```csharp
// src/JiraUnpacked.Domain/Entities/Status.cs
using JiraUnpacked.Domain.Abstractions;
namespace JiraUnpacked.Domain.Entities;
public sealed class Status : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid EndUserId { get; set; }
    public Guid JiraConnectionId { get; set; }
    public string JiraStatusId { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string? StatusCategory { get; set; }
}
```
```csharp
// src/JiraUnpacked.Domain/Entities/IssueType.cs
using JiraUnpacked.Domain.Abstractions;
namespace JiraUnpacked.Domain.Entities;
public sealed class IssueType : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid EndUserId { get; set; }
    public Guid JiraConnectionId { get; set; }
    public string JiraIssueTypeId { get; set; } = default!;
    public string Name { get; set; } = default!;
}
```
```csharp
// src/JiraUnpacked.Domain/Entities/Priority.cs
using JiraUnpacked.Domain.Abstractions;
namespace JiraUnpacked.Domain.Entities;
public sealed class Priority : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid EndUserId { get; set; }
    public Guid JiraConnectionId { get; set; }
    public string JiraPriorityId { get; set; } = default!;
    public string Name { get; set; } = default!;
}
```
```csharp
// src/JiraUnpacked.Domain/Entities/JiraUser.cs
using JiraUnpacked.Domain.Abstractions;
namespace JiraUnpacked.Domain.Entities;
public sealed class JiraUser : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid EndUserId { get; set; }
    public Guid JiraConnectionId { get; set; }
    public string JiraAccountId { get; set; } = default!;
    public string DisplayName { get; set; } = default!;
}
```
```csharp
// src/JiraUnpacked.Domain/Entities/Sprint.cs
using JiraUnpacked.Domain.Abstractions;
namespace JiraUnpacked.Domain.Entities;
public sealed class Sprint : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid EndUserId { get; set; }
    public Guid JiraProjectId { get; set; }
    public string JiraSprintId { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string? State { get; set; }
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
}
```
```csharp
// src/JiraUnpacked.Domain/Entities/Team.cs
using JiraUnpacked.Domain.Abstractions;
namespace JiraUnpacked.Domain.Entities;
public sealed class Team : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid EndUserId { get; set; }
    public Guid JiraProjectId { get; set; }
    public string JiraTeamId { get; set; } = default!;
    public string Name { get; set; } = default!;
}
```

- [ ] **Step 4: Add the six configurations**

Per-connection dictionaries (Status shown; IssueType/Priority/JiraUser identical shape with their own id/name columns):

```csharp
// src/JiraUnpacked.Infrastructure/Persistence/Configurations/StatusConfiguration.cs
using JiraUnpacked.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace JiraUnpacked.Infrastructure.Persistence.Configurations;

public class StatusConfiguration : IEntityTypeConfiguration<Status>
{
    public void Configure(EntityTypeBuilder<Status> b)
    {
        b.ToTable("Statuses");
        b.HasKey(x => x.Id);
        b.Property(x => x.JiraStatusId).IsRequired().HasMaxLength(64);
        b.Property(x => x.Name).IsRequired().HasMaxLength(256);
        b.Property(x => x.StatusCategory).HasMaxLength(64);
        b.HasIndex(x => x.EndUserId);
        b.HasIndex(x => new { x.EndUserId, x.JiraConnectionId, x.JiraStatusId }).IsUnique();
        b.HasOne<JiraConnection>().WithMany().HasForeignKey(x => x.JiraConnectionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
```
```csharp
// src/JiraUnpacked.Infrastructure/Persistence/Configurations/IssueTypeConfiguration.cs
using JiraUnpacked.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace JiraUnpacked.Infrastructure.Persistence.Configurations;

public class IssueTypeConfiguration : IEntityTypeConfiguration<IssueType>
{
    public void Configure(EntityTypeBuilder<IssueType> b)
    {
        b.ToTable("IssueTypes");
        b.HasKey(x => x.Id);
        b.Property(x => x.JiraIssueTypeId).IsRequired().HasMaxLength(64);
        b.Property(x => x.Name).IsRequired().HasMaxLength(256);
        b.HasIndex(x => x.EndUserId);
        b.HasIndex(x => new { x.EndUserId, x.JiraConnectionId, x.JiraIssueTypeId }).IsUnique();
        b.HasOne<JiraConnection>().WithMany().HasForeignKey(x => x.JiraConnectionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
```
```csharp
// src/JiraUnpacked.Infrastructure/Persistence/Configurations/PriorityConfiguration.cs
using JiraUnpacked.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace JiraUnpacked.Infrastructure.Persistence.Configurations;

public class PriorityConfiguration : IEntityTypeConfiguration<Priority>
{
    public void Configure(EntityTypeBuilder<Priority> b)
    {
        b.ToTable("Priorities");
        b.HasKey(x => x.Id);
        b.Property(x => x.JiraPriorityId).IsRequired().HasMaxLength(64);
        b.Property(x => x.Name).IsRequired().HasMaxLength(256);
        b.HasIndex(x => x.EndUserId);
        b.HasIndex(x => new { x.EndUserId, x.JiraConnectionId, x.JiraPriorityId }).IsUnique();
        b.HasOne<JiraConnection>().WithMany().HasForeignKey(x => x.JiraConnectionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
```
```csharp
// src/JiraUnpacked.Infrastructure/Persistence/Configurations/JiraUserConfiguration.cs
using JiraUnpacked.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace JiraUnpacked.Infrastructure.Persistence.Configurations;

public class JiraUserConfiguration : IEntityTypeConfiguration<JiraUser>
{
    public void Configure(EntityTypeBuilder<JiraUser> b)
    {
        b.ToTable("JiraUsers");
        b.HasKey(x => x.Id);
        b.Property(x => x.JiraAccountId).IsRequired().HasMaxLength(128);
        b.Property(x => x.DisplayName).IsRequired().HasMaxLength(256);
        b.HasIndex(x => x.EndUserId);
        b.HasIndex(x => new { x.EndUserId, x.JiraConnectionId, x.JiraAccountId }).IsUnique();
        b.HasOne<JiraConnection>().WithMany().HasForeignKey(x => x.JiraConnectionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
```

Per-project dictionaries:

```csharp
// src/JiraUnpacked.Infrastructure/Persistence/Configurations/SprintConfiguration.cs
using JiraUnpacked.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace JiraUnpacked.Infrastructure.Persistence.Configurations;

public class SprintConfiguration : IEntityTypeConfiguration<Sprint>
{
    public void Configure(EntityTypeBuilder<Sprint> b)
    {
        b.ToTable("Sprints");
        b.HasKey(x => x.Id);
        b.Property(x => x.JiraSprintId).IsRequired().HasMaxLength(64);
        b.Property(x => x.Name).IsRequired().HasMaxLength(256);
        b.Property(x => x.State).HasMaxLength(32);
        b.HasIndex(x => x.EndUserId);
        b.HasIndex(x => new { x.EndUserId, x.JiraProjectId, x.JiraSprintId }).IsUnique();
        b.HasOne<JiraProject>().WithMany().HasForeignKey(x => x.JiraProjectId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
```
```csharp
// src/JiraUnpacked.Infrastructure/Persistence/Configurations/TeamConfiguration.cs
using JiraUnpacked.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace JiraUnpacked.Infrastructure.Persistence.Configurations;

public class TeamConfiguration : IEntityTypeConfiguration<Team>
{
    public void Configure(EntityTypeBuilder<Team> b)
    {
        b.ToTable("Teams");
        b.HasKey(x => x.Id);
        b.Property(x => x.JiraTeamId).IsRequired().HasMaxLength(64);
        b.Property(x => x.Name).IsRequired().HasMaxLength(256);
        b.HasIndex(x => x.EndUserId);
        b.HasIndex(x => new { x.EndUserId, x.JiraProjectId, x.JiraTeamId }).IsUnique();
        b.HasOne<JiraProject>().WithMany().HasForeignKey(x => x.JiraProjectId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
```

- [ ] **Step 5: DbSets + query filters in `AppDbContext`**

Add DbSets after the existing `StatusSemantics`:
```csharp
public DbSet<Status> Statuses => Set<Status>();
public DbSet<IssueType> IssueTypes => Set<IssueType>();
public DbSet<Priority> Priorities => Set<Priority>();
public DbSet<JiraUser> JiraUsers => Set<JiraUser>();
public DbSet<Sprint> Sprints => Set<Sprint>();
public DbSet<Team> Teams => Set<Team>();
```
In `OnModelCreating`, after the existing `StatusSemantic` filter:
```csharp
modelBuilder.Entity<Status>().HasQueryFilter(x => x.EndUserId == _currentTenantId);
modelBuilder.Entity<IssueType>().HasQueryFilter(x => x.EndUserId == _currentTenantId);
modelBuilder.Entity<Priority>().HasQueryFilter(x => x.EndUserId == _currentTenantId);
modelBuilder.Entity<JiraUser>().HasQueryFilter(x => x.EndUserId == _currentTenantId);
modelBuilder.Entity<Sprint>().HasQueryFilter(x => x.EndUserId == _currentTenantId);
modelBuilder.Entity<Team>().HasQueryFilter(x => x.EndUserId == _currentTenantId);
```

- [ ] **Step 6: Run tests + commit**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~SyncDictionaryModelTests` → PASS (3).

```bash
git add src/JiraUnpacked.Domain/Entities/Status.cs src/JiraUnpacked.Domain/Entities/IssueType.cs \
        src/JiraUnpacked.Domain/Entities/Priority.cs src/JiraUnpacked.Domain/Entities/JiraUser.cs \
        src/JiraUnpacked.Domain/Entities/Sprint.cs src/JiraUnpacked.Domain/Entities/Team.cs \
        src/JiraUnpacked.Infrastructure/Persistence/Configurations/ \
        src/JiraUnpacked.Infrastructure/Persistence/AppDbContext.cs \
        tests/JiraUnpacked.Tests/Persistence/SyncDictionaryModelTests.cs
git commit -m "feat: sync-engine dictionary entities (mixed grain) (Module D schema)"
```

---

## Task 2 (SS2): Core entities + schema migration

**Files:**
- Create: `src/JiraUnpacked.Domain/Enums/SyncRunStatus.cs`
- Create: `src/JiraUnpacked.Domain/Entities/RawIssueSnapshot.cs`, `Issue.cs`, `IssueChangeHistory.cs`, `SyncRun.cs`
- Create: `src/JiraUnpacked.Infrastructure/Persistence/Configurations/RawIssueSnapshotConfiguration.cs` (+ Issue/IssueChangeHistory/SyncRun)
- Modify: `src/JiraUnpacked.Infrastructure/Persistence/AppDbContext.cs` (4 DbSets + 4 query filters)
- Create (generated): `src/JiraUnpacked.Infrastructure/Persistence/Migrations/<ts>_AddSyncEngineTables.cs`
- Test: `tests/JiraUnpacked.Tests/Persistence/SyncCoreModelTests.cs`

**Interfaces:**
- Consumes: the six dictionary entities (SS1) for the `Issues` FKs.
- Produces: `enum SyncRunStatus { Running, Succeeded, Failed, PartiallyFailed }`; entities `RawIssueSnapshot`, `Issue`, `IssueChangeHistory`, `SyncRun` (all `ITenantOwned`) with the columns in the steps below.

- [ ] **Step 1: Write the failing model test**

```csharp
// tests/JiraUnpacked.Tests/Persistence/SyncCoreModelTests.cs
using JiraUnpacked.Infrastructure.Persistence;
using JiraUnpacked.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace JiraUnpacked.Tests.Persistence;

public class SyncCoreModelTests
{
    private static AppDbContext Ctx()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=model-only;Username=u;Password=p").Options;
        return new AppDbContext(options, new NullCurrentTenant());
    }

    [Fact]
    public void Model_includes_the_core_tables()
    {
        using var ctx = Ctx();
        var names = ctx.Model.GetEntityTypes().Select(e => e.ClrType.Name).ToHashSet();
        foreach (var n in new[] { "RawIssueSnapshot", "Issue", "IssueChangeHistory", "SyncRun" })
            Assert.Contains(n, names);
    }

    [Fact]
    public void RawIssueSnapshot_payload_is_jsonb_and_unique_per_issue()
    {
        using var ctx = Ctx();
        var e = ctx.Model.FindEntityType(typeof(Domain.Entities.RawIssueSnapshot))!;
        Assert.Equal("jsonb", e.FindProperty(nameof(Domain.Entities.RawIssueSnapshot.Payload))!.GetColumnType());
        Assert.Contains(e.GetIndexes(), i => i.IsUnique
            && i.Properties.Any(p => p.Name == "EndUserId")
            && i.Properties.Any(p => p.Name == "JiraIssueId"));
    }

    [Fact]
    public void Issue_is_unique_per_issue_and_has_a_self_parent_fk()
    {
        using var ctx = Ctx();
        var e = ctx.Model.FindEntityType(typeof(Domain.Entities.Issue))!;
        Assert.Contains(e.GetIndexes(), i => i.IsUnique
            && i.Properties.Any(p => p.Name == "EndUserId")
            && i.Properties.Any(p => p.Name == "JiraIssueId"));
        Assert.Contains(e.GetForeignKeys(), fk => fk.PrincipalEntityType.ClrType == typeof(Domain.Entities.Issue));
    }

    [Fact]
    public void SyncRun_status_is_stored_as_string()
    {
        using var ctx = Ctx();
        var e = ctx.Model.FindEntityType(typeof(Domain.Entities.SyncRun))!;
        Assert.Equal(typeof(string), e.FindProperty(nameof(Domain.Entities.SyncRun.Status))!.GetProviderClrType());
    }

    [Fact]
    public void ChangeHistory_is_unique_by_issue_history_and_field()
    {
        using var ctx = Ctx();
        var e = ctx.Model.FindEntityType(typeof(Domain.Entities.IssueChangeHistory))!;
        Assert.Contains(e.GetIndexes(), i => i.IsUnique
            && i.Properties.Any(p => p.Name == "JiraHistoryId")
            && i.Properties.Any(p => p.Name == "Field"));
    }
}
```

- [ ] **Step 2: Run, verify failure**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~SyncCoreModelTests`
Expected: FAIL (entities not defined).

- [ ] **Step 3: Add enum + entities**

```csharp
// src/JiraUnpacked.Domain/Enums/SyncRunStatus.cs
namespace JiraUnpacked.Domain.Enums;
public enum SyncRunStatus { Running, Succeeded, Failed, PartiallyFailed }
```
```csharp
// src/JiraUnpacked.Domain/Entities/RawIssueSnapshot.cs
using JiraUnpacked.Domain.Abstractions;
namespace JiraUnpacked.Domain.Entities;
public sealed class RawIssueSnapshot : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid EndUserId { get; set; }
    public Guid JiraProjectId { get; set; }
    public string JiraIssueId { get; set; } = default!;
    public string Payload { get; set; } = default!; // jsonb
    public DateTime FetchedAt { get; set; }
}
```
```csharp
// src/JiraUnpacked.Domain/Entities/Issue.cs
using JiraUnpacked.Domain.Abstractions;
namespace JiraUnpacked.Domain.Entities;
public sealed class Issue : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid EndUserId { get; set; }
    public Guid JiraProjectId { get; set; }
    public string JiraIssueId { get; set; } = default!;
    public string JiraKey { get; set; } = default!;
    public Guid? IssueTypeId { get; set; }
    public Guid? StatusId { get; set; }
    public Guid? PriorityId { get; set; }
    public Guid? AssigneeId { get; set; }
    public Guid? SprintId { get; set; }
    public Guid? TeamId { get; set; }
    public decimal? StoryPoints { get; set; }
    public string? ParentJiraIssueId { get; set; }
    public Guid? ParentIssueId { get; set; }
    public string? Summary { get; set; }
    public DateTime JiraCreatedAt { get; set; }
    public DateTime JiraUpdatedAt { get; set; }
    public DateTime LastSyncedAt { get; set; }
}
```
```csharp
// src/JiraUnpacked.Domain/Entities/IssueChangeHistory.cs
using JiraUnpacked.Domain.Abstractions;
namespace JiraUnpacked.Domain.Entities;
public sealed class IssueChangeHistory : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid EndUserId { get; set; }
    public Guid JiraProjectId { get; set; }
    public string JiraIssueId { get; set; } = default!;
    public string JiraHistoryId { get; set; } = default!;
    public string Field { get; set; } = default!;
    public string? FromValue { get; set; }
    public string? ToValue { get; set; }
    public string? Author { get; set; }
    public DateTime ChangedAt { get; set; }
}
```
```csharp
// src/JiraUnpacked.Domain/Entities/SyncRun.cs
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Enums;
namespace JiraUnpacked.Domain.Entities;
public sealed class SyncRun : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid EndUserId { get; set; }
    public Guid JiraProjectId { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public SyncRunStatus Status { get; set; }
    public int IssuesProcessed { get; set; }
    public string? Error { get; set; }
    public DateTime? NextRunAt { get; set; }
}
```

- [ ] **Step 4: Add the four configurations**

```csharp
// src/JiraUnpacked.Infrastructure/Persistence/Configurations/RawIssueSnapshotConfiguration.cs
using JiraUnpacked.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace JiraUnpacked.Infrastructure.Persistence.Configurations;

public class RawIssueSnapshotConfiguration : IEntityTypeConfiguration<RawIssueSnapshot>
{
    public void Configure(EntityTypeBuilder<RawIssueSnapshot> b)
    {
        b.ToTable("RawIssueSnapshots");
        b.HasKey(x => x.Id);
        b.Property(x => x.JiraIssueId).IsRequired().HasMaxLength(64);
        b.Property(x => x.Payload).IsRequired().HasColumnType("jsonb");
        b.Property(x => x.FetchedAt).IsRequired();
        b.HasIndex(x => x.EndUserId);
        b.HasIndex(x => new { x.EndUserId, x.JiraIssueId }).IsUnique();
        b.HasOne<JiraProject>().WithMany().HasForeignKey(x => x.JiraProjectId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
```
```csharp
// src/JiraUnpacked.Infrastructure/Persistence/Configurations/IssueConfiguration.cs
using JiraUnpacked.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace JiraUnpacked.Infrastructure.Persistence.Configurations;

public class IssueConfiguration : IEntityTypeConfiguration<Issue>
{
    public void Configure(EntityTypeBuilder<Issue> b)
    {
        b.ToTable("Issues");
        b.HasKey(x => x.Id);
        b.Property(x => x.JiraIssueId).IsRequired().HasMaxLength(64);
        b.Property(x => x.JiraKey).IsRequired().HasMaxLength(64);
        b.Property(x => x.ParentJiraIssueId).HasMaxLength(64);
        b.Property(x => x.StoryPoints).HasPrecision(9, 2);
        b.Property(x => x.Summary); // unlimited text (inline free text)

        b.HasIndex(x => x.EndUserId);
        b.HasIndex(x => new { x.EndUserId, x.JiraIssueId }).IsUnique();
        b.HasIndex(x => new { x.EndUserId, x.JiraProjectId });
        b.HasIndex(x => new { x.EndUserId, x.ParentJiraIssueId });

        // Owning project: cascade. Dictionary FKs: NoAction (a dict delete must not delete issues).
        b.HasOne<JiraProject>().WithMany().HasForeignKey(x => x.JiraProjectId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<IssueType>().WithMany().HasForeignKey(x => x.IssueTypeId).OnDelete(DeleteBehavior.NoAction);
        b.HasOne<Status>().WithMany().HasForeignKey(x => x.StatusId).OnDelete(DeleteBehavior.NoAction);
        b.HasOne<Priority>().WithMany().HasForeignKey(x => x.PriorityId).OnDelete(DeleteBehavior.NoAction);
        b.HasOne<JiraUser>().WithMany().HasForeignKey(x => x.AssigneeId).OnDelete(DeleteBehavior.NoAction);
        b.HasOne<Sprint>().WithMany().HasForeignKey(x => x.SprintId).OnDelete(DeleteBehavior.NoAction);
        b.HasOne<Team>().WithMany().HasForeignKey(x => x.TeamId).OnDelete(DeleteBehavior.NoAction);
        // Self parent link: restrict (no cascade cycle).
        b.HasOne<Issue>().WithMany().HasForeignKey(x => x.ParentIssueId).OnDelete(DeleteBehavior.Restrict);
    }
}
```
```csharp
// src/JiraUnpacked.Infrastructure/Persistence/Configurations/IssueChangeHistoryConfiguration.cs
using JiraUnpacked.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace JiraUnpacked.Infrastructure.Persistence.Configurations;

public class IssueChangeHistoryConfiguration : IEntityTypeConfiguration<IssueChangeHistory>
{
    public void Configure(EntityTypeBuilder<IssueChangeHistory> b)
    {
        b.ToTable("IssueChangeHistory");
        b.HasKey(x => x.Id);
        b.Property(x => x.JiraIssueId).IsRequired().HasMaxLength(64);
        b.Property(x => x.JiraHistoryId).IsRequired().HasMaxLength(64);
        b.Property(x => x.Field).IsRequired().HasMaxLength(256);
        b.Property(x => x.Author).HasMaxLength(256);
        b.Property(x => x.ChangedAt).IsRequired();
        b.HasIndex(x => x.EndUserId);
        b.HasIndex(x => new { x.EndUserId, x.JiraIssueId, x.JiraHistoryId, x.Field }).IsUnique();
        b.HasIndex(x => new { x.EndUserId, x.JiraProjectId, x.JiraIssueId });
        b.HasIndex(x => new { x.JiraIssueId, x.Field, x.ChangedAt });
        b.HasOne<JiraProject>().WithMany().HasForeignKey(x => x.JiraProjectId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
```
```csharp
// src/JiraUnpacked.Infrastructure/Persistence/Configurations/SyncRunConfiguration.cs
using JiraUnpacked.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace JiraUnpacked.Infrastructure.Persistence.Configurations;

public class SyncRunConfiguration : IEntityTypeConfiguration<SyncRun>
{
    public void Configure(EntityTypeBuilder<SyncRun> b)
    {
        b.ToTable("SyncRuns");
        b.HasKey(x => x.Id);
        b.Property(x => x.StartedAt).IsRequired();
        b.Property(x => x.Status).IsRequired().HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Error); // unlimited text
        b.HasIndex(x => x.EndUserId);
        b.HasIndex(x => new { x.EndUserId, x.JiraProjectId, x.StartedAt });
        b.HasOne<JiraProject>().WithMany().HasForeignKey(x => x.JiraProjectId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
```

- [ ] **Step 5: DbSets + query filters in `AppDbContext`**

Add DbSets after the dictionary DbSets from SS1:
```csharp
public DbSet<RawIssueSnapshot> RawIssueSnapshots => Set<RawIssueSnapshot>();
public DbSet<Issue> Issues => Set<Issue>();
public DbSet<IssueChangeHistory> IssueChangeHistory => Set<IssueChangeHistory>();
public DbSet<SyncRun> SyncRuns => Set<SyncRun>();
```
In `OnModelCreating`, after the dictionary filters:
```csharp
modelBuilder.Entity<RawIssueSnapshot>().HasQueryFilter(x => x.EndUserId == _currentTenantId);
modelBuilder.Entity<Issue>().HasQueryFilter(x => x.EndUserId == _currentTenantId);
modelBuilder.Entity<IssueChangeHistory>().HasQueryFilter(x => x.EndUserId == _currentTenantId);
modelBuilder.Entity<SyncRun>().HasQueryFilter(x => x.EndUserId == _currentTenantId);
```

- [ ] **Step 6: Generate the schema migration**

Run:
```
dotnet ef migrations add AddSyncEngineTables \
  --project src/JiraUnpacked.Infrastructure --startup-project src/JiraUnpacked.Infrastructure
```
Inspect `*_AddSyncEngineTables.cs`: confirm all 10 tables created; `RawIssueSnapshots.Payload` is `jsonb`; `SyncRuns.Status` is text; `Issues.StoryPoints` is `numeric(9,2)`; the unique indexes from §Global Constraints; `Issues` has a self-referencing FK (`ParentIssueId`) and the dictionary FKs are `ON DELETE NO ACTION` / `RESTRICT` while `JiraProjectId` is `ON DELETE CASCADE`.

- [ ] **Step 7: Run tests + drift check + commit**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~SyncCoreModelTests` → PASS (5).
Run: `dotnet ef migrations has-pending-model-changes --project src/JiraUnpacked.Infrastructure --startup-project src/JiraUnpacked.Infrastructure` → "No changes".

```bash
git add src/JiraUnpacked.Domain/Enums/SyncRunStatus.cs \
        src/JiraUnpacked.Domain/Entities/RawIssueSnapshot.cs src/JiraUnpacked.Domain/Entities/Issue.cs \
        src/JiraUnpacked.Domain/Entities/IssueChangeHistory.cs src/JiraUnpacked.Domain/Entities/SyncRun.cs \
        src/JiraUnpacked.Infrastructure/Persistence/Configurations/ \
        src/JiraUnpacked.Infrastructure/Persistence/AppDbContext.cs \
        src/JiraUnpacked.Infrastructure/Persistence/Migrations/ \
        tests/JiraUnpacked.Tests/Persistence/SyncCoreModelTests.cs
git commit -m "feat: sync-engine core entities + schema migration (Module D schema)"
```

---

## Task 3 (SS3): RLS for all sync tables + gate extension to Issues  *(critical — opus review)*

**Files:**
- Create (generated, then hand-edited): `src/JiraUnpacked.Infrastructure/Persistence/Migrations/<ts>_AddSyncEngineRls.cs`
- Modify: `tests/JiraUnpacked.Tests/Tenancy/TenantIsolationGateTests.cs`

**Interfaces:**
- Consumes: the 10 tables from SS1/SS2; the `jiraunpacked_app` role + `tenant_isolation` convention from `AddTenantRls`.
- Produces: RLS (ENABLE + FORCE + policy + GRANT) on all 10 new tables; the gate test now also proves RLS on `Issues`.

- [ ] **Step 1: Generate an empty migration**

Run:
```
dotnet ef migrations add AddSyncEngineRls \
  --project src/JiraUnpacked.Infrastructure --startup-project src/JiraUnpacked.Infrastructure
```

- [ ] **Step 2: Replace Up/Down with raw SQL (mirror `AddSemanticLayerRls`)**

```csharp
protected override void Up(MigrationBuilder migrationBuilder)
{
    var tables = new[]
    {
        "RawIssueSnapshots", "Statuses", "IssueTypes", "Priorities", "JiraUsers",
        "Sprints", "Teams", "Issues", "IssueChangeHistory", "SyncRuns"
    };
    foreach (var table in tables)
    {
        migrationBuilder.Sql($@"GRANT SELECT, INSERT, UPDATE, DELETE ON ""{table}"" TO jiraunpacked_app;");
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
    var tables = new[]
    {
        "RawIssueSnapshots", "Statuses", "IssueTypes", "Priorities", "JiraUsers",
        "Sprints", "Teams", "Issues", "IssueChangeHistory", "SyncRuns"
    };
    foreach (var table in tables)
    {
        migrationBuilder.Sql($@"
DROP POLICY IF EXISTS tenant_isolation ON ""{table}"";
ALTER TABLE ""{table}"" NO FORCE ROW LEVEL SECURITY;
ALTER TABLE ""{table}"" DISABLE ROW LEVEL SECURITY;
");
    }
}
```

- [ ] **Step 3: Extend the gate test to prove RLS on `Issues`**

Append a `[Fact]` to `TenantIsolationGateTests` that seeds one issue per tenant via the privileged connection, then verifies (as the `jiraunpacked_app` role with the GUC set to tenant B) that A's issue is invisible and B's own is visible. Reuse the existing `SetTenantAsync` helper.

```csharp
[Fact]
public async Task Tenant_cannot_read_another_tenants_issues_via_rls()
{
    var (tenantA, issueA) = await SeedIssueAsync();
    var (tenantB, issueB) = await SeedIssueAsync();

    await using var conn = new Npgsql.NpgsqlConnection(JiraUnpackedApiFactory.TestAppConnectionString);
    await conn.OpenAsync();
    await SetTenantAsync(conn, tenantB);

    Assert.Equal(0L, await CountIssueById(conn, issueA)); // A hidden from B
    Assert.Equal(1L, await CountIssueById(conn, issueB)); // B sees its own
}

// Inserts a JiraConnection -> JiraProject -> Issue chain for a fresh tenant via the
// privileged (RLS-bypassing) connection. Returns (tenantId, issueId).
private async Task<(Guid tenant, Guid issue)> SeedIssueAsync()
{
    var tenant = Guid.NewGuid();
    var connectionId = Guid.NewGuid();
    var projectId = Guid.NewGuid();
    var issueId = Guid.NewGuid();
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
        c2.Parameters.AddWithValue("id", projectId);
        c2.Parameters.AddWithValue("t", tenant);
        c2.Parameters.AddWithValue("conn", connectionId);
        await c2.ExecuteNonQueryAsync();
    }
    await using (var c3 = conn.CreateCommand())
    {
        c3.CommandText = @"INSERT INTO ""Issues""
            (""Id"",""EndUserId"",""JiraProjectId"",""JiraIssueId"",""JiraKey"",
             ""JiraCreatedAt"",""JiraUpdatedAt"",""LastSyncedAt"")
            VALUES (@id,@t,@proj,@jid,'ACME-1',now(),now(),now())";
        c3.Parameters.AddWithValue("id", issueId);
        c3.Parameters.AddWithValue("t", tenant);
        c3.Parameters.AddWithValue("proj", projectId);
        c3.Parameters.AddWithValue("jid", Guid.NewGuid().ToString("N"));
        await c3.ExecuteNonQueryAsync();
    }
    return (tenant, issueId);
}

private static async Task<long> CountIssueById(Npgsql.NpgsqlConnection conn, Guid id)
{
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"SELECT count(*) FROM ""Issues"" WHERE ""Id"" = @id";
    cmd.Parameters.AddWithValue("id", id);
    return (long)(await cmd.ExecuteScalarAsync())!;
}
```

(Ensure `using Npgsql;` is present in the file, or use the fully-qualified `Npgsql.NpgsqlConnection` as shown.)

- [ ] **Step 4: Run the gate + full suite + drift, then commit**

Run: `dotnet test tests/JiraUnpacked.Tests --filter FullyQualifiedName~TenantIsolationGateTests` → PASS (3 facts: connections, projects, issues).
Run: `dotnet test JiraUnpacked.sln` → all green.
Run: `dotnet ef migrations has-pending-model-changes --project src/JiraUnpacked.Infrastructure --startup-project src/JiraUnpacked.Infrastructure` → "No changes".

```bash
git add src/JiraUnpacked.Infrastructure/Persistence/Migrations/ \
        tests/JiraUnpacked.Tests/Tenancy/TenantIsolationGateTests.cs
git commit -m "feat: RLS on sync-engine tables + gate covers Issues (Module D schema, FR-B2)"
```

---

## Final verification (controller, after all tasks)

- [ ] `dotnet build JiraUnpacked.sln -c Release` → 0 warnings / 0 errors.
- [ ] `dotnet test JiraUnpacked.sln -c Release` → all green.
- [ ] `dotnet ef migrations has-pending-model-changes ...` → "No changes".
- [ ] Push `feat/sync-schema`; verify `origin == local`; open PR → `develop`; CI green; squash-merge; verify develop has the code; cut the `develop → main` release PR (merge-commit).

## Notes for execution
- **No behavior** is added here — only schema. Resist adding services/endpoints; those are Module D Specs 2 (extract) and 3 (transform).
- Every new tenant table MUST get both the query filter (SS1/SS2) and the RLS policy (SS3) — the gate test is the backstop.
- The schema migration (SS2) and RLS migration (SS3) are applied to `jiraunpacked_test` by `EnsureDatabaseAsync` when integration tests run; the default privileges from `AddTenantRls` already grant the new tables to `jiraunpacked_app`, and SS3 re-grants explicitly.
