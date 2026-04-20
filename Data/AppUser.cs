namespace SlotAd_Globe.Data;

public class AppUser
{
    public Guid Id { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>When set, <see cref="PasswordHash"/> is Base64(SHA256(salt || UTF8(password))). When null, PasswordHash is ASP.NET Identity (PBKDF2).</summary>
    public string? PasswordSalt { get; set; }

    public bool IsAdmin { get; set; }

    public DateTime CreatedUtc { get; set; }

    /// <summary>Optional pointer for Home redirect; not a navigational FK to avoid circular migrations.</summary>
    public Guid? LastReportUploadId { get; set; }

    public ICollection<ReportUploadEntity> ReportUploads { get; set; } = new List<ReportUploadEntity>();
}
