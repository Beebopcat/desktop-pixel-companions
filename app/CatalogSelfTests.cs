using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using AzerothCompanion.Core;
using AzerothCompanion.Services;

namespace AzerothCompanion;

/// <summary>Offline catalog, routing and UI checks. No native playback, capture or inference.</summary>
internal static class CatalogSelfTests
{
    internal static readonly (string Id,string Label)[] Expected = [
        ("qn_private_jinx_separated","Jinx"), ("qn_private_jaina_separated","Jaina"),
        ("qn_private_silco_separated","Silco"), ("qn_private_vi_separated","Vi"),
        ("qn_private_rowan_moss_review","Rowan"), ("qn_private_palpatine_moss_review","Senator Palpatine"),
        ("qn_private_sylvanas_moss_review","Sylvanas (whisper/asmr version)"),
        ("qn_private_jaina_moss_review","Jaina (flat voice/no emotion)"),
        ("qn_lorath_refined","Lorath"), ("qn_private_anime_leader_girl","Anime Leader Girl Voice"),
        ("qn_source_narrator","Lorath - Alternate"), ("qn_private_thrall","Thrall"),
        ("qn_private_sylvanas","Sylvanas"), ("qn_private_matthew_mcconaughey","Matthew McConaughey"),
        ("qn_private_jaina","Elowen Starfall"), ("af_heart","Heart"), ("af_bella","Bella"), ("af_nicole","Nicole"),
        ("bf_emma","Emma (British English)"), ("am_michael","Michael (US male)"), ("am_fenrir","Fenrir (US male)"),
        ("am_puck","Puck (US male)"), ("bm_george","George (UK male)"), ("bm_fable","Fable (UK male)"),
        ("bm_lewis","Lewis (UK male)"), ("bm_daniel","Daniel (UK male)"), ("am_onyx","Onyx (US male)"),
        ("am_adam","Adam (US male)"), ("af_sarah","Sarah (US English)"), ("af_kore","Kore (US English)"),
        ("qn_high_king","High King - Warcraft original"), ("qn_iron_veteran","Iron Veteran - Warcraft original"),
        ("qn_star_chronicler","Star Chronicler - FFXIV original"),
        ("qn_private_palpatine","Senator Palpatine - reference only; clone quality unverified"),
        ("qn_private_vi","Vi - review candidate"), ("qn_private_silco","Silco - review candidate")
    ];
    private const string Pin="aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    internal static async Task<object> ReadLiveCatalogAsync()
    {
        var narrator=new NarrationClient();var catalog=await narrator.GetVoiceCatalogAsync();
        Check(catalog.Select(voice=>(voice.Id,voice.Label)).SequenceEqual(Expected),"Live catalog matches all 36 identities and exact labels");
        Check(narrator.CatalogVersion=="20261004.1"&&catalog.Count(voice=>voice.PrivateLocalOnly)==16,"Live canonical version and private pins");
        MainWindow? main=null;
        try
        {
            main=new MainWindow(null,narrator);await main.FixtureRefreshVoicesAsync();
            var primary=Choices(main.PrimaryVoicePicker.ItemsSource).Where(item=>Expected.Any(voice=>voice.Id==item.Item1)).ToArray();
            var partner=Choices(main.PartnerVoicePicker.ItemsSource).Where(item=>Expected.Any(voice=>voice.Id==item.Item1)).ToArray();
            Check(primary.SequenceEqual(Expected)&&partner.SequenceEqual(Expected),"Live WPF selectors share the daemon order");
        }
        finally{main?.Close();}
        return new{schema=1,timeUtc=DateTimeOffset.UtcNow,allPassed=true,catalogVersion=narrator.CatalogVersion,voiceCount=catalog.Count,
            source="live-loopback-catalog",order=catalog.Select((voice,index)=>new{position=index+1,voice.Id,voice.Label,voice.Engine,
                voice.ReferenceSha256,voice.ReferenceTextSha256,voice.SourceSha256}),renderCalls=0,speakerPlayback=false,
            capture=false,microphone=false,gpuInference=false,paidCalls=false};
    }
    internal static async Task<object> RunAsync()
    {
        var checks=new List<string>();
        using var handler=new CatalogHandler();using var http=new HttpClient(handler);
        var narrator=new NarrationClient(http, "");
        var catalog=await narrator.GetVoiceCatalogAsync();
        Check(catalog.Select(v=>(v.Id,v.Label)).SequenceEqual(Expected),"Exact 36-voice catalog order/labels");
        Check(narrator.CatalogVersion=="20261004.1", "Catalog version");
        Check(catalog.Count(v=>v.PrivateLocalOnly)==16, "16 private identities available without the previous eight-profile limit");
        Check(catalog.Count(v=>v.PrivateLocalOnly&&v.UserApproved==false)==3, "Retained candidates are not silently filtered");
        checks.Add("All 36 exact names and stable IDs are accepted in canonical order, including 16 private profiles and retained candidates.");
        foreach(var voice in catalog)
        {
            var audio=await narrator.RenderAsync("Generated offline route fixture.",voice.Id);
            Check(audio?.ProfileId==voice.Id&&audio.SampleRate==24000&&audio.Channels==1,"Exact voice route");
        }
        Check(handler.RenderedIds.SequenceEqual(Expected.Select(v=>v.Id)),"All routes preserve voice identity");
        Check(handler.PostRoutes.All(route=>route=="/render-voice"), "Canonical dispatcher only");
        checks.Add("All 36 voices route through /render-voice with identity, seed, source/reference hashes and finite 24 kHz mono PCM validation.");
        var before=handler.RenderedIds.Count;
        await RejectAsync(()=>narrator.RenderAsync("Unknown fixture.","qn_unregistered"));
        Check(handler.RenderedIds.Count==before,"Unknown voice cannot reach inference");
        checks.Add("An unknown voice is rejected before rendering, without voice substitution.");
        handler.DuplicateLabel=true;
        await RejectAsync(()=>narrator.GetVoiceCatalogAsync());handler.DuplicateLabel=false;
        checks.Add("Case-insensitive display-name collisions are rejected before replacing choices.");
        handler.BadSourceHash=true;
        await RejectAsync(()=>narrator.RenderAsync("Mismatch fixture.",Expected[0].Id));handler.BadSourceHash=false;
        handler.BadEngine=true;
        await RejectAsync(()=>narrator.RenderAsync("Mismatch fixture.",Expected[0].Id));handler.BadEngine=false;
        checks.Add("Mismatched source hash or render engine rejects PCM before playback handoff.");
        handler.HoldNext=true;
        var stale=narrator.RenderAsync("Stale fixture.",Expected[0].Id);
        await handler.Received.Task.WaitAsync(TimeSpan.FromSeconds(5));narrator.CancelPending();handler.Release.TrySetResult();
        Check(await stale is null,"Cancelled generation drops stale PCM");
        checks.Add("CancelPending discards a late render while retaining serial ownership until its response arrives.");
        var preserved=await narrator.RenderLorathAsync("Approved legacy fixture.");
        Check(preserved?.ProfileId==NarrationClient.ApprovedProfile&&handler.PostRoutes.Last()=="/render-background", "Lorath B compatibility route");
        checks.Add("Legacy Lorath B remains on /render-background with seed 93019.");

        var queue=new DialogueSpeechQueue();var now=DateTimeOffset.UtcNow;
        var observation=GameTextObservation.FromLines(GameTextSource.VerifiedHook,[new("Literal fixture.",null,1)],now,
            GameTextExactness.ExportedText,"Offline fixture") with{IsEventStream=true,SourceInstanceId="fixture-1"};
        var routes=DialogueVoiceRouter.Route(observation,new Dictionary<string,string>(),Expected[0].Id);
        Check(routes.Single().VoiceId==Expected[0].Id,"Selected narrator default");
        Check(queue.Accept(observation,routes,false,now).TextOnly==1&&
            queue.Accept(observation,routes,true,now).Duplicates==1&&!queue.HasPending,"Muted events never replay");
        var second=observation with{Lines=[new("Next literal.",null,2)],Text="Next literal."};
        var secondRoutes=DialogueVoiceRouter.Route(second,new Dictionary<string,string>(),Expected[1].Id);
        queue.Accept(second,secondRoutes,true,now);Check(queue.Cancel("Voice changed")==1&&!queue.HasPending,"Voice-change backlog cancellation");
        checks.Add("Primary selected voice narrates by default; muted events do not replay, and voice changes cancel queued dialogue.");

        MainWindow? main=null;
        try
        {
            main=new MainWindow(null,narrator);
            var prefs=Field<Preferences>(main,"preferences");prefs.CharacterVoices.Clear();
            await main.FixtureRefreshVoicesAsync();
            Check(Choices(main.PrimaryVoicePicker.ItemsSource).SequenceEqual(Expected),"Primary UI catalog");
            Check(Choices(main.PartnerVoicePicker.ItemsSource).SequenceEqual(Expected),"Partner UI catalog");
            Check(main.Title=="Desktop Pixel Companions","Product title");
            checks.Add("Both WPF voice selectors expose the same exact 36 entries and product title.");
            prefs.CharacterVoices["jinx"]="af_heart";await main.FixtureRefreshVoicesAsync();
            Check(main.FixtureVoiceFor("jinx")=="af_heart","Explicit saved assignments preserved");
            Check(main.FixtureVoiceFor("jaina")=="qn_private_jaina_separated"&&
                main.FixtureVoiceFor("rowan")=="qn_private_rowan_moss_review","Updated defaults");
            checks.Add("Saved explicit IDs survive catalog refresh; unassigned characters use the updated defaults.");
            var generation=Field<long>(main,"generation");
            main.PrimaryVoicePicker.SelectedValue="qn_private_jinx_separated";
            Check(Field<long>(main,"generation")>generation,"Voice picker cancels owned generation");
            main.FixtureSetSpeaker(Characters.All.First(p=>p.Id!=Field<CharacterProfile>(main,"character").Id).Id);
            Invoke(main,"SetVisual","speaking",null);
            Check(Field<string>(main.MainPortrait,"state")=="idle","Inactive primary never speaks for partner");
            main.FixtureSetSpeaker(null);Invoke(main,"SetVisual","speaking",null);
            Check(Field<string>(main.MainPortrait,"state")=="speaking","Primary owns narrator visual");
            main.FixtureQuiet();var rendered=handler.RenderedIds.Count;
            await main.FixtureSpeakAsync("Muted UI fixture.");
            Check(handler.RenderedIds.Count==rendered&&main.SpeechCheck.IsChecked!=true,"Quiet never renders or plays");
            checks.Add("Actual voice-picker events cancel pending activity; partner speech leaves primary idle, and Quiet blocks rendering.");
        }
        finally{main?.Close();}
        return new{schema=1,timeUtc=DateTimeOffset.UtcNow,allPassed=true,checks,voiceCount=catalog.Count,
            catalogVersion=narrator.CatalogVersion,expectedOrder=Expected.Select((v,i)=>new{position=i+1,id=v.Id,label=v.Label}),
            mockTransportOnly=true,speakerPlayback=false,capture=false,microphone=false,gpuInference=false,paidCalls=false,
            fullAnimationVisualRunsVerified=0,realGameDialogueProfilesVerified=0};
    }
    private static IEnumerable<(string,string)> Choices(System.Collections.IEnumerable source)=>source.Cast<object>().Select(v=>
        ((string)v.GetType().GetProperty("Id")!.GetValue(v)!, (string)v.GetType().GetProperty("Label")!.GetValue(v)!));
    private static T Field<T>(object value,string name)=>(T)value.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(value)!;
    private static void Invoke(object value,string name,params object?[] args)=>value.GetType().GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(value,args);
    private static void Check(bool condition,string name){if(!condition)throw new InvalidOperationException("Catalog self-test failed: "+name);}
    private static async Task RejectAsync(Func<Task> action){try{await action();}catch(CompanionServiceException){return;}throw new InvalidOperationException("Expected rejection.");}
    private sealed class CatalogHandler:HttpMessageHandler
    {
        internal List<string> RenderedIds {get;}=[];
        internal List<string> PostRoutes {get;}=[];
        internal bool DuplicateLabel,BadSourceHash,BadEngine,HoldNext;
        internal TaskCompletionSource Received {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        private static Dictionary<string,object?> Metadata((string Id,string Label) voice)
        {
            var privateOnly=voice.Id.StartsWith("qn_private_",StringComparison.Ordinal);
            var engine=voice.Id.StartsWith("qn_",StringComparison.Ordinal)?"qwen3-tts":"kokoro";
            var row=new Dictionary<string,object?>{{"id",voice.Id},{"label",voice.Label},{"lang","en-us"},{"engine",engine},
                {"renderRoute","/render-voice"},{"renderOnly",true},{"privateLocalOnly",privateOnly},{"publicPublicationAllowed",!privateOnly},
                {"userApproved",voice.Id is not ("qn_private_anime_leader_girl" or "qn_private_vi" or "qn_private_silco")}};
            if(engine!="kokoro")row["seed"]=voice.Id==NarrationClient.ApprovedProfile?NarrationClient.ApprovedSeed:42;
            if(privateOnly){row["referenceSha256"]=Pin;row["referenceTextSha256"]=Pin;row["sourceSha256"]=Pin;}
            return row;
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            var path=request.RequestUri!.AbsolutePath;
            if(path=="/voices")
            {
                var rows=Expected.Select(Metadata).ToArray();if(DuplicateLabel)rows[1]["label"]="jinx";
                return Json(new{ok=true,catalogVersion="20261004.1",voices=rows});
            }
            if(path=="/status")return Json(new{ready=true,state="idle"});
            Check(request.Method==HttpMethod.Post&&path is "/render-voice" or "/render-background","Render-only endpoint");
            using var body=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var id=body.RootElement.GetProperty("voice").GetString()!;var voice=Expected.First(v=>v.Id==id);
            RenderedIds.Add(id);PostRoutes.Add(path);
            if(HoldNext){HoldNext=false;Received.TrySetResult();await Release.Task;}
            var answer=Metadata(voice);answer["completed"]=true;answer["requestId"]=body.RootElement.GetProperty("requestId").GetString();
            answer["profileId"]=id;answer["sampleRate"]=24000;answer["channels"]=1;answer["dtype"]="float32-le";answer["frames"]=1;
            answer["pcm"]=Convert.ToBase64String(BitConverter.GetBytes(0.25f));
            if(BadSourceHash)answer["sourceSha256"]=new string('b',64);if(BadEngine)answer["engine"]="unknown";
            return Json(answer);
        }
        private static HttpResponseMessage Json(object value)=>new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(value),Encoding.UTF8,"application/json")};
    }
}
