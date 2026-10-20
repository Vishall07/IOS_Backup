using System.Windows;
using IosBackupApp.Services;
using IosBackupApp.ViewModels;

namespace IosBackupApp;

public partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }

    public MainWindow()
    {
        InitializeComponent();

        var deviceService = new DeviceService();
        var localStorageService = new LocalStorageService();
        var diffEngine = new DiffEngine();
        var transferEngine = new TransferEngine(deviceService, localStorageService);

        ViewModel = new MainViewModel(deviceService, localStorageService, diffEngine, transferEngine);
        DataContext = ViewModel;

        ViewModel.ProcessLogs.CollectionChanged += (s, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add && ViewModel.ProcessLogs.Count > 0)
            {
                Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        if (ModalLogListBox?.Items.Count > 0)
                        {
                            ModalLogListBox.ScrollIntoView(ModalLogListBox.Items[^1]);
                        }
                        if (SidebarLogListBox?.Items.Count > 0)
                        {
                            SidebarLogListBox.ScrollIntoView(SidebarLogListBox.Items[^1]);
                        }
                    }
                    catch { }
                });
            }
        };

        Loaded += async (s, e) =>
        {
            await ViewModel.InitializeAsync();
        };
    }
}