using Wotc.Mtga.DuelScene.Interactions.OptionalAction;

namespace ForgeBridge
{
    /// <summary>
    /// Yes/no questions whose two answers are not "Take Action" / "Decline" ("Tap or untap target creature?"). The client's default
    /// optional-action workflow labels its buttons from lookup trees keyed on the prompt, and those prompts have no entry, so the player saw
    /// Take Action (= tap) and Decline (= untap). The Patcher calls <see cref="Relabel"/> just before OptionalActionMessageWorkflow.SetButtons
    /// publishes the buttons; it swaps the two loc keys (existing client strings) for the prompts listed here. Yes (first button) = tap.
    /// </summary>
    public static class ButtonLabels
    {
        // "Tap or untap target creature / artifact or creature / nonland permanent / permanent?"
        private static bool TapOrUntap(uint promptId) => promptId == 1349 || promptId == 1503 || promptId == 5384 || promptId == 2621;

        public static void Relabel(OptionalActionMessageWorkflow workflow)
        {
            var prompt = workflow.Prompt;
            var buttons = workflow.Buttons;
            if (prompt == null || buttons == null || buttons.WorkflowButtons.Count < 2 || !TapOrUntap(prompt.PromptId)) return;
            buttons.WorkflowButtons[0].ButtonText = "DuelScene/ClientPrompt/TapTarget";
            buttons.WorkflowButtons[1].ButtonText = "DuelScene/Interaction/OptionalAction/Option_Untap";
        }
    }
}
