using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using OpenShiina.App;
using OpenShiina.Game;

namespace Tyranor.OpenShiina.Host;

public sealed class TyranorShiinaRioApp : global::Avalonia.Application
{
    public static ContentControl RootHost { get; } = new();

    public override void Initialize()
    {
        Styles.Add(PlayerTheme.Fluent());
        PlayerTheme.Apply(this, AppTheme.Shiina);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            singleView.MainView = RootHost;
        }
        base.OnFrameworkInitializationCompleted();
    }
}
