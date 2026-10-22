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
        static readonly Point[] SparkPaths = { new Point(40, -70), new Point(72, -96), new Point(104, -60), new Point(128, -100), new Point(-24, -60), new Point(150, -36), new Point(88, -124), new Point(-6, -96) };

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

            for (int k = 0; k < SparkPaths.Length; k++)
            {
                double size = k % 2 == 0 ? 8 : 5;
                var move = new TranslateTransform();
                var spark = new Rectangle
                {
                    Width = size, Height = size, RadiusX = 2, RadiusY = 2, Opacity = 0, RenderTransform = move,
                    Fill = new SolidColorBrush(k % 2 == 0 ? Color.FromRgb(0xFF, 0xF3, 0xC4) : Color.FromRgb(0xFF, 0xD2, 0x7A)),
                };
                Canvas.SetLeft(spark, 370); Canvas.SetTop(spark, 168);
                Sparks.Children.Add(spark);
                var p = SparkPaths[k];
                double delay = k * 0.015;
                var fade = Keys(0, 0, 0.35, 0, 0.37, 1, 0.80, 0.9, 1, 0);
                var dx = Keys(0, 0, 0.35, 0, 0.80, p.X * 0.8, 1, p.X);
                var dy = Keys(0, 0, 0.35, 0, 0.80, p.Y * 0.8, 1, p.Y + 22);
                foreach (var a in new[] { fade, dx, dy }) a.BeginTime = TimeSpan.FromSeconds(delay);
                spark.BeginAnimation(OpacityProperty, fade);
                move.BeginAnimation(TranslateTransform.XProperty, dx);
                move.BeginAnimation(TranslateTransform.YProperty, dy);
            }
        }
    }
}
