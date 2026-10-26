// SEMANTIC ANCHOR: Edit ribbon form group delegates deletion to the existing viewer form-field operation.
// No independent field selection, editing, persistence or deletion state is introduced.
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace PewDF
{
    public partial class MainWindow
    {
        // SEMANTIC ANCHOR: Edit ribbon form group does not own selection. Both button states reflect
        // the active viewer and existing _currentTool. Do not introduce a second selection source.
        private bool _formRibbonEventsRegistered;

        private void FormRibbonTab_Loaded(object sender, RoutedEventArgs e)
        {
            if (!_formRibbonEventsRegistered)
            {
                _formRibbonEventsRegistered = true;
                AddHandler(Mouse.MouseUpEvent, new MouseButtonEventHandler(FormRibbonInteractionFinished), true);
                AddHandler(Keyboard.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(FormRibbonFocusChanged), true);
            }
            RefreshFormRibbonButtons();
        }

        private void FormRibbonInteractionFinished(object sender, MouseButtonEventArgs e)
            => QueueFormRibbonRefresh();

        private void FormRibbonFocusChanged(object sender, KeyboardFocusChangedEventArgs e)
            => QueueFormRibbonRefresh();

        private void QueueFormRibbonRefresh()
            => Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(RefreshFormRibbonButtons));

        private void RefreshFormRibbonButtons()
        {
            if (FormRibbonSelectButton is null || FormRibbonDeleteButton is null) return;
            FormRibbonSelectButton.Tag = _currentTool == EditTool.Select ? "selected" : null;
            // Do not display deletion unless the viewer confirms an actual selected field.
            FormRibbonDeleteButton.Visibility = ActiveViewer != null && ActiveViewer.HasSelectedFormFieldExt
                ? Visibility.Visible : Visibility.Collapsed;
        }

        private void FormRibbonDeleteSelected_Click(object sender, RoutedEventArgs e)
        {
            if (ActiveViewer.HasSelectedFormFieldExt)
            {
                ActiveViewer.DeleteSelectedFormFieldExt();
                RefreshFormRibbonButtons();
            }
        }
    }
}
