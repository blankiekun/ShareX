namespace ShareX.Tools.Localization {
    // Fork-only strings, kept out of upstream's Strings*.resx so upstream merges don't conflict on them.
    // Keys here override Strings keys of the same name at the call sites that use LinuxStrings.
    public class LinuxStrings {
        private static global::System.Resources.ResourceManager resourceMan;

        public static global::System.Resources.ResourceManager ResourceManager => resourceMan ??= new global::System.Resources.ResourceManager("ShareX.Tools.Localization.LinuxStrings", typeof(LinuxStrings).Assembly);

        public static string BackgroundRemoverWindow_No_model_found => ResourceManager.GetString("BackgroundRemoverWindow_No_model_found", Strings.Culture);

        public static string MouseHighlighter_PointerOnlyHelp => ResourceManager.GetString("MouseHighlighter_PointerOnlyHelp", Strings.Culture);

        public static string OCRHelper_Failed => ResourceManager.GetString("OCRHelper_Failed", Strings.Culture);

        public static string OCRHelper_Language_data_missing => ResourceManager.GetString("OCRHelper_Language_data_missing", Strings.Culture);

        public static string OCRHelper_No_languages => ResourceManager.GetString("OCRHelper_No_languages", Strings.Culture);

        public static string OCRHelper_Tesseract_missing => ResourceManager.GetString("OCRHelper_Tesseract_missing", Strings.Culture);

        public static string OCRHelper_Timed_out => ResourceManager.GetString("OCRHelper_Timed_out", Strings.Culture);

        public static string VideoPlayer_MpvMissing => ResourceManager.GetString("VideoPlayer_MpvMissing", Strings.Culture);

        public static string VideoPlayer_PlaybackError => ResourceManager.GetString("VideoPlayer_PlaybackError", Strings.Culture);

        public static string BorderlessWindowManager_Not_supported_by_KWin => ResourceManager.GetString("BorderlessWindowManager_Not_supported_by_KWin", Strings.Culture);

        public static string InspectWindowViewModel_Desktop_file => ResourceManager.GetString("InspectWindowViewModel_Desktop_file", Strings.Culture);

        public static string InspectWindowViewModel_Output => ResourceManager.GetString("InspectWindowViewModel_Output", Strings.Culture);

        public static string InspectWindowViewModel_Resource_name => ResourceManager.GetString("InspectWindowViewModel_Resource_name", Strings.Culture);

        public static string InspectWindowViewModel_Window_id => ResourceManager.GetString("InspectWindowViewModel_Window_id", Strings.Culture);

        public static string InspectWindowViewModel_Window_role => ResourceManager.GetString("InspectWindowViewModel_Window_role", Strings.Culture);

        public static string InspectWindowViewModel_Window_state => ResourceManager.GetString("InspectWindowViewModel_Window_state", Strings.Culture);

        public static string InspectWindowViewModel_Window_type => ResourceManager.GetString("InspectWindowViewModel_Window_type", Strings.Culture);

        public static string InspectWindowViewModel_Pick_a_window => ResourceManager.GetString("InspectWindowViewModel_Pick_a_window", Strings.Culture);
    }
}
