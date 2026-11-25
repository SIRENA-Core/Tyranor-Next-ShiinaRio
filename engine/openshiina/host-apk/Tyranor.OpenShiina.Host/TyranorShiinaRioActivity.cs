using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Media;
using Android.OS;
using Android.Views;
using Avalonia;
using Avalonia.Android;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using NAudio.Wave;
using OpenShiina.App;
using OpenShiina.Archives;
using OpenShiina.Game;
using OpenShiina.Gpu;
using OpenShiina.Localization;
using Tyranor.OpenShiina.Host.Shared;

namespace Tyranor.OpenShiina.Host;

/// <summary>
/// 隔离在 :game 子进程中的游戏执行窗口，直接挂载 OpenShiina.App.GameView(library: false)。
/// </summary>
[Activity(
    Name = "com.tyranor.next.shiinario.TyranorShiinaRioActivity",
    Exported = false,
    Process = ":game",
    LaunchMode = LaunchMode.SingleTask,
    ScreenOrientation = ScreenOrientation.SensorLandscape,
    ConfigurationChanges = ConfigChanges.Orientation
        | ConfigChanges.ScreenSize
        | ConfigChanges.ScreenLayout
        | ConfigChanges.SmallestScreenSize
        | ConfigChanges.KeyboardHidden
        | ConfigChanges.Keyboard
        | ConfigChanges.Navigation
        | ConfigChanges.UiMode
        | ConfigChanges.Density,
    Theme = "@style/TyranorShiinaRioTheme"
)]
public sealed class TyranorShiinaRioActivity : AvaloniaMainActivity
{
    private GameView? _gameView;
    private string? _activeGameKey;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        var launch = ReadLaunchExtras(Intent);
        if (launch is null)
        {
            base.OnCreate(savedInstanceState);
            FinishAndRemoveTask();
            return;
        }

        _activeGameKey = launch.GameKey;
        PrepareEngineRuntime(launch);
        base.OnCreate(savedInstanceState);
        VolumeControlStream = global::Android.Media.Stream.Music;
        EnableImmersiveFullscreen();
        StartGame(launch);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        Intent = intent;
        var launch = ReadLaunchExtras(intent);
        if (launch is null)
        {
            return;
        }

        _activeGameKey = launch.GameKey;
        _gameView?.Close();
        _gameView = null;
        PrepareEngineRuntime(launch);
        StartGame(launch);
    }

    protected override void OnResume()
    {
        base.OnResume();
        PlayerPlatform.SetActive(true);
        EnableImmersiveFullscreen();
    }

    protected override void OnPause()
    {
        PlayerPlatform.SetActive(false);
        base.OnPause();
    }

    protected override void OnDestroy()
    {
        _gameView?.Close();
        _gameView = null;
        base.OnDestroy();
        if (IsFinishing)
        {
            Android.OS.Process.KillProcess(Android.OS.Process.MyPid());
        }
    }

    public override void OnBackPressed()
    {
        if (_gameView is not null && !_gameView.AllowClose())
        {
            return;
        }
        _gameView?.Close();
        FinishAndRemoveTask();
    }

    private void PrepareEngineRuntime(LaunchArgs launch)
    {
        Directory.CreateDirectory(launch.DataRoot);
        global::System.Environment.SetEnvironmentVariable("OPENSHIINA_DATA", launch.DataRoot);

        var formatsDir = EnsureFormatsExtracted();
        FormatManager.DataFolder = formatsDir;

        Languages.Apply(launch.Language);
        PlayerPlatform.Touch = true;
        PlayerPlatform.OpenJoystick = static () => null;
        PlayerPlatform.StartSound = TryStartAndroidAudio;
        PlayerPlatform.FolderPath = static folder => folder.TryGetLocalPath();
        PlayerPlatform.EnsureFileAccessAsync = static () => Task.FromResult(true);
        PlayerPlatform.OpenFolder = null;

        PlayerSettings.AcceleratorFactory = string.Equals(launch.Renderer, "Cpu", StringComparison.OrdinalIgnoreCase)
            ? null
            : static () => (VulkanAccelerator.TryCreate(out string? error), error);

        if (Avalonia.Application.Current is { } app)
        {
            PlayerTheme.Apply(app, ParseAppTheme(launch.Theme));
        }
    }

    private void StartGame(LaunchArgs launch)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                var view = new GameView(library: false);
                view.GameEnded += () =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (_gameView == view)
                        {
                            _gameView = null;
                        }
                        FinishAndRemoveTask();
                    });
                };

                _gameView = view;
                TyranorShiinaRioApp.RootHost.Content = view;

                var syncSw = System.Diagnostics.Stopwatch.StartNew();
                var syncResult = await Task.Run(
                    () => ShiinaRioEvbTranslationSync.TrySyncSafe(launch.GameDir, launch.LaunchTarget)
                );
                syncSw.Stop();
                if (syncResult.Status == EvbTranslationSyncStatus.Failed)
                {
                    Android.Util.Log.Warn(
                        "TyranorShiinaRio",
                        $"EVB 汉化同步失败（已降级继续原版启动）：exe={syncResult.SourceExeName ?? "<none>"}, elapsedMs={syncSw.ElapsedMilliseconds}, error={syncResult.Message}"
                    );
                }
                else
                {
                    Android.Util.Log.Info(
                        "TyranorShiinaRio",
                        $"EVB 汉化同步完成：status={syncResult.Status}, exe={syncResult.SourceExeName ?? "<none>"}, files={syncResult.FileCount}, elapsedMs={syncSw.ElapsedMilliseconds}"
                    );
                }

                EncryptionScheme? scheme = null;
                if (!string.IsNullOrEmpty(launch.SchemeName))
                {
                    scheme = FormatManager.Instance.GetScheme(launch.SchemeName);
                }
                if (scheme is null)
                {
                    var detected = SchemeDetector.Detect(launch.GameDir);
                    if (detected.Count > 0)
                    {
                        scheme = FormatManager.Instance.GetScheme(detected[0].Name);
                    }
                }

                var error = await view.OpenAsync(launch.GameDir, scheme);
                if (!string.IsNullOrEmpty(error))
                {
                    ShowRuntimeError(FormatErrorMessage(Resource.String.err_open_failed, error));
                }
            }
            catch (Exception ex)
            {
                ShowRuntimeError(FormatErrorMessage(Resource.String.err_open_failed, ex.Message));
            }
        });
    }

    private string FormatErrorMessage(int resId, string? detail)
    {
        var arg = detail ?? "";
        return GetString(resId, [new Java.Lang.String(arg)]) ?? arg;
    }

    private string EnsureFormatsExtracted()
    {
        var formatsDir = Path.Combine(FilesDir!.AbsolutePath, "openshiina-formats");
        Directory.CreateDirectory(formatsDir);
        var destPath = Path.Combine(formatsDir, FormatManager.SchemeFileName);

        using var assetStream = Assets?.Open(FormatManager.SchemeFileName)
            ?? throw new FileNotFoundException($"APK assets 缺少 {FormatManager.SchemeFileName}");
        using var mem = new MemoryStream();
        assetStream.CopyTo(mem);
        var bytes = mem.ToArray();

        if (!File.Exists(destPath) || new FileInfo(destPath).Length != bytes.Length)
        {
            var tmp = destPath + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, destPath, overwrite: true);
        }
        return formatsDir;
    }

    private void EnableImmersiveFullscreen()
    {
        if (Window is null)
        {
            return;
        }
        var controller = Window.InsetsController;
        if (!OperatingSystem.IsAndroidVersionAtLeast(35))
        {
#pragma warning disable CA1422
            Window.SetDecorFitsSystemWindows(false);
#pragma warning restore CA1422
        }
        Window.AddFlags(WindowManagerFlags.KeepScreenOn);
        if (controller is not null)
        {
            controller.Hide(WindowInsets.Type.StatusBars() | WindowInsets.Type.NavigationBars());
            controller.SystemBarsBehavior = (int)WindowInsetsControllerBehavior.ShowTransientBarsBySwipe;
        }
    }

    private void ShowRuntimeError(string message)
    {
        RunOnUiThread(() =>
        {
            if (IsFinishing || IsDestroyed)
            {
                return;
            }
            new AlertDialog.Builder(this)
                .SetTitle(Resource.String.err_title)!
                .SetMessage(message)!
                .SetCancelable(false)!
                .SetPositiveButton(Resource.String.btn_close, (_, _) => FinishAndRemoveTask())!
                .Show();
        });
    }

    private static AppTheme ParseAppTheme(string raw) => raw.Trim() switch
    {
        "Parchment" => AppTheme.Parchment,
        "Graphite" => AppTheme.Graphite,
        "White" => AppTheme.White,
        "System" => AppTheme.System,
        _ => AppTheme.Shiina,
    };

    private static LaunchArgs? ReadLaunchExtras(Intent? intent)
    {
        if (intent is null)
        {
            return null;
        }
        var gameDir = intent.GetStringExtra(ShiinaRioContract.InternalExtraGameDir);
        var dataRoot = intent.GetStringExtra(ShiinaRioContract.InternalExtraDataRoot);
        var gameKey = intent.GetStringExtra(ShiinaRioContract.InternalExtraGameKey);
        if (string.IsNullOrWhiteSpace(gameDir)
            || string.IsNullOrWhiteSpace(dataRoot)
            || string.IsNullOrWhiteSpace(gameKey))
        {
            return null;
        }
        return new LaunchArgs(
            GameDir: gameDir,
            GameKey: gameKey,
            GameName: intent.GetStringExtra(ShiinaRioContract.InternalExtraGameName) ?? "ShiinaRio",
            SchemeName: intent.GetStringExtra(ShiinaRioContract.InternalExtraSchemeName) ?? "",
            DataRoot: dataRoot,
            Language: intent.GetStringExtra(ShiinaRioContract.InternalExtraLanguage) ?? "",
            Renderer: intent.GetStringExtra(ShiinaRioContract.InternalExtraRenderer) ?? "Gpu",
            Theme: intent.GetStringExtra(ShiinaRioContract.InternalExtraTheme) ?? "Shiina",
            LaunchTarget: intent.GetStringExtra(ShiinaRioContract.InternalExtraLaunchTarget)
        );
    }

    private static IDisposable? TryStartAndroidAudio(ISampleProvider source) =>
        AndroidAudioTrackOutput.TryStart(source);

    private sealed record LaunchArgs(
        string GameDir,
        string GameKey,
        string GameName,
        string SchemeName,
        string DataRoot,
        string Language,
        string Renderer,
        string Theme,
        string? LaunchTarget
    );

    private sealed class AndroidAudioTrackOutput : IDisposable
    {
        private const int Rate = 44100;
        private const int Channels = 2;
        private const int ChunkFrames = 512;

        private readonly ISampleProvider _source;
        private readonly AudioTrack _track;
        private readonly Thread _thread;
        private volatile bool _stop;

        private AndroidAudioTrackOutput(ISampleProvider source, AudioTrack track)
        {
            _source = source;
            _track = track;
            _thread = new Thread(Feed)
            {
                IsBackground = true,
                Name = "ShiinaRioAudio",
                Priority = global::System.Threading.ThreadPriority.AboveNormal,
            };
            _thread.Start();
        }

        public static IDisposable? TryStart(ISampleProvider source)
        {
            if (source.WaveFormat.SampleRate != Rate || source.WaveFormat.Channels != Channels)
            {
                throw new ArgumentException("OpenShiina 音频流必须为 44.1 kHz 双声道。", nameof(source));
            }
            try
            {
                var least = AudioTrack.GetMinBufferSize(Rate, ChannelOut.Stereo, Encoding.PcmFloat);
                if (least <= 0)
                {
                    return null;
                }
                var track = new AudioTrack.Builder()
                    .SetAudioAttributes(
                        new AudioAttributes.Builder()
                            .SetUsage(AudioUsageKind.Game)!
                            .SetContentType(AudioContentType.Music)!
                            .Build()!)!
                    .SetAudioFormat(
                        new AudioFormat.Builder()
                            .SetEncoding(Encoding.PcmFloat)!
                            .SetSampleRate(Rate)!
                            .SetChannelMask(ChannelOut.Stereo)!
                            .Build()!)!
                    .SetBufferSizeInBytes(Math.Max(least * 2, ChunkFrames * Channels * sizeof(float) * 2))!
                    .SetTransferMode(AudioTrackMode.Stream)!
                    .Build()!;
                if (track.State != AudioTrackState.Initialized)
                {
                    track.Release();
                    track.Dispose();
                    return null;
                }
                track.Play();
                return new AndroidAudioTrackOutput(source, track);
            }
            catch (Exception ex) when (ex is Java.Lang.Exception or InvalidOperationException or ArgumentException)
            {
                return null;
            }
        }

        private void Feed()
        {
            var buffer = new float[ChunkFrames * Channels];
            while (!_stop)
            {
                var n = _source.Read(buffer, 0, buffer.Length);
                if (n <= 0)
                {
                    Array.Clear(buffer);
                    n = buffer.Length;
                }
                for (var at = 0; at < n && !_stop;)
                {
                    var written = _track.Write(buffer, at, n - at, WriteMode.NonBlocking);
                    if (written < 0)
                    {
                        return;
                    }
                    at += written;
                    if (at < n)
                    {
                        Thread.Sleep(2);
                    }
                }
            }
        }

        public void Dispose()
        {
            if (_stop)
            {
                return;
            }
            _stop = true;
            _thread.Join(500);
            try
            {
                _track.Stop();
            }
            catch (Java.Lang.IllegalStateException)
            {
                // 已停止时忽略
            }
            _track.Release();
            _track.Dispose();
        }
    }
}
