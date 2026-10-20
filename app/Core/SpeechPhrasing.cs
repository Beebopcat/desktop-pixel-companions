using System.Globalization;

namespace AzerothCompanion.Core;

public enum SpeechBoundary { End,Paragraph,Line,Sentence,Clause,Word,Hard }
/// <summary>SourceText preserves the complete original substring, including whitespace.
/// Text removes only outer whitespace for the renderer; punctuation and words are never rewritten.</summary>
public sealed record SpeechPhrase(string SourceText,string Text,SpeechBoundary Boundary,TimeSpan PauseAfter);

/// <summary>Voice-independent, literal phrasing. Native TTS handles punctuation inside each
/// phrase. Additional silence is used only at paragraph/line boundaries and forced word splits;
/// this does not inject emotion tags or change a voice, reference, seed, rate or pitch.</summary>
public static class SpeechPhrasing
{
    public const int MaximumCharacters=500;
    private static readonly HashSet<string> Abbreviations=new(StringComparer.OrdinalIgnoreCase)
    {"mr","mrs","ms","dr","prof","sr","jr","st","vs","etc","e.g","i.e","a.m","p.m","u.s","u.k","no","vol","fig","dept","approx","inc","ltd","capt","lt","col","gen","rev"};

    public static IReadOnlyList<SpeechPhrase> Split(string? text,int maximumCharacters=MaximumCharacters)
    {
        if(maximumCharacters is <16 or >MaximumCharacters)throw new ArgumentOutOfRangeException(nameof(maximumCharacters));
        if(string.IsNullOrEmpty(text))return [];
        var result=new List<SpeechPhrase>();int offset=0;
        while(offset<text.Length)
        {
            int limit=Math.Min(text.Length,offset+maximumCharacters);
            if(limit<text.Length&&char.IsHighSurrogate(text[limit-1])&&char.IsLowSurrogate(text[limit]))limit--;
            int paragraph=-1,line=-1,sentence=-1,clause=-1,word=-1;
            for(int i=offset;i<limit;i++)
            {
                char c=text[i];
                if(char.IsWhiteSpace(c))
                {
                    word=i+1;
                    if(c is '\r' or '\n')
                    {
                        int end=i+1;if(c=='\r'&&end<limit&&text[end]=='\n')end++;
                        int next=end;while(next<limit&&text[next] is ' ' or '\t')next++;
                        if(next<limit&&text[next] is '\r' or '\n')
                        {
                            while(next<limit&&char.IsWhiteSpace(text[next]))next++;
                            // A paragraph is an intentional break, even in a short utterance.
                            paragraph=next;break;
                        }
                        line=end;
                    }
                    continue;
                }
                if(IsSentenceBoundary(text,i))
                {
                    int end=i+1;while(end<limit&&(text[end] is '.' or '!' or '?' or '…'||IsClosing(text[end])))end++;
                    if(end>=text.Length||char.IsWhiteSpace(text[end]))sentence=end;
                }
                else if(c is ',' or ';' or ':' or '—')
                {if(i+1==text.Length||char.IsWhiteSpace(text[i+1]))clause=i+1;}
            }
            int cut;SpeechBoundary boundary;
            if(paragraph>offset){cut=paragraph;boundary=SpeechBoundary.Paragraph;}
            else if(limit==text.Length){cut=limit;boundary=SpeechBoundary.End;}
            else if(sentence>offset){cut=sentence;boundary=SpeechBoundary.Sentence;}
            else if(line>offset){cut=line;boundary=SpeechBoundary.Line;}
            else if(clause>offset){cut=clause;boundary=SpeechBoundary.Clause;}
            else if(word>offset){cut=word;boundary=SpeechBoundary.Word;}
            else{cut=limit;boundary=SpeechBoundary.Hard;}
            // Put following whitespace in this source segment when it fits. This keeps
            // exact source reconstruction possible while render strings remain bounded.
            while(cut<limit&&char.IsWhiteSpace(text[cut]))cut++;
            var source=text[offset..cut];var render=source.Trim();
            if(cut==text.Length)boundary=SpeechBoundary.End;
            var pause=boundary switch{SpeechBoundary.Paragraph=>TimeSpan.FromMilliseconds(400),SpeechBoundary.Line=>TimeSpan.FromMilliseconds(180),SpeechBoundary.Word=>TimeSpan.FromMilliseconds(120),_=>TimeSpan.Zero};
            result.Add(new(source,render,boundary,pause));offset=cut;
        }
        return result.AsReadOnly();
    }

    /// <summary>Cancellation owns silence just as it owns playback. Call only BETWEEN phrases.
    /// Generation/Quiet state is checked before and after; no delay is added after the last phrase.</summary>
    public static async Task<bool> WaitBetweenAsync(SpeechPhrase previous,CancellationToken cancellationToken,
        Func<bool>? stillCurrent=null,Func<TimeSpan,CancellationToken,Task>? delay=null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if(stillCurrent?.Invoke()==false)return false;
        if(previous.PauseAfter>TimeSpan.Zero)
            await (delay?.Invoke(previous.PauseAfter,cancellationToken)??Task.Delay(previous.PauseAfter,cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();return stillCurrent?.Invoke()!=false;
    }
    private static bool IsClosing(char c)=>c is '"' or '\'' or '”' or '’' or ')' or ']' or '}';
    private static bool IsSentenceBoundary(string text,int i)
    {
        char c=text[i];if(c is '!' or '?' or '…')return true;if(c!='.')return false;
        if(i>0&&i+1<text.Length&&char.IsDigit(text[i-1])&&char.IsDigit(text[i+1]))return false;
        if(i+1<text.Length&&text[i+1]=='.')return false;
        if(i>0&&text[i-1]=='.')return true;
        int start=i-1;while(start>=0&&(char.IsLetter(text[start])||text[start]=='.'))start--;
        string word=text[(start+1)..i].Trim('.');
        if(Abbreviations.Contains(word)||word.Length==1&&char.IsUpper(word[0]))return false;
        // Do not split inside URLs, filenames, version labels or decimal-like identifiers.
        int after=i+1;while(after<text.Length&&IsClosing(text[after]))after++;
        return after==text.Length||char.IsWhiteSpace(text[after]);
    }
}
