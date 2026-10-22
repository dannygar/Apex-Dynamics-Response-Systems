extern alias WorkerAssembly;
using System.Net;
using System.Net.Sockets;
using Evaluation.Core;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using EvaluationWorker = WorkerAssembly::EvaluationWorker;

namespace Evaluation.Tests;

public sealed class FakeJudge : IJudgeAdapter
{
    public bool Configured { get; init; } = true;
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromMinutes(1);
    public JudgeContractSnapshot Contract { get; set; } = JudgeFixtures.Contract;
    public Func<string, IReadOnlyList<JudgeFile>, CancellationToken, Task<JudgeResponse>> OnEvaluate { get; set; } =
        (kind, _, _) => throw new InvalidOperationException("Unexpected judge call.");
    public int ContractCalls;
    public List<(string Kind, JudgeFile[] Files)> Calls { get; } = [];
    public Task<JudgeContractSnapshot> GetContract(CancellationToken ct)
    {
        Interlocked.Increment(ref ContractCalls);
        return Configured ? Task.FromResult(Contract) : throw new JudgeUnavailableException();
    }
    public Task<JudgeResponse> Evaluate(string kind, IReadOnlyList<JudgeFile> files, CancellationToken ct)
    {
        lock (Calls) Calls.Add((kind, files.ToArray()));
        return Configured ? OnEvaluate(kind, files, ct) : throw new JudgeUnavailableException();
    }
}

// Isolated file-backed SQLite and private storage per test; separate connections allow real concurrency.
public sealed class WorkerHarness : IAsyncDisposable
{
    public readonly string Root = Path.Combine(AppContext.BaseDirectory, "worker-fixture", Guid.NewGuid().ToString("N"));
    public readonly Guid Team = Guid.NewGuid(), Project = Guid.NewGuid(), Submission = Guid.NewGuid();
    public readonly string Requester = Guid.NewGuid().ToString();
    public ServiceProvider Provider { get; }
    public PrivateFiles Files { get; }
    public IConfiguration Config { get; }
    public WorkerHarness(Dictionary<string, string?>? settings = null)
    {
        Directory.CreateDirectory(Root);
        var values = new Dictionary<string, string?> { ["Storage:Root"] = Path.Combine(Root, "private") };
        foreach (var pair in settings ?? []) values[pair.Key] = pair.Value;
        Config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        Files = new PrivateFiles(Config);
        var services = new ServiceCollection();
        services.AddDbContext<EvaluationDb>(o => o.UseSqlite($"Data Source={Path.Combine(Root, "evaluation.db")};Pooling=False"));
        Provider = services.BuildServiceProvider();
    }
    public async Task Initialize(bool member = true)
    {
        await Use(async db =>
        {
            await db.Database.MigrateAsync();
            db.Teams.Add(new Team { Id = Team });
            if (member) db.Memberships.Add(new Membership { TeamId = Team, Subject = Requester, Role = TeamRole.Member });
            db.Projects.Add(new Project { Id = Project, TeamId = Team });
            db.Submissions.Add(new Submission { Id = Submission, TeamId = Team, ProjectId = Project });
            await db.SaveChangesAsync();
        });
    }
    public async Task<T> Use<T>(Func<EvaluationDb, Task<T>> action)
    {
        using var scope = Provider.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<EvaluationDb>());
    }
    public Task Use(Func<EvaluationDb, Task> action) => Use(async db => { await action(db); return true; });
    public async Task<DocumentVersion> AddDocument(DocumentRole role, string text, Guid? bundle = null, string? path = null, string status = "Ready")
    {
        var id = Guid.NewGuid();
        var version = new DocumentVersion { Id = id, TeamId = Team, SubmissionId = Submission, Role = role, Status = status, Hash = "hash",
            FileName = Path.GetFileName(path ?? $"{role}.md"), RelativePath = path ?? $"{role}.md", BundleId = bundle ?? id,
            TextKey = status == "Ready" ? await Files.StoreText(text, default) : null, TextLength = text.Length };
        await Use(async db => { db.Documents.Add(version); await db.SaveChangesAsync(); });
        return version;
    }
    public async Task<Guid> AddScan(IEnumerable<DocumentVersion> inputs, string status = "Queued", JudgeContractSnapshot? contract = null)
    {
        var id = Guid.NewGuid();
        await Use(async db =>
        {
            db.Scans.Add(new Scan { Id = id, TeamId = Team, SubmissionId = Submission, Status = status, Requester = Requester,
                IdempotencyKey = Guid.NewGuid().ToString(), Policy = Json.Write(JudgeFixtures.Policy), Judge = Json.Write(contract ?? JudgeFixtures.Contract),
                Inputs = Json.Write(inputs.Select(x => new InputSnapshot(x.Id, x.Role, x.Hash, x.TextLength, x.BundleId, x.RelativePath)).ToArray()) });
            db.Outbox.Add(new OutboxEvent { Id = Guid.NewGuid(), TeamId = Team, ScanId = id });
            await db.SaveChangesAsync();
        });
        return id;
    }
    public EvaluationWorker Worker(IJudgeAdapter judge) =>
        new(Provider.GetRequiredService<IServiceScopeFactory>(), Files, new MalwareScanner(Config), judge, NullLogger<EvaluationWorker>.Instance)
        { LeaseHeartbeat = TimeSpan.FromMilliseconds(100), PollInterval = TimeSpan.FromMilliseconds(50) };
    public async Task<bool> RunUntil(EvaluationWorker worker, Func<EvaluationDb, Task<bool>> done, int milliseconds = 15000)
    {
        await worker.StartAsync(default);
        try
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
            while (DateTime.UtcNow < deadline)
            {
                if (await Use(done)) return true;
                await Task.Delay(50);
            }
            return false;
        }
        finally { await worker.StopAsync(default); worker.Dispose(); }
    }
    public Task<Scan> Scan() => Use(db => db.Scans.AsNoTracking().SingleAsync());
    public Task<OutboxEvent> Outbox() => Use(db => db.Outbox.AsNoTracking().SingleAsync());
    public async ValueTask DisposeAsync()
    {
        await Provider.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(Root, true); } catch (IOException) { }
    }
}

public sealed class WorkerTests
{
    private static Func<EvaluationDb, Task<bool>> OutboxIs(string state) => async db => (await db.Outbox.SingleAsync()).State == state;

    [Theory]
    [InlineData("Queued", "TechnicalFailure", "DeadLetter")]
    [InlineData("Evaluating", "TechnicalFailure", "DeadLetter")]
    [InlineData("Completed", "Completed", "Dispatched")]
    [InlineData("Revoked", "TechnicalFailure", "DeadLetter")]
    public async Task UnavailableInterruptedAndDuplicateJobsNeverInvokeJudge(string initial, string expectedStatus, string expectedOutbox)
    {
        await using var harness = new WorkerHarness();
        await harness.Initialize(member: initial != "Revoked");
        var target = await harness.AddDocument(DocumentRole.BRD, "# BRD");
        await harness.AddScan([target], initial == "Revoked" ? "Queued" : initial);
        var judge = new FakeJudge { Configured = initial != "Queued" };
        Assert.True(await harness.RunUntil(harness.Worker(judge), OutboxIs(expectedOutbox)));
        var scan = await harness.Scan();
        Assert.Equal(expectedStatus, scan.Status); Assert.Equal("Inconclusive", scan.Outcome);
        Assert.Empty(judge.Calls);
    }

    [Fact] public async Task ContractChangeStopsBeforeInvocation()
    {
        await using var harness = new WorkerHarness();
        await harness.Initialize();
        await harness.AddScan([await harness.AddDocument(DocumentRole.PRD, "# PRD")]);
        var judge = new FakeJudge { Contract = JudgeFixtures.Contract with { Version = "agent-eval/next" } };
        Assert.True(await harness.RunUntil(harness.Worker(judge), OutboxIs("DeadLetter")));
        var scan = await harness.Scan();
        Assert.Equal("TechnicalFailure", scan.Status);
        Assert.Equal("Judge contract changed since scan was requested; start a new scan.", scan.Error);
        Assert.Empty(judge.Calls); Assert.Null(scan.RemoteInvocationId); Assert.Null(scan.Result);
    }

    [Fact] public async Task EvaluatesEachArtifactWithoutSendingSourceEvidence()
    {
        await using var harness = new WorkerHarness();
        await harness.Initialize();
        var bundle = Guid.NewGuid();
        var dt1 = await harness.AddDocument(DocumentRole.DT, "# Stakeholders", bundle, "dt/method-01-scope/stakeholder-map.md");
        var dt2 = await harness.AddDocument(DocumentRole.DT, "# Coaching", bundle, "dt/coaching-state.md");
        var brd = await harness.AddDocument(DocumentRole.BRD, "# BRD text");
        var source = await harness.AddDocument(DocumentRole.Source, "CONFIDENTIAL SOURCE CONTEXT");
        await harness.AddScan([dt1, dt2, brd, source]);
        var judge = new FakeJudge
        {
            OnEvaluate = (kind, _, _) => Task.FromResult(kind == "dt" ? JudgeFixtures.Scored(DocumentRole.DT, 0.75) : JudgeFixtures.Scored(DocumentRole.BRD, 0.5))
        };
        Assert.True(await harness.RunUntil(harness.Worker(judge), OutboxIs("Dispatched")));
        Assert.Equal(["dt", "brd"], judge.Calls.Select(c => c.Kind));
        Assert.Equal(["dt/coaching-state.md", "dt/method-01-scope/stakeholder-map.md"], judge.Calls[0].Files.Select(f => f.Path));
        Assert.Equal("# BRD text", Assert.Single(judge.Calls[1].Files).Text);
        Assert.DoesNotContain(judge.Calls.SelectMany(c => c.Files), f => f.Text.Contains("CONFIDENTIAL"));
        var scan = await harness.Scan();
        Assert.Equal("Completed", scan.Status); Assert.Equal("Fail", scan.Outcome);
        var result = Json.Read<ScanResult>(scan.Result!);
        Assert.Equal("Pass", result.Artifacts[0].Decision); Assert.Equal(75, result.Artifacts[0].Score100!.Value, 6);
        Assert.Equal(2, result.Artifacts[0].VersionIds.Length);
        Assert.Equal("Fail", result.Artifacts[1].Decision); Assert.Equal(80, result.Artifacts[1].Threshold);
        Assert.Equal(2, Json.Read<string[]>(scan.RemoteInvocationId!).Length);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(502)]
    [InlineData(500)]
    [InlineData(null)]
    public async Task JudgeFailureKeepsValidatedPartialResultsAndNeverRetries(int? code)
    {
        await using var harness = new WorkerHarness();
        await harness.Initialize();
        var dt = await harness.AddDocument(DocumentRole.DT, "# DT", path: "a.md");
        var brd = await harness.AddDocument(DocumentRole.BRD, "# BRD");
        var prd = await harness.AddDocument(DocumentRole.PRD, "# PRD");
        await harness.AddScan([dt, brd, prd]);
        var judge = new FakeJudge
        {
            OnEvaluate = (kind, _, _) => kind == "dt" ? Task.FromResult(JudgeFixtures.Scored(DocumentRole.DT, 0.9)) :
                throw new JudgeCallException(code is null ? "Judge call timed out after 10 minutes. No score produced." : $"AI judge failed ({code}). No score produced.", code)
        };
        Assert.True(await harness.RunUntil(harness.Worker(judge), OutboxIs("DeadLetter")));
        Assert.Equal(["dt", "brd"], judge.Calls.Select(c => c.Kind));
        var scan = await harness.Scan();
        Assert.Equal("TechnicalFailure", scan.Status); Assert.Equal("Inconclusive", scan.Outcome);
        var result = Json.Read<ScanResult>(scan.Result!);
        Assert.Equal(["Pass", "Inconclusive", "Inconclusive"], result.Artifacts.Select(a => a.Decision));
        Assert.Null(result.Artifacts[1].Score100); Assert.Null(result.Artifacts[2].Score100);
        Assert.Contains("No score produced", result.Artifacts[1].Errors[0]);
        Assert.Contains("earlier judge call failed", result.Artifacts[2].Errors[0]);
    }

    [Fact] public async Task InvalidResponseIsInconclusiveButOtherArtifactsContinue()
    {
        await using var harness = new WorkerHarness();
        await harness.Initialize();
        await harness.AddScan([await harness.AddDocument(DocumentRole.BRD, "# BRD"), await harness.AddDocument(DocumentRole.PRD, "# PRD")]);
        var judge = new FakeJudge
        {
            OnEvaluate = (kind, _, _) => Task.FromResult(kind == "brd" ? JudgeFixtures.Scored(DocumentRole.BRD, 0.9) with { Score = 0.1 } :
                JudgeFixtures.Scored(DocumentRole.PRD, 0.95))
        };
        Assert.True(await harness.RunUntil(harness.Worker(judge), OutboxIs("Dispatched")));
        var scan = await harness.Scan();
        Assert.Equal("Completed", scan.Status); Assert.Equal("Inconclusive", scan.Outcome);
        Assert.Equal(["Inconclusive", "Pass"], Json.Read<ScanResult>(scan.Result!).Artifacts.Select(a => a.Decision));
    }

    [Fact] public async Task LongCallKeepsItsLeaseAlive()
    {
        await using var harness = new WorkerHarness();
        await harness.Initialize();
        await harness.AddScan([await harness.AddDocument(DocumentRole.BRD, "# BRD")]);
        var extended = false;
        var judge = new FakeJudge
        {
            OnEvaluate = async (_, _, ct) =>
            {
                // Simulate a lease about to expire mid-call; the heartbeat must renew it before the call returns.
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                await harness.Use(db => db.Outbox.ExecuteUpdateAsync(s => s.SetProperty(x => x.LeaseUntil, now + 3)));
                for (var i = 0; i < 100 && !extended; i++)
                {
                    await Task.Delay(50, ct);
                    extended = (await harness.Outbox()).LeaseUntil >= now + JobLease.Seconds - 5;
                }
                return JudgeFixtures.Scored(DocumentRole.BRD, 0.85);
            }
        };
        Assert.True(await harness.RunUntil(harness.Worker(judge), OutboxIs("Dispatched")));
        Assert.True(extended);
        Assert.Equal("Pass", (await harness.Scan()).Outcome);
    }

    [Fact] public async Task LostLeaseStopsWithoutOverwriting()
    {
        await using var harness = new WorkerHarness();
        await harness.Initialize();
        await harness.AddScan([await harness.AddDocument(DocumentRole.BRD, "# BRD")]);
        var cancelled = new TaskCompletionSource();
        var judge = new FakeJudge
        {
            OnEvaluate = async (_, _, ct) =>
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                await harness.Use(db => db.Outbox.ExecuteUpdateAsync(s => s.SetProperty(x => x.LeaseOwner, "another-worker")
                    .SetProperty(x => x.LeaseUntil, now + JobLease.Seconds)));
                try { await Task.Delay(Timeout.Infinite, ct); }
                catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
                return JudgeFixtures.Scored(DocumentRole.BRD, 1);
            }
        };
        Assert.True(await harness.RunUntil(harness.Worker(judge), async _ =>
        {
            if (!cancelled.Task.IsCompleted) return false;
            await Task.Delay(500); // Give a misbehaving worker the chance to overwrite before asserting it did not.
            return true;
        }));
        var scan = await harness.Scan();
        Assert.Equal("Evaluating", scan.Status);
        Assert.Null(scan.RemoteInvocationId);
        Assert.Equal("Pending", Json.Read<ScanResult>(scan.Result!).Outcome);
        Assert.Equal("another-worker", (await harness.Outbox()).LeaseOwner);
    }

    [Fact] public async Task UnreachableScannerLeavesUploadsQuarantined()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        await using var harness = new WorkerHarness(new() { ["Malware:ClamAvHost"] = "127.0.0.1", ["Malware:ClamAvPort"] = port.ToString() });
        await harness.Initialize();
        var upload = await harness.AddDocument(DocumentRole.DT, "# DT", status: "Quarantined");
        var worker = harness.Worker(new FakeJudge());
        Assert.False(await harness.RunUntil(worker, async db => (await db.Documents.SingleAsync()).Status != "Quarantined", 1500));
        var document = await harness.Use(db => db.Documents.AsNoTracking().SingleAsync(x => x.Id == upload.Id));
        Assert.Equal("Quarantined", document.Status); Assert.Null(document.LeaseOwner); Assert.Equal(0, document.LeaseUntil);
    }
}
