using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace iPodManager
{
    public partial class MainWindow
    {
        private readonly Dictionary<string, string> _tooltipMessages =
            new(StringComparer.OrdinalIgnoreCase);
        private string? _activeTooltipKey;

        private void LoadTooltipCatalog()
        {
            TooltipText.Text = string.Empty;

            try
            {
                Uri resourceUri = new("Assets/Data/tooltips.json", UriKind.Relative);
                using System.IO.Stream? stream =
                    Application.GetResourceStream(resourceUri)?.Stream;
                if (stream == null)
                    return;

                using JsonDocument document = JsonDocument.Parse(stream);
                if (!document.RootElement.TryGetProperty("tooltips", out JsonElement tooltips))
                    return;

                foreach (JsonProperty tooltip in tooltips.EnumerateObject())
                    _tooltipMessages[tooltip.Name] = tooltip.Value.GetString() ?? string.Empty;
            }
            catch (JsonException)
            {
                // An invalid catalog leaves the tooltip bar empty without affecting the UI.
            }
        }

        private void TooltipTarget_MouseEnter(object sender, MouseEventArgs e)
        {
            PlayMenuSelectSoundForTooltipTarget(sender);
            QueueTooltipRefresh();
        }

        private void TooltipTarget_MouseLeave(object sender, MouseEventArgs e)
        {
            QueueTooltipRefresh();
        }

        private void QueueTooltipRefresh()
        {
            Dispatcher.BeginInvoke(
                DispatcherPriority.Input,
                new Action(RefreshTooltipFromMousePosition));
        }

        private void RefreshTooltipFromMousePosition()
        {
            DependencyObject? current =
                InputHitTest(Mouse.GetPosition(this)) as DependencyObject;

            while (current != null)
            {
                if (current is FrameworkElement element &&
                    element.Tag is string key &&
                    _tooltipMessages.TryGetValue(key, out string? tooltip))
                {
                    ShowTooltipText(key, tooltip);
                    return;
                }

                current = GetTooltipVisualParent(current);
            }

            _activeTooltipKey = null;
            StopTrackTitleMarquee(TooltipTextViewport);
            TooltipText.Text = string.Empty;
            TooltipTextViewport.OpacityMask = null;
        }

        private static DependencyObject? GetTooltipVisualParent(DependencyObject element)
        {
            if (element is Visual || element is Visual3D)
                return VisualTreeHelper.GetParent(element);

            return LogicalTreeHelper.GetParent(element);
        }

        private void ShowTooltipText(string key, string tooltip)
        {
            if (string.Equals(_activeTooltipKey, key, StringComparison.OrdinalIgnoreCase))
                return;

            _activeTooltipKey = key;
            TooltipText.Text = tooltip;
            QueueTrackTitleMarqueeUpdate(TooltipTextViewport);
        }
    }
}
