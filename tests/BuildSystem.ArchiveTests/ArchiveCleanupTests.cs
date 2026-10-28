using System.Security.Cryptography;
using System.Text;
using BuildSystem.Agent.Archives;
using Xunit;
namespace BuildSystem.ArchiveTests;
public sealed class ArchiveCleanupTests
{
    private sealed class Fixture : IDisposable
    {
        public string Home { get; } = Path.Combine(Path.GetTempPath(), "cleanup-test-"+Guid.NewGuid().ToString("N"));
        public string Source => Path.Combine(Home,"source"); public string Staging => Path.Combine(Home,"stage");
        public Guid Root { get; } = Guid.NewGuid();
        public StageRequest Stage { get; private set; } = null!;
        public ArchiveCleanupRequest Cleanup { get; private set; } = null!;
        public string Artifact => Path.Combine(Staging,Cleanup.ArtifactId.ToString("N"));
        public ArchiveStaging Engine() => new([new(Root,Source,true)],Staging,1024*1024,1024*1024);
        public async Task Initialize()
        {
            Directory.CreateDirectory(Source); await File.WriteAllTextAsync(Path.Combine(Source,"sample.dt"),"SYNTHETIC NOT REAL DT "+new string('X',4096));
            var engine=Engine(); var fp=await engine.FingerprintAsync(Root,"sample.dt");
            Stage=new(Guid.NewGuid(),Guid.NewGuid(),fp); var artifact=await engine.StageAsync(Stage);
            Cleanup=new(Guid.NewGuid(),Guid.ParseExact(artifact.ArtifactId,"N"),Stage.ReservationId,Stage.VersionId,Root,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("sample.dt"))).ToLowerInvariant(),fp.Sha256,fp.Size,fp.Format);
        }
        public void Dispose(){if(Directory.Exists(Home))Directory.Delete(Home,true);}
    }
    [Fact]
    public async Task Valid_cleanup_is_idempotent_source_untouched_and_identity_cannot_be_restaged()
    {
        using var f=new Fixture();await f.Initialize();var source=await File.ReadAllBytesAsync(Path.Combine(f.Source,"sample.dt"));
        await f.Engine().CleanupAsync(f.Cleanup);await f.Engine().CleanupAsync(f.Cleanup);
        Assert.False(Directory.Exists(f.Artifact));Assert.Equal(source,await File.ReadAllBytesAsync(Path.Combine(f.Source,"sample.dt")));
        Assert.True(File.Exists(Path.Combine(f.Staging,".retired-"+f.Cleanup.ArtifactId.ToString("N")+".json")));
        Assert.True(File.Exists(Path.Combine(f.Staging,".deleted-"+f.Cleanup.ArtifactId.ToString("N")+".json")));
        await Assert.ThrowsAsync<ArchiveException>(()=>f.Engine().StageAsync(f.Stage));
    }
    [Theory]
    [InlineData("before-intent")][InlineData("after-intent")][InlineData("after-data")][InlineData("after-manifest")][InlineData("after-directory")][InlineData("after-completed")]
    public async Task Crash_checkpoint_reopen_resumes_only_exact_identity(string phase)
    {
        using var f=new Fixture();await f.Initialize();var e=f.Engine();e.CleanupCheckpoint=p=>{if(p==phase)throw new IOException("Injected crash");};
        await Assert.ThrowsAsync<IOException>(()=>e.CleanupAsync(f.Cleanup));
        await f.Engine().CleanupAsync(f.Cleanup);await f.Engine().CleanupAsync(f.Cleanup);
        Assert.False(Directory.Exists(f.Artifact));Assert.True(File.Exists(Path.Combine(f.Source,"sample.dt")));
    }
    [Fact]
    public async Task Missing_original_without_intent_is_not_success()
    {
        using var f=new Fixture();await f.Initialize();Directory.Delete(f.Artifact,true);
        await Assert.ThrowsAsync<ArchiveException>(()=>f.Engine().CleanupAsync(f.Cleanup));
    }
    [Fact]
    public async Task Wrong_manifest_fingerprint_does_not_publish_intent_or_delete()
    {
        using var f=new Fixture();await f.Initialize();
        await Assert.ThrowsAsync<ArchiveException>(()=>f.Engine().CleanupAsync(f.Cleanup with{RootId=Guid.NewGuid()}));
        await Assert.ThrowsAsync<ArchiveException>(()=>f.Engine().CleanupAsync(f.Cleanup with{SourceNameHash=new string('0',64)}));
        Assert.True(File.Exists(Path.Combine(f.Artifact,"data")));Assert.Empty(Directory.GetFiles(f.Staging,".retired-*"));
    }
    [Fact]
    public async Task Data_tamper_blocks_before_any_delete()
    {
        using var f=new Fixture();await f.Initialize();await File.WriteAllTextAsync(Path.Combine(f.Artifact,"data"),"tampered");
        await Assert.ThrowsAsync<ArchiveException>(()=>f.Engine().CleanupAsync(f.Cleanup));Assert.True(File.Exists(Path.Combine(f.Artifact,"manifest.json")));
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task Extra_file_or_directory_is_never_removed(bool directory)
    {
        using var f=new Fixture();await f.Initialize();var extra=Path.Combine(f.Artifact,"extra");if(directory)Directory.CreateDirectory(extra);else await File.WriteAllTextAsync(extra,"foreign");
        await Assert.ThrowsAsync<ArchiveException>(()=>f.Engine().CleanupAsync(f.Cleanup));Assert.True(File.Exists(Path.Combine(f.Artifact,"data")));
        Assert.True(directory?Directory.Exists(extra):File.Exists(extra));
    }
    [Fact]
    public async Task Rebound_authorization_replay_is_rejected()
    {
        using var f=new Fixture();await f.Initialize();var e=f.Engine();e.CleanupCheckpoint=p=>{if(p=="after-intent")throw new IOException();};
        await Assert.ThrowsAsync<IOException>(()=>e.CleanupAsync(f.Cleanup));
        await Assert.ThrowsAsync<ArchiveException>(()=>f.Engine().CleanupAsync(f.Cleanup with{AuthorizationId=Guid.NewGuid()}));
        Assert.True(File.Exists(Path.Combine(f.Artifact,"data")));
    }
    [Fact]
    public async Task Completed_marker_never_deletes_resurrected_directory()
    {
        using var f=new Fixture();await f.Initialize();await f.Engine().CleanupAsync(f.Cleanup);Directory.CreateDirectory(f.Artifact);await File.WriteAllTextAsync(Path.Combine(f.Artifact,"data"),"future");
        await Assert.ThrowsAsync<ArchiveException>(()=>f.Engine().CleanupAsync(f.Cleanup));Assert.Equal("future",await File.ReadAllTextAsync(Path.Combine(f.Artifact,"data")));
    }
    [Fact]
    public async Task Cancellation_after_intent_is_recoverable()
    {
        using var f=new Fixture();await f.Initialize();using var cts=new CancellationTokenSource();var e=f.Engine();e.CleanupCheckpoint=p=>{if(p=="after-intent")cts.Cancel();};
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>e.CleanupAsync(f.Cleanup,cts.Token));
        await f.Engine().CleanupAsync(f.Cleanup);Assert.False(Directory.Exists(f.Artifact));
    }
    [Fact]
    public async Task Invalid_request_identity_never_touches_artifact()
    {
        using var f=new Fixture();await f.Initialize();
        await Assert.ThrowsAsync<ArgumentException>(()=>f.Engine().CleanupAsync(f.Cleanup with{ArtifactId=Guid.NewGuid()}));
        await Assert.ThrowsAsync<ArgumentException>(()=>f.Engine().CleanupAsync(f.Cleanup with{AuthorizationId=Guid.Empty}));
        Assert.True(File.Exists(Path.Combine(f.Artifact,"data")));
    }
    [Fact]
    public async Task Interrupted_marker_publication_blocks_cleanup_and_restage()
    {
        using var f=new Fixture();await f.Initialize();await File.WriteAllTextAsync(Path.Combine(f.Staging,".retired-"+f.Cleanup.ArtifactId.ToString("N")+".json.pending-orphan"),"partial");
        await Assert.ThrowsAsync<ArchiveException>(()=>f.Engine().CleanupAsync(f.Cleanup));await Assert.ThrowsAsync<ArchiveException>(()=>f.Engine().StageAsync(f.Stage));
        Assert.True(File.Exists(Path.Combine(f.Artifact,"data")));
    }
    [Fact]
    public async Task Malformed_permanent_marker_blocks_cleanup_and_restage()
    {
        using var f=new Fixture();await f.Initialize();await File.WriteAllTextAsync(Path.Combine(f.Staging,".retired-"+f.Cleanup.ArtifactId.ToString("N")+".json"),"{bad");
        await Assert.ThrowsAsync<ArchiveException>(()=>f.Engine().CleanupAsync(f.Cleanup));await Assert.ThrowsAsync<ArchiveException>(()=>f.Engine().StageAsync(f.Stage));
        Assert.True(File.Exists(Path.Combine(f.Artifact,"data")));
    }
    [Fact]
    public async Task Remaining_manifest_tamper_after_intent_is_not_ignored()
    {
        using var f=new Fixture();await f.Initialize();var engine=f.Engine();engine.CleanupCheckpoint=p=>{if(p=="after-data")throw new IOException();};
        await Assert.ThrowsAsync<IOException>(()=>engine.CleanupAsync(f.Cleanup));
        await File.WriteAllTextAsync(Path.Combine(f.Artifact,"manifest.json"),"{}");
        await Assert.ThrowsAsync<ArchiveException>(()=>f.Engine().CleanupAsync(f.Cleanup));Assert.True(Directory.Exists(f.Artifact));
    }
    [Fact]
    public async Task Dangling_artifact_symlink_after_intent_is_not_treated_as_deleted()
    {
        if(OperatingSystem.IsWindows())return; // Requires environment-specific symlink privilege on Windows.
        using var f=new Fixture();await f.Initialize();var engine=f.Engine();engine.CleanupCheckpoint=p=>{if(p=="after-intent")throw new IOException();};
        await Assert.ThrowsAsync<IOException>(()=>engine.CleanupAsync(f.Cleanup));
        Directory.Delete(f.Artifact,true);Directory.CreateSymbolicLink(f.Artifact,Path.Combine(f.Home,"missing-target"));
        try
        {
            await Assert.ThrowsAsync<ArchiveException>(()=>f.Engine().CleanupAsync(f.Cleanup));
            Assert.False(File.Exists(Path.Combine(f.Staging,".deleted-"+f.Cleanup.ArtifactId.ToString("N")+".json")));
        }
        finally { File.Delete(f.Artifact); }
    }
    [Fact]
    public async Task Reparse_data_is_rejected_without_touching_target()
    {
        if(OperatingSystem.IsWindows())return; // Junction/symlink privilege policy is deployment-specific on Windows.
        using var f=new Fixture();await f.Initialize();var data=Path.Combine(f.Artifact,"data");File.Delete(data);var target=Path.Combine(f.Home,"foreign");await File.WriteAllTextAsync(target,"untouched");File.CreateSymbolicLink(data,target);
        await Assert.ThrowsAsync<ArchiveException>(()=>f.Engine().CleanupAsync(f.Cleanup));Assert.Equal("untouched",await File.ReadAllTextAsync(target));
    }
}
// end
