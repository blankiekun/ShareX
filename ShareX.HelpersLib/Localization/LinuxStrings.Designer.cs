namespace ShareX.HelpersLib.Localization {
    // Fork-only strings, kept out of upstream's Strings*.resx so upstream merges don't conflict on them.
    // Keys here override Strings keys of the same name at the call sites that use LinuxStrings.
    public class LinuxStrings {
        private static global::System.Resources.ResourceManager resourceMan;

        public static global::System.Resources.ResourceManager ResourceManager => resourceMan ??= new global::System.Resources.ResourceManager("ShareX.HelpersLib.Localization.LinuxStrings", typeof(LinuxStrings).Assembly);

        public static string HotkeyType_PinToScreenToggleClickThrough => ResourceManager.GetString("HotkeyType_PinToScreenToggleClickThrough", Strings.Culture);

        public static string ImageIO_Open_needs_ImageMagick => ResourceManager.GetString("ImageIO_Open_needs_ImageMagick", Strings.Culture);

        public static string ImageIO_Save_needs_ImageMagick => ResourceManager.GetString("ImageIO_Save_needs_ImageMagick", Strings.Culture);

        public static string PrintHelper_Print_failed => ResourceManager.GetString("PrintHelper_Print_failed", Strings.Culture);
    }
}
