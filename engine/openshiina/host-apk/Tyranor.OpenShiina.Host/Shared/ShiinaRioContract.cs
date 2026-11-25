namespace Tyranor.OpenShiina.Host.Shared;

/// <summary>
/// Tyranor-Next 与 OpenShiina 外置宿主之间的 Intent 协议常量契约。
/// </summary>
public static class ShiinaRioContract
{
    public const string PackageName = "com.tyranor.next.shiinario";
    public const string LaunchActivity = "com.tyranor.next.shiinario.ShiinaRioLaunchActivity";
    public const string RuntimeActivity = "com.tyranor.next.shiinario.TyranorShiinaRioActivity";
    public const string ActionLaunch = "com.tyranor.next.action.LAUNCH_SHIINARIO";

    public const string EngineId = "shiinario";
    public const int ProtocolVersion = 1;

    public const string ExtraProtocolVersion = "com.tyranor.next.extra.PROTOCOL_VERSION";
    public const string ExtraEngine = "com.tyranor.next.extra.ENGINE";
    public const string ExtraGameId = "com.tyranor.next.extra.GAME_ID";
    public const string ExtraGameName = "com.tyranor.next.extra.GAME_NAME";
    public const string ExtraGameKey = "com.tyranor.next.extra.GAME_KEY";
    public const string ExtraGameDir = "com.tyranor.next.extra.GAME_DIR";
    public const string ExtraLaunchTarget = "com.tyranor.next.extra.LAUNCH_TARGET";
    public const string ExtraSettingsJson = "com.tyranor.next.extra.SETTINGS_JSON";
    public const string ExtraLocale = "com.tyranor.next.extra.LOCALE";

    public const string InternalExtraGameDir = "com.tyranor.next.shiinario.internal.GAME_DIR";
    public const string InternalExtraGameKey = "com.tyranor.next.shiinario.internal.GAME_KEY";
    public const string InternalExtraGameName = "com.tyranor.next.shiinario.internal.GAME_NAME";
    public const string InternalExtraSchemeName = "com.tyranor.next.shiinario.internal.SCHEME_NAME";
    public const string InternalExtraDataRoot = "com.tyranor.next.shiinario.internal.DATA_ROOT";
    public const string InternalExtraLocale = "com.tyranor.next.shiinario.internal.LOCALE";
    public const string InternalExtraRenderer = "com.tyranor.next.shiinario.internal.RENDERER";
    public const string InternalExtraTheme = "com.tyranor.next.shiinario.internal.THEME";
    public const string InternalExtraLanguage = "com.tyranor.next.shiinario.internal.LANGUAGE";
    public const string InternalExtraLaunchTarget = "tyn.internal.launch_target";
}
