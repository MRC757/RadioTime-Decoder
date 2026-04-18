using System.Windows;
using System.Windows.Controls;

namespace WwvDecoder.Stations;

public partial class StationReferenceWindow : Window
{
    public StationReferenceWindow()
    {
        InitializeComponent();
        StationGrid.ItemsSource = StationsDatabase.All;
        StationGrid.SelectionChanged += OnSelectionChanged;
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (StationGrid.SelectedItem is StationInfo station)
            NotesBlock.Text = station.Notes;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
