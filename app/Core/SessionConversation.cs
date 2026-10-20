namespace AzerothCompanion.Core;
/// <summary>Bounded volatile conversation memory; no chat or captured images are written to disk.</summary>
public sealed class SessionConversation
{
    private readonly Queue<(string User,string Answer)> turns=new();
    public void Clear()=>turns.Clear();
    public void Add(string user,string answer)
    {
        static string Short(string text,int limit)=>text.Length<=limit?text:text[..limit];
        turns.Enqueue((Short(user,700),Short(answer,1300)));
        while(turns.Count>4)turns.Dequeue();
    }
    public string Context()=>string.Join("\n",turns.Select(t=>$"Previous user: {t.User}\nPrevious companion: {t.Answer}"));
}
