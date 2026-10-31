using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Data;
using System.Windows.Input;
using LitSSHmcp.App.Services;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Datasource;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.App.ViewModels;

public sealed class DatabaseUserItem
{
    public string Name { get; init; } = string.Empty;
    public string Host { get; init; } = string.Empty;
    public string DisplayName => string.IsNullOrWhiteSpace(Host) ? Name : $"{Name}@{Host}";
    public bool IsCurrent { get; init; }
    public bool CanLogin { get; init; }
    public bool IsSuperuser { get; init; }
    public bool IsLocked { get; init; }
    public bool PasswordExpired { get; init; }
    public bool CanCreateDb { get; init; }
    public bool CanCreateRole { get; init; }
    public string AccountStatus { get; init; } = string.Empty;
    public string PasswordStatus { get; init; } = string.Empty;
    public string RoleAttributes { get; init; } = string.Empty;
    public string GrantSummary { get; init; } = string.Empty;
    public string Key => DisplayName;
}

public sealed class DatabasePermissionToggle : INotifyPropertyChanged
{
    private bool _isGranted;

    public DatabasePermissionToggle(string name, bool granted, bool inherited = false)
    {
        Name = name;
        IsInherited = inherited;
        _isGranted = granted || inherited;
        Original = _isGranted;
    }

    public string Name { get; }
    public bool Original { get; }
    public bool IsInherited { get; }
    public bool CanEdit => !IsInherited;
    public string? InheritanceTooltip => IsInherited ? "继承自数据库级权限；如需撤权，请修改数据库级权限" : null;
    public bool IsGranted
    {
        get => _isGranted;
        set
        {
            if (_isGranted == value) return;
            _isGranted = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class DatabasePermissionRow : INotifyPropertyChanged
{
    public required string Label { get; init; }
    public required string Kind { get; init; }
    public string Database { get; init; } = string.Empty;
    public string Schema { get; init; } = string.Empty;
    public string Table { get; init; } = string.Empty;
    public string Owner { get; init; } = string.Empty;
    public bool IsSystem { get; init; }
    public string InheritanceHint => Toggles.Any(toggle => toggle.IsInherited) ? "部分权限继承自数据库级" : string.Empty;
    public ObservableCollection<DatabasePermissionToggle> Toggles { get; } = new();
    public bool HasChanges => Toggles.Any(toggle => toggle.IsGranted != toggle.Original);
    public event Action? Changed;

    public void AddToggle(DatabasePermissionToggle toggle)
    {
        toggle.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(DatabasePermissionToggle.IsGranted)) return;
            OnPropertyChanged(nameof(HasChanges));
            Changed?.Invoke();
        };
        Toggles.Add(toggle);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class DatabasePermissionDiffItem
{
    public required string Change { get; init; }
    public required string Object { get; init; }
    public required string Detail { get; init; }
}

public sealed class DatabaseSnapshotRow
{
    public string Kind { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Database { get; set; } = string.Empty;
    public string Schema { get; set; } = string.Empty;
    public string Table { get; set; } = string.Empty;
    public string Owner { get; set; } = string.Empty;
    public List<string> Granted { get; set; } = new();
}

public sealed class DatabasePermissionSnapshot
{
    public string DataSourceId { get; set; } = string.Empty;
    public string DataSourceName { get; set; } = string.Empty;
    public string Backend { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public List<DatabaseSnapshotRow> Rows { get; set; } = new();
}

public sealed class DatabasePermissionSnapshotItem
{
    public required string FilePath { get; init; }
    public required DateTime CreatedAt { get; init; }
    public required DatabasePermissionSnapshot Snapshot { get; init; }
    public string CreatedAtText => CreatedAt.ToString("yyyy-MM-dd HH:mm:ss");
    public string Summary => $"{Snapshot.Rows.Count} 个对象 · {Snapshot.Backend}";
}

public sealed class DatabaseSnapshotDetailItem
{
    public required string Object { get; init; }
    public required string Kind { get; init; }
    public string Owner { get; init; } = string.Empty;
    public required string Permissions { get; init; }
}

public sealed class PostgresRoleMembershipItem
{
    public required string RoleName { get; init; }
    public required string MemberName { get; init; }
    public required string Direction { get; init; }
    public string AdminOption { get; init; } = string.Empty;
    public string Grantor { get; init; } = string.Empty;
    public string Summary => $"{RoleName} → {MemberName}（{Direction}）";
}

public sealed class PostgresDefaultPrivilegeItem
{
    public required string Schema { get; init; }
    public required string Owner { get; init; }
    public required string ObjectType { get; init; }
    public required string Grantee { get; init; }
    public required string Privilege { get; init; }
    public bool IsGrantable { get; init; }
    public string GrantableText => IsGrantable ? "可转授" : "不可转授";
}

public sealed class DatabaseUserManagerViewModel : INotifyPropertyChanged
{
    private readonly DataSourceConfig _datasource;
    private readonly IDatasourceDriver _driver;
    private readonly IUserDialogService _dialogs;
    private readonly IAuditLogService? _audit;
    private readonly List<string> _pendingStatements = new();
    private string? _loadedUserKey;
    private string? _postgresAdvancedLoadedKey;
    private bool _permissionsLoading;
    private bool _busy;
    private bool _suppressSql;

    private static readonly string[] MySqlDatabasePrivileges =
    {
        "SELECT", "INSERT", "UPDATE", "DELETE", "CREATE", "DROP", "ALTER", "INDEX",
        "REFERENCES", "CREATE VIEW", "SHOW VIEW", "TRIGGER", "EXECUTE"
    };

    private static readonly string[] MySqlTablePrivileges =
    {
        "SELECT", "INSERT", "UPDATE", "DELETE", "CREATE", "DROP", "ALTER", "INDEX",
        "REFERENCES", "CREATE VIEW", "SHOW VIEW", "TRIGGER"
    };

    private static readonly string[] PostgresDatabasePrivileges = { "CONNECT", "CREATE", "TEMP" };
    private static readonly string[] PostgresSchemaPrivileges = { "USAGE", "CREATE" };
    private static readonly string[] PostgresTablePrivileges =
    {
        "SELECT", "INSERT", "UPDATE", "DELETE", "TRUNCATE", "REFERENCES", "TRIGGER"
    };

    public DatabaseUserManagerViewModel(DataSourceConfig datasource, IDatasourceDriverRegistry registry,
        IUserDialogService? dialogs = null, IAuditLogService? audit = null)
    {
        _datasource = datasource;
        _driver = registry.GetRequired(datasource.Type);
        _dialogs = dialogs ?? new UserDialogService();
        _audit = audit;
        RefreshCommand = new RelayCommand(_ => _ = RefreshAsync(), _ => !IsBusy);
        ExecuteCommand = new RelayCommand(_ => _ = ExecuteAsync(), _ => !IsBusy && HasSqlChanges);
        DeleteUserCommand = new RelayCommand(_ => _ = DeleteUserAsync(), _ => !IsBusy && SelectedUser is not null);
        RollbackSnapshotCommand = new RelayCommand(_ => ApplySelectedSnapshot(), _ => !IsBusy && SelectedSnapshot is not null);
        RefreshSnapshotsCommand = new RelayCommand(_ => LoadSnapshots(SelectedUser), _ => SelectedUser is not null);
        RefreshPreviewCommand = new RelayCommand(_ => RebuildSqlPreview(), _ => SelectedUser is not null);
        DeleteSnapshotCommand = new RelayCommand(_ => DeleteSelectedSnapshot(), _ => !IsBusy && SelectedSnapshot is not null);
        OpenSnapshotFolderCommand = new RelayCommand(_ => OpenSnapshotFolder());
        LockAccountCommand = new RelayCommand(_ => _ = LockAccountAsync(), _ => CanLockAccount);
        UnlockAccountCommand = new RelayCommand(_ => _ = UnlockAccountAsync(), _ => CanUnlockAccount);
        ExpirePasswordCommand = new RelayCommand(_ => _ = ExpirePasswordAsync(), _ => CanExpirePassword);
        ClearPasswordExpiryCommand = new RelayCommand(_ => _ = ClearPasswordExpiryAsync(), _ => CanClearPasswordExpiry);
        ToggleLoginCommand = new RelayCommand(_ => _ = ToggleLoginAsync(), _ => CanToggleLogin);
        ToggleCreateDbCommand = new RelayCommand(_ => _ = ToggleCreateDbAsync(), _ => CanToggleCreateDb);
        ToggleCreateRoleCommand = new RelayCommand(_ => _ = ToggleCreateRoleAsync(), _ => CanToggleCreateRole);
        GrantRoleCommand = new RelayCommand(_ => _ = GrantRoleAsync(), _ => CanGrantRole);
        RevokeRoleCommand = new RelayCommand(_ => _ = RevokeRoleAsync(), _ => CanRevokeRole);
        AddDefaultPrivilegeCommand = new RelayCommand(_ => _ = AddDefaultPrivilegeAsync(), _ => CanAddDefaultPrivilege);
        RevokeDefaultPrivilegeCommand = new RelayCommand(_ => _ = RevokeDefaultPrivilegeAsync(), _ => CanRevokeDefaultPrivilege);
        RefreshPostgresAdvancedCommand = new RelayCommand(_ => _ = ReloadPostgresAdvancedAsync(),
            _ => IsPostgresBackend && SelectedUser is not null && !IsBusy && !_permissionsLoading);
        UsersView = CollectionViewSource.GetDefaultView(Users);
        UsersView.Filter = FilterUser;
        PermissionScopes.Add("全部");
        PermissionPrevPageCommand = new RelayCommand(_ => ChangePermissionPage(-1), _ => PermissionPage > 1);
        PermissionNextPageCommand = new RelayCommand(_ => ChangePermissionPage(1), _ => PermissionPage < PermissionPageCount);
        SelectAllVisibleTablesCommand = new RelayCommand(_ => SetVisibleTables(true), _ => CanEditPermissions);
        ClearVisibleTablesCommand = new RelayCommand(_ => SetVisibleTables(false), _ => CanEditPermissions);
    }

    public DataSourceConfig Datasource => _datasource;
    public string Title => $"数据库用户管理 - {_datasource.Name}";
    public string Subtitle => $"{BackendText} · {_datasource.Host}:{_datasource.Port}";
    public string BackendText => _datasource.Type.Equals("mysql", StringComparison.OrdinalIgnoreCase) ? "MySQL" : "PostgreSQL";
    public ObservableCollection<DatabaseUserItem> Users { get; } = new();
    public ICollectionView UsersView { get; }
    public ObservableCollection<DatabasePermissionRow> PermissionRows { get; } = new();
    public ObservableCollection<DatabasePermissionRow> PagedPermissionRows { get; } = new();
    public ObservableCollection<string> PermissionScopes { get; } = new();
    public ObservableCollection<DatabasePermissionDiffItem> Diffs { get; } = new();
    public ObservableCollection<DatabasePermissionSnapshotItem> Snapshots { get; } = new();
    public ObservableCollection<DatabaseSnapshotDetailItem> SnapshotDetails { get; } = new();
    public ObservableCollection<PostgresRoleMembershipItem> RoleMemberships { get; } = new();
    public ObservableCollection<string> GrantableRoles { get; } = new();
    public ObservableCollection<PostgresDefaultPrivilegeItem> DefaultPrivileges { get; } = new();
    public ObservableCollection<string> DefaultPrivilegeSchemas { get; } = new();
    public ObservableCollection<string> DefaultPrivilegeGrantees { get; } = new();
    public IReadOnlyList<string> DefaultPrivilegeObjectTypes { get; } =
        new[] { "TABLES", "SEQUENCES", "FUNCTIONS", "TYPES", "SCHEMAS" };
    public IReadOnlyList<string> DefaultPrivilegePrivileges { get; } =
        new[] { "ALL PRIVILEGES", "SELECT", "INSERT", "UPDATE", "DELETE", "TRUNCATE", "REFERENCES", "TRIGGER", "USAGE", "CREATE", "CONNECT", "TEMP", "EXECUTE" };

    public ICommand RefreshCommand { get; }
    public ICommand ExecuteCommand { get; }
    public ICommand DeleteUserCommand { get; }
    public ICommand RollbackSnapshotCommand { get; }
    public ICommand RefreshSnapshotsCommand { get; }
    public ICommand RefreshPreviewCommand { get; }
    public ICommand DeleteSnapshotCommand { get; }
    public ICommand OpenSnapshotFolderCommand { get; }
    public ICommand PermissionPrevPageCommand { get; }
    public ICommand PermissionNextPageCommand { get; }
    public ICommand SelectAllVisibleTablesCommand { get; }
    public ICommand ClearVisibleTablesCommand { get; }
    public ICommand LockAccountCommand { get; }
    public ICommand UnlockAccountCommand { get; }
    public ICommand ExpirePasswordCommand { get; }
    public ICommand ClearPasswordExpiryCommand { get; }
    public ICommand ToggleLoginCommand { get; }
    public ICommand ToggleCreateDbCommand { get; }
    public ICommand ToggleCreateRoleCommand { get; }
    public ICommand GrantRoleCommand { get; }
    public ICommand RevokeRoleCommand { get; }
    public ICommand AddDefaultPrivilegeCommand { get; }
    public ICommand RevokeDefaultPrivilegeCommand { get; }
    public ICommand RefreshPostgresAdvancedCommand { get; }
    public bool IsMySqlBackend => IsMySql;
    public bool IsPostgresBackend => !IsMySql;
    public string LoginActionText => SelectedUser?.CanLogin == true ? "禁止登录" : "允许登录";
    public string CreateDbActionText => SelectedUser?.CanCreateDb == true ? "禁止 CREATEDB" : "允许 CREATEDB";
    public string CreateRoleActionText => SelectedUser?.CanCreateRole == true ? "禁止 CREATEROLE" : "允许 CREATEROLE";

    private bool _isPostgresAdvancedSelected;
    public bool IsPostgresAdvancedSelected
    {
        get => _isPostgresAdvancedSelected;
        set
        {
            if (_isPostgresAdvancedSelected == value) return;
            _isPostgresAdvancedSelected = value;
            OnPropertyChanged();
            if (value) _ = EnsurePostgresAdvancedAsync();
        }
    }

    private PostgresRoleMembershipItem? _selectedRoleMembership;
    public PostgresRoleMembershipItem? SelectedRoleMembership
    {
        get => _selectedRoleMembership;
        set
        {
            if (ReferenceEquals(_selectedRoleMembership, value)) return;
            _selectedRoleMembership = value;
            OnPropertyChanged();
            (RevokeRoleCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    private string _selectedRoleToGrant = string.Empty;
    public string SelectedRoleToGrant
    {
        get => _selectedRoleToGrant;
        set
        {
            if (_selectedRoleToGrant == value) return;
            _selectedRoleToGrant = value;
            OnPropertyChanged();
            (GrantRoleCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    private string _roleMembershipSummary = "尚未读取角色成员关系。";
    public string RoleMembershipSummary
    {
        get => _roleMembershipSummary;
        private set { if (_roleMembershipSummary == value) return; _roleMembershipSummary = value; OnPropertyChanged(); }
    }

    private PostgresDefaultPrivilegeItem? _selectedDefaultPrivilege;
    public PostgresDefaultPrivilegeItem? SelectedDefaultPrivilege
    {
        get => _selectedDefaultPrivilege;
        set
        {
            if (ReferenceEquals(_selectedDefaultPrivilege, value)) return;
            _selectedDefaultPrivilege = value;
            OnPropertyChanged();
            (RevokeDefaultPrivilegeCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    private string _selectedDefaultPrivilegeSchema = "public";
    public string SelectedDefaultPrivilegeSchema
    {
        get => _selectedDefaultPrivilegeSchema;
        set
        {
            if (_selectedDefaultPrivilegeSchema == value) return;
            _selectedDefaultPrivilegeSchema = value;
            OnPropertyChanged();
            (AddDefaultPrivilegeCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    private string _selectedDefaultPrivilegeGrantee = string.Empty;
    public string SelectedDefaultPrivilegeGrantee
    {
        get => _selectedDefaultPrivilegeGrantee;
        set
        {
            if (_selectedDefaultPrivilegeGrantee == value) return;
            _selectedDefaultPrivilegeGrantee = value;
            OnPropertyChanged();
            (AddDefaultPrivilegeCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    private string _selectedDefaultPrivilegeObjectType = "TABLES";
    public string SelectedDefaultPrivilegeObjectType
    {
        get => _selectedDefaultPrivilegeObjectType;
        set
        {
            if (_selectedDefaultPrivilegeObjectType == value) return;
            _selectedDefaultPrivilegeObjectType = value;
            OnPropertyChanged();
        }
    }

    private string _selectedDefaultPrivilegePrivilege = "ALL PRIVILEGES";
    public string SelectedDefaultPrivilegePrivilege
    {
        get => _selectedDefaultPrivilegePrivilege;
        set
        {
            if (_selectedDefaultPrivilegePrivilege == value) return;
            _selectedDefaultPrivilegePrivilege = value;
            OnPropertyChanged();
        }
    }

    private string _defaultPrivilegeSummary = "尚未读取默认权限。";
    public string DefaultPrivilegeSummary
    {
        get => _defaultPrivilegeSummary;
        private set { if (_defaultPrivilegeSummary == value) return; _defaultPrivilegeSummary = value; OnPropertyChanged(); }
    }

    public bool IsBusy
    {
        get => _busy;
        private set
        {
            if (_busy == value) return;
            _busy = value;
            OnPropertyChanged();
            (RefreshCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (ExecuteCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (DeleteUserCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (RollbackSnapshotCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (RefreshSnapshotsCommand as RelayCommand)?.RaiseCanExecuteChanged();
            RaisePageCommands();
            RaiseAccountCommands();
        }
    }

    private string _statusText = "点击刷新读取数据库用户和权限。";
    public string StatusText
    {
        get => _statusText;
        private set { if (_statusText == value) return; _statusText = value; OnPropertyChanged(); }
    }

    private string _currentUserText = "—";
    public string CurrentUserText
    {
        get => _currentUserText;
        private set { if (_currentUserText == value) return; _currentUserText = value; OnPropertyChanged(); }
    }

    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (_searchText == value) return;
            _searchText = value;
            OnPropertyChanged();
            UsersView.Refresh();
        }
    }

    private DatabaseUserItem? _selectedUser;
    public DatabaseUserItem? SelectedUser
    {
        get => _selectedUser;
        set
        {
            if (ReferenceEquals(_selectedUser, value)) return;
            _selectedUser = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LoginActionText));
            OnPropertyChanged(nameof(CreateDbActionText));
            OnPropertyChanged(nameof(CreateRoleActionText));
            (DeleteUserCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (RefreshSnapshotsCommand as RelayCommand)?.RaiseCanExecuteChanged();
            RaisePageCommands();
            RaiseAccountCommands();
            LoadSnapshots(value);
            if (value is not null && !string.Equals(_loadedUserKey, value.Key, StringComparison.Ordinal))
                _ = LoadPermissionsAsync(value);
        }
    }

    private string _permissionSearchText = string.Empty;
    public string PermissionSearchText
    {
        get => _permissionSearchText;
        set
        {
            if (_permissionSearchText == value) return;
            _permissionSearchText = value;
            OnPropertyChanged();
            PermissionPage = 1;
            RefreshPagedPermissions();
        }
    }

    private bool _showOnlyChangedPermissions;
    public bool ShowOnlyChangedPermissions
    {
        get => _showOnlyChangedPermissions;
        set
        {
            if (_showOnlyChangedPermissions == value) return;
            _showOnlyChangedPermissions = value;
            OnPropertyChanged();
            PermissionPage = 1;
            RefreshPagedPermissions();
        }
    }

    private string _selectedPermissionScope = "全部";
    public string SelectedPermissionScope
    {
        get => _selectedPermissionScope;
        set
        {
            if (_selectedPermissionScope == value) return;
            _selectedPermissionScope = value;
            OnPropertyChanged();
            PermissionPage = 1;
            RefreshPagedPermissions();
        }
    }

    public const int PermissionPageSize = 200;
    private int _permissionPage = 1;
    public int PermissionPage
    {
        get => _permissionPage;
        private set { if (_permissionPage == value) return; _permissionPage = value; OnPropertyChanged(); OnPropertyChanged(nameof(PermissionPageText)); RaisePageCommands(); }
    }
    private int _permissionPageCount = 1;
    public int PermissionPageCount
    {
        get => _permissionPageCount;
        private set
        {
            if (_permissionPageCount == value) return;
            _permissionPageCount = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PermissionPageText));
        }
    }
    public string PermissionPageText => $"第 {PermissionPage} / {PermissionPageCount} 页";
    private bool CanEditPermissions => SelectedUser is not null && !IsBusy;
    private bool CanManageAccount => !IsBusy && SelectedUser is { IsCurrent: false };
    private bool CanManagePostgresAccount => CanManageAccount && SelectedUser is { IsSuperuser: false };
    private bool CanLockAccount => CanManageAccount && IsMySql && SelectedUser is { IsLocked: false };
    private bool CanUnlockAccount => CanManageAccount && IsMySql && SelectedUser is { IsLocked: true };
    private bool CanExpirePassword => CanManageAccount && IsMySql && SelectedUser is { PasswordExpired: false };
    private bool CanClearPasswordExpiry => CanManageAccount && IsMySql && SelectedUser is { PasswordExpired: true };
    private bool CanToggleLogin => CanManagePostgresAccount;
    private bool CanToggleCreateDb => CanManagePostgresAccount;
    private bool CanToggleCreateRole => CanManagePostgresAccount;
    private bool CanGrantRole => CanManagePostgresAccount && SelectedUser is { } user &&
                                 !string.IsNullOrWhiteSpace(SelectedRoleToGrant) &&
                                 !string.Equals(SelectedRoleToGrant, user.Name, StringComparison.Ordinal);
    private bool CanRevokeRole => CanManagePostgresAccount && SelectedRoleMembership is not null;
    private bool CanAddDefaultPrivilege => CanManagePostgresAccount &&
                                           !string.IsNullOrWhiteSpace(SelectedDefaultPrivilegeSchema) &&
                                           !string.IsNullOrWhiteSpace(SelectedDefaultPrivilegeGrantee) &&
                                           !string.IsNullOrWhiteSpace(SelectedDefaultPrivilegeObjectType) &&
                                           !string.IsNullOrWhiteSpace(SelectedDefaultPrivilegePrivilege);
    private bool CanRevokeDefaultPrivilege => CanManagePostgresAccount && SelectedDefaultPrivilege is not null;

    private string _permissionFilterSummary = string.Empty;
    public string PermissionFilterSummary
    {
        get => _permissionFilterSummary;
        private set { if (_permissionFilterSummary == value) return; _permissionFilterSummary = value; OnPropertyChanged(); }
    }

    private DatabasePermissionSnapshotItem? _selectedSnapshot;
    public DatabasePermissionSnapshotItem? SelectedSnapshot
    {
        get => _selectedSnapshot;
        set
        {
            if (ReferenceEquals(_selectedSnapshot, value)) return;
            _selectedSnapshot = value;
            OnPropertyChanged();
            LoadSnapshotDetails(value);
            (RollbackSnapshotCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (DeleteSnapshotCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    private string _grantSummary = "—";
    public string GrantSummary
    {
        get => _grantSummary;
        private set { if (_grantSummary == value) return; _grantSummary = value; OnPropertyChanged(); }
    }

    private string _previewSummary = "暂无变更（勾选权限后自动生成）";
    public string PreviewSummary
    {
        get => _previewSummary;
        private set { if (_previewSummary == value) return; _previewSummary = value; OnPropertyChanged(); }
    }

    private string _sqlPreview = string.Empty;
    public string SqlPreview
    {
        get => _sqlPreview;
        private set
        {
            if (_sqlPreview == value) return;
            _sqlPreview = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSqlChanges));
            (ExecuteCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public bool HasSqlChanges => _pendingStatements.Count > 0;

    private string _newUserName = string.Empty;
    public string NewUserName
    {
        get => _newUserName;
        set { if (_newUserName == value) return; _newUserName = value; OnPropertyChanged(); }
    }

    private string _newUserHost = "%";
    public string NewUserHost
    {
        get => _newUserHost;
        set { if (_newUserHost == value) return; _newUserHost = value; OnPropertyChanged(); }
    }

    private bool _newUserCanLogin = true;
    public bool NewUserCanLogin
    {
        get => _newUserCanLogin;
        set { if (_newUserCanLogin == value) return; _newUserCanLogin = value; OnPropertyChanged(); }
    }

    private async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusText = "正在读取数据库用户…";
        var previousKey = SelectedUser?.Key;
        try
        {
            Users.Clear();
            PermissionRows.Clear();
            SqlPreview = string.Empty;
            _pendingStatements.Clear();
            _loadedUserKey = null;
            _postgresAdvancedLoadedKey = null;

            var current = await QueryAsync(CurrentUserSql, 1);
            CurrentUserText = current.Success && current.Rows.Count > 0
                ? Convert.ToString(current.Rows[0].Values.FirstOrDefault()) ?? "—"
                : "—";

            var result = await QueryAsync(UserListSql, 1000);
            if (!result.Success && IsMySql)
                result = await QueryAsync("SELECT user, host FROM mysql.user ORDER BY user, host", 1000);
            if (!result.Success)
            {
                StatusText = "读取用户失败：" + result.Error;
                return;
            }

            foreach (var row in result.Rows)
                Users.Add(ParseUser(row));
            StatusText = $"已读取 {Users.Count} 个用户。";

            SelectedUser = Users.FirstOrDefault(user => string.Equals(user.Key, previousKey, StringComparison.Ordinal))
                           ?? Users.FirstOrDefault(user => user.IsCurrent)
                           ?? Users.FirstOrDefault();
        }
        catch (Exception ex)
        {
            StatusText = "读取失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private string CurrentUserSql => IsMySql ? "SELECT CURRENT_USER()" : "SELECT current_user";

    private string UserListSql => IsMySql
        ? "SELECT user, host, account_locked, password_expired, password_last_changed FROM mysql.user ORDER BY user, host"
        : "SELECT rolname, rolsuper, rolcanlogin, rolcreatedb, rolcreaterole, rolvaliduntil FROM pg_roles WHERE rolname NOT LIKE 'pg_%' ORDER BY rolname";

    private bool IsMySql => _datasource.Type.Equals("mysql", StringComparison.OrdinalIgnoreCase);

    private DatabaseUserItem ParseUser(Dictionary<string, object?> row)
    {
        if (IsMySql)
        {
            var user = Text(row, "user");
            var host = Text(row, "host");
            var locked = Bool(row, "account_locked");
            var expired = Bool(row, "password_expired");
            return new DatabaseUserItem
            {
                Name = user,
                Host = host,
                CanLogin = !locked,
                IsLocked = locked,
                IsCurrent = string.Equals($"{user}@{host}", CurrentUserText, StringComparison.OrdinalIgnoreCase),
                AccountStatus = locked ? "已锁定" : "正常",
                PasswordStatus = expired ? "密码已过期" : "正常",
                RoleAttributes = $"plugin={Text(row, "plugin")}",
                GrantSummary = string.Empty
            };
        }

        var name = Text(row, "rolname");
        var super = Bool(row, "rolsuper");
        var login = Bool(row, "rolcanlogin");
        var createDb = Bool(row, "rolcreatedb");
        var createRole = Bool(row, "rolcreaterole");
        var valid = Text(row, "rolvaliduntil");
        return new DatabaseUserItem
        {
            Name = name,
            CanLogin = login,
            IsSuperuser = super,
            IsCurrent = string.Equals(name, CurrentUserText, StringComparison.OrdinalIgnoreCase),
            AccountStatus = login ? "可登录" : "不可登录",
            PasswordStatus = string.IsNullOrWhiteSpace(valid) ? "无到期时间" : "到期 " + valid,
            RoleAttributes = $"super={super}, createdb={createDb}, createrole={createRole}",
            GrantSummary = string.Empty
        };
    }

    private async Task LoadPermissionsAsync(DatabaseUserItem user)
    {
        _loadedUserKey = user.Key;
        _postgresAdvancedLoadedKey = null;
        _permissionsLoading = true;
        PermissionRows.Clear();
        RoleMemberships.Clear();
        GrantableRoles.Clear();
        DefaultPrivileges.Clear();
        DefaultPrivilegeSchemas.Clear();
        DefaultPrivilegeGrantees.Clear();
        SelectedRoleMembership = null;
        SelectedDefaultPrivilege = null;
        SqlPreview = string.Empty;
        _pendingStatements.Clear();
        GrantSummary = "读取中…";
        StatusText = "正在读取权限…";
        try
        {
            if (IsMySql)
                await LoadMySqlPermissionsAsync(user);
            else
                await LoadPostgresPermissionsAsync(user);
            GrantSummary = string.IsNullOrWhiteSpace(GrantSummary) ? "未读取到权限" : GrantSummary;
            StatusText = PermissionRows.Count == 0
                ? "未读取到可管理的数据库/表对象：" + GrantSummary
                : $"已加载 {PermissionRows.Count} 个权限对象。";
        }
        catch (Exception ex)
        {
            GrantSummary = "读取失败：" + ex.Message;
            StatusText = "读取权限失败：" + ex.Message;
        }
        finally
        {
            _permissionsLoading = false;
            RebuildSqlPreview();
            RefreshPermissionScopes();
            RefreshPagedPermissions();
            (RefreshPostgresAdvancedCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
        if (IsPostgresBackend && IsPostgresAdvancedSelected)
            await EnsurePostgresAdvancedAsync();
    }

    public async Task EnsurePostgresAdvancedAsync()
    {
        if (!IsPostgresBackend || SelectedUser is not { } user || IsBusy || _permissionsLoading) return;
        if (string.Equals(_postgresAdvancedLoadedKey, user.Key, StringComparison.Ordinal)) return;
        _postgresAdvancedLoadedKey = user.Key;
        await LoadPostgresAdvancedAsync(user);
    }

    private async Task ReloadPostgresAdvancedAsync()
    {
        _postgresAdvancedLoadedKey = null;
        await EnsurePostgresAdvancedAsync();
    }

    private async Task LoadMySqlPermissionsAsync(DatabaseUserItem user)
    {
        var grantee = MySqlString($"'{user.Name}'@'{user.Host}'");
        var grants = await QueryAsync($"SHOW GRANTS FOR {MySqlString(user.Name)}@{MySqlString(user.Host)}", 100);
        GrantSummary = grants.Success
            ? string.Join(" | ", grants.Rows.Select(row => row.Values.FirstOrDefault()?.ToString()).Where(x => !string.IsNullOrWhiteSpace(x)))
            : "权限读取失败：" + grants.Error;

        var schemas = await QueryAsync("SHOW DATABASES", 500);
        var schemaPrivs = await QueryAsync(
            $"SELECT table_schema, privilege_type FROM information_schema.SCHEMA_PRIVILEGES WHERE grantee = {grantee}", 2000);
        var tablePrivs = await QueryAsync(
            $"SELECT table_schema, table_name, privilege_type FROM information_schema.TABLE_PRIVILEGES WHERE grantee = {grantee}", 5000);
        var tables = await QueryAsync(
            "SELECT table_schema, table_name FROM information_schema.tables " +
            "WHERE table_type = 'BASE TABLE' AND table_schema <> 'information_schema' " +
            "ORDER BY table_schema, table_name LIMIT 1200", 1200);

        var schemaGrantSet = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        if (schemaPrivs.Success)
            foreach (var row in schemaPrivs.Rows)
                AddPrivilege(schemaGrantSet, Text(row, "table_schema"), Text(row, "privilege_type"));
        var tableGrantSet = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        if (tablePrivs.Success)
            foreach (var row in tablePrivs.Rows)
                AddPrivilege(tableGrantSet, Text(row, "table_schema") + "." + Text(row, "table_name"), Text(row, "privilege_type"));

        if (schemas.Success)
            foreach (var row in schemas.Rows)
            {
                var db = FirstValue(row);
                if (IsInformationSchema(db)) continue;
                AddRow("数据库", IsSystemDatabase(db) ? $"{db}（系统库）" : db, db, string.Empty, string.Empty,
                    MySqlDatabasePrivileges, schemaGrantSet.GetValueOrDefault(db) ?? new HashSet<string>(),
                    IsSystemDatabase(db));
            }
        if (tables.Success)
            foreach (var row in tables.Rows)
            {
                var db = Text(row, "table_schema");
                var table = Text(row, "table_name");
                AddRow("表", IsSystemDatabase(db) ? $"{db}.{table}（系统库）" : $"{db}.{table}",
                    db, string.Empty, table, MySqlTablePrivileges,
                    tableGrantSet.GetValueOrDefault($"{db}.{table}") ?? new HashSet<string>(),
                    IsSystemDatabase(db), schemaGrantSet.GetValueOrDefault(db));
            }
    }

    private async Task LoadPostgresPermissionsAsync(DatabaseUserItem user)
    {
        var role = PostgresString(user.Name);
        var dbPrivs = await QueryAsync(
            $"SELECT datname, pg_get_userbyid(datdba) AS owner, " +
            $"has_database_privilege({role}, datname, 'CONNECT') AS p_connect, " +
            $"has_database_privilege({role}, datname, 'CREATE') AS p_create, " +
            $"has_database_privilege({role}, datname, 'TEMP') AS p_temp " +
            "FROM pg_database WHERE datistemplate = false ORDER BY datname", 500);
        var schemaPrivs = await QueryAsync(
            $"SELECT nspname, pg_get_userbyid(nspowner) AS owner, " +
            $"has_schema_privilege({role}, nspname, 'USAGE') AS p_usage, " +
            $"has_schema_privilege({role}, nspname, 'CREATE') AS p_create " +
            "FROM pg_namespace WHERE nspname NOT LIKE 'pg_%' AND nspname <> 'information_schema' ORDER BY nspname", 500);
        var tablePrivs = await QueryAsync(
            $"SELECT t.schemaname AS table_schema, t.tablename AS table_name, t.tableowner AS owner, " +
            $"has_table_privilege({role}, format('%I.%I', t.schemaname, t.tablename), 'SELECT') AS p_select, " +
            $"has_table_privilege({role}, format('%I.%I', t.schemaname, t.tablename), 'INSERT') AS p_insert, " +
            $"has_table_privilege({role}, format('%I.%I', t.schemaname, t.tablename), 'UPDATE') AS p_update, " +
            $"has_table_privilege({role}, format('%I.%I', t.schemaname, t.tablename), 'DELETE') AS p_delete, " +
            $"has_table_privilege({role}, format('%I.%I', t.schemaname, t.tablename), 'TRUNCATE') AS p_truncate, " +
            $"has_table_privilege({role}, format('%I.%I', t.schemaname, t.tablename), 'REFERENCES') AS p_references, " +
            $"has_table_privilege({role}, format('%I.%I', t.schemaname, t.tablename), 'TRIGGER') AS p_trigger " +
            "FROM pg_tables t WHERE t.schemaname NOT IN ('pg_catalog','information_schema') " +
            "ORDER BY t.schemaname, t.tablename LIMIT 800", 800);

        if (dbPrivs.Success)
            foreach (var row in dbPrivs.Rows)
                AddPostgresRow("数据库", Text(row, "datname"), Text(row, "datname"), string.Empty, string.Empty,
                    PostgresDatabasePrivileges, row, Text(row, "owner"));
        if (schemaPrivs.Success)
            foreach (var row in schemaPrivs.Rows)
                AddPostgresRow("Schema", Text(row, "nspname"), string.Empty, Text(row, "nspname"), string.Empty,
                    PostgresSchemaPrivileges, row, Text(row, "owner"));
        if (tablePrivs.Success)
            foreach (var row in tablePrivs.Rows)
                AddPostgresRow("表", $"{Text(row, "table_schema")}.{Text(row, "table_name")}",
                    string.Empty, Text(row, "table_schema"), Text(row, "table_name"), PostgresTablePrivileges, row,
                    Text(row, "owner"));

        GrantSummary = $"有效权限：数据库 {dbPrivs.Rows.Count} · Schema {schemaPrivs.Rows.Count} · 表 {tablePrivs.Rows.Count}";
    }

    private void AddPostgresRow(string kind, string label, string database, string schema, string table,
        string[] privileges, Dictionary<string, object?> row, string owner)
    {
        var granted = privileges
            .Where(privilege => Bool(row, "p_" + privilege.ToLowerInvariant()))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        AddRow(kind, label, database, schema, table, privileges, granted, owner: owner);
    }

    private void RefreshDefaultPrivilegeOptions()
    {
        var currentSchema = SelectedDefaultPrivilegeSchema;
        var schemas = PermissionRows.Where(row => row.Kind == "Schema")
            .Select(row => row.Schema).Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name).ToList();
        if (!schemas.Contains("public", StringComparer.OrdinalIgnoreCase)) schemas.Insert(0, "public");
        DefaultPrivilegeSchemas.Clear();
        DefaultPrivilegeSchemas.Add("*");
        foreach (var schema in schemas) DefaultPrivilegeSchemas.Add(schema);
        SelectedDefaultPrivilegeSchema = DefaultPrivilegeSchemas.Contains(currentSchema)
            ? currentSchema
            : (DefaultPrivilegeSchemas.Contains("public") ? "public" : DefaultPrivilegeSchemas[0]);

        var currentGrantee = SelectedDefaultPrivilegeGrantee;
        DefaultPrivilegeGrantees.Clear();
        foreach (var roleName in GrantableRoles) DefaultPrivilegeGrantees.Add(roleName);
        DefaultPrivilegeGrantees.Add("PUBLIC");
        SelectedDefaultPrivilegeGrantee = DefaultPrivilegeGrantees.Contains(currentGrantee)
            ? currentGrantee
            : (DefaultPrivilegeGrantees.FirstOrDefault() ?? "PUBLIC");
    }

    private async Task LoadPostgresAdvancedAsync(DatabaseUserItem user)
    {
        var role = PostgresString(user.Name);
        var sql =
            "SELECT json_build_object(" +
            $"'roles', COALESCE((SELECT json_agg(rolname ORDER BY rolname) FROM pg_roles WHERE rolname NOT LIKE 'pg_%' " +
            $"AND rolname <> {role} AND rolsuper = false), '[]'::json), " +
            "'memberships', COALESCE((SELECT json_agg(json_build_object('role_name', r.rolname, " +
            "'member_name', m.rolname, 'admin_option', am.admin_option, 'grantor', pg_get_userbyid(am.grantor)) " +
            "ORDER BY r.rolname, m.rolname) FROM pg_auth_members am JOIN pg_roles r ON r.oid = am.roleid " +
            $"JOIN pg_roles m ON m.oid = am.member WHERE (r.rolname = {role} OR m.rolname = {role}) " +
            "AND r.rolname NOT LIKE 'pg_%' AND m.rolname NOT LIKE 'pg_%'), '[]'::json), " +
            "'defaults', COALESCE((SELECT json_agg(json_build_object('schema_name', COALESCE(n.nspname, '*'), " +
            "'owner_name', r.rolname, 'object_type', d.defaclobjtype::text, 'grantee', COALESCE(g.rolname, 'PUBLIC'), " +
            "'privilege', a.privilege_type, 'is_grantable', a.is_grantable) ORDER BY n.nspname, d.defaclobjtype, " +
            "g.rolname, a.privilege_type) FROM pg_default_acl d JOIN pg_roles r ON r.oid = d.defaclrole " +
            "LEFT JOIN pg_namespace n ON n.oid = d.defaclnamespace CROSS JOIN LATERAL aclexplode(d.defaclacl) a " +
            $"LEFT JOIN pg_roles g ON g.oid = a.grantee WHERE r.rolname = {role}), '[]'::json)) AS payload";

        var result = await QueryAsync(sql, 1);
        if (!result.Success)
        {
            RoleMembershipSummary = "角色成员关系读取失败：" + result.Error;
            DefaultPrivilegeSummary = "默认权限读取失败：" + result.Error;
            RefreshDefaultPrivilegeOptions();
            return;
        }

        var payload = result.Rows.Count == 0 ? string.Empty : Text(result.Rows[0], "payload");
        if (string.IsNullOrWhiteSpace(payload))
        {
            RoleMembershipSummary = "当前账号没有直接角色成员关系。";
            DefaultPrivilegeSummary = "当前账号没有定义默认权限。";
            RefreshDefaultPrivilegeOptions();
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            GrantableRoles.Clear();
            if (root.TryGetProperty("roles", out var roles) && roles.ValueKind == JsonValueKind.Array)
                foreach (var element in roles.EnumerateArray())
                {
                    var name = element.GetString();
                    if (!string.IsNullOrWhiteSpace(name)) GrantableRoles.Add(name);
                }
            SelectedRoleToGrant = GrantableRoles.FirstOrDefault() ?? string.Empty;

            RoleMemberships.Clear();
            if (root.TryGetProperty("memberships", out var memberships) && memberships.ValueKind == JsonValueKind.Array)
                foreach (var element in memberships.EnumerateArray())
                {
                    var roleName = element.TryGetProperty("role_name", out var roleValue) ? roleValue.GetString() ?? string.Empty : string.Empty;
                    var memberName = element.TryGetProperty("member_name", out var memberValue) ? memberValue.GetString() ?? string.Empty : string.Empty;
                    var admin = element.TryGetProperty("admin_option", out var adminValue) &&
                                adminValue.ValueKind == JsonValueKind.True;
                    RoleMemberships.Add(new PostgresRoleMembershipItem
                    {
                        RoleName = roleName,
                        MemberName = memberName,
                        Direction = string.Equals(memberName, user.Name, StringComparison.Ordinal) ? "授予当前账号" : "当前账号授予他人",
                        AdminOption = admin ? "WITH ADMIN OPTION" : string.Empty,
                        Grantor = element.TryGetProperty("grantor", out var grantorValue) ? grantorValue.GetString() ?? string.Empty : string.Empty
                    });
                }
            SelectedRoleMembership = RoleMemberships.FirstOrDefault();
            RoleMembershipSummary = RoleMemberships.Count == 0
                ? "当前账号没有直接角色成员关系。"
                : $"共 {RoleMemberships.Count} 条直接成员关系（PG 有效权限可能还包含嵌套角色继承）。";

            DefaultPrivileges.Clear();
            if (root.TryGetProperty("defaults", out var defaults) && defaults.ValueKind == JsonValueKind.Array)
                foreach (var element in defaults.EnumerateArray())
                {
                    var objectType = (element.TryGetProperty("object_type", out var typeValue) ? typeValue.GetString() : string.Empty) switch
                    {
                        "r" => "TABLES",
                        "S" => "SEQUENCES",
                        "f" => "FUNCTIONS",
                        "T" => "TYPES",
                        "n" => "SCHEMAS",
                        var other => other ?? string.Empty
                    };
                    DefaultPrivileges.Add(new PostgresDefaultPrivilegeItem
                    {
                        Schema = element.TryGetProperty("schema_name", out var schemaValue) ? schemaValue.GetString() ?? string.Empty : string.Empty,
                        Owner = element.TryGetProperty("owner_name", out var ownerValue) ? ownerValue.GetString() ?? string.Empty : string.Empty,
                        ObjectType = objectType,
                        Grantee = element.TryGetProperty("grantee", out var granteeValue) ? granteeValue.GetString() ?? string.Empty : string.Empty,
                        Privilege = element.TryGetProperty("privilege", out var privilegeValue) ? privilegeValue.GetString() ?? string.Empty : string.Empty,
                        IsGrantable = element.TryGetProperty("is_grantable", out var grantableValue) &&
                                      grantableValue.ValueKind == JsonValueKind.True
                    });
                }
            SelectedDefaultPrivilege = DefaultPrivileges.FirstOrDefault();
            DefaultPrivilegeSummary = DefaultPrivileges.Count == 0
                ? "当前账号没有定义默认权限。"
                : $"共 {DefaultPrivileges.Count} 条默认权限规则。";
            RefreshDefaultPrivilegeOptions();
        }
        catch (JsonException ex)
        {
            RoleMembershipSummary = "角色成员关系解析失败：" + ex.Message;
            DefaultPrivilegeSummary = "默认权限解析失败：" + ex.Message;
            RefreshDefaultPrivilegeOptions();
        }
    }

    private void AddRow(string kind, string label, string database, string schema, string table,
        string[] privileges, HashSet<string> granted, bool isSystem = false, HashSet<string>? inherited = null,
        string owner = "")
    {
        var row = new DatabasePermissionRow
        {
            Kind = kind,
            Label = label,
            Database = database,
            Schema = schema,
            Table = table,
            IsSystem = isSystem,
            Owner = owner
        };
        foreach (var privilege in privileges)
            row.AddToggle(new DatabasePermissionToggle(privilege, granted.Contains(privilege),
                inherited?.Contains(privilege) == true));
        row.Changed += RebuildSqlPreview;
        PermissionRows.Add(row);
    }

    private void RebuildSqlPreview()
    {
        if (_suppressSql || SelectedUser is not { } user)
            return;
        _pendingStatements.Clear();
        Diffs.Clear();
        var statements = new List<string>();
        foreach (var row in PermissionRows)
        {
            var added = row.Toggles.Where(toggle => toggle.IsGranted && !toggle.Original).Select(toggle => toggle.Name).ToList();
            var removed = row.Toggles.Where(toggle => !toggle.IsGranted && toggle.Original).Select(toggle => toggle.Name).ToList();
            if (added.Count == 0 && removed.Count == 0) continue;
            if (added.Count > 0)
                Diffs.Add(new DatabasePermissionDiffItem { Change = "新增", Object = row.Label, Detail = string.Join(", ", added) });
            if (removed.Count > 0)
                Diffs.Add(new DatabasePermissionDiffItem { Change = "移除", Object = row.Label, Detail = string.Join(", ", removed) });

            if (IsMySql)
            {
                var target = row.Kind == "数据库"
                    ? $"{MySqlIdent(row.Database)}.*"
                    : $"{MySqlIdent(row.Database)}.{MySqlIdent(row.Table)}";
                var userSpec = $"{MySqlString(user.Name)}@{MySqlString(user.Host)}";
                if (added.Count > 0) statements.Add($"GRANT {string.Join(", ", added)} ON {target} TO {userSpec};");
                if (removed.Count > 0) statements.Add($"REVOKE {string.Join(", ", removed)} ON {target} FROM {userSpec};");
            }
            else
            {
                var role = PostgresIdent(user.Name);
                var on = row.Kind switch
                {
                    "数据库" => $"DATABASE {PostgresIdent(row.Database)}",
                    "Schema" => $"SCHEMA {PostgresIdent(row.Schema)}",
                    _ => $"TABLE {PostgresIdent(row.Schema)}.{PostgresIdent(row.Table)}"
                };
                if (added.Count > 0) statements.Add($"GRANT {string.Join(", ", added)} ON {on} TO {role};");
                if (removed.Count > 0) statements.Add($"REVOKE {string.Join(", ", removed)} ON {on} FROM {role};");
            }
        }
        _pendingStatements.AddRange(statements);
        SqlPreview = string.Join(Environment.NewLine, statements);
        PreviewSummary = Diffs.Count == 0 ? "暂无变更（勾选权限后自动生成）" : $"{Diffs.Count} 项权限变更";
        RefreshPagedPermissions();
    }

    private static string SnapshotDirectory =>
        Path.Combine(ConfigPaths.AppDataDir, "db-user-snapshots");

    private string SnapshotFilePrefix(DatabaseUserItem user) =>
        $"{SafeFilePart(_datasource.Id)}-{SafeFilePart(user.Name)}-{SafeFilePart(user.Host)}";

    private string? SavePermissionSnapshot()
    {
        if (SelectedUser is not { } user || PermissionRows.Count == 0)
            return null;
        try
        {
            var dir = SnapshotDirectory;
            Directory.CreateDirectory(dir);
            var createdAt = DateTime.Now;
            var snapshot = new DatabasePermissionSnapshot
            {
                DataSourceId = _datasource.Id,
                DataSourceName = _datasource.Name,
                Backend = BackendText,
                UserName = user.Name,
                Host = user.Host,
                CreatedAt = createdAt,
                Rows = PermissionRows.Select(row => new DatabaseSnapshotRow
                {
                    Kind = row.Kind,
                    Label = row.Label,
                    Database = row.Database,
                    Schema = row.Schema,
                    Table = row.Table,
                    Owner = row.Owner,
                    Granted = row.Toggles.Where(toggle => toggle.Original).Select(toggle => toggle.Name).ToList()
                }).ToList()
            };
            var file = Path.Combine(dir,
                $"{SnapshotFilePrefix(user)}-{createdAt:yyyyMMdd-HHmmss-fff}.json");
            File.WriteAllText(file, JsonSerializer.Serialize(snapshot,
                new JsonSerializerOptions { WriteIndented = true }));
            LoadSnapshots(user);
            return file;
        }
        catch
        {
            return null;
        }
    }

    private void LoadSnapshotDetails(DatabasePermissionSnapshotItem? item)
    {
        SnapshotDetails.Clear();
        if (item is null) return;
        foreach (var row in item.Snapshot.Rows)
            SnapshotDetails.Add(new DatabaseSnapshotDetailItem
            {
                Object = row.Label,
                Kind = row.Kind,
                Owner = row.Owner,
                Permissions = row.Granted.Count == 0 ? "无显式权限" : string.Join(", ", row.Granted)
            });
    }

    private void DeleteSelectedSnapshot()
    {
        if (SelectedSnapshot is not { } item) return;
        if (!_dialogs.Confirm($"删除快照「{item.CreatedAtText}」？", "数据库用户管理", true))
            return;
        try
        {
            File.Delete(item.FilePath);
            LoadSnapshots(SelectedUser);
            StatusText = "权限快照已删除。";
        }
        catch (Exception ex)
        {
            _dialogs.Error("删除快照失败：" + ex.Message, "数据库用户管理");
        }
    }

    private void OpenSnapshotFolder()
    {
        Directory.CreateDirectory(SnapshotDirectory);
        NativeShell.OpenFolder(SnapshotDirectory);
    }
    private void LoadSnapshots(DatabaseUserItem? user)
    {
        Snapshots.Clear();
        SelectedSnapshot = null;
        if (user is null) return;
        try
        {
            var dir = SnapshotDirectory;
            if (!Directory.Exists(dir)) return;
            foreach (var file in Directory.GetFiles(dir, $"{SnapshotFilePrefix(user)}-*.json")
                         .OrderByDescending(File.GetLastWriteTimeUtc))
            {
                try
                {
                    var snapshot = JsonSerializer.Deserialize<DatabasePermissionSnapshot>(
                        File.ReadAllText(file), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (snapshot is null || snapshot.DataSourceId != _datasource.Id ||
                        snapshot.UserName != user.Name || snapshot.Host != user.Host)
                        continue;
                    Snapshots.Add(new DatabasePermissionSnapshotItem
                    {
                        FilePath = file,
                        CreatedAt = snapshot.CreatedAt == default ? File.GetLastWriteTime(file) : snapshot.CreatedAt,
                        Snapshot = snapshot
                    });
                }
                catch
                {
                    // 单个损坏快照不影响其他快照
                }
            }
        }
        catch
        {
            // 快照目录不可读时保持空列表
        }
        SelectedSnapshot = Snapshots.FirstOrDefault();
    }

    private void ApplySelectedSnapshot()
    {
        if (SelectedUser is not { } user || SelectedSnapshot is not { } item) return;
        var snapshotRows = item.Snapshot.Rows
            .GroupBy(SnapshotKey)
            .ToDictionary(group => group.Key, group => group.First().Granted, StringComparer.Ordinal);
        _suppressSql = true;
        try
        {
            foreach (var row in PermissionRows)
            {
                if (!snapshotRows.TryGetValue(SnapshotKey(row), out var granted)) continue;
                var set = granted.ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var toggle in row.Toggles)
                    toggle.IsGranted = set.Contains(toggle.Name);
            }
        }
        finally
        {
            _suppressSql = false;
        }
        RebuildSqlPreview();
        StatusText = $"已载入 {item.CreatedAtText} 的权限快照，确认差异后点击「执行变更」。";
    }

    private static string SnapshotKey(DatabasePermissionRow row) =>
        $"{row.Kind}|{row.Database}|{row.Schema}|{row.Table}";

    private static string SnapshotKey(DatabaseSnapshotRow row) =>
        $"{row.Kind}|{row.Database}|{row.Schema}|{row.Table}";

    private static string SafeFilePart(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var text = new string((value ?? string.Empty).Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray())
            .Trim('_', ' ');
        return text.Length == 0 ? "item" : text;
    }
    private async Task ExecuteAsync()
    {
        if (_pendingStatements.Count == 0 || IsBusy)
            return;
        var preview = SqlPreview;
        var systemWarning = PermissionRows.Any(row => row.IsSystem && row.HasChanges)
            ? "\n\n注意：本次变更包含 MySQL 系统库（mysql / performance_schema / sys），请确认授权范围。"
            : string.Empty;
        if (!_dialogs.Confirm(
                $"将执行以下 SQL：\n\n{preview}{systemWarning}\n\n确认执行并生效？",
                "数据库用户管理", warning: true))
            return;

        var snapshot = SavePermissionSnapshot();
        IsBusy = true;
        StatusText = "正在执行权限变更…";
        try
        {
            foreach (var statement in _pendingStatements)
            {
                var result = await _driver.ExecuteAsync(_datasource, statement);
                await AuditAsync("grant_revoke", statement, result.Success, result.Error);
                if (!result.Success)
                {
                    StatusText = "执行失败：" + result.Error;
                    _dialogs.Error(StatusText, "数据库用户管理");
                    return;
                }
            }
            StatusText = "权限变更已执行。";
            var selected = SelectedUser;
            IsBusy = false;
            await RefreshAsync();
            if (selected is not null)
                SelectedUser = Users.FirstOrDefault(user => user.Key == selected.Key);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RaiseAccountCommands()
    {
        (LockAccountCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (UnlockAccountCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ExpirePasswordCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ClearPasswordExpiryCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ToggleLoginCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ToggleCreateDbCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ToggleCreateRoleCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (GrantRoleCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RevokeRoleCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (AddDefaultPrivilegeCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RevokeDefaultPrivilegeCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RefreshPostgresAdvancedCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private Task LockAccountAsync() =>
        RunMySqlAccountActionAsync("lock_account", "ACCOUNT LOCK", "账号已锁定");

    private Task UnlockAccountAsync() =>
        RunMySqlAccountActionAsync("unlock_account", "ACCOUNT UNLOCK", "账号已解锁");

    private Task ExpirePasswordAsync() =>
        RunMySqlAccountActionAsync("expire_password", "PASSWORD EXPIRE", "密码已设为立即过期");

    private Task ClearPasswordExpiryAsync() =>
        RunMySqlAccountActionAsync("clear_password_expiry", "PASSWORD EXPIRE NEVER", "已取消密码过期");

    private async Task RunMySqlAccountActionAsync(string operation, string clause, string successText)
    {
        if (SelectedUser is not { } user || !IsMySql || user.IsCurrent) return;
        var sql = $"ALTER USER {MySqlString(user.Name)}@{MySqlString(user.Host)} {clause};";
        await RunAccountActionAsync(operation, sql, successText);
    }

    private async Task ToggleLoginAsync()
    {
        if (SelectedUser is not { } user || IsMySql || user.IsCurrent || user.IsSuperuser) return;
        var sql = $"ALTER ROLE {PostgresIdent(user.Name)} {(user.CanLogin ? "NOLOGIN" : "LOGIN")};";
        await RunAccountActionAsync("toggle_login", sql, user.CanLogin ? "已禁止登录" : "已允许登录");
    }

    private async Task ToggleCreateDbAsync()
    {
        if (SelectedUser is not { } user || IsMySql || user.IsCurrent || user.IsSuperuser) return;
        var sql = $"ALTER ROLE {PostgresIdent(user.Name)} {(user.CanCreateDb ? "NOCREATEDB" : "CREATEDB")};";
        await RunAccountActionAsync("toggle_createdb", sql, user.CanCreateDb ? "已禁止 CREATEDB" : "已允许 CREATEDB");
    }

    private async Task ToggleCreateRoleAsync()
    {
        if (SelectedUser is not { } user || IsMySql || user.IsCurrent || user.IsSuperuser) return;
        var sql = $"ALTER ROLE {PostgresIdent(user.Name)} {(user.CanCreateRole ? "NOCREATEROLE" : "CREATEROLE")};";
        await RunAccountActionAsync("toggle_createrole", sql, user.CanCreateRole ? "已禁止 CREATEROLE" : "已允许 CREATEROLE");
    }

    private async Task GrantRoleAsync()
    {
        if (SelectedUser is not { } user || string.IsNullOrWhiteSpace(SelectedRoleToGrant)) return;
        var sql = $"GRANT {PostgresIdent(SelectedRoleToGrant)} TO {PostgresIdent(user.Name)};";
        await RunAccountActionAsync("grant_role", sql, $"已将角色 {SelectedRoleToGrant} 授予 {user.Name}");
    }

    private async Task RevokeRoleAsync()
    {
        if (SelectedRoleMembership is not { } membership) return;
        var sql = $"REVOKE {PostgresIdent(membership.RoleName)} FROM {PostgresIdent(membership.MemberName)};";
        await RunAccountActionAsync("revoke_role", sql,
            $"已撤销 {membership.RoleName} → {membership.MemberName}");
    }

    private async Task AddDefaultPrivilegeAsync()
    {
        if (SelectedUser is not { } user || string.IsNullOrWhiteSpace(SelectedDefaultPrivilegeGrantee)) return;
        var schema = SelectedDefaultPrivilegeSchema == "*"
            ? string.Empty
            : $" IN SCHEMA {PostgresIdent(SelectedDefaultPrivilegeSchema)}";
        var sql = $"ALTER DEFAULT PRIVILEGES FOR ROLE {PostgresIdent(user.Name)}{schema} " +
                  $"GRANT {SelectedDefaultPrivilegePrivilege} ON {SelectedDefaultPrivilegeObjectType} " +
                  $"TO {PostgresRoleIdent(SelectedDefaultPrivilegeGrantee)};";
        await RunAccountActionAsync("add_default_privilege", sql, "默认权限已添加");
    }

    private async Task RevokeDefaultPrivilegeAsync()
    {
        if (SelectedDefaultPrivilege is not { } item) return;
        var schema = item.Schema == "*" ? string.Empty : $" IN SCHEMA {PostgresIdent(item.Schema)}";
        var sql = $"ALTER DEFAULT PRIVILEGES FOR ROLE {PostgresIdent(item.Owner)}{schema} " +
                  $"REVOKE {item.Privilege} ON {item.ObjectType} FROM {PostgresRoleIdent(item.Grantee)};";
        await RunAccountActionAsync("revoke_default_privilege", sql,
            $"已撤销默认权限 {item.Privilege} / {item.ObjectType} / {item.Grantee}");
    }

    private async Task RunAccountActionAsync(string operation, string sql, string successText)
    {
        if (SelectedUser is not { } user || IsBusy || user.IsCurrent || (!IsMySql && user.IsSuperuser)) return;
        if (!_dialogs.Confirm($"确认执行数据库账号变更？\n\n{sql}", "数据库账号高级属性", true))
            return;

        IsBusy = true;
        try
        {
            var result = await _driver.ExecuteAsync(_datasource, sql);
            await AuditAsync(operation, sql, result.Success, result.Error);
            if (!result.Success)
            {
                StatusText = "账号变更失败：" + result.Error;
                _dialogs.Error(StatusText, "数据库账号高级属性");
                return;
            }
            StatusText = successText + "。";
            IsBusy = false;
            await RefreshAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }
    private async Task DeleteUserAsync()
    {
        if (SelectedUser is not { } user || IsBusy) return;
        if (user.IsCurrent)
        {
            _dialogs.Warn("不能删除当前连接账号。", "数据库用户管理");
            return;
        }
        var sql = IsMySql
            ? $"DROP USER {MySqlString(user.Name)}@{MySqlString(user.Host)};"
            : $"DROP ROLE {PostgresIdent(user.Name)};";
        if (!_dialogs.Confirm($"确认删除用户/角色「{user.DisplayName}」？\n\n{sql}", "数据库用户管理", true))
            return;

        IsBusy = true;
        try
        {
            var result = await _driver.ExecuteAsync(_datasource, sql);
            await AuditAsync("drop_user", sql, result.Success, result.Error);
            if (!result.Success)
            {
                StatusText = "删除失败：" + result.Error;
                _dialogs.Error(StatusText, "数据库用户管理");
                return;
            }
            StatusText = "用户/角色已删除。";
            IsBusy = false;
            await RefreshAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task ChangePasswordAsync(string password)
    {
        if (SelectedUser is not { } user || IsBusy) return;
        if (string.IsNullOrEmpty(password))
        {
            _dialogs.Warn("请输入新密码。", "数据库用户管理");
            return;
        }
        var actual = IsMySql
            ? $"ALTER USER {MySqlString(user.Name)}@{MySqlString(user.Host)} IDENTIFIED BY {MySqlString(password)};"
            : $"ALTER ROLE {PostgresIdent(user.Name)} PASSWORD {PostgresString(password)};";
        var preview = IsMySql
            ? $"ALTER USER {MySqlString(user.Name)}@{MySqlString(user.Host)} IDENTIFIED BY '<redacted>';"
            : $"ALTER ROLE {PostgresIdent(user.Name)} PASSWORD '<redacted>';";
        if (!_dialogs.Confirm($"将执行：\n\n{preview}\n\n确认修改密码？", "数据库用户管理", true))
            return;

        IsBusy = true;
        try
        {
            var result = await _driver.ExecuteAsync(_datasource, actual);
            await AuditAsync("change_password", preview, result.Success, result.Error);
            StatusText = result.Success ? "密码已修改。" : "修改密码失败：" + ExplainError(result.Error);
            if (!result.Success) _dialogs.Error(StatusText, "数据库用户管理");
            IsBusy = false;
            await RefreshAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task CreateUserAsync(string name, string host, string password, bool canLogin)
    {
        if (IsBusy) return;
        if (string.IsNullOrWhiteSpace(name) || !Regex.IsMatch(name, "^[A-Za-z0-9_.-]+$"))
        {
            _dialogs.Warn("用户名只能包含字母、数字、下划线、点和短横线。", "数据库用户管理");
            return;
        }
        if (string.IsNullOrEmpty(password))
        {
            _dialogs.Warn("请输入初始密码。", "数据库用户管理");
            return;
        }
        var actual = IsMySql
            ? $"CREATE USER {MySqlString(name)}@{MySqlString(host)} IDENTIFIED BY {MySqlString(password)};"
            : $"CREATE ROLE {PostgresIdent(name)} {(canLogin ? "LOGIN" : "NOLOGIN")} PASSWORD {PostgresString(password)};";
        var preview = IsMySql
            ? $"CREATE USER {MySqlString(name)}@{MySqlString(host)} IDENTIFIED BY '<redacted>';"
            : $"CREATE ROLE {PostgresIdent(name)} {(canLogin ? "LOGIN" : "NOLOGIN")} PASSWORD '<redacted>';";
        if (!_dialogs.Confirm($"将执行：\n\n{preview}\n\n确认创建用户？", "数据库用户管理", true))
            return;

        IsBusy = true;
        try
        {
            var result = await _driver.ExecuteAsync(_datasource, actual);
            await AuditAsync("create_user", preview, result.Success, result.Error);
            if (!result.Success)
            {
                StatusText = "创建失败：" + ExplainError(result.Error);
                _dialogs.Error(StatusText, "数据库用户管理");
                return;
            }
            NewUserName = string.Empty;
            StatusText = "用户/角色已创建。";
            IsBusy = false;
            await RefreshAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<DatasourceQueryResult> QueryAsync(string sql, int maxRows) =>
        await _driver.QueryAsync(_datasource, sql, maxRows);

    private static string ExplainError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return "未知错误";
        if (error.Contains("password does not satisfy", StringComparison.OrdinalIgnoreCase))
            return error + Environment.NewLine + Environment.NewLine +
                   "MySQL validate_password 策略要求密码满足长度、大小写、数字和特殊字符等规则。" +
                   "请使用更复杂的密码，或由 DBA 调整 validate_password.policy。";
        return error;
    }

    private async Task AuditAsync(string operation, string sql, bool success, string? error)
    {
        if (_audit is null) return;
        try
        {
            await _audit.LogSqlAsync(new SqlAuditLog
            {
                DataSourceId = _datasource.Id,
                DataSourceName = _datasource.Name,
                Tool = "db_user_manage",
                Sql = sql,
                Operation = SqlOperation.Execute,
                Status = success ? CommandStatus.Executed : CommandStatus.Failed,
                Result = success ? operation : error,
                Category = AuditCategory.Exec
            });
        }
        catch
        {
            // 审计失败不改变执行结果
        }
    }

    private bool FilterPermissionRow(DatabasePermissionRow row)
    {
        if (ShowOnlyChangedPermissions && !row.HasChanges) return false;
        if (SelectedPermissionScope != "全部" && ScopeOf(row) != SelectedPermissionScope) return false;
        var text = PermissionSearchText.Trim();
        if (text.Length == 0) return true;
        return row.Label.Contains(text, StringComparison.OrdinalIgnoreCase) ||
               row.Kind.Contains(text, StringComparison.OrdinalIgnoreCase) ||
               row.Database.Contains(text, StringComparison.OrdinalIgnoreCase) ||
               row.Schema.Contains(text, StringComparison.OrdinalIgnoreCase) ||
               row.Table.Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    private string ScopeOf(DatabasePermissionRow row) =>
        IsMySql ? row.Database : (string.IsNullOrWhiteSpace(row.Schema) ? row.Database : row.Schema);

    private void RefreshPagedPermissions()
    {
        var filtered = PermissionRows.Where(FilterPermissionRow).ToList();
        PermissionPageCount = Math.Max(1, (filtered.Count + PermissionPageSize - 1) / PermissionPageSize);
        if (PermissionPage > PermissionPageCount) PermissionPage = PermissionPageCount;
        if (PermissionPage < 1) PermissionPage = 1;
        PagedPermissionRows.Clear();
        foreach (var row in filtered.Skip((PermissionPage - 1) * PermissionPageSize).Take(PermissionPageSize))
            PagedPermissionRows.Add(row);
        PermissionFilterSummary = $"显示 {PagedPermissionRows.Count} / {filtered.Count} / {PermissionRows.Count} 个对象";
        RaisePageCommands();
    }

    private void RefreshPermissionScopes()
    {
        var current = SelectedPermissionScope;
        var scopes = PermissionRows.Select(ScopeOf)
            .Where(scope => !string.IsNullOrWhiteSpace(scope))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(scope => scope)
            .ToList();
        PermissionScopes.Clear();
        PermissionScopes.Add("全部");
        foreach (var scope in scopes) PermissionScopes.Add(scope);
        SelectedPermissionScope = PermissionScopes.Contains(current) ? current : "全部";
    }

    private void ChangePermissionPage(int delta)
    {
        var next = Math.Clamp(PermissionPage + delta, 1, PermissionPageCount);
        if (next == PermissionPage) return;
        PermissionPage = next;
        RefreshPagedPermissions();
    }

    private void SetVisibleTables(bool granted)
    {
        _suppressSql = true;
        try
        {
            foreach (var row in PermissionRows.Where(FilterPermissionRow).Where(row => row.Kind == "表"))
                foreach (var toggle in row.Toggles.Where(toggle => toggle.CanEdit))
                    toggle.IsGranted = granted;
        }
        finally
        {
            _suppressSql = false;
        }
        RebuildSqlPreview();
        RefreshPagedPermissions();
    }

    private void RaisePageCommands()
    {
        (PermissionPrevPageCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (PermissionNextPageCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (SelectAllVisibleTablesCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ClearVisibleTablesCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }
    private bool FilterUser(object item)
    {
        if (item is not DatabaseUserItem user) return false;
        var text = SearchText.Trim();
        return text.Length == 0 ||
               user.Name.Contains(text, StringComparison.OrdinalIgnoreCase) ||
               user.Host.Contains(text, StringComparison.OrdinalIgnoreCase) ||
               user.AccountStatus.Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSystemDatabase(string name) =>
        name.Equals("mysql", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("performance_schema", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("sys", StringComparison.OrdinalIgnoreCase);

    private static bool IsInformationSchema(string name) =>
        name.Equals("information_schema", StringComparison.OrdinalIgnoreCase);

    private static void AddPrivilege(Dictionary<string, HashSet<string>> map, string key, string privilege)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(privilege)) return;
        if (!map.TryGetValue(key, out var set))
            map[key] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        set.Add(privilege);
    }

    private static string Text(Dictionary<string, object?> row, string key) =>
        row.TryGetValue(key, out var value) ? Convert.ToString(value) ?? string.Empty : string.Empty;

    private static string FirstValue(Dictionary<string, object?> row) =>
        row.Values.Select(value => Convert.ToString(value)).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
        ?? string.Empty;

    private static bool Bool(Dictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out var value) || value is null) return false;
        if (value is bool b) return b;
        var text = Convert.ToString(value)?.Trim();
        return text is not null &&
               (text.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("t", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("y", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("on", StringComparison.OrdinalIgnoreCase));
    }

    private static string MySqlIdent(string value) => "`" + value.Replace("`", "``") + "`";
    private static string MySqlString(string value) => "'" + value.Replace("\\", "\\\\").Replace("'", "''") + "'";
    private static string PostgresIdent(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
    private static string PostgresRoleIdent(string value) =>
        value.Equals("PUBLIC", StringComparison.OrdinalIgnoreCase) ? "PUBLIC" : PostgresIdent(value);
    private static string PostgresString(string value) => "'" + value.Replace("'", "''") + "'";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
