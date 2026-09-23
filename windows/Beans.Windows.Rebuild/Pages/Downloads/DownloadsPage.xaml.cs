using System.Collections.ObjectModel;
using Beans.Windows.Rebuild.Services.Downloads;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Beans.Windows.Rebuild.Pages.Downloads;

public sealed partial class DownloadsPage : UserControl
{
    private readonly IDownloadManager _manager;
    private readonly Window _ownerWindow;
    private readonly ObservableCollection<DownloadTaskSnapshot> _tasks = [];
    private bool _loaded;

    public DownloadsPage() : this(
        App.Services.GetRequiredService<IDownloadManager>(),
        App.Services.GetRequiredService<MainWindow>()) { }

    public DownloadsPage(IDownloadManager manager, Window ownerWindow)
    {
        _manager = manager;
        _ownerWindow = ownerWindow;
        InitializeComponent();
        TaskList.ItemsSource = _tasks;
        Loaded += DownloadsPage_Loaded;
        Unloaded += DownloadsPage_Unloaded;
    }

    private async void DownloadsPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        _manager.TasksChanged += Manager_TasksChanged;
        await RefreshAsync();
    }

    private void DownloadsPage_Unloaded(object sender, RoutedEventArgs e)
    {
        _manager.TasksChanged -= Manager_TasksChanged;
        _loaded = false;
    }

    private async void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.MusicLibrary };
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(_ownerWindow));
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;
            var result = await _manager.ConfigureDestinationDirectoryAsync(folder.Path);
            StatusText.Text = result.SafeMessage;
            await RefreshAsync();
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            StatusText.Text = "无法使用该下载目录，请重新选择";
        }
    }

    private async void Pause_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(sender, _manager.PauseAsync);
    private async void Resume_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(sender, _manager.ResumeAsync);
    private async void Cancel_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(sender, _manager.CancelAsync);
    private async void Remove_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(sender, _manager.RemoveAsync);

    private async Task ExecuteAsync(object sender, Func<string, CancellationToken, Task<DownloadOperationResult>> operation)
    {
        if (sender is not Button { Tag: DownloadTaskSnapshot task }) return;
        var result = await operation(task.Id, CancellationToken.None);
        StatusText.Text = result.SafeMessage;
        await RefreshAsync();
    }

    private void Manager_TasksChanged(object? sender, EventArgs e) =>
        DispatcherQueue.TryEnqueue(async () => await RefreshAsync());

    private async Task RefreshAsync()
    {
        var tasks = await _manager.GetTasksAsync();
        _tasks.Clear();
        foreach (var task in tasks) _tasks.Add(task);
        DestinationText.Text = _manager.DestinationDirectory;
        EmptyPanel.Visibility = tasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TaskList.Visibility = tasks.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        var active = tasks.Count(task => task.State is DownloadTaskState.Queued or DownloadTaskState.Resolving or DownloadTaskState.Downloading);
        var completed = tasks.Count(task => task.State == DownloadTaskState.Completed);
        SummaryText.Text = $"{tasks.Count} 个任务";
        if (active > 0) StatusText.Text = $"{active} 个进行中 · {completed} 个已完成";
        else if (tasks.Count > 0) StatusText.Text = $"{completed} 个已完成";
        else StatusText.Text = "尚无下载记录";
    }
}
