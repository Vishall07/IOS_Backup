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

        Loaded += async (s, e) =>
        {
            await ViewModel.InitializeAsync();
        };
    }
}