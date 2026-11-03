using System;
using System.Reflection;
using GreClient.Rules;
using Color = UnityEngine.Color;
using SpriteRenderer = UnityEngine.SpriteRenderer;
using Wizards.Mtga;
using Wotc.Mtgo.Gre.External.Messaging;

namespace ForgeBridge
{
    /// <summary>
    /// Archenemy: Sparky (server Archenemy.STAGES): the three stages share Sparky's avatar; the second ("Enraged Sparky") is drawn tinted red and the
    /// third ("Sparky Unleashed") bright yellow, in the match's avatar portrait. The Patcher calls Apply at the start of DuelScene_AvatarView.Init;
    /// the opponent is told apart by its screen name (the stage name the server sends in MatchCreated).
    /// </summary>
    public static class AvatarTint
    {
        private static readonly Color Enraged = new Color(1f, 0.28f, 0.22f, 1f);
        private static readonly Color Unleashed = new Color(1f, 0.93f, 0.35f, 1f);
        private static FieldInfo portraitField;

        private static bool TryTint(string name, out Color tint)
        {
            tint = Color.white;
            if (name == "Enraged Sparky") tint = Enraged;
            else if (name == "Sparky Unleashed") tint = Unleashed;
            else return false;
            return true;
        }

        /// <summary>The versus screen (VSScreen.SetPlayerNames, Patcher hook 6k): the opponent's full avatar image gets the same tint.</summary>
        public static void ApplyVs(object screen)
        {
            try
            {
                string name = Pantry.Get<MatchManager>()?.OpponentName;
                if (!TryTint(name, out Color tint)) return;
                var flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                object elements = screen.GetType().GetField("_opponentElements", flags)?.GetValue(screen);
                var avatar = elements?.GetType().GetField("Avatar", flags)?.GetValue(elements) as UnityEngine.UI.Image;
                if (avatar == null) { Log.Error("AvatarTint: VSScreen has no _opponentElements.Avatar image"); return; }
                avatar.color = tint;
                Log.Info("vs avatar tint: " + name);
            }
            catch (Exception e) { Log.Error("AvatarTint.ApplyVs: " + e); }
        }

        public static void Apply(object view, object player)
        {
            try
            {
                var mtg = player as MtgPlayer;
                if (mtg == null || mtg.ClientPlayerEnum != GREPlayerNum.Opponent) return;
                string name = Pantry.Get<MatchManager>()?.OpponentName;
                if (!TryTint(name, out Color tint)) return;
                if (portraitField == null) portraitField = view.GetType().GetField("_portrait", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                var portrait = portraitField?.GetValue(view) as SpriteRenderer;
                if (portrait == null) { Log.Error("AvatarTint: DuelScene_AvatarView has no _portrait sprite renderer"); return; }
                portrait.color = tint;
                Log.Info("avatar tint: " + name);
            }
            catch (Exception e) { Log.Error("AvatarTint.Apply: " + e); }
        }
    }
}
