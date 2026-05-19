using System.Globalization;
using Windows.Globalization;
using Windows.Media.SpeechRecognition;

namespace AgentDock.Services;

public enum DictationState
{
    Idle,
    Starting,
    Listening,
    Stopping,
    Error
}

/// <summary>
/// Wraps Windows.Media.SpeechRecognition for continuous-dictation in a WPF-friendly
/// shape. Events fire on a thread-pool thread; callers must marshal to the UI thread.
/// </summary>
public class DictationService : IDisposable
{
    private SpeechRecognizer? _recognizer;
    private bool _starting;
    private bool _listening;

    /// <summary>Final phrase recognized. May fire many times during a session.</summary>
    public event Action<string>? TextRecognized;

    public event Action<DictationState>? StateChanged;

    /// <summary>Human-readable error message; <see cref="State"/> is also set to Error.</summary>
    public event Action<string>? ErrorOccurred;

    public DictationState State { get; private set; } = DictationState.Idle;

    public bool IsActive => _listening || _starting;

    /// <summary>
    /// True when this OS supports the WinRT speech-recognition API used here.
    /// Windows 10 version 2004 (build 19041) introduced the recognizer the modern
    /// dictation engine ships with; older Win10 builds get a graceful "no mic".
    /// </summary>
    public static bool IsSupportedOnThisOS
        => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041);

    public async Task ToggleAsync()
    {
        if (IsActive) await StopAsync();
        else await StartAsync();
    }

    public async Task StartAsync()
    {
        if (IsActive) return;
        if (!IsSupportedOnThisOS)
        {
            SetError("Dictation requires Windows 10 version 2004 (build 19041) or later.");
            return;
        }
        _starting = true;
        SetState(DictationState.Starting);

        try
        {
            if (_recognizer == null)
            {
                var lang = ResolveLanguage();
                _recognizer = lang != null ? new SpeechRecognizer(lang) : new SpeechRecognizer();
                _recognizer.Constraints.Add(new SpeechRecognitionTopicConstraint(
                    SpeechRecognitionScenario.Dictation, "dictation"));
                _recognizer.ContinuousRecognitionSession.ResultGenerated += OnResult;
                _recognizer.ContinuousRecognitionSession.Completed += OnCompleted;

                var compile = await _recognizer.CompileConstraintsAsync();
                if (compile.Status != SpeechRecognitionResultStatus.Success)
                {
                    SetError($"Speech recognizer could not initialize ({compile.Status}). " +
                             "Install a Windows Speech language pack via Settings → Time & Language → Speech.");
                    DisposeRecognizer();
                    return;
                }
            }

            await _recognizer.ContinuousRecognitionSession.StartAsync();
            _listening = true;
            SetState(DictationState.Listening);
        }
        catch (UnauthorizedAccessException)
        {
            SetError("Microphone access denied. Allow microphone for desktop apps in " +
                     "Settings → Privacy & Security → Microphone.");
            DisposeRecognizer();
        }
        catch (Exception ex)
        {
            SetError(ex.Message);
            DisposeRecognizer();
        }
        finally
        {
            _starting = false;
        }
    }

    public async Task StopAsync()
    {
        if (!_listening || _recognizer == null) return;
        SetState(DictationState.Stopping);
        try
        {
            await _recognizer.ContinuousRecognitionSession.StopAsync();
        }
        catch
        {
            // Stop racing with end-of-session is benign — just fall through.
        }
        finally
        {
            _listening = false;
            if (State != DictationState.Error)
                SetState(DictationState.Idle);
        }
    }

    private void OnResult(SpeechContinuousRecognitionSession session,
        SpeechContinuousRecognitionResultGeneratedEventArgs args)
    {
        var text = args.Result?.Text;
        if (!string.IsNullOrWhiteSpace(text))
            TextRecognized?.Invoke(text);
    }

    private void OnCompleted(SpeechContinuousRecognitionSession session,
        SpeechContinuousRecognitionCompletedEventArgs args)
    {
        _listening = false;
        if (State != DictationState.Error)
            SetState(DictationState.Idle);
    }

    private void SetState(DictationState state)
    {
        if (state == State) return;
        State = state;
        StateChanged?.Invoke(state);
    }

    private void SetError(string message)
    {
        State = DictationState.Error;
        StateChanged?.Invoke(DictationState.Error);
        ErrorOccurred?.Invoke(message);
    }

    private static Language? ResolveLanguage()
    {
        // Prefer the user's UI culture if Windows has a recognizer for it; otherwise let
        // SpeechRecognizer pick its default.
        try
        {
            var tag = CultureInfo.CurrentUICulture.IetfLanguageTag;
            if (string.IsNullOrEmpty(tag)) return null;
            return new Language(tag);
        }
        catch
        {
            return null;
        }
    }

    private void DisposeRecognizer()
    {
        if (_recognizer == null) return;
        try { _recognizer.Dispose(); } catch { }
        _recognizer = null;
        _listening = false;
    }

    public void Dispose() => DisposeRecognizer();
}
