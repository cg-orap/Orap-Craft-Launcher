using System.Windows.Media;
using Orion.App.Mvvm;
using Orion.App.Services;
using Orion.Core;
using Orion.Core.Auth;
using Orion.Core.Diagnostics;

namespace Orion.App.ViewModels;

/// <summary>One avatar row in the login window.</summary>
public sealed class AccountEntry(Account account) : ObservableObject
{
    private ImageSource? _avatar;

    public Account Account { get; } = account;

    public string Username => Account.Username;

    public string Detail => Account.Type == AccountType.Microsoft
        ? (Account.IsExpired ? "微软正版账户 · 需要刷新令牌" : "微软正版账户")
        : "离线账户 · 仅单机与局域网";

    /// <summary>Skin head, or null to fall back to an initial badge.</summary>
    public ImageSource? Avatar
    {
        get => _avatar;
        set
        {
            if (SetProperty(ref _avatar, value)) OnPropertyChanged(nameof(HasAvatar));
        }
    }

    public bool HasAvatar => _avatar is not null;
}

/// <summary>
/// The dedicated login window's view model: saved accounts, offline account creation and the
/// Microsoft browser sign-in. The browser itself lives in the window (only one method is kept).
/// </summary>
public sealed class LoginViewModel : ObservableObject
{
    private readonly LauncherContext _context;

    private AccountEntry? _selected;
    private string _offlineName = "";
    private string _status = "";
    private bool _busy;

    public LoginViewModel(LauncherContext context)
    {
        _context = context;

        AddOfflineCommand = new AsyncRelayCommand(AddOfflineAsync, () => OfflineAccounts.IsValidName(OfflineName) && !_busy);
        RemoveAccountCommand = new AsyncRelayCommand(RemoveAccountAsync, () => _selected is not null && !_busy);
    }

    /// <summary>Auth service used by the window's embedded browser sign-in.</summary>
    public MicrosoftAuthService Auth => _context.Auth;

    public ObservableList<AccountEntry> Accounts { get; } = [];

    public AsyncRelayCommand AddOfflineCommand { get; }
    public AsyncRelayCommand RemoveAccountCommand { get; }

    /// <summary>Raised whenever the account list or the selection changed (the shell reloads).</summary>
    public event Action? AccountsChanged;

    public AccountEntry? Selected
    {
        get => _selected;
        set
        {
            if (!SetProperty(ref _selected, value)) return;

            RemoveAccountCommand.RaiseCanExecuteChanged();

            if (value is not null)
            {
                _context.Settings.SelectedAccountId = value.Account.Id;
                AccountsChanged?.Invoke();
            }
        }
    }

    public string OfflineName
    {
        get => _offlineName;
        set
        {
            if (!SetProperty(ref _offlineName, value)) return;
            AddOfflineCommand.RaiseCanExecuteChanged();
        }
    }

    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    public bool IsBusy
    {
        get => _busy;
        set
        {
            if (!SetProperty(ref _busy, value)) return;
            AddOfflineCommand.RaiseCanExecuteChanged();
            RemoveAccountCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Explains which Microsoft flow the window will use (v2.0 vs legacy).</summary>
    public string MicrosoftHint => _context.Auth.CanUseModernFlows
        ? "在内置浏览器中登录微软账户，选择账户并同意后自动完成，无需复制任何内容。"
        : "在内置浏览器中登录微软账户。当前未配置 Azure 应用 ID，使用旧版流程；"
          + "若提示 Xbox Live 拒绝令牌，请在「设置 → 微软登录」填入自己的应用 ID。";

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var accounts = await _context.AccountStore.LoadAsync(ct).ConfigureAwait(true);

        Accounts.Replace(accounts.Select(a => new AccountEntry(a)));

        var selectedId = _context.Settings.SelectedAccountId;
        Selected = Accounts.FirstOrDefault(e => string.Equals(e.Account.Id, selectedId, StringComparison.OrdinalIgnoreCase))
                   ?? Accounts.FirstOrDefault();

        await LoadAvatarsAsync(Accounts, ct).ConfigureAwait(true);

        if (Accounts.Count == 0) Status = "还没有账户。先在右侧用微软账户登录，或添加一个离线账户。";
    }

    /// <summary>Downloads (and caches) skin heads for all entries; failures keep the initial badge.</summary>
    public async Task LoadAvatarsAsync(IEnumerable<AccountEntry> entries, CancellationToken ct = default)
    {
        foreach (var entry in entries.ToList())
        {
            if (entry.Account.Type != AccountType.Microsoft || entry.HasAvatar) continue;

            var avatar = await SkinAvatars
                .LoadAsync(_context.Http, entry.Account.SkinUrl, entry.Account.EffectiveUuid, _context.Paths.Root, ct)
                .ConfigureAwait(true);

            if (avatar is not null) entry.Avatar = avatar;
        }
    }

    /// <summary>Persists a freshly authenticated Microsoft account and selects it.</summary>
    public async Task AcceptMicrosoftAccountAsync(Account account, CancellationToken ct = default)
    {
        var accounts = (await _context.AccountStore.LoadAsync(ct).ConfigureAwait(true))
            .Where(a => !string.Equals(a.Uuid, account.Uuid, StringComparison.OrdinalIgnoreCase))
            .ToList();

        accounts.Add(account);
        await _context.AccountStore.SaveAsync(accounts, ct).ConfigureAwait(true);

        // Keep existing entries (they already carry their avatars) and add the new one.
        var entries = accounts
            .Select(a => Accounts.FirstOrDefault(e => string.Equals(e.Account.Id, a.Id, StringComparison.Ordinal))
                         ?? new AccountEntry(a))
            .ToList();

        Accounts.Replace(entries);
        Selected = Accounts.First(e => string.Equals(e.Account.Id, account.Id, StringComparison.Ordinal));

        await LoadAvatarsAsync([Selected], ct).ConfigureAwait(true);

        Status = $"已登录：{account.Username}";
        _context.Settings.SelectedAccountId = account.Id;
        await _context.SettingsStore.SaveAsync(_context.Settings, ct).ConfigureAwait(true);

        AppLog.Info("ui", "login-window-account-accepted", ("name", account.Username));
        AccountsChanged?.Invoke();
    }

    private async Task AddOfflineAsync()
    {
        var name = OfflineName.Trim();
        if (!OfflineAccounts.IsValidName(name))
        {
            Status = "离线用户名需为 3-16 位字母、数字或下划线。";
            return;
        }

        var accounts = (await _context.AccountStore.LoadAsync().ConfigureAwait(true)).ToList();
        if (accounts.Any(a => string.Equals(a.Username, name, StringComparison.OrdinalIgnoreCase)))
        {
            Status = "该离线账户已存在。";
            return;
        }

        var account = OfflineAccounts.Create(name);
        accounts.Add(account);
        await _context.AccountStore.SaveAsync(accounts).ConfigureAwait(true);

        var entry = new AccountEntry(account);
        Accounts.Add(entry);
        Selected = entry;

        OfflineName = "";
        Status = $"已添加离线账户 {account.Username}。";

        _context.Settings.SelectedAccountId = account.Id;
        await _context.SettingsStore.SaveAsync(_context.Settings).ConfigureAwait(true);
        AccountsChanged?.Invoke();
    }

    private async Task RemoveAccountAsync()
    {
        if (Selected is null) return;

        var target = Selected;
        var accounts = (await _context.AccountStore.LoadAsync().ConfigureAwait(true))
            .Where(a => !string.Equals(a.Id, target.Account.Id, StringComparison.Ordinal))
            .ToList();

        await _context.AccountStore.SaveAsync(accounts).ConfigureAwait(true);
        Accounts.Remove(target);
        Selected = Accounts.FirstOrDefault();

        Status = $"已移除账户 {target.Username}（不会影响游戏文件）。";
        AccountsChanged?.Invoke();
    }
}
