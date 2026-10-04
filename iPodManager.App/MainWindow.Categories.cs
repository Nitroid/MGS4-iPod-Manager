using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace iPodManager
{
    public partial class MainWindow
    {
        public ObservableCollection<CategoryItem> CategoryItems { get; } =
            new ObservableCollection<CategoryItem>();

        private Border? _heldCategorySelectedHighlight;

        private void CategoryList_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key is Key.Up or Key.Down or Key.Left or Key.Right)
                e.Handled = true;
        }

        private void CategoryList_PreviewMouseLeftButtonDown(
            object sender,
            MouseButtonEventArgs e)
        {
            ReleaseHeldCategoryHighlight();

            if (e.ChangedButton != MouseButton.Left ||
                CategoryList.SelectedItem == null ||
                !(e.OriginalSource is DependencyObject source))
            {
                return;
            }

            ListBoxItem? pressedItem = FindVisualParent<ListBoxItem>(source);

            if (pressedItem == null || pressedItem.DataContext == CategoryList.SelectedItem)
                return;

            if (!(CategoryList.ItemContainerGenerator.ContainerFromItem(CategoryList.SelectedItem)
                  is ListBoxItem selectedItem))
            {
                return;
            }

            selectedItem.ApplyTemplate();
            _heldCategorySelectedHighlight =
                selectedItem.Template.FindName("SelectedHighlight", selectedItem) as Border;

            if (_heldCategorySelectedHighlight != null)
                _heldCategorySelectedHighlight.Opacity = 0.34;
        }

        private void CategoryList_PreviewMouseLeftButtonUp(
            object sender,
            MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                ReleaseHeldCategoryHighlight();
            }
        }

        private void CategoryList_LostMouseCapture(object sender, MouseEventArgs e)
        {
            ReleaseHeldCategoryHighlight();
        }

        private void ReleaseHeldCategoryHighlight()
        {
            if (_heldCategorySelectedHighlight == null)
                return;

            _heldCategorySelectedHighlight.ClearValue(OpacityProperty);
            _heldCategorySelectedHighlight = null;
        }

    }
}
