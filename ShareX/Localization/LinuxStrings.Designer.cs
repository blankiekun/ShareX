namespace ShareX.Localization {
    // Fork-only strings, kept out of upstream's Strings*.resx so upstream merges don't conflict on them.
    // Keys here override Strings keys of the same name at the call sites that use LinuxStrings.
    public class LinuxStrings {
        private static global::System.Resources.ResourceManager resourceMan;

        public static global::System.Resources.ResourceManager ResourceManager => resourceMan ??= new global::System.Resources.ResourceManager("ShareX.Localization.LinuxStrings", typeof(LinuxStrings).Assembly);

        public static string ApplicationSettingsWindow_Desktop => ResourceManager.GetString("ApplicationSettingsWindow_Desktop", Strings.Culture);

        public static string ApplicationSettingsWindow_RunShareXAtLogin => ResourceManager.GetString("ApplicationSettingsWindow_RunShareXAtLogin", Strings.Culture);

        public static string ApplicationSettingsWindow_ShowEditWithShareXInDolphinContextMenu => ResourceManager.GetString("ApplicationSettingsWindow_ShowEditWithShareXInDolphinContextMenu", Strings.Culture);

        public static string ApplicationSettingsWindow_ShowUploadWithShareXInDolphinContextMenu => ResourceManager.GetString("ApplicationSettingsWindow_ShowUploadWithShareXInDolphinContextMenu", Strings.Culture);

        public static string CaptureBase_NoOtherWindow => ResourceManager.GetString("CaptureBase_NoOtherWindow", Strings.Culture);

        public static string MainMenuBuilder_PinToScreenToggleClickThrough => ResourceManager.GetString("MainMenuBuilder_PinToScreenToggleClickThrough", Strings.Culture);

        public static string ScreenRecord_GifNotFinished => ResourceManager.GetString("ScreenRecord_GifNotFinished", Strings.Culture);

        public static string ScreenRecord_RecordingKept => ResourceManager.GetString("ScreenRecord_RecordingKept", Strings.Culture);

        public static string ScreenRecord_WindowNotOnOneMonitor => ResourceManager.GetString("ScreenRecord_WindowNotOnOneMonitor", Strings.Culture);

        public static string ScreenRecord_RecordingsRecovered => ResourceManager.GetString("ScreenRecord_RecordingsRecovered", Strings.Culture);

        public static string ShareXCannotBeClosedWhileScreenRecordingIsActive => ResourceManager.GetString("ShareXCannotBeClosedWhileScreenRecordingIsActive", Strings.Culture);

        public static string StartScreen_PlatformNote => ResourceManager.GetString("StartScreen_PlatformNote", Strings.Culture);

        public static string TaskHelpers_PinToScreenToggleClickThrough_Off => ResourceManager.GetString("TaskHelpers_PinToScreenToggleClickThrough_Off", Strings.Culture);

        public static string TaskHelpers_PinToScreenToggleClickThrough_On => ResourceManager.GetString("TaskHelpers_PinToScreenToggleClickThrough_On", Strings.Culture);

        public static string SettingManager_ExportPlaintextWarning => ResourceManager.GetString("SettingManager_ExportPlaintextWarning", Strings.Culture);

        public static string SettingManager_ImportReportTitle => ResourceManager.GetString("SettingManager_ImportReportTitle", Strings.Culture);

        public static string SettingManager_ImportReportSignInAgain => ResourceManager.GetString("SettingManager_ImportReportSignInAgain", Strings.Culture);

        public static string SettingManager_ImportReportSecretsImported => ResourceManager.GetString("SettingManager_ImportReportSecretsImported", Strings.Culture);

        public static string SettingManager_ImportReportPathsMapped => ResourceManager.GetString("SettingManager_ImportReportPathsMapped", Strings.Culture);

        public static string SettingManager_ImportReportPathsReset => ResourceManager.GetString("SettingManager_ImportReportPathsReset", Strings.Culture);

        public static string SettingManager_ImportReportTurnedOff => ResourceManager.GetString("SettingManager_ImportReportTurnedOff", Strings.Culture);

        public static string SettingManager_ImportReportDroppedEffects => ResourceManager.GetString("SettingManager_ImportReportDroppedEffects", Strings.Culture);

        public static string SettingManager_ImportReportHistory => ResourceManager.GetString("SettingManager_ImportReportHistory", Strings.Culture);

        public static string SettingManager_ImportReportHistoryBackup => ResourceManager.GetString("SettingManager_ImportReportHistoryBackup", Strings.Culture);

        public static string SettingManager_ImportReportHistoryFailed => ResourceManager.GetString("SettingManager_ImportReportHistoryFailed", Strings.Culture);

        public static string SettingManager_ImportReportSkipped => ResourceManager.GetString("SettingManager_ImportReportSkipped", Strings.Culture);

        public static string SettingManager_ImportReportPlaintextWarning => ResourceManager.GetString("SettingManager_ImportReportPlaintextWarning", Strings.Culture);

        public static string SettingManager_ImportNothingFound => ResourceManager.GetString("SettingManager_ImportNothingFound", Strings.Culture);

        public static string SettingManager_ImportFileDamaged => ResourceManager.GetString("SettingManager_ImportFileDamaged", Strings.Culture);

        public static string SettingManager_ImportNeedsWallet => ResourceManager.GetString("SettingManager_ImportNeedsWallet", Strings.Culture);

        public static string SettingManager_ImportSaveFailed => ResourceManager.GetString("SettingManager_ImportSaveFailed", Strings.Culture);
    }
}
