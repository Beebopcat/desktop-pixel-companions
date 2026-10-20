using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using AzerothCompanion.Core;
namespace AzerothCompanion.Services;
public sealed partial class FreeGameResearchService
{
    private async Task ExpandPublicPagesAsync(string game,string topic,List<GameKnowledgeSource> sources,CancellationToken ct)
    {
        var candidates=sources.Where(source=>source.Kind.Contains("snippet",StringComparison.OrdinalIgnoreCase)&&Uri.TryCreate(source.Url,UriKind.Absolute,out var uri)&&PublicResearchTransport.AllowsPage(game,uri)).Take(2).ToArray();
        foreach(var lead in candidates)
        {
            ct.ThrowIfCancellationRequested();var page=await ReadPageAsync(game,new Uri(lead.Url),topic,ct).ConfigureAwait(false);if(page is null)continue;
            var source=page with{Number=lead.Number,Topic=topic};int at=sources.IndexOf(lead);if(at>=0)sources[at]=source;
        }
    }
    private async Task<GameKnowledgeSource?> ReadPageAsync(string game,Uri initial,string topic,CancellationToken ct)
    {
        Uri uri=initial;using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            for(int redirect=0;redirect<=2;redirect++)
            {
                if(!PublicResearchTransport.AllowsPage(game,uri)||!await SpendRequestAsync(ct).ConfigureAwait(false))return null;
                using var request=new HttpRequestMessage(HttpMethod.Get,uri);request.Headers.UserAgent.ParseAdd("AzerothCompanion/0.2 (bounded public game reference retrieval)");
                using var response=await _http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token).ConfigureAwait(false);
                if((int)response.StatusCode is >=300 and <=399)
                {
                    if(redirect==2||response.Headers.Location is null)return null;var next=response.Headers.Location.IsAbsoluteUri?response.Headers.Location:new Uri(uri,response.Headers.Location);
                    // Same origin only; no authentication, paywall, cookies or challenge bypass.
                    if(next.Host!=uri.Host||!PublicResearchTransport.AllowsPage(game,next))return null;uri=next;continue;
                }
                if(!response.IsSuccessStatusCode||response.Content.Headers.ContentLength>256*1024||response.Content.Headers.ContentType?.MediaType is not("text/html" or "application/xhtml+xml"))return null;
                await using var stream=await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);using var bytes=new MemoryStream();var block=new byte[8192];int read;
                while((read=await stream.ReadAsync(block,timeout.Token).ConfigureAwait(false))>0){if(bytes.Length+read>256*1024)return null;bytes.Write(block,0,read);}
                string html=Encoding.UTF8.GetString(bytes.ToArray());string title=GameKnowledgeText.Clean(Regex.Match(html,@"<title\b[^>]*>(.*?)</title>",RegexOptions.IgnoreCase|RegexOptions.Singleline,TimeSpan.FromMilliseconds(200)).Groups[1].Value,240);
                string clean=Regex.Replace(html,@"<(script|style|nav|header|footer|form|noscript)\b[^>]*>.*?</\1\s*>"," ",RegexOptions.IgnoreCase|RegexOptions.Singleline,TimeSpan.FromMilliseconds(300));
                var article=Regex.Match(clean,@"<(article|main)\b[^>]*>(.*?)</\1\s*>",RegexOptions.IgnoreCase|RegexOptions.Singleline,TimeSpan.FromMilliseconds(300));
                string plain=GameKnowledgeText.Clean(article.Success?article.Groups[2].Value:clean,16000);
                if(plain.Length<100||!RelatesToGame(game,title+" "+plain[..Math.Min(4000,plain.Length)]))return null;
                string[] denied={"verify you are human","access denied","enable javascript and cookies","sign in to continue"};if(denied.Any(value=>plain[..Math.Min(800,plain.Length)].Contains(value,StringComparison.OrdinalIgnoreCase)))return null;
                string excerpt=GameEvidenceExcerpt.Select(plain,topic);
                bool official=uri.Host.EndsWith(".blizzard.com",StringComparison.Ordinal);
                return new(0,title.Length>0?title:game+" public guide",uri.AbsoluteUri,_clock(),official?"official publisher page; bounded topic excerpt":"community guide/wiki page retrieved; bounded topic excerpt, not a complete guide",excerpt);
            }
        }
        catch(OperationCanceledException)when(!ct.IsCancellationRequested){}
        catch(Exception error)when(error is HttpRequestException or IOException or RegexMatchTimeoutException or ArgumentException){}
        return null;
    }
}
