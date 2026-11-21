using System.Windows.Input;

namespace LitSSHmcp.App.ViewModels;

public partial class MainViewModel
{
    private FileTransferManageViewModel? _fileTransferPage;
    private ICommand? _navigateFileTransferCommand;

    public FileTransferManageViewModel? FileTransferPage
    {
        get => _fileTransferPage;
        private set { _fileTransferPage = value; OnPropertyChanged(); }
    }

    public ICommand NavigateFileTransferCommand =>
        _navigateFileTransferCommand ??= new RelayCommand(_ => NavigateFileTransfer());

    private void NavigateFileTransfer()
    {
        CurrentPage = MainPage.FileTransfer;
        FileTransferPage ??= new FileTransferManageViewModel(
            _configService,
            _dialogs,
            _windows,
            _services.Ssh,
            _services.CreateAuditLog());
        EnsureFileTransferPageView();
        _ = FileTransferPage.LoadAsync();
    }
}
