using System.Windows;
using Microsoft.Win32;

namespace D365FO.Compare.Desktop.ViewModels;

public interface IDataComparisonInteraction
{
    bool Confirm(string message);
    string? ExportPath(bool json);
}
public sealed class DataComparisonInteraction : IDataComparisonInteraction
{
    public bool Confirm(string message) => MessageBox.Show(message, "Data comparison", MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK;
    public string? ExportPath(bool json)
    {
        var dialog = new SaveFileDialog { Filter = json ? "JSON report|*.json" : "CSV report|*.csv", DefaultExt = json ? ".json" : ".csv", OverwritePrompt = true,
            FileName = "data-comparison-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}