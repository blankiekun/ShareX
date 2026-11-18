namespace ShareX.ScreenCaptureLib.Localization {
    // Fork-only strings, kept out of upstream's Strings*.resx so upstream merges don't conflict on them.
    // Keys here override Strings keys of the same name at the call sites that use LinuxStrings.
    public class LinuxStrings {
        private static global::System.Resources.ResourceManager resourceMan;

        public static global::System.Resources.ResourceManager ResourceManager => resourceMan ??= new global::System.Resources.ResourceManager("ShareX.ScreenCaptureLib.Localization.LinuxStrings", typeof(LinuxStrings).Assembly);

        public static string ScrollingCaptureWindow_Press_start => ResourceManager.GetString("ScrollingCaptureWindow_Press_start", Strings.Culture);

        public static string ScrollingCaptureWindow_Scroll_now => ResourceManager.GetString("ScrollingCaptureWindow_Scroll_now", Strings.Culture);

        public static string ScrollingCaptureWindow_Scroll_back => ResourceManager.GetString("ScrollingCaptureWindow_Scroll_back", Strings.Culture);

        public static string ScrollingCaptureWindow_Stop => ResourceManager.GetString("ScrollingCaptureWindow_Stop", Strings.Culture);

        public static string ScrollingCaptureWindow_Height_limit_reached => ResourceManager.GetString("ScrollingCaptureWindow_Height_limit_reached", Strings.Culture);
    }
}
