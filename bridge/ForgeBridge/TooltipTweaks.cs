using System;
using System.Reflection;

namespace ForgeBridge
{
    /// <summary>
    /// The prompt-style tooltip (the warning on a greyed-out Submit button: "Insufficient blockers declared for attacker.") renders
    /// its text in capitals. The Patcher calls Apply at the start of TooltipSystem.Tooltip.SetAndSizeTextRect; it takes the
    /// upper-case bit off the TextMeshPro text's font style so the sentence reads as written.
    /// </summary>
    public static class TooltipTweaks
    {
        private const int UpperCase = 0x10, SmallCaps = 0x20;

        private static FieldInfo textField;
        private static PropertyInfo styleProp;
        private static bool resolved;

        public static void Apply(object tooltip)
        {
            try
            {
                if (!resolved)
                {
                    textField = tooltip.GetType().GetField("_text", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    resolved = true;
                    if (textField == null) Log.Error("TooltipTweaks: Tooltip has no _text");
                }
                var text = textField?.GetValue(tooltip);
                if (text == null) return;
                styleProp = styleProp ?? text.GetType().GetProperty("fontStyle", BindingFlags.Instance | BindingFlags.Public);
                if (styleProp == null) return;
                int style = (int)styleProp.GetValue(text);
                if ((style & (UpperCase | SmallCaps)) == 0) return;
                styleProp.SetValue(text, Enum.ToObject(styleProp.PropertyType, style & ~(UpperCase | SmallCaps)));
            }
            catch (Exception e) { Log.Error("TooltipTweaks.Apply: " + e); }
        }
    }
}
