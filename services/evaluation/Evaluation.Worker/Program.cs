using Evaluation.Core;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

// PDF parsing runs in a disposable child process, not in the long-lived queue loop.
// The parent bounds wall time, working set, input bytes and output characters.
if (args is ["--extract-pdf"])
{
    try
    {
        using var bytes = new MemoryStream();
        var input = Console.OpenStandardInput();
        var buffer = new byte[81920]; int read;
        while ((read = await input.ReadAsync(buffer)) > 0)
        {
            if (bytes.Length + read > PrivateFiles.MaximumBytes) throw new InvalidDataException();
            await bytes.WriteAsync(buffer.AsMemory(0, read));
        }
        bytes.Position = 0;
        var text = await PrivateFiles.Extract(bytes, ".pdf", default);
        Console.OutputEncoding = new UTF8Encoding(false);
        await Console.Out.WriteAsync(text);
    }
    catch { Environment.ExitCode = 1; }
    return;
}

var builder = Host.CreateApplicationBuilder(args);
// The worker polls the database every 2 seconds; per-command SQL logs produced ~170 MB/day of Log Analytics ingestion.
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
builder.Services.AddEvaluation(builder.Configuration);
builder.Services.AddJudge(builder.Configuration);
builder.Services.AddSingleton<MalwareScanner>();
builder.Services.AddHostedService<EvaluationWorker>();
builder.Services.AddHostedService<JudgeRegistrationService>();
var host = builder.Build();
if (args.Contains("--initialize-database"))
{
    using var scope = host.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<EvaluationDb>().Database.MigrateAsync();
    return;
}
await host.RunAsync();

// Registers the sidecar contract at startup and about every 60 seconds. The API is ready only while it is fresh.
public sealed class JudgeRegistrationService(IServiceScopeFactory scopes, IJudgeAdapter judge, ILogger<JudgeRegistrationService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!judge.Configured)
        {
            logger.LogWarning("Judge:SidecarUrl is not configured; the judge stays unregistered and scans fail closed.");
            return;
        }
        bool? healthy = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            bool registered;
            using (var scope = scopes.CreateScope())
                registered = await JudgeRegistry.Refresh(scope.ServiceProvider.GetRequiredService<EvaluationDb>(), judge, stoppingToken);
            if (registered != healthy)
            {
                if (registered) logger.LogInformation("Judge sidecar contract registered.");
                else logger.LogWarning("Judge sidecar contract could not be fetched, validated or stored; retrying.");
                healthy = registered;
            }
            try { await Task.Delay(registered ? TimeSpan.FromSeconds(60) : TimeSpan.FromSeconds(15), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}

public sealed class EvaluationWorker(IServiceScopeFactory scopes, PrivateFiles files, MalwareScanner malware,
    IJudgeAdapter judge, ILogger<EvaluationWorker> logger) : BackgroundService
{
    public TimeSpan LeaseHeartbeat { get; init; } = JobLease.HeartbeatInterval;
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);
    private bool? scannerReady;

    // Ingestion and scans run independently so a multi-minute judge call never blocks malware scanning.
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.WhenAll(
        Loop(async ct => { if (malware.Configured) await IngestOne(ct); }, stoppingToken),
        Loop(ProcessOne, stoppingToken));

    private async Task Loop(Func<CancellationToken, Task> step, CancellationToken stoppingToken)
    {
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await step(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception)
            {
                // No exception messages: provider/scanner exceptions can contain sensitive URLs or content.
                logger.LogError("Worker cycle failed; durable leases permit recovery.");
            }
            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task IngestOne(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EvaluationDb>();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var candidate = await db.Documents.AsNoTracking().Where(x => x.Status == "Quarantined" && x.LeaseUntil < now).FirstOrDefaultAsync(ct);
        if (candidate is null) return;
        // Never claim (or reject) uploads while ClamAV is still loading signatures or unreachable.
        var ready = await malware.Ping(ct);
        if (ready != scannerReady)
        {
            if (ready) logger.LogInformation("Malware scanner answered PING; quarantined uploads will be scanned.");
            else logger.LogWarning("Malware scanner is not answering PING; uploads remain quarantined.");
            scannerReady = ready;
        }
        if (!ready) return;
        var owner = Guid.NewGuid().ToString("N");
        var updated = await db.Documents.Where(x => x.Id == candidate.Id && x.TeamId == candidate.TeamId && x.Status == "Quarantined" && x.LeaseUntil < now)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LeaseOwner, owner).SetProperty(x => x.LeaseUntil, now + 120), ct);
        if (updated == 0) return;
        string? textKey = null;
        string status; string? error = null; int length = 0;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(90));
            await using (var stream = files.Open(candidate.BlobKey)) await malware.Scan(stream, timeout.Token);
            await using var clean = files.Open(candidate.BlobKey);
            var text = Path.GetExtension(candidate.FileName).Equals(".pdf", StringComparison.OrdinalIgnoreCase)
                ? await ExtractPdf(clean, timeout.Token)
                : await PrivateFiles.Extract(clean, Path.GetExtension(candidate.FileName).ToLowerInvariant(), timeout.Token);
            length = text.Length;
            textKey = await files.StoreText(text, timeout.Token);
            status = "Ready";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (ScannerUnavailableException)
        {
            // Scanner went away mid-cycle: release the claim and keep the upload Quarantined rather than rejecting it.
            await db.Documents.Where(x => x.Id == candidate.Id && x.TeamId == candidate.TeamId && x.LeaseOwner == owner)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.LeaseOwner, (string?)null).SetProperty(x => x.LeaseUntil, 0), ct);
            scannerReady = false;
            return;
        }
        catch (Exception)
        {
            status = "Rejected";
            error = Path.GetExtension(candidate.FileName).Equals(".pdf", StringComparison.OrdinalIgnoreCase)
                ? "PDF malware scan or bounded parsing failed. Encrypted, image-only, oversized or unreadable PDFs are unsupported; use a text export."
                : "Malware scanning or bounded extraction failed. Check scanner health and supported document format, then upload a new version.";
        }

        static async Task<string> ExtractPdf(Stream content, CancellationToken ct)
        {
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Worker executable unavailable.");
            var info = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
                RedirectStandardError = true, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8
            };
            if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                info.ArgumentList.Add(typeof(EvaluationWorker).Assembly.Location);
            info.ArgumentList.Add("--extract-pdf");
            using var process = Process.Start(info) ?? throw new InvalidOperationException("PDF parser did not start.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                var output = ReadOutput();
                var write = WriteInput();
                while (!process.HasExited)
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    process.Refresh();
                    if (process.WorkingSet64 > 256L * 1024 * 1024) throw new InvalidDataException("PDF memory limit exceeded.");
                    if (output.IsFaulted) await output;
                    await Task.Delay(100, timeout.Token);
                }
                await write;
                var text = await output;
                if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("PDF parsing failed.");
                return text;
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
            async Task WriteInput()
            {
                try { await content.CopyToAsync(process.StandardInput.BaseStream, timeout.Token); }
                finally { process.StandardInput.Close(); }
            }
            async Task<string> ReadOutput()
            {
                var buffer = new char[8192]; var result = new StringBuilder(); int read;
                while ((read = await process.StandardOutput.ReadAsync(buffer.AsMemory(), timeout.Token)) > 0)
                {
                    if (result.Length + read > PrivateFiles.MaximumText + 1) throw new InvalidDataException("PDF text limit exceeded.");
                    result.Append(buffer, 0, read);
                }
                return result.ToString();
            }
        }
        var completionNow = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await using var completion = await db.Database.BeginTransactionAsync(ct);
        updated = await db.Documents.Where(x => x.Id == candidate.Id && x.TeamId == candidate.TeamId && x.LeaseOwner == owner && x.LeaseUntil > completionNow)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, status).SetProperty(x => x.Error, error)
                .SetProperty(x => x.TextKey, textKey).SetProperty(x => x.TextLength, length)
                .SetProperty(x => x.LeaseOwner, (string?)null).SetProperty(x => x.LeaseUntil, 0), ct);
        if (updated == 0 && textKey is not null) files.Delete(textKey);
        if (updated == 1)
        {
            db.Audit.Add(new AuditEvent { Id = Guid.NewGuid(), TeamId = candidate.TeamId, Actor = "worker",
                Action = $"Document{status}", Resource = candidate.Id.ToString() });
            await db.SaveChangesAsync(ct); await completion.CommitAsync(ct);
        }
    }

    private async Task ProcessOne(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EvaluationDb>();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var item = await db.Outbox.AsNoTracking().Where(x => (x.State == "Pending" || x.State == "Leased") &&
            x.AvailableAt <= now && x.LeaseUntil < now).FirstOrDefaultAsync(ct);
        if (item is null) return;
        var owner = Guid.NewGuid().ToString("N");
        if (!await JobLease.TryClaim(db, item.Id, owner, now, ct)) return;
        var scan = await db.Scans.SingleOrDefaultAsync(x => x.Id == item.ScanId && x.TeamId == item.TeamId, ct);
        if (scan is null) return; // FK normally makes this impossible; never follow a forged team key.
        if (scan.Status is "Completed" or "TechnicalFailure")
        { await JobLease.Finish(db, item.Id, owner, "Dispatched", ct); return; }
        if (scan.Status == "Evaluating" || scan.RemoteInvocationId is not null)
        {
            // A crash after invocation starts is ambiguous. Never automatically repeat a billable call.
            const string interrupted = "Invocation interrupted; operator reconciliation required before a new manual scan.";
            scan.Status = "TechnicalFailure"; scan.Error = interrupted; scan.Outcome = Decisions.Inconclusive;
            MarkInterrupted(scan, interrupted);
            await FinishWithScan(db, item.Id, owner, "DeadLetter", ct); return;
        }
        try
        {
            scan.Status = "Preparing";
            if (!await SaveWithLease(db, item.Id, owner, ct)) return;
            if (!await db.Memberships.AnyAsync(m => m.TeamId == scan.TeamId && m.Subject == scan.Requester &&
                (m.Role == TeamRole.Administrator || m.Role == TeamRole.Member), ct))
                throw new InvalidDataException("Requester no longer has scan permission.");
            var inputs = Json.Read<InputSnapshot[]>(scan.Inputs);
            var contract = Json.Read<JudgeContractSnapshot>(scan.Judge);
            var policy = Json.Read<PolicySnapshot>(scan.Policy);
            contract.Validate(); policy.Validate();
            var ids = inputs.Select(x => x.VersionId).ToArray();
            var versions = await db.Documents.Where(x => x.TeamId == scan.TeamId && x.SubmissionId == scan.SubmissionId && ids.Contains(x.Id)).ToListAsync(ct);
            if (versions.Count != ids.Length || versions.Any(v => v.Status != "Ready" || v.TextKey is null ||
                !inputs.Any(i => i.VersionId == v.Id && i.Hash == v.Hash && i.Role == v.Role && i.TextLength == v.TextLength &&
                    i.BundleId == v.BundleId && i.RelativePath == v.RelativePath)))
                throw new InvalidDataException("Input ownership or snapshot validation failed.");
            var targets = inputs.Where(x => x.Role != DocumentRole.Source).ToArray();
            var bundles = targets.Where(x => x.Role == DocumentRole.DT).Select(x => x.BundleId).Distinct().ToArray();
            var members = await db.Documents.Where(x => x.TeamId == scan.TeamId && x.SubmissionId == scan.SubmissionId && bundles.Contains(x.BundleId))
                .Select(x => new { x.Id, x.BundleId }).ToListAsync(ct);
            var artifacts = Artifacts.Group(targets, members.Select(x => (x.Id, x.BundleId)).ToArray());
            if (!judge.Configured) throw new JudgeUnavailableException();
            // Source evidence is context only: it is snapshotted with the scan but never sent to the judge.
            var payloads = new List<(ArtifactSelection Artifact, JudgeFile[] Files)>();
            foreach (var artifact in artifacts)
            {
                var artifactFiles = new List<JudgeFile>();
                foreach (var id in artifact.VersionIds)
                {
                    var version = versions.Single(v => v.Id == id);
                    var text = await files.ReadText(version.TextKey!, ct);
                    if (text.Length != version.TextLength) throw new InvalidDataException("Extracted text does not match its recorded length.");
                    artifactFiles.Add(new JudgeFile(version.RelativePath, text));
                }
                payloads.Add((artifact, artifactFiles.ToArray()));
            }
            var live = await judge.GetContract(ct);
            if (live.Version != contract.Version)
            {
                scan.Status = "TechnicalFailure"; scan.Outcome = Decisions.Inconclusive;
                scan.Error = "Judge contract changed since scan was requested; start a new scan.";
                await FinishWithScan(db, item.Id, owner, "DeadLetter", ct);
                return;
            }
            // Persist invocation-start intent before any external effect, so recovery fails closed.
            var outcomes = new List<ArtifactOutcome>();
            var invocations = new List<string>();
            scan.Status = "Evaluating";
            scan.Result = Json.Write(Progress(outcomes, payloads.Select(p => p.Artifact), policy));
            if (!await SaveWithLease(db, item.Id, owner, ct)) return;
            string? failure = null;
            using var overall = CancellationTokenSource.CreateLinkedTokenSource(ct);
            overall.CancelAfter(judge.RequestTimeout * payloads.Count + TimeSpan.FromMinutes(2));
            for (var index = 0; index < payloads.Count; index++)
            {
                var (artifact, artifactFiles) = payloads[index];
                if (failure is not null)
                {
                    outcomes.Add(Acceptance.Inconclusive(artifact, policy, "Not evaluated because an earlier judge call failed."));
                    continue;
                }
                JudgeResponse? response = null;
                var keeper = LeaseKeeper.Start(token => ExtendLease(item.Id, owner, token), LeaseHeartbeat, JobLease.Duration, overall.Token);
                try { response = await judge.Evaluate(Acceptance.Kind(artifact.Role), artifactFiles, keeper.Token); }
                catch (OperationCanceledException) when (keeper.Lost) { }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                { failure = "Scan exceeded its overall judge time budget. No score produced."; }
                catch (JudgeCallException e) { failure = e.Message; }
                finally { await keeper.DisposeAsync(); }
                if (keeper.Lost)
                {
                    // Another worker may own this job now; stop without writing anything.
                    logger.LogWarning("Scan lease lost during a judge call; stopping without overwriting.");
                    return;
                }
                if (response?.InvocationId is { Length: > 0 } invocation) invocations.Add(Acceptance.Bound(invocation, 128));
                outcomes.Add(response is null ? Acceptance.Inconclusive(artifact, policy, failure!) :
                    Acceptance.Evaluate(artifact, contract, policy, response));
                scan.RemoteInvocationId = invocations.Count == 0 ? null : Json.Write(invocations);
                scan.Result = Json.Write(Progress(outcomes, payloads.Skip(index + 1).Select(p => p.Artifact), policy));
                if (!await SaveWithLease(db, item.Id, owner, ct)) return;
            }
            var result = new ScanResult(Acceptance.Combine(outcomes), outcomes.ToArray(), failure is null ? [] : [failure]);
            scan.Result = Json.Write(result); scan.Outcome = result.Outcome;
            scan.Status = failure is null ? "Completed" : "TechnicalFailure";
            scan.Error = failure;
            await FinishWithScan(db, item.Id, owner, failure is null ? "Dispatched" : "DeadLetter", ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            scan.Outcome = Decisions.Inconclusive;
            if (scan.Status == "Evaluating" || ex is JudgeUnavailableException or InvalidDataException or ArgumentException or JsonException)
            {
                scan.Status = "TechnicalFailure";
                scan.Error = ex is JudgeUnavailableException ? "Judge integration unavailable; no model was invoked." :
                    "Evaluation could not be validated; operator reconciliation may be required. No score fabricated.";
                MarkInterrupted(scan, scan.Error);
                await FinishWithScan(db, item.Id, owner, "DeadLetter", ct);
            }
            else
            {
                // Only pre-invocation transient failures (for example the sidecar restarting) may be retried.
                var attempts = item.Attempts + 1;
                scan.Status = attempts >= 3 ? "TechnicalFailure" : "Queued";
                scan.Error = attempts >= 3 ? "Preparation retries exhausted; the judge sidecar or storage was unavailable." : "Preparation retry pending.";
                await FinishWithScan(db, item.Id, owner, attempts >= 3 ? "DeadLetter" : "Pending", ct, now + (long)Math.Pow(2, attempts) * 5);
            }
        }
    }

    private static ScanResult Progress(IEnumerable<ArtifactOutcome> done, IEnumerable<ArtifactSelection> pending, PolicySnapshot policy) =>
        new(Decisions.Pending, done.Concat(pending.Select(a => Acceptance.Pending(a, policy))).ToArray(), []);

    private static void MarkInterrupted(Scan scan, string error)
    {
        ScanResult? result = null;
        try { if (scan.Result is not null) result = Json.Read<ScanResult>(scan.Result); }
        catch (JsonException) { }
        scan.Result = result is null ? null : Json.Write(Acceptance.Interrupt(result, error));
    }

    private async Task<bool> ExtendLease(Guid id, string owner, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        return await JobLease.Extend(scope.ServiceProvider.GetRequiredService<EvaluationDb>(), id, owner, ct);
    }

    private static async Task<bool> SaveWithLease(EvaluationDb db, Guid id, string owner, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (!await JobLease.Extend(db, id, owner, ct)) return false;
        await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
        return true;
    }

    private static async Task FinishWithScan(EvaluationDb db, Guid id, string owner, string state, CancellationToken ct, long available = 0)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (!await JobLease.Finish(db, id, owner, state, ct, available)) return;
        var scan = db.ChangeTracker.Entries<Scan>().Select(e => e.Entity).SingleOrDefault();
        if (scan is not null)
            db.Audit.Add(new AuditEvent { Id = Guid.NewGuid(), TeamId = scan.TeamId, Actor = "worker",
                Action = $"Scan{state}", Resource = scan.Id.ToString() });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }
}