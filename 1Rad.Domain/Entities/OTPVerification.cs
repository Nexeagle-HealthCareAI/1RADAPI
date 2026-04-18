namespace _1Rad.Domain.Entities;

public class OTPVerification
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Identifier { get; set; } = string.Empty;
    public string CodeHash { get; set; } = string.Empty;
    public string Purpose { get; set; } = "Authentication"; // e.g., Authentication, PasswordReset
    public DateTime ExpiresAt { get; set; }
    public bool IsUsed { get; set; } = false;
}
