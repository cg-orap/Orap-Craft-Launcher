using System.Text.Json;
using System.Text.Json.Serialization;
using Orion.Core.Diagnostics;
using Orion.Core.Util;

namespace Orion.Core.Auth;

/// <summary>
/// Persists accounts to disk. Tokens are encrypted with DPAPI before being written; the
/// file deliberately contains no usable credential in plain text.
/// </summary>
public sealed class AccountStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<Account>? _cache;

    public AccountStore(string path) => _path = path;

    public string FilePath => _path;

    public async Task<IReadOnlyList<Account>> LoadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cache is not null) return _cache;

            if (!File.Exists(_path))
            {
                _cache = [];
                return _cache;
            }

            AccountFile? file;
            try
            {
                file = await JsonUtil.DeserializeFileAsync<AccountFile>(_path, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                AppLog.Warn("accounts", "store-unreadable", ("path", _path), ("error", ex.Message));
                _cache = [];
                return _cache;
            }

            var accounts = new List<Account>();
            foreach (var record in file?.Accounts ?? [])
            {
                var account = record.ToAccount();
                if (account is not null) accounts.Add(account);
            }

            _cache = accounts;
            AppLog.Info("accounts", "loaded", ("count", accounts.Count));
            return accounts;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(IEnumerable<Account> accounts, CancellationToken ct = default)
    {
        var list = accounts.ToList();

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var file = new AccountFile
            {
                Version = 1,
                Accounts = list.Select(AccountRecord.From).ToList(),
            };

            await JsonUtil.WriteFileAsync(_path, file, ct).ConfigureAwait(false);
            _cache = list;
            AppLog.Info("accounts", "saved", ("count", list.Count), ("path", _path));
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed class AccountFile
    {
        [JsonPropertyName("version")] public int Version { get; set; } = 1;
        [JsonPropertyName("accounts")] public List<AccountRecord> Accounts { get; set; } = [];
    }

    private sealed class AccountRecord
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("username")] public string Username { get; set; } = "";
        [JsonPropertyName("uuid")] public string Uuid { get; set; } = "";
        [JsonPropertyName("type")] public string Type { get; set; } = "offline";
        [JsonPropertyName("xboxUserId")] public string? XboxUserId { get; set; }
        [JsonPropertyName("expiresAt")] public DateTimeOffset? ExpiresAt { get; set; }
        [JsonPropertyName("lastUsedAt")] public DateTimeOffset? LastUsedAt { get; set; }
        [JsonPropertyName("skinModel")] public string? SkinModel { get; set; }

        /// <summary>DPAPI protected JSON blob: <c>{"accessToken":"...","refreshToken":"..."}</c>.</summary>
        [JsonPropertyName("protectedTokens")] public string? ProtectedTokens { get; set; }

        public static AccountRecord From(Account account)
        {
            string? protectedTokens = null;

            if (account.Type == AccountType.Microsoft &&
                (!string.IsNullOrEmpty(account.AccessToken) || !string.IsNullOrEmpty(account.RefreshToken)))
            {
                var payload = JsonSerializer.Serialize(
                    new TokenPayload(account.AccessToken, account.RefreshToken),
                    JsonUtil.Compact);

                protectedTokens = ProtectedData.Protect(payload);
            }

            return new AccountRecord
            {
                Id = account.Id,
                Username = account.Username,
                Uuid = account.Uuid,
                Type = account.Type == AccountType.Microsoft ? "microsoft" : "offline",
                XboxUserId = account.XboxUserId,
                ExpiresAt = account.ExpiresAt,
                LastUsedAt = account.LastUsedAt,
                SkinModel = account.SkinModel,
                ProtectedTokens = protectedTokens,
            };
        }

        public Account? ToAccount()
        {
            if (string.IsNullOrWhiteSpace(Id) && string.IsNullOrWhiteSpace(Username)) return null;

            var account = new Account
            {
                Id = string.IsNullOrWhiteSpace(Id) ? Username : Id,
                Username = Username,
                Uuid = Uuid,
                Type = string.Equals(Type, "microsoft", StringComparison.OrdinalIgnoreCase) ? AccountType.Microsoft : AccountType.Offline,
                XboxUserId = XboxUserId,
                ExpiresAt = ExpiresAt,
                LastUsedAt = LastUsedAt,
                SkinModel = SkinModel,
            };

            if (!string.IsNullOrWhiteSpace(ProtectedTokens))
            {
                var plaintext = ProtectedData.Unprotect(ProtectedTokens);
                if (plaintext is not null)
                {
                    try
                    {
                        var payload = JsonSerializer.Deserialize<TokenPayload>(plaintext, JsonUtil.Compact);
                        account.AccessToken = payload?.AccessToken;
                        account.RefreshToken = payload?.RefreshToken;
                    }
                    catch (JsonException)
                    {
                    }
                }
            }

            return account;
        }
    }

    private sealed record TokenPayload(
        [property: JsonPropertyName("accessToken")] string? AccessToken,
        [property: JsonPropertyName("refreshToken")] string? RefreshToken);
}
