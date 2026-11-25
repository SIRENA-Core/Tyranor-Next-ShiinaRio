using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Provider;
using Tyranor.OpenShiina.Host.Shared;

namespace Tyranor.OpenShiina.Host;

/// <summary>
/// 唯一向外部导出的显式启动入口（无桌面启动图标），校验参数并准备独立运行目录后拉起 :game 进程。
/// </summary>
[Activity(
    Name = "com.tyranor.next.shiinario.ShiinaRioLaunchActivity",
    Exported = true,
    LaunchMode = LaunchMode.SingleTask,
    NoHistory = true,
    ExcludeFromRecents = true,
    Theme = "@android:style/Theme.Translucent.NoTitleBar"
)]
public sealed class ShiinaRioLaunchActivity : Activity
{
    private ShiinaRioRequest? _pendingRequest;
    private bool _askedStoragePermission;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        HandleIntent(Intent);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        Intent = intent;
        _askedStoragePermission = false;
        HandleIntent(intent);
    }

    protected override void OnResume()
    {
        base.OnResume();
        if (_pendingRequest is not null && Android.OS.Environment.IsExternalStorageManager)
        {
            var ready = _pendingRequest;
            _pendingRequest = null;
            StartGameProcess(ready);
        }
        else if (_askedStoragePermission && _pendingRequest is not null)
        {
            ShowErrorAndFinish(GetString(Resource.String.err_storage_permission) ?? "");
        }
    }

    private void HandleIntent(Intent? intent)
    {
        if (intent is null || intent.Action != ShiinaRioContract.ActionLaunch)
        {
            ShowErrorAndFinish(FormatErrorMessage(Resource.String.err_invalid_request, intent?.Action ?? "missing action"));
            return;
        }

        ShiinaRioCatalog catalog;
        try
        {
            catalog = LoadCatalogFromAssets();
        }
        catch (Exception ex)
        {
            ShowErrorAndFinish(FormatErrorMessage(Resource.String.err_invalid_request, ex.Message));
            return;
        }

        var raw = new ShiinaRioRawLaunchInput(
            ProtocolVersion: intent.GetIntExtra(ShiinaRioContract.ExtraProtocolVersion, -1),
            Engine: intent.GetStringExtra(ShiinaRioContract.ExtraEngine),
            GameId: intent.GetStringExtra(ShiinaRioContract.ExtraGameId),
            GameName: intent.GetStringExtra(ShiinaRioContract.ExtraGameName),
            GameKey: intent.GetStringExtra(ShiinaRioContract.ExtraGameKey),
            GameDir: intent.GetStringExtra(ShiinaRioContract.ExtraGameDir),
            LaunchTarget: intent.GetStringExtra(ShiinaRioContract.ExtraLaunchTarget),
            SettingsJson: intent.GetStringExtra(ShiinaRioContract.ExtraSettingsJson),
            Locale: intent.GetStringExtra(ShiinaRioContract.ExtraLocale)
        );

        if (!ShiinaRioRequest.TryParse(raw, catalog, Directory.Exists, out var request, out var error) || request is null)
        {
            ShowErrorAndFinish(FormatErrorMessage(Resource.String.err_invalid_request, error ?? "unknown"));
            return;
        }

        if (!Android.OS.Environment.IsExternalStorageManager)
        {
            _pendingRequest = request;
            ShowStoragePermissionPrompt();
            return;
        }

        StartGameProcess(request);
    }

    private ShiinaRioCatalog LoadCatalogFromAssets()
    {
        using var stream = Assets?.Open("tyn/games.json")
            ?? throw new FileNotFoundException("assets/tyn/games.json 缺失");
        using var reader = new StreamReader(stream);
        return ShiinaRioCatalog.Parse(reader.ReadToEnd());
    }

    private void StartGameProcess(ShiinaRioRequest request)
    {
        try
        {
            var externalRoot = GetExternalFilesDir(null)?.AbsolutePath
                ?? throw new InvalidOperationException("外部应用目录不可用");
            var dataRoot = Path.Combine(externalRoot, "games", request.GameKey, "openshiina-data");
            ShiinaRioPlayerConfig.WriteSettingsFile(dataRoot, request.Settings, request.Locale);

            var launch = new Intent(this, typeof(TyranorShiinaRioActivity));
            launch.AddFlags(ActivityFlags.NewTask | ActivityFlags.ClearTask);
            launch.PutExtra(ShiinaRioContract.InternalExtraGameDir, request.GameDir);
            launch.PutExtra(ShiinaRioContract.InternalExtraGameKey, request.GameKey);
            launch.PutExtra(ShiinaRioContract.InternalExtraGameName, request.GameName);
            launch.PutExtra(ShiinaRioContract.InternalExtraSchemeName, request.ResolvedSchemeName ?? "");
            launch.PutExtra(ShiinaRioContract.InternalExtraDataRoot, dataRoot);
            launch.PutExtra(
                ShiinaRioContract.InternalExtraLanguage,
                ShiinaRioPlayerConfig.ResolveEffectiveLanguage(request.Settings, request.Locale)
            );
            launch.PutExtra(ShiinaRioContract.InternalExtraRenderer, request.Settings.Renderer);
            launch.PutExtra(ShiinaRioContract.InternalExtraTheme, request.Settings.Theme);
            launch.PutExtra(ShiinaRioContract.InternalExtraLocale, request.Locale ?? "");
            launch.PutExtra(ShiinaRioContract.InternalExtraLaunchTarget, request.LaunchTarget ?? "");

            StartActivity(launch);
            Finish();
        }
        catch (Exception ex)
        {
            ShowErrorAndFinish(FormatErrorMessage(Resource.String.err_open_failed, ex.Message));
        }
    }

    private string FormatErrorMessage(int resId, string? detail)
    {
        var arg = detail ?? "";
        return GetString(resId, [new Java.Lang.String(arg)]) ?? arg;
    }

    private void ShowStoragePermissionPrompt()
    {
        if (IsFinishing || IsDestroyed)
        {
            return;
        }
        new AlertDialog.Builder(this)
            .SetTitle(Resource.String.err_title)!
            .SetMessage(Resource.String.err_storage_permission)!
            .SetCancelable(false)!
            .SetPositiveButton(Resource.String.btn_grant_permission, (_, _) =>
            {
                _askedStoragePermission = true;
                var uri = Android.Net.Uri.Parse($"package:{PackageName}");
                var permIntent = new Intent(Settings.ActionManageAppAllFilesAccessPermission, uri);
                StartActivity(permIntent);
            })!
            .SetNegativeButton(Resource.String.btn_close, (_, _) => Finish())!
            .Show();
    }

    private void ShowErrorAndFinish(string message)
    {
        if (IsFinishing || IsDestroyed)
        {
            return;
        }
        new AlertDialog.Builder(this)
            .SetTitle(Resource.String.err_title)!
            .SetMessage(message)!
            .SetCancelable(true)!
            .SetOnCancelListener(new DismissFinishListener(this))!
            .SetPositiveButton(Resource.String.btn_close, (_, _) => Finish())!
            .Show();
    }

    private sealed class DismissFinishListener(Activity owner)
        : Java.Lang.Object, IDialogInterfaceOnCancelListener
    {
        public void OnCancel(IDialogInterface? dialog) => owner.Finish();
    }
}
