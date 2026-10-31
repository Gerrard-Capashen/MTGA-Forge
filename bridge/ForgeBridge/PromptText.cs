using Wotc.Mtgo.Gre.External.Messaging;

namespace ForgeBridge
{
    /// <summary>
    /// Prompts with Forge's own wording. The client's prompts come from its Prompts table, and none of them shows free text, so the server
    /// sends prompt id <see cref="ForgeText"/> (not in the table) with a NonLocalizedString parameter "Text", e.g. "Choose a card from this
    /// Kaladesh booster pack" (ForgeBackend.PROMPT_FORGE_TEXT). The Patcher calls <see cref="Custom"/> at the start of
    /// PromptTextProvider.GetPromptText(Prompt): a non-null result is the prompt's text (browser subheaders, the prompt bar).
    /// </summary>
    public static class PromptText
    {
        public const uint ForgeText = 9000001;

        public static string Custom(Prompt prompt)
        {
            if (prompt == null || prompt.PromptId != ForgeText) return null;
            foreach (var p in prompt.Parameters)
                if (p.Type == ParameterType.NonLocalizedString && p.ParameterName == "Text") return p.StringValue;
            return null;
        }
    }
}
