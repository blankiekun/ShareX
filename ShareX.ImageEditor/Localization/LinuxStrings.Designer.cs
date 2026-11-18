namespace ShareX.ImageEditor.Localization {
    // Fork-only strings, kept out of upstream's Strings*.resx so upstream merges don't conflict on them.
    // Keys here override Strings keys of the same name at the call sites that use LinuxStrings.
    public class LinuxStrings {
        private static global::System.Resources.ResourceManager resourceMan;

        public static global::System.Resources.ResourceManager ResourceManager => resourceMan ??= new global::System.Resources.ResourceManager("ShareX.ImageEditor.Localization.LinuxStrings", typeof(LinuxStrings).Assembly);

        public static string EmojiPickerDialogView_NoEmojiFont => ResourceManager.GetString("EmojiPickerDialogView_NoEmojiFont", Strings.Culture);
    }
}
