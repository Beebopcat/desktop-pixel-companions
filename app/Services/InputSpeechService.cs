using System.Speech.Recognition;

namespace AzerothCompanion.Services;

public sealed record SpeechTranscript(long RecordingEpoch,string Text);
public sealed record SpeechInputError(long RecordingEpoch,string Message);
public sealed record SpeechInputStopped(long RecordingEpoch,bool Cancelled);

// Independent of engine lifetime: cancellation also invalidates already queued results.
internal sealed class SpeechRecordingEpoch
{
    private long current;
    public long Next()=>++current;
    public bool IsCurrent(long epoch)=>epoch>0&&epoch==current;
}

internal sealed record SpeechEngineUtterance(string Text);
internal sealed record SpeechEngineCompletion(bool Cancelled,Exception? Error=null);

// Internal adapter permits lifecycle verification without a microphone or SAPI engine.
internal interface ISpeechInputEngine:IDisposable
{
    event EventHandler<SpeechEngineUtterance>? Recognized;
    event EventHandler<SpeechEngineCompletion>? Completed;
    void Start();
    void Stop();
    void Cancel();
    void DisconnectInput();
}

internal sealed class LocalSpeechInputEngine:ISpeechInputEngine
{
    private readonly SpeechRecognitionEngine engine;
    public event EventHandler<SpeechEngineUtterance>? Recognized;
    public event EventHandler<SpeechEngineCompletion>? Completed;
    internal LocalSpeechInputEngine()
    {
        var available=SpeechRecognitionEngine.InstalledRecognizers();
        var recognizer=available.FirstOrDefault(r=>r.Culture.TwoLetterISOLanguageName=="en")??available.FirstOrDefault();
        if(recognizer==null)throw new InvalidOperationException("No local Windows speech recognizer is installed. Use typed input or add Windows speech recognition for your language.");
        engine=new SpeechRecognitionEngine(recognizer);
        try
        {
            engine.LoadGrammar(new DictationGrammar());
            engine.SpeechRecognized+=OnRecognized;engine.RecognizeCompleted+=OnCompleted;
        }
        catch{engine.Dispose();throw;}
    }
    private void OnRecognized(object? sender,SpeechRecognizedEventArgs args)=>Recognized?.Invoke(this,new(args.Result.Text));
    private void OnCompleted(object? sender,RecognizeCompletedEventArgs args)=>Completed?.Invoke(this,new(args.Cancelled,args.Error));
    public void Start(){engine.SetInputToDefaultAudioDevice();engine.RecognizeAsync(RecognizeMode.Multiple);}
    public void Stop()=>engine.RecognizeAsyncStop();
    public void Cancel()=>engine.RecognizeAsyncCancel();
    public void DisconnectInput()=>engine.SetInputToNull();
    public void Dispose()
    {
        engine.SpeechRecognized-=OnRecognized;engine.RecognizeCompleted-=OnCompleted;engine.Dispose();
    }
}

/// <summary>Local Windows/SAPI input. Construction is inactive; only an explicit Begin opens the microphone.</summary>
public sealed class InputSpeechService:IDisposable
{
    private readonly object _gate=new();
    private readonly SpeechRecordingEpoch _epochs=new();
    private readonly Func<ISpeechInputEngine> _engineFactory;
    private readonly Func<IReadOnlyList<string>> _languages;
    private readonly List<string> _phrases=new();
    private ISpeechInputEngine? _engine;
    private ISpeechInputEngine? _pendingEngine;
    private SpeechEngineCompletion? _pendingCompletion;
    private object? _nativeGate;
    private Task<long>? _startupTask;
    private long _engineEpoch;
    private bool _disposed,_ending,_continuous,_starting;
    private Task _cleanupTask=Task.CompletedTask;

    public InputSpeechService():this(()=>new LocalSpeechInputEngine(),InstalledLanguages){ }
    internal InputSpeechService(Func<ISpeechInputEngine> engineFactory,Func<IReadOnlyList<string>> languages)
    {_engineFactory=engineFactory;_languages=languages;}
    public event EventHandler<SpeechTranscript>? TextRecognized;
    public event EventHandler<SpeechInputError>? Error;
    public event EventHandler<SpeechInputStopped>? Stopped;
    public bool IsListening{get{lock(_gate)return _engine!=null;}}
    public bool IsCurrentRecording(long epoch){lock(_gate)return !_disposed&&_epochs.IsCurrent(epoch);}
    public IReadOnlyList<string> AvailableLanguages=>_languages();
    public bool IsAvailable=>AvailableLanguages.Count>0;
    private static IReadOnlyList<string> InstalledLanguages()
    {
        try{return SpeechRecognitionEngine.InstalledRecognizers().Select(r=>r.Culture.Name).Distinct().ToArray();}
        catch{return Array.Empty<string>();}
    }

    // Synchronous compatibility for non-UI callers; WPF callers use the async methods.
    public long Begin()=>BeginAsync().GetAwaiter().GetResult();
    /// <summary>Deliver each complete utterance immediately until explicitly stopped or cancelled.</summary>
    public long BeginContinuous()=>BeginContinuousAsync().GetAwaiter().GetResult();
    public Task<long> BeginAsync(CancellationToken cancellationToken=default)=>BeginCoreAsync(false,cancellationToken);
    public Task<long> BeginContinuousAsync(CancellationToken cancellationToken=default)=>BeginCoreAsync(true,cancellationToken);
    /// <summary>Await native cleanup before opening another recognizer; never blocks the UI thread.</summary>
    public Task WaitForStoppedAsync(CancellationToken cancellationToken=default)
    {
        Task cleanup;lock(_gate)cleanup=_cleanupTask;
        return cancellationToken.CanBeCanceled?cleanup.WaitAsync(cancellationToken):cleanup;
    }

    private Task<long> BeginCoreAsync(bool continuous,CancellationToken cancellationToken)
    {
        if(cancellationToken.IsCancellationRequested)return Task.FromCanceled<long>(cancellationToken);
        lock(_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed,this);
            if(_engine!=null)
            {
                if(_continuous!=continuous)throw new InvalidOperationException("Stop the current microphone mode before changing it.");
                return Task.FromResult(_engineEpoch);
            }
            if(_starting)
            {
                if(!_epochs.IsCurrent(_engineEpoch))
                    throw new InvalidOperationException("Previous microphone startup is closing. Await WaitForStoppedAsync before opening it again.");
                if(_continuous!=continuous)throw new InvalidOperationException("Stop the current microphone mode before changing it.");
                return cancellationToken.CanBeCanceled?_startupTask!.WaitAsync(cancellationToken):_startupTask!;
            }
            if(!_cleanupTask.IsCompleted)
                throw new InvalidOperationException("Previous microphone input is closing. Await WaitForStoppedAsync before opening it again.");
            var epoch=_epochs.Next();
            var started=new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
            var finished=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var nativeGate=new object();
            _engineEpoch=epoch;_continuous=continuous;_starting=true;_ending=false;
            _pendingEngine=null;_pendingCompletion=null;_phrases.Clear();
            _startupTask=started.Task;_cleanupTask=finished.Task;
            _=Task.Run(()=>StartOwned(epoch,nativeGate,started,finished,cancellationToken));
            return started.Task;
        }
    }

    private void StartOwned(long epoch,object nativeGate,TaskCompletionSource<long> started,
        TaskCompletionSource finished,CancellationToken cancellationToken)
    {
        ISpeechInputEngine? engine=null;
        Exception? failure=null;
        bool published=false;
        using var registration=cancellationToken.Register(()=>CancelRecording(epoch));
        try
        {
            lock(_gate)
                if(_disposed||!_epochs.IsCurrent(epoch))throw new OperationCanceledException();
            // Installed recognizer discovery, grammar loading, and device setup stay off
            // the dispatcher and outside the state gate, so Cancel never waits for them.
            engine=_engineFactory();
            engine.Recognized+=OnRecognized;engine.Completed+=OnCompleted;
            lock(_gate)
            {
                if(_disposed||!_epochs.IsCurrent(epoch))throw new OperationCanceledException();
                _pendingEngine=engine;
            }
            lock(nativeGate)engine.Start();
            lock(_gate)
            {
                if(_disposed||!_epochs.IsCurrent(epoch))throw new OperationCanceledException();
                if(_pendingCompletion is { } completed)
                    throw completed.Error??new InvalidOperationException("Local speech recognition stopped during microphone startup.");
                _engine=engine;_nativeGate=nativeGate;_pendingEngine=null;_pendingCompletion=null;_starting=false;
                published=true;
            }
        }
        catch(Exception ex){failure=ex;}
        if(published)
        {
            finished.TrySetResult();started.TrySetResult(epoch);return;
        }
        if(engine!=null){engine.Recognized-=OnRecognized;engine.Completed-=OnCompleted;}
        var cleanupError=engine==null?null:CleanupNative(engine,nativeGate,true);
        bool cancelled;
        lock(_gate)
        {
            cancelled=_disposed||!_epochs.IsCurrent(epoch)||cancellationToken.IsCancellationRequested;
            if(_engineEpoch==epoch)
            {
                _pendingEngine=null;_pendingCompletion=null;_starting=false;_ending=false;_continuous=false;_phrases.Clear();
                if(_epochs.IsCurrent(epoch))_epochs.Next();
            }
        }
        // Startup remains the exclusive owner until any late native engine is fully
        // released. Cleanup completion is successful even for an intentional cancel.
        finished.TrySetResult();
        if(cancelled)started.TrySetCanceled(cancellationToken.IsCancellationRequested?cancellationToken:new CancellationToken(true));
        else started.TrySetException(failure??cleanupError??new InvalidOperationException("Microphone startup failed."));
        Stopped?.Invoke(this,new(epoch,true));
    }

    /// <summary>Finish buffered push-to-talk input. Continuous mode may also be ended explicitly.</summary>
    public void End()
    {
        lock(_gate)
        {
            if(_starting){Cancel();return;}
            if(_engine==null||_ending)return;
            _ending=true;
            var engine=_engine;var nativeGate=_nativeGate!;var epoch=_engineEpoch;
            _=Task.Run(()=>
            {
                try
                {
                    lock(nativeGate)
                    {
                        lock(_gate)
                            if(_disposed||!ReferenceEquals(engine,_engine)||!_epochs.IsCurrent(epoch))return;
                        engine.Stop();
                    }
                }
                catch(Exception ex)
                {
                    CancelRecording(epoch);Error?.Invoke(this,new(epoch,"Local microphone completion: "+ex.Message));
                }
            });
        }
    }

    /// <summary>Invalidate results immediately; native cancellation/disposal run off the UI thread.</summary>
    public void Cancel()
    {
        lock(_gate)CancelLocked();
    }

    private void CancelRecording(long epoch)
    {
        lock(_gate)if(_engineEpoch==epoch&&_epochs.IsCurrent(epoch))CancelLocked();
    }

    private void CancelLocked()
    {
        _epochs.Next();_phrases.Clear();_ending=false;
        // A blocked startup owns its own cleanup; let it finish without allowing
        // a second engine or racing a native Cancel against an in-progress Start.
        if(_starting)return;
        var engine=_engine;var nativeGate=_nativeGate;var epoch=_engineEpoch;
        _engine=null;_nativeGate=null;_continuous=false;
        if(engine!=null)QueueCleanupLocked(engine,nativeGate!,epoch,true,true);
    }

    private void OnRecognized(object? sender,SpeechEngineUtterance args)
    {
        SpeechTranscript? transcript=null;
        lock(_gate)
        {
            if(_disposed||!ReferenceEquals(sender,_engine)||string.IsNullOrWhiteSpace(args.Text))return;
            if(_continuous)transcript=new(_engineEpoch,args.Text);
            else _phrases.Add(args.Text);
        }
        // Subscribers marshal to their UI and re-check the epoch, including cancellation races.
        if(transcript!=null)TextRecognized?.Invoke(this,transcript);
    }

    private void OnCompleted(object? sender,SpeechEngineCompletion args)
    {
        string text;long epoch;
        var delivered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock(_gate)
        {
            if(ReferenceEquals(sender,_pendingEngine)&&_starting){_pendingCompletion=args;return;}
            if(_disposed||!ReferenceEquals(sender,_engine))return;
            var engine=_engine!;var nativeGate=_nativeGate!;epoch=_engineEpoch;
            text=_continuous?"":string.Join(" ",_phrases);
            _engine=null;_nativeGate=null;_ending=false;_continuous=false;_phrases.Clear();
            QueueCleanupLocked(engine,nativeGate,epoch,args.Cancelled,false,delivered.Task);
        }
        try
        {
            if(args.Error!=null)Error?.Invoke(this,new(epoch,"Local speech recognition: "+args.Error.Message));
            else if(!args.Cancelled&&!string.IsNullOrWhiteSpace(text))TextRecognized?.Invoke(this,new(epoch,text));
        }
        finally{delivered.TrySetResult();}
    }

    private static Exception? CleanupNative(ISpeechInputEngine engine,object nativeGate,bool requestCancel)
    {
        Exception? cleanupError=null;
        lock(nativeGate)
        {
            if(requestCancel)
                try{engine.Cancel();}catch(InvalidOperationException){ }catch(Exception ex){cleanupError=ex;}
            try{engine.DisconnectInput();}catch(InvalidOperationException){ }catch(Exception ex){cleanupError??=ex;}
            try{engine.Dispose();}catch(Exception ex){cleanupError??=ex;}
        }
        return cleanupError;
    }

    private void QueueCleanupLocked(ISpeechInputEngine engine,object nativeGate,long epoch,bool cancelled,bool requestCancel,Task? delivered=null)
    {
        // SAPI Dispose can wait for recognition callbacks. Never dispose on one of those
        // callbacks, or hold the service gate while native shutdown waits for them.
        _cleanupTask=Task.Run(async()=>
        {
            engine.Recognized-=OnRecognized;engine.Completed-=OnCompleted;
            var cleanupError=CleanupNative(engine,nativeGate,requestCancel);
            if(delivered!=null)await delivered.ConfigureAwait(false);
            if(cleanupError!=null)Error?.Invoke(this,new(epoch,"Local microphone shutdown: "+cleanupError.Message));
            Stopped?.Invoke(this,new(epoch,cancelled));
        });
    }

    public void Dispose()
    {
        lock(_gate)
        {
            if(_disposed)return;
            _disposed=true;Cancel();
        }
    }
}
