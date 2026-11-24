using System.Runtime.InteropServices;
using Android.App;
using Android.Runtime;
using Avalonia.Android;

namespace Tyranor.OpenShiina.Host;

[Application(Name = "com.tyranor.next.shiinario.TyranorShiinaRioApplication")]
public sealed class TyranorShiinaRioApplication : AvaloniaAndroidApplication<TyranorShiinaRioApp>
{
    public TyranorShiinaRioApplication(IntPtr javaReference, JniHandleOwnership transfer)
        : base(javaReference, transfer)
    {
    }

    public override void OnCreate()
    {
        // .NET 10 CoreCLR 在 Android 上需要提前在主线程预热 Choreographer，规避首帧 6 秒加载延迟
        try
        {
            _ = AChoreographer_getInstance();
        }
        catch
        {
            // 部分精简环境缺失符号时忽略，交由 Avalonia 自身初始化
        }

        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OPENSHIINA_DATA")))
        {
            var root = GetExternalFilesDir(null)?.AbsolutePath ?? FilesDir?.AbsolutePath;
            if (!string.IsNullOrEmpty(root))
            {
                Environment.SetEnvironmentVariable("OPENSHIINA_DATA", root);
            }
        }

        base.OnCreate();
    }

    [DllImport("android")]
    private static extern IntPtr AChoreographer_getInstance();
}
