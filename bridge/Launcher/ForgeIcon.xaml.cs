using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace MtgaForgeLauncher
{
    /// <summary>The anvil and hammer. With <see cref="Animate"/> it floats, the hammer strikes and sparks fly.</summary>
    public partial class ForgeIcon : UserControl
    {
        static readonly Duration Cycle = new Duration(TimeSpan.FromSeconds(2.4));
        const int SparkCount = 28;

        public static readonly DependencyProperty AnimateProperty = DependencyProperty.Register("Animate", typeof(bool), typeof(ForgeIcon), new PropertyMetadata(false));
        public bool Animate { get { return (bool)GetValue(AnimateProperty); } set { SetValue(AnimateProperty, value); } }

        public ForgeIcon()
        {
            InitializeComponent();
            Loaded += (s, e) => { if (Animate) Start(); };
        }

        static DoubleAnimationUsingKeyFrames Keys(params double[] timeValuePairs)
        {
            var a = new DoubleAnimationUsingKeyFrames { Duration = Cycle, RepeatBehavior = RepeatBehavior.Forever };
            for (int i = 0; i < timeValuePairs.Length; i += 2)
                a.KeyFrames.Add(new SplineDoubleKeyFrame(timeValuePairs[i + 1], KeyTime.FromPercent(timeValuePairs[i]), new KeySpline(0.4, 0, 0.6, 1)));
            return a;
        }

        void Start()
        {
            var bob = new DoubleAnimation(0, -9, new Duration(TimeSpan.FromSeconds(2.4))) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
            FloatMove.BeginAnimation(TranslateTransform.YProperty, bob);
            ShadowScale.CenterX = 170; ShadowScale.CenterY = 13;
            var shrink = new DoubleAnimation(1, 0.86, new Duration(TimeSpan.FromSeconds(2.4))) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
            ShadowScale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);

            // lift and tilt, drop onto the anvil at 36%, rest, lift again
            HammerMove.BeginAnimation(TranslateTransform.YProperty, Keys(0, -30, 0.12, -30, 0.36, 0, 0.44, 0, 0.70, -30, 1, -30));
            HammerTilt.BeginAnimation(RotateTransform.AngleProperty, Keys(0, -7, 0.12, -7, 0.36, 0, 0.44, 0, 0.70, -7, 1, -7));

            // anvil squashes on the hit, glow throbs and spikes on impact
            AnvilSquash.BeginAnimation(ScaleTransform.ScaleYProperty, Keys(0, 1, 0.35, 1, 0.38, 0.96, 0.5, 1.01, 0.6, 1, 1, 1));
            // the lava cracks flare hot on the hit and cool off again
            AnvilLava.BeginAnimation(OpacityProperty, Keys(0, 0, 0.35, 0, 0.38, 1, 0.5, 0.8, 0.75, 0, 1, 0));
            HammerLava.BeginAnimation(OpacityProperty, Keys(0, 0, 0.35, 0, 0.38, 1, 0.5, 0.8, 0.75, 0, 1, 0));
            var bloom = new double[] { 0, 0, 0.35, 0, 0.38, 1, 0.55, 0.5, 0.75, 0, 1, 0 };
            AnvilBloom.BeginAnimation(OpacityProperty, Keys(bloom));
            HammerBloom.BeginAnimation(OpacityProperty, Keys(bloom));
            Halo.BeginAnimation(OpacityProperty, Keys(0, 0, 0.35, 0, 0.38, 0.7, 0.6, 0.25, 0.8, 0, 1, 0));

            Flash.BeginAnimation(OpacityProperty, Keys(0, 0, 0.35, 0, 0.375, 1, 0.52, 0, 1, 0));
            FlashScale.BeginAnimation(ScaleTransform.ScaleXProperty, Keys(0, 0.4, 0.35, 0.4, 0.52, 1.6, 1, 1.6));
            FlashScale.BeginAnimation(ScaleTransform.ScaleYProperty, Keys(0, 0.4, 0.35, 0.4, 0.52, 1.6, 1, 1.6));
            Ring.BeginAnimation(OpacityProperty, Keys(0, 0, 0.35, 0, 0.375, 0.9, 0.6, 0, 1, 0));
            RingScale.BeginAnimation(ScaleTransform.ScaleXProperty, Keys(0, 0.3, 0.35, 0.3, 0.6, 2.4, 1, 2.4));
            RingScale.BeginAnimation(ScaleTransform.ScaleYProperty, Keys(0, 0.3, 0.35, 0.3, 0.6, 2.4, 1, 2.4));

            // burst of sparks from the strike point: a fan upward, thrown out, then pulled down by gravity
            var rnd = new Random(7);
            for (int k = 0; k < SparkCount; k++)
            {
                bool big = k % 3 == 0;
                double size = big ? 9 : 4 + rnd.NextDouble() * 3;
                var move = new TranslateTransform();
                var spark = new Ellipse
                {
                    Width = size, Height = size, Opacity = 0, RenderTransform = move,
                    Fill = new SolidColorBrush(big ? Color.FromRgb(0xFF, 0xF3, 0xC4) : k % 2 == 0 ? Color.FromRgb(0xFF, 0xD2, 0x7A) : Color.FromRgb(0xFF, 0x8A, 0x2B)),
                };
                Canvas.SetLeft(spark, 378 - size / 2); Canvas.SetTop(spark, 213 - size / 2);
                Sparks.Children.Add(spark);
                double angle = (-170 + rnd.NextDouble() * 160) * Math.PI / 180;
                double speed = 70 + rnd.NextDouble() * 120;
                double vx = Math.Cos(angle) * speed, vy = Math.Sin(angle) * speed;
                double delay = rnd.NextDouble() * 0.02;
                var fade = Keys(0, 0, 0.35, 0, 0.37, 1, 0.7, 0.9, 0.9, 0);
                var dx = Keys(0, 0, 0.36, 0, 0.9, vx);
                var dy = Keys(0, 0, 0.36, 0, 0.52, vy, 0.9, vy * 0.2 + 70);
                foreach (var a in new[] { fade, dx, dy }) a.BeginTime = TimeSpan.FromSeconds(delay);
                spark.BeginAnimation(OpacityProperty, fade);
                move.BeginAnimation(TranslateTransform.XProperty, dx);
                move.BeginAnimation(TranslateTransform.YProperty, dy);
            }
        }
    }
}
