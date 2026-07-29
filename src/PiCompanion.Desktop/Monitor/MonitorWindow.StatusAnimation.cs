using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using PiCompanion.Core.Runs;

namespace PiCompanion.Desktop.Monitor;

public partial class MonitorWindow
{
    private const double StatusHaloRestingOpacity = 0.15;
    private Guid? _statusIndicatorTaskId;
    private Guid? _statusIndicatorRunId;
    private RunStatus? _statusIndicatorStatus;
    private bool _isAiSummaryLoading;

    private bool StatusAnimationsEnabled =>
        _settings.AnimationsEnabled && SystemParameters.ClientAreaAnimation;

    private void UpdateStatusIndicatorAnimation(RunStatus status, Guid? taskId, Guid? runId)
    {
        var previousTaskId = _statusIndicatorTaskId;
        var previousRunId = _statusIndicatorRunId;
        var previousStatus = _statusIndicatorStatus;
        var stateChanged =
            previousTaskId != taskId ||
            previousRunId != runId ||
            previousStatus != status;
        var includeTerminalTransition =
            previousTaskId is not null &&
            previousTaskId == taskId &&
            previousRunId == runId &&
            previousStatus != status;
        _statusIndicatorTaskId = taskId;
        _statusIndicatorStatus = status;
        _statusIndicatorRunId = runId;
        if (!stateChanged)
        {
            return;
        }

        StopStatusIndicatorAnimations();
        if (StatusAnimationsEnabled && IsVisible)
        {
            StartStatusIndicatorAnimation(status, includeTerminalTransition);
        }
    }

    private void ApplyStatusAnimationPreference()
    {
        StopStatusIndicatorAnimations();
        StopAiSummaryLoadingAnimation();
        if (StatusAnimationsEnabled && IsVisible && _statusIndicatorStatus is { } status)
        {
            StartStatusIndicatorAnimation(status, includeTerminalTransition: false);
        }

        if (StatusAnimationsEnabled && IsVisible && _isAiSummaryLoading)
        {
            StartAiSummaryLoadingAnimation();
        }
    }

    private void OnMonitorVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        StopStatusIndicatorAnimations();
        StopAiSummaryLoadingAnimation();
        if (IsVisible && StatusAnimationsEnabled && _statusIndicatorStatus is { } status)
        {
            StartStatusIndicatorAnimation(status, includeTerminalTransition: false);
        }

        if (IsVisible && StatusAnimationsEnabled && _isAiSummaryLoading)
        {
            StartAiSummaryLoadingAnimation();
        }
    }

    private void UpdateAiSummaryLoadingState(bool isLoading)
    {
        ResultSummaryLoading.Visibility = isLoading
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (_isAiSummaryLoading == isLoading)
        {
            return;
        }

        _isAiSummaryLoading = isLoading;
        StopAiSummaryLoadingAnimation();
        if (isLoading && StatusAnimationsEnabled && IsVisible)
        {
            StartAiSummaryLoadingAnimation();
        }
    }

    private void StartAiSummaryLoadingAnimation()
    {
        ResultSummarySpinnerRotate.BeginAnimation(
            RotateTransform.AngleProperty,
            new DoubleAnimation(
                0,
                360,
                TimeSpan.FromMilliseconds(900))
            {
                RepeatBehavior = RepeatBehavior.Forever,
            },
            HandoffBehavior.SnapshotAndReplace);
    }

    private void StopAiSummaryLoadingAnimation()
    {
        ResultSummarySpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, null);
        ResultSummarySpinnerRotate.Angle = 0;
    }

    private void StartStatusIndicatorAnimation(RunStatus status, bool includeTerminalTransition)
    {
        switch (status)
        {
            case RunStatus.Queued:
                StartBreathingAnimation(1600, 0.08, 0.2, 0.94, 1.08, pulseDot: false);
                break;
            case RunStatus.Starting:
                StartBreathingAnimation(650, 0.08, 0.3, 0.86, 1.18, pulseDot: true);
                break;
            case RunStatus.Running:
                StartBreathingAnimation(1100, 0.09, 0.28, 0.9, 1.16, pulseDot: true);
                break;
            case RunStatus.WaitingForApproval:
            case RunStatus.WaitingForAnswer:
                StartBreathingAnimation(700, 0.08, 0.38, 0.84, 1.22, pulseDot: true);
                break;
            case RunStatus.Cancelling:
                StartBreathingAnimation(500, 0.05, 0.24, 0.78, 1, pulseDot: true);
                break;
            case RunStatus.Completed when includeTerminalTransition:
                StartCompletedAnimation();
                break;
            case RunStatus.Failed when includeTerminalTransition:
                StartFailedAnimation();
                break;
            case RunStatus.Interrupted when includeTerminalTransition:
                StartInterruptedAnimation();
                break;
        }
    }

    private void StartBreathingAnimation(
        int durationMilliseconds,
        double minimumOpacity,
        double maximumOpacity,
        double minimumScale,
        double maximumScale,
        bool pulseDot)
    {
        ForEachStatusIndicator((halo, dot, haloScale, dotScale) =>
        {
            halo.BeginAnimation(
                OpacityProperty,
                RepeatingAnimation(minimumOpacity, maximumOpacity, durationMilliseconds));
            haloScale.BeginAnimation(
                ScaleTransform.ScaleXProperty,
                RepeatingAnimation(minimumScale, maximumScale, durationMilliseconds));
            haloScale.BeginAnimation(
                ScaleTransform.ScaleYProperty,
                RepeatingAnimation(minimumScale, maximumScale, durationMilliseconds));

            if (!pulseDot)
            {
                return;
            }

            dotScale.BeginAnimation(
                ScaleTransform.ScaleXProperty,
                RepeatingAnimation(0.96, 1.05, durationMilliseconds));
            dotScale.BeginAnimation(
                ScaleTransform.ScaleYProperty,
                RepeatingAnimation(0.96, 1.05, durationMilliseconds));
        });
    }

    private void StartCompletedAnimation()
    {
        ForEachStatusIndicator((halo, dot, haloScale, dotScale) =>
        {
            halo.BeginAnimation(
                OpacityProperty,
                OneShotAnimation(0.34, 0, 520));
            haloScale.BeginAnimation(
                ScaleTransform.ScaleXProperty,
                OneShotAnimation(0.72, 1.5, 520));
            haloScale.BeginAnimation(
                ScaleTransform.ScaleYProperty,
                OneShotAnimation(0.72, 1.5, 520));
            BeginDotPop(dotScale);
        });
    }

    private void StartFailedAnimation()
    {
        ForEachStatusIndicator((halo, dot, haloScale, dotScale) =>
        {
            var flash = new DoubleAnimationUsingKeyFrames
            {
                Duration = TimeSpan.FromMilliseconds(440),
                FillBehavior = FillBehavior.Stop,
            };
            flash.KeyFrames.Add(new LinearDoubleKeyFrame(0.12, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            flash.KeyFrames.Add(new LinearDoubleKeyFrame(0.42, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(90))));
            flash.KeyFrames.Add(new LinearDoubleKeyFrame(0.1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(190))));
            flash.KeyFrames.Add(new LinearDoubleKeyFrame(0.4, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(290))));
            flash.KeyFrames.Add(new LinearDoubleKeyFrame(
                StatusHaloRestingOpacity,
                KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(440))));
            halo.BeginAnimation(OpacityProperty, flash);

            var dotPulse = OneShotAnimation(0.82, 1.16, 220);
            dotPulse.AutoReverse = true;
            dotScale.BeginAnimation(ScaleTransform.ScaleXProperty, dotPulse);
            dotScale.BeginAnimation(ScaleTransform.ScaleYProperty, dotPulse);
        });
    }

    private void StartInterruptedAnimation()
    {
        ForEachStatusIndicator((halo, dot, haloScale, dotScale) =>
        {
            halo.BeginAnimation(
                OpacityProperty,
                OneShotAnimation(0.28, 0.06, 360));
            haloScale.BeginAnimation(
                ScaleTransform.ScaleXProperty,
                OneShotAnimation(1.18, 0.78, 360));
            haloScale.BeginAnimation(
                ScaleTransform.ScaleYProperty,
                OneShotAnimation(1.18, 0.78, 360));
            dotScale.BeginAnimation(
                ScaleTransform.ScaleXProperty,
                OneShotAnimation(1, 0.82, 220));
            dotScale.BeginAnimation(
                ScaleTransform.ScaleYProperty,
                OneShotAnimation(1, 0.82, 220));
        });
    }

    private static void BeginDotPop(ScaleTransform dotScale)
    {
        var pop = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromMilliseconds(360),
            FillBehavior = FillBehavior.Stop,
        };
        pop.KeyFrames.Add(new SplineDoubleKeyFrame(
            0.68,
            KeyTime.FromTimeSpan(TimeSpan.Zero)));
        pop.KeyFrames.Add(new SplineDoubleKeyFrame(
            1.24,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(170)),
            new KeySpline(0.2, 0.8, 0.2, 1)));
        pop.KeyFrames.Add(new SplineDoubleKeyFrame(
            1,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(360)),
            new KeySpline(0.4, 0, 0.2, 1)));
        dotScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        dotScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
    }

    private static DoubleAnimation RepeatingAnimation(
        double from,
        double to,
        int durationMilliseconds) =>
        new(from, to, TimeSpan.FromMilliseconds(durationMilliseconds))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };

    private static DoubleAnimation OneShotAnimation(
        double from,
        double to,
        int durationMilliseconds) =>
        new(from, to, TimeSpan.FromMilliseconds(durationMilliseconds))
        {
            FillBehavior = FillBehavior.Stop,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };

    private void StopStatusIndicatorAnimations()
    {
        ForEachStatusIndicator((halo, dot, haloScale, dotScale) =>
        {
            halo.BeginAnimation(OpacityProperty, null);
            dot.BeginAnimation(OpacityProperty, null);
            halo.Opacity = StatusHaloRestingOpacity;
            dot.Opacity = 1;
            ResetScale(haloScale);
            ResetScale(dotScale);
        });
    }

    private static void ResetScale(ScaleTransform transform)
    {
        transform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        transform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        transform.ScaleX = 1;
        transform.ScaleY = 1;
    }

    private void ForEachStatusIndicator(
        Action<Ellipse, Ellipse, ScaleTransform, ScaleTransform> action)
    {
        action(
            HeaderStatusHalo,
            HeaderStatusDot,
            HeaderStatusHaloScale,
            HeaderStatusDotScale);
    }
}
