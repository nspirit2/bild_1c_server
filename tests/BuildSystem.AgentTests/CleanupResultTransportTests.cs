using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BuildSystem.Agent;
using BuildSystem.Contracts;
using Xunit;
namespace BuildSystem.AgentTests;
public sealed class CleanupResultTransportTests
{
    private static ReportStageCleanup Input() => new(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),1);
    private static StageCleanupResultReceipt Receipt(ReportStageCleanup r) => new(r.ResultId,r.AuthorizationId,r.ArtifactId,"DeletedReported",DateTimeOffset.UtcNow);
    private sealed class Handler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct) { Calls++; return send(request,ct); }
    }
    [Fact]
    public async Task ExactPostHasSixFieldsBearerAndFiveFieldReceipt()
    {
        var r=Input(); var receipt=Receipt(r); var h=new Handler(async (q,ct)=>{
            Assert.Equal(HttpMethod.Post,q.Method); Assert.Equal("https://example.test/api/agent/stage/cleanup-result",q.RequestUri!.AbsoluteUri);
            Assert.Equal(new string('A',64),q.Headers.Authorization!.Parameter);
            var body=await q.Content!.ReadFromJsonAsync<JsonElement>(ct);
            Assert.Equal(new[]{"artifactId","authorizationId","fencingToken","jobId","resultId","retirementId"},body.EnumerateObject().Select(p=>p.Name).Order(StringComparer.Ordinal).ToArray());
            return new(HttpStatusCode.OK){Content=JsonContent.Create(receipt)};
        });
        using var t=new AgentTransport(new("https://example.test"),new string('A',64),h);
        Assert.Equal(receipt,await t.ReportCleanupResultAsync(r,default)); Assert.Equal(1,h.Calls);
    }
    [Theory]
    [InlineData("result")] [InlineData("authorization")] [InlineData("retirement")] [InlineData("job")] [InlineData("artifact")] [InlineData("fence")]
    public async Task InvalidReportRejectedBeforeIo(string field)
    {
        var r=Input(); r=field switch {"result"=>r with{ResultId=Guid.Empty},"authorization"=>r with{AuthorizationId=Guid.Empty},"retirement"=>r with{RetirementId=Guid.Empty},"job"=>r with{JobId=Guid.Empty},"artifact"=>r with{ArtifactId=Guid.Empty},_=>r with{FencingToken=0}};
        var h=new Handler((_,_)=>throw new Exception("Must not send")); using var t=new AgentTransport(new("https://example.test"),new string('A',64),h);
        await Assert.ThrowsAsync<ArgumentException>(()=>t.ReportCleanupResultAsync(r,default)); Assert.Equal(0,h.Calls);
    }
    [Theory]
    [InlineData(201)] [InlineData(204)] [InlineData(302)] [InlineData(400)] [InlineData(401)] [InlineData(409)] [InlineData(500)]
    public async Task Only200AcceptedAndErrorBodyNeverExposed(int status)
    {
        var h=new Handler((_,_)=>Task.FromResult(new HttpResponseMessage((HttpStatusCode)status){Content=new StringContent("PRIVATE-PATH-TOKEN")}));
        using var t=new AgentTransport(new("https://example.test"),new string('A',64),h);
        var e=await Assert.ThrowsAsync<HttpRequestException>(()=>t.ReportCleanupResultAsync(Input(),default));
        Assert.Equal((HttpStatusCode)status,e.StatusCode); Assert.DoesNotContain("PRIVATE",e.ToString()); Assert.Equal(1,h.Calls);
    }
    [Theory]
    [InlineData("empty")] [InlineData("null")] [InlineData("broken")] [InlineData("unknown")] [InlineData("oversize")]
    public async Task MalformedUnknownAndOversizedReceiptBecomeStableMismatch(string mode)
    {
        var r=Input(); var json=JsonSerializer.Serialize(Receipt(r),new JsonSerializerOptions(JsonSerializerDefaults.Web));
        json=mode switch {"empty"=>"","null"=>"null","broken"=>"{ PRIVATE-PATH-TOKEN", "unknown"=>json[..^1]+",\"path\":\"PRIVATE-PATH-TOKEN\"}",_=>new string(' ',16*1024+1)+json};
        var h=new Handler((_,_)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(json,Encoding.UTF8,"application/json")}));
        using var t=new AgentTransport(new("https://example.test"),new string('A',64),h);
        var e=await Assert.ThrowsAsync<CleanupResultReceiptException>(()=>t.ReportCleanupResultAsync(r,default)); Assert.Equal("ReceiptMismatch",e.Code); Assert.DoesNotContain("PRIVATE",e.ToString());
    }
    [Theory]
    [InlineData("result")] [InlineData("authorization")] [InlineData("artifact")] [InlineData("state")] [InlineData("date")]
    public async Task ReceiptIdentityAndStateChecked(string field)
    {
        var r=Input();var a=Receipt(r);a=field switch{"result"=>a with{ResultId=Guid.NewGuid()},"authorization"=>a with{AuthorizationId=Guid.NewGuid()},"artifact"=>a with{ArtifactId=Guid.NewGuid()},"state"=>a with{State="DeletedLocal"},_=>a with{ReceivedAt=default}};
        var h=new Handler((_,_)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=JsonContent.Create(a)}));using var t=new AgentTransport(new("https://example.test"),new string('A',64),h);
        await Assert.ThrowsAsync<CleanupResultReceiptException>(()=>t.ReportCleanupResultAsync(r,default));
    }
    private sealed class InfiniteReceiptStream : Stream
    {
        public int BytesRead;
        public override bool CanRead=>true; public override bool CanSeek=>false; public override bool CanWrite=>false;
        public override long Length=>throw new NotSupportedException(); public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
        public override int Read(byte[] b,int o,int n){Array.Fill(b,(byte)' ',o,n);BytesRead+=n;return n;}
        public override ValueTask<int> ReadAsync(Memory<byte> b,CancellationToken ct=default){ct.ThrowIfCancellationRequested();b.Span.Fill((byte)' ');BytesRead+=b.Length;return ValueTask.FromResult(b.Length);}
        public override void Flush()=>throw new NotSupportedException();public override long Seek(long o,SeekOrigin s)=>throw new NotSupportedException();public override void SetLength(long n)=>throw new NotSupportedException();public override void Write(byte[] b,int o,int n)=>throw new NotSupportedException();
    }
    [Fact]
    public async Task StreamingReceiptIsBoundedEvenWhenContentLengthLies()
    {
        var stream=new InfiniteReceiptStream();var content=new StreamContent(stream);content.Headers.ContentLength=1;
        var h=new Handler((_,_)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=content}));using var t=new AgentTransport(new("https://example.test"),new string('A',64),h);
        await Assert.ThrowsAsync<CleanupResultReceiptException>(()=>t.ReportCleanupResultAsync(Input(),default));
        Assert.Equal(16*1024+1,stream.BytesRead);
    }
    [Theory]
    [InlineData(409,"CleanupResultMismatch",true)] [InlineData(400,"InvalidCleanupResult",true)]
    [InlineData(409,"CleanupResultDisabled",false)] [InlineData(409,"Unknown",false)]
    [InlineData(400,"CleanupResultMismatch",false)] [InlineData(409,"InvalidCleanupResult",false)]
    public async Task OnlyKnownCodeAndMatchingStatusAreDefinitive(int status,string code,bool definitive)
    {
        var h=new Handler((_,_)=>Task.FromResult(new HttpResponseMessage((HttpStatusCode)status){Content=JsonContent.Create(new{code})}));
        using var t=new AgentTransport(new("https://example.test"),new string('A',64),h);
        var e=await Assert.ThrowsAnyAsync<HttpRequestException>(()=>t.ReportCleanupResultAsync(Input(),default));
        Assert.Equal(definitive,e is CleanupResultConflictException or CleanupResultInvalidException);Assert.DoesNotContain(code,e.Message);
    }
    [Theory]
    [InlineData("broken")] [InlineData("oversize")] [InlineData("extra")] [InlineData("duplicate")]
    public async Task UntrustedErrorPayloadNeverCreatesDefinitiveConflict(string mode)
    {
        var json=mode switch {"broken"=>"{PRIVATE", "oversize"=>new string(' ',16385)+"{\"code\":\"CleanupResultMismatch\"}","extra"=>"{\"code\":\"CleanupResultMismatch\",\"path\":\"PRIVATE\"}",_=>"{\"code\":\"CleanupResultMismatch\",\"code\":\"CleanupResultMismatch\"}"};
        var h=new Handler((_,_)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict){Content=new StringContent(json)}));using var t=new AgentTransport(new("https://example.test"),new string('A',64),h);
        var e=await Assert.ThrowsAsync<HttpRequestException>(()=>t.ReportCleanupResultAsync(Input(),default));Assert.DoesNotContain("PRIVATE",e.ToString());
    }
    [Fact]
    public async Task CallerCancellationPropagatesWithoutReceiptMismatch()
    {
        var h=new Handler(async (_,ct)=>{await Task.Delay(Timeout.Infinite,ct);return new(HttpStatusCode.OK);});using var t=new AgentTransport(new("https://example.test"),new string('A',64),h);
        using var c=new CancellationTokenSource();c.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>t.ReportCleanupResultAsync(Input(),c.Token));
    }
}
// end
