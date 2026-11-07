using System.IO;
using System.Windows;
using Microsoft.Win32;
using D365FO.Compare.Infrastructure.Persistence;

namespace D365FO.Compare.Desktop.ViewModels;

public interface IDataComparisonInteraction
{
    bool Confirm(string message);
    string? ExportPath(bool json);
    string? SaveProfilePath(string suggestedFileName);
    string? LoadProfilePath();
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
    public string? SaveProfilePath(string suggestedFileName)
    {
        Directory.CreateDirectory(JsonDataComparisonProfileRepository.DefaultDirectory);
        var dialog = new SaveFileDialog
        {
            Filter = "Data comparison profile|*.profile",
            DefaultExt = ".profile",
            AddExtension = true,
            OverwritePrompt = true,
            InitialDirectory = JsonDataComparisonProfileRepository.DefaultDirectory,
            FileName = suggestedFileName
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
    public string? LoadProfilePath()
    {
        Directory.CreateDirectory(JsonDataComparisonProfileRepository.DefaultDirectory);
        var dialog = new OpenFileDialog
        {
            Filter = "Data comparison profile|*.profile",
            DefaultExt = ".profile",
            CheckFileExists = true,
            InitialDirectory = JsonDataComparisonProfileRepository.DefaultDirectory
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}