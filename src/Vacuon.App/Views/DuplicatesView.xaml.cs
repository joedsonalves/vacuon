using System.Windows;
using System.Windows.Controls;
using Vacuon.App.ViewModels;

namespace Vacuon.App.Views;

/// <summary>
/// The duplicates screen — milestone M6.
/// <para>
/// The keeper of each group is rendered without a checkbox rather than with a disabled one.
/// A disabled control still says "this could have been selected"; leaving it out means the
/// screen cannot express deleting every copy of something, which is the failure mode a
/// duplicate finder has to make impossible rather than merely discourage.
/// </para>
/// </summary>
public partial class DuplicatesView : UserControl
{
    private MainViewModel? Model => DataContext as MainViewModel;

    public DuplicatesView()
    {
        InitializeComponent();

        // Stage 1 costs nothing and answers "what would this cost?", so it runs on arrival
        // rather than waiting for someone to commit to the expensive part first.
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true) Model?.MeasureDuplicateScope();
        };
    }

    private void OnStop(object sender, RoutedEventArgs e) => Model?.CancelDuplicateSearch();

    private void OnQuarantineSelected(object sender, RoutedEventArgs e)
    {
        MainViewModel? model = Model;
        if (model is null) return;

        Window owner = Window.GetWindow(this) ?? Application.Current.MainWindow;
        model.QuarantineDuplicatesCommand.Execute(owner);
    }
}
