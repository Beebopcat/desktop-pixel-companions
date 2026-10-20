using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using AzerothCompanion.Core;
namespace AzerothCompanion.Services;

internal static class PublicResearchTransport
{
    internal static SocketsHttpHandler CreateHandler()=>new()
    {
        AllowAutoRedirect=false,UseCookies=false,UseProxy=false,ConnectTimeout=TimeSpan.FromSeconds(4),MaxConnectionsPerServer=2,
        AutomaticDecompression=DecompressionMethods.GZip|DecompressionMethods.Deflate,PooledConnectionLifetime=TimeSpan.FromMinutes(1),
        ConnectCallback=async(context,ct)=>
        {
            if(context.DnsEndPoint.Port!=443)throw new HttpRequestException("Only public HTTPS is permitted.");
            var addresses=await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host,ct).ConfigureAwait(false);
            if(addresses.Length==0||addresses.Any(address=>!IsPublicAddress(address)))throw new HttpRequestException("Private or reserved destinations are blocked.");
            var socket=new Socket(SocketType.Stream,ProtocolType.Tcp){NoDelay=true};
            try{await socket.ConnectAsync(addresses,443,ct).ConfigureAwait(false);return new NetworkStream(socket,true);}catch{socket.Dispose();throw;}
        }
    };
    internal static bool IsPublicAddress(IPAddress address)
    {
        if(address.IsIPv4MappedToIPv6)return IsPublicAddress(address.MapToIPv4());
        if(IPAddress.IsLoopback(address)||address.IsIPv6LinkLocal||address.IsIPv6SiteLocal||address.IsIPv6Multicast)return false;
        byte[] b=address.GetAddressBytes();
        if(b.Length==16)return (b[0]&0xe0)==0x20&&!(b[0]==0x20&&b[1]==0x02)&&
            !(b[0]==0x20&&b[1]==0x01&&(b[2]<2||b[2]==0x0d&&b[3]==0xb8));
        if(b.Length!=4)return false;
        return b[0] is not(0 or 10 or 127)&&b[0]<224&&!(b[0]==169&&b[1]==254)&&!(b[0]==172&&b[1] is >=16 and <=31)&&
            !(b[0]==192&&(b[1]==168||b[1]==0||b[1]==2||b[1]==88&&b[2]==99))&&!(b[0]==100&&b[1] is >=64 and <=127)&&
            !(b[0]==198&&(b[1] is 18 or 19||b[1]==51&&b[2]==100))&&!(b[0]==203&&b[1]==0&&b[2]==113);
    }
    internal static bool AllowsPage(string game,Uri uri)
    {
        if(!GameKnowledgeText.IsPublicSourceUrl(uri.AbsoluteUri))return false;
        if(uri.Host=="steamcommunity.com")return uri.AbsolutePath.Equals("/sharedfiles/filedetails/",StringComparison.OrdinalIgnoreCase)&&
            System.Text.RegularExpressions.Regex.IsMatch(uri.Query,@"(?:^\?|&)id=\d{1,12}(?:&|$)",System.Text.RegularExpressions.RegexOptions.None,TimeSpan.FromMilliseconds(100));
        string key=GameKnowledgeText.MatchKey(game);
        if(key=="roadwarden")return uri.Host=="roadwarden.fandom.com"&&uri.AbsolutePath.StartsWith("/wiki/",StringComparison.Ordinal)&&!uri.AbsolutePath.Contains(':');
        if(key.Contains("worldofwarcraft",StringComparison.Ordinal))return
            uri.Host=="warcraft.wiki.gg"&&uri.AbsolutePath.StartsWith("/wiki/",StringComparison.Ordinal)&&!uri.AbsolutePath.Contains(':')||
            uri.Host is "www.wowhead.com" or "worldofwarcraft.blizzard.com" or "news.blizzard.com";
        return false;
    }
}
