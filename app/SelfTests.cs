using System.Text.Json;
using AzerothCompanion.Core;
using AzerothCompanion.Services;
namespace AzerothCompanion;
public static class SelfTests
{
    public static async Task<int> RunAsync()
    {
        var passed=new List<string>();var errors=new List<string>();
        void check(string name,Func<bool> test){try{if(!test())throw new Exception("Assertion failed");passed.Add(name);}catch(Exception ex){errors.Add(name+": "+ex.Message);}}
        check("Eight requested characters retain distinct identities",()=>Characters.All.Length==8&&Characters.All.Select(x=>x.Id).ToHashSet().SetEquals(new[]{"sylvanas","jaina","rowan","thrall","palpatine","vi","silco","jinx"}));
        check("One and two avatars stay within 4.5 percent physical display area",()=>{foreach(var size in new[]{(1280,720),(1920,1080),(2560,1440),(3840,2160),(1080,1920),(5120,1440)})foreach(var duo in new[]{false,true})foreach(var fraction in new[]{.015,.04,.045,1.0,double.NaN}){var p=OverlaySizing.Calculate(size.Item1,size.Item2,duo,fraction);if((double)p.Width*p.Height>(double)size.Item1*size.Item2*.045)return false;}return true;});
        check("Session context is bounded and clears without persistence",()=>{var c=new SessionConversation();for(int i=0;i<20;i++)c.Add("question "+i,new string('x',5000));var text=c.Context();if(text.Length>8400||text.Contains("question 0\n")||!text.Contains("question 19"))return false;c.Clear();return c.Context().Length==0;});
        check("Microphone epoch rejects old completion after cancel or new hold",()=>{var epochs=new SpeechRecordingEpoch();var first=epochs.Next();if(!epochs.IsCurrent(first))return false;epochs.Next();var second=epochs.Next();return !epochs.IsCurrent(first)&&epochs.IsCurrent(second)&&!epochs.IsCurrent(0);});
        check("OCR waits for stable text and suppresses repeats",()=>{var p=new ObservationPolicy();var now=DateTimeOffset.UtcNow;return p.Observe("A quest awaits",now)==null&&p.Observe(" A  quest awaits ",now.AddMilliseconds(400))==null&&p.Observe("A quest awaits",now.AddSeconds(2))!=null&&p.Observe("A quest awaits",now.AddSeconds(3))==null;});
        check("OCR changing text waits again",()=>{var p=new ObservationPolicy();var now=DateTimeOffset.UtcNow;p.Observe("One quest",now);p.Observe("Another quest",now.AddSeconds(2));return p.Observe("Another quest",now.AddSeconds(2.2))==null;});
        check("Session prevents unsolicited speech when quiet or busy",()=>{var s=new SessionPolicy{Running=true};var n=DateTimeOffset.UtcNow;if(!s.CanBanter(n))return false;s.DialogueOrCombat=true;if(s.CanBanter(n))return false;s.DialogueOrCombat=false;s.Quiet=true;return !s.CanBanter(n);});
        check("Banter respects 90 second cooldown",()=>{var n=DateTimeOffset.UtcNow;var s=new SessionPolicy{Running=true,LastBanter=n};return !s.CanBanter(n.AddSeconds(89))&&s.CanBanter(n.AddSeconds(91));});
        check("Cloud requires active session and explicit consent",()=>{var s=new SessionPolicy();if(s.AllowCloud(true,true))return false;s.Running=true;return !s.AllowCloud(true,false)&&s.AllowCloud(true,true);});
        check("Speech chunks are bounded with complete content",()=>{var text=string.Join(" ",Enumerable.Repeat("Keep the current voice safe.",100));var chunks=MainWindow.SplitForSpeech(text).ToArray();return chunks.All(x=>x.Length<=500)&&string.Join(" ",chunks)==text;});
        check("Crop rejects full-window overflow",()=>{var f=new CapturedFrame(2,2,new byte[16],DateTimeOffset.UtcNow,"test");try{f.Crop(new NormalizedRegion(.5,0,1,1));return false;}catch(ArgumentOutOfRangeException){return true;}});
        check("Crop preserves selected pixel boundaries",()=>{var pixels=Enumerable.Range(0,64).Select(x=>(byte)x).ToArray();var f=new CapturedFrame(4,4,pixels,DateTimeOffset.UtcNow,"test");var c=f.Crop(new(.25,.25,.5,.5));return c.Width==2&&c.Height==2&&c.BgraPixels[0]==20&&c.BgraPixels[^1]==43;});
        try{var type=typeof(SelfTests).Assembly.GetType("AzerothCompanion.Services.BackendSelfTests");var method=type?.GetMethod("RunAsync");if(method!=null){var task=(Task)method.Invoke(null,null)!;await task;var result=task.GetType().GetProperty("Result")?.GetValue(task);if(result is IEnumerable<string> items)passed.AddRange(items);else passed.Add("Backend self-tests completed");}else errors.Add("Backend self-tests missing");}catch(Exception ex){errors.Add("Backend: "+ex.GetBaseException().Message);}
        var directory=Path.Combine(AppContext.BaseDirectory,"Verification");Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory,"self-tests.json"),JsonSerializer.Serialize(new{time=DateTimeOffset.Now,passed,errors,allPassed=errors.Count==0,hardwareCaptureTested=false,paidApiCalled=false,audioPlayed=false},new JsonSerializerOptions{WriteIndented=true}));
        return errors.Count==0?0:1;
    }
}
