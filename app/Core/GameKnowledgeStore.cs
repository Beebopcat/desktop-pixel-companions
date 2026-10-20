using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AzerothCompanion.Core;

/// <summary>Bounded local cache of public source excerpts, never captures, credentials, or model files.</summary>
public sealed class GameKnowledgeStore
{
    private readonly string _directory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    public GameKnowledgeStore(string? directory = null) => _directory = directory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AzerothCompanion", "knowledge");
    public static string Key(string title, string topic, string? storeId) => Convert.ToHexStringLower(SHA256.HashData(
        Encoding.UTF8.GetBytes(title.ToLowerInvariant() + "\n" + topic.ToLowerInvariant() + "\n" + storeId)));
    public async Task<FreeGameResearchResult?> LoadAsync(string key, DateTimeOffset now, TimeSpan maxAge, CancellationToken ct = default)
    {
        var path = CachePath(key);
        await _gate.WaitAsync(ct);
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 128 * 1024) return null;
            var value = JsonSerializer.Deserialize<FreeGameResearchResult>(await File.ReadAllTextAsync(path, ct));
            if (value is null || !value.HasContext || value.Context.Length > 18000 || value.Sources.Count is < 1 or > 8 ||
                value.RetrievedUtc > now.AddMinutes(1) || now - value.RetrievedUtc >= maxAge || value.Sources.Any(source =>
                    !GameKnowledgeText.IsPublicSourceUrl(source.Url) || source.Excerpt.Length > 6000 || source.Title.Length > 240)) return null;
            return value with { Status = FreeGameResearchStatus.Cached, Message = "Using cached free research; source timestamps are retained." };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NullReferenceException) { return null; }
        finally { _gate.Release(); }
    }
    public async Task SaveAsync(string key, FreeGameResearchResult result, CancellationToken ct = default)
    {
        if (!result.HasContext) return;
        var path = CachePath(key);
        await _gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(_directory);
            var json = JsonSerializer.Serialize(result);
            if (Encoding.UTF8.GetByteCount(json) > 128 * 1024) return;
            await File.WriteAllTextAsync(path + ".tmp", json, ct);
            File.Move(path + ".tmp", path, true);
            foreach (var stale in Directory.EnumerateFiles(_directory, "*.json").Where(file =>
                Path.GetFileNameWithoutExtension(file).Length == 64 && Path.GetFileNameWithoutExtension(file).All(Uri.IsHexDigit))
                .OrderByDescending(File.GetLastWriteTimeUtc).Skip(24)) File.Delete(stale);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* Retrieval remains usable without a writable cache. */ }
        finally
        {
            try { if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp"); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            _gate.Release();
        }
    }
    private string CachePath(string key)
    {
        if (key.Length != 64 || !key.All(Uri.IsHexDigit)) throw new ArgumentException("Invalid knowledge cache key.");
        return Path.Combine(_directory, key + ".json");
    }
    public async Task<GameResearchDossier?> LoadDossierAsync(string key,DateTimeOffset now,TimeSpan maxAge,CancellationToken ct=default)
    {
        string path=Path.ChangeExtension(CachePath(key),"dossier.json");await _gate.WaitAsync(ct);
        try
        {
            if(!File.Exists(path)||new FileInfo(path).Length>768*1024)return null;
            var value=JsonSerializer.Deserialize<GameResearchDossier>(await File.ReadAllTextAsync(path,ct));
            if(value is null||value.Schema!=1||value.Entries.Count>8||value.RequestsUsed is <0 or >36||value.StartedUtc>now.AddMinutes(1)||now-value.StartedUtc>=maxAge||
                Key(value.Title,"dossier-v1",value.StoreId)!=key||value.CanonicalTitle.Length is <2 or >160||value.Entries.Any(entry=>entry.Topic.Query.Length>300||entry.Result.Sources.Count>8||entry.Result.Context.Length>18000||entry.Result.Sources.Any(source=>
                    !GameKnowledgeText.IsPublicSourceUrl(source.Url)||source.Excerpt.Length>6000||source.Title.Length>240||source.RetrievedUtc>now.AddMinutes(1))))return null;
            return value;
        }
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or JsonException or NullReferenceException or ArgumentException){return null;}
        finally{_gate.Release();}
    }
    public async Task SaveDossierAsync(string key,GameResearchDossier dossier,CancellationToken ct=default)
    {
        string path=Path.ChangeExtension(CachePath(key),"dossier.json");await _gate.WaitAsync(ct);
        try
        {
            string json=JsonSerializer.Serialize(dossier);if(Encoding.UTF8.GetByteCount(json)>768*1024)return;
            Directory.CreateDirectory(_directory);await File.WriteAllTextAsync(path+".tmp",json,ct);File.Move(path+".tmp",path,true);
            foreach(string stale in Directory.EnumerateFiles(_directory,"*.dossier.json").Where(file=>Path.GetFileName(file).Length==77&&Path.GetFileName(file)[..64].All(Uri.IsHexDigit)).OrderByDescending(File.GetLastWriteTimeUtc).Skip(24))File.Delete(stale);
        }
        catch(Exception error)when(error is IOException or UnauthorizedAccessException){}
        finally{try{if(File.Exists(path+".tmp"))File.Delete(path+".tmp");}catch(IOException){}catch(UnauthorizedAccessException){}_gate.Release();}
    }
    public async Task<GameResearchRequestBudget?> ReserveRequestAsync(string key,DateTimeOffset now,TimeSpan window,CancellationToken ct=default)
    {
        string path=Path.ChangeExtension(CachePath(key),"budget.json");await _gate.WaitAsync(ct);
        try
        {
            GameResearchRequestBudget? budget=null;
            if(File.Exists(path))
            {
                if(new FileInfo(path).Length>1024)return null;
                budget=JsonSerializer.Deserialize<GameResearchRequestBudget>(await File.ReadAllTextAsync(path,ct));
                if(budget is null||budget.Requests is <0 or >36||budget.StartedUtc>now.AddMinutes(1))return null;
            }
            if(budget is null||now-budget.StartedUtc>=window)budget=new(now,0);
            if(budget.Requests>=36)return null;
            budget=budget with{Requests=budget.Requests+1};Directory.CreateDirectory(_directory);
            await File.WriteAllTextAsync(path+".tmp",JsonSerializer.Serialize(budget),ct);File.Move(path+".tmp",path,true);return budget;
        }
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or JsonException){return null;}
        finally{try{if(File.Exists(path+".tmp"))File.Delete(path+".tmp");}catch(IOException){}catch(UnauthorizedAccessException){}_gate.Release();}
    }
}
