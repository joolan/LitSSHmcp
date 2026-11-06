namespace LitSSHmcp.App.Services.Toolbox;

public enum PasswordItemType
{
    Login,
    App,
    Email,
    Bank,
    ApiKey,
    SecureNote
}

public sealed class PasswordCustomField
{
    public string Label { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public bool IsSecret { get; set; }
}

public sealed class PasswordTotpConfig
{
    public string Secret { get; set; } = string.Empty;
    public string Issuer { get; set; } = string.Empty;
    public string Account { get; set; } = string.Empty;
    public string Algorithm { get; set; } = "SHA1";
    public int Digits { get; set; } = 6;
    public int Period { get; set; } = 30;
    public bool IsHotp { get; set; }
    public long Counter { get; set; }
}

public sealed class PasswordAttachment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public long Size { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public byte[] WrappedDataKey { get; set; } = Array.Empty<byte>();
}

public sealed class PasswordHistoryEntry
{
    public string Password { get; set; } = string.Empty;
    public DateTimeOffset ChangedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class PasswordItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public PasswordItemType Type { get; set; } = PasswordItemType.Login;
    public string Title { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public List<PasswordHistoryEntry> PasswordHistory { get; set; } = new();
    public string Url { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public string Group { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = new();
    public bool IsFavorite { get; set; }
    public string SecurityPhone { get; set; } = string.Empty;
    public string RecoveryEmail { get; set; } = string.Empty;
    public List<PasswordCustomField> CustomFields { get; set; } = new();
    public PasswordTotpConfig? Totp { get; set; }
    public List<PasswordAttachment> Attachments { get; set; } = new();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? LastUsedAt { get; set; }

    public PasswordItem Clone() => new()
    {
        Id = Id,
        Type = Type,
        Title = Title,
        Username = Username,
        Password = Password,
        PasswordHistory = (PasswordHistory ?? new()).Select(h => new PasswordHistoryEntry
        {
            Password = h.Password,
            ChangedAt = h.ChangedAt
        }).ToList(),
        Url = Url,
        Notes = Notes,
        Group = Group,
        Tags = new List<string>(Tags),
        IsFavorite = IsFavorite,
        SecurityPhone = SecurityPhone,
        RecoveryEmail = RecoveryEmail,
        CustomFields = CustomFields.Select(f => new PasswordCustomField
        {
            Label = f.Label,
            Value = f.Value,
            IsSecret = f.IsSecret
        }).ToList(),
        Totp = Totp is null ? null : new PasswordTotpConfig
        {
            Secret = Totp.Secret,
            Issuer = Totp.Issuer,
            Account = Totp.Account,
            Algorithm = Totp.Algorithm,
            Digits = Totp.Digits,
            Period = Totp.Period,
            IsHotp = Totp.IsHotp,
            Counter = Totp.Counter
        },
        Attachments = Attachments.Select(a => new PasswordAttachment
        {
            Id = a.Id,
            Name = a.Name,
            Size = a.Size,
            CreatedAt = a.CreatedAt,
            WrappedDataKey = a.WrappedDataKey.ToArray()
        }).ToList(),
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
        LastUsedAt = LastUsedAt
    };
}

public sealed class PasswordVaultIndex
{
    public int Version { get; set; } = 1;
    public string Name { get; set; } = "密码保管箱";
    public List<PasswordItem> Items { get; set; } = new();
}

public static class PasswordItemTypeText
{
    public static string For(PasswordItemType type) => type switch
    {
        PasswordItemType.Login => "网站",
        PasswordItemType.App => "应用",
        PasswordItemType.Email => "邮箱",
        PasswordItemType.Bank => "银行",
        PasswordItemType.ApiKey => "API Key",
        PasswordItemType.SecureNote => "安全笔记",
        _ => "其他"
    };
}
