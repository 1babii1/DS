using System.ComponentModel.DataAnnotations;

namespace AuthService.Web.Configuration;

public class EmailOptions
{
    public const string SectionName = "Email";

    [Required]
    public string SmtpHost { get; set; } = null!;

    public int SmtpPort { get; set; } = 1025;

    public bool UseSsl { get; set; }

    public string? SmtpUsername { get; set; }

    public string? SmtpPassword { get; set; }

    [Required]
    public string FromAddress { get; set; } = null!;

    public string FromName { get; set; } = "Portfolio Platform";
}
